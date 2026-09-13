using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

using WinMan;
using WinMan.Windows;

namespace FancyWM.Utilities
{
    internal class WindowDragger
    {
        public IWindow Window => m_window;

        private readonly IWindow m_window;
        private readonly Action m_focus;
        private readonly Action m_started;
        private readonly Action m_ended;
        private readonly Func<Action, Task> m_dispatch;
        private readonly IWorkspace m_workspace;
        private readonly Rectangle m_originalRect;
        private readonly int m_xOffset;
        private readonly int m_yOffset;
        private readonly object m_gate = new();
        private readonly object m_positionGate = new();
        private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool m_beginClaimed;
        private bool m_stopping;
        private bool m_subscriptionOwned;
        private bool m_unsubscribePending;
        private bool m_notificationPredecessorAssigned;
        private bool m_notificationPredecessorReady = true;
        private bool m_holdCursorWrites;
        private bool m_serializeCursorWrites;
        private bool m_heldCursorWrite;
        private Point m_heldCursorLocation;
        private bool m_startScheduled;
        private bool m_startSubmissionClaimed;
        private bool m_startSubmitted;
        private bool m_startEntered;
        private bool m_startCompleted;
        private bool m_cursorEffectEntered;
        private bool m_endScheduled;
        private bool m_endCompleted;
        private bool m_completionSignaled;
        private int m_enteredEffects;
        private int m_pendingDispatches;
        private Exception? m_failure;
        private List<Exception>? m_laterFailures;

        public WindowDragger(IWindow window)
            : this(window, () => FocusHelper.ForceActivate(window.Handle),
                () => ((Win32Window)window).RaisePositionChangeStart(),
                () => ((Win32Window)window).RaisePositionChangeEnd(), CreateDispatcher())
        {
        }

        internal WindowDragger(IWindow window, Action focus, Action started, Action ended, Func<Action, Task> dispatch)
        {
            m_window = window;
            m_focus = focus;
            m_started = started;
            m_ended = ended;
            m_dispatch = dispatch;
            m_workspace = window.Workspace;
            m_originalRect = window.Position;
            m_xOffset = m_workspace.CursorLocation.X - m_originalRect.Left;
            m_yOffset = m_workspace.CursorLocation.Y - m_originalRect.Top;
        }

        internal Task Completion => m_completion.Task;

        internal void SetNotificationPredecessor(Task predecessor)
        {
            ArgumentNullException.ThrowIfNull(predecessor);
            bool observe;
            lock (m_gate)
            {
                if (m_notificationPredecessorAssigned || m_beginClaimed || m_stopping)
                {
                    throw new InvalidOperationException("A drag notification predecessor must be assigned before Begin.");
                }
                m_notificationPredecessorAssigned = true;
                observe = !predecessor.IsCompleted;
                m_notificationPredecessorReady = !observe;
                m_holdCursorWrites = observe;
                m_serializeCursorWrites = observe;
            }
            if (observe) { _ = ObserveNotificationPredecessorAsync(predecessor); }
        }

        private static Func<Action, Task> CreateDispatcher()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            return action => dispatcher.InvokeAsync(action).Task;
        }

        internal void Begin(bool activateWindow)
        {
            lock (m_gate)
            {
                if (m_beginClaimed || m_stopping) { return; }
                m_beginClaimed = true;
                m_enteredEffects++;
            }

            bool subscriptionMayExist = false;
            try
            {
                var state = m_window.State;
                if (!CanContinue()) { return; }
                if (state != WindowState.Restored)
                {
                    m_window.SetState(WindowState.Restored);
                    if (!CanContinue()) { return; }
                }
                if (activateWindow)
                {
                    m_focus();
                    if (!CanContinue()) { return; }
                }

                if (!CanContinue()) { return; }
                subscriptionMayExist = true;
                m_workspace.CursorLocationChanged += OnCursorLocationChanged;

                bool retainSubscription;
                lock (m_gate)
                {
                    retainSubscription = !m_stopping;
                    if (retainSubscription) { m_subscriptionOwned = true; }
                }
                subscriptionMayExist = false;
                if (!retainSubscription)
                {
                    m_workspace.CursorLocationChanged -= OnCursorLocationChanged;
                    return;
                }
                TryScheduleStart();
            }
            catch (Exception error)
            {
                RequestStop(error);
                if (subscriptionMayExist)
                {
                    try { m_workspace.CursorLocationChanged -= OnCursorLocationChanged; }
                    catch (Exception cleanupError) { RecordFailure(cleanupError); }
                }
                throw;
            }
            finally
            {
                FinishEffect();
            }
        }

        internal void End()
        {
            RequestStop();
        }

        private void OnCursorLocationChanged(object? sender, CursorLocationChangedEventArgs e)
        {
            bool hold;
            bool serialize;
            lock (m_gate)
            {
                if (m_stopping || !m_subscriptionOwned) { return; }
                m_cursorEffectEntered = true;
                m_enteredEffects++;
                hold = m_holdCursorWrites;
                serialize = m_serializeCursorWrites;
                if (hold)
                {
                    m_heldCursorWrite = true;
                    m_heldCursorLocation = e.NewLocation;
                }
            }
            try
            {
                if (!hold)
                {
                    if (serialize)
                    {
                        lock (m_positionGate) { ApplyCursorLocation(e.NewLocation); }
                    }
                    else { ApplyCursorLocation(e.NewLocation); }
                }
            }
            finally
            {
                FinishEffect();
            }
        }

        private void ApplyCursorLocation(Point location)
        {
            try
            {
                m_window.SetPosition(Rectangle.OffsetAndSize(location.X - m_xOffset, location.Y - m_yOffset,
                    m_originalRect.Width, m_originalRect.Height));
            }
            catch (InvalidWindowReferenceException) { RequestStop(); }
            catch (Exception) { RequestStop(); }
        }

        private void ReleaseHeldCursorWrite()
        {
            lock (m_positionGate)
            {
                Point location = default;
                bool write;
                lock (m_gate)
                {
                    if (!m_holdCursorWrites) { return; }
                    write = m_heldCursorWrite;
                    if (write) { location = m_heldCursorLocation; }
                    m_heldCursorWrite = false;
                    m_holdCursorWrites = false;
                }
                if (write) { ApplyCursorLocation(location); }
            }
        }

        private bool CanContinue()
        {
            lock (m_gate) { return !m_stopping; }
        }

        private void TryScheduleStart()
        {
            bool submit;
            lock (m_gate)
            {
                if (m_startScheduled || (!m_cursorEffectEntered && (m_stopping || !m_subscriptionOwned))) { return; }
                m_startScheduled = true;
                m_pendingDispatches++;
                submit = m_notificationPredecessorReady;
                if (submit) { m_startSubmissionClaimed = true; }
            }
            if (submit) { Dispatch(InvokeStart, isEnd: false); }
        }

        private async Task ObserveNotificationPredecessorAsync(Task predecessor)
        {
            try { await predecessor.ConfigureAwait(false); }
            catch (Exception) { }

            bool submit;
            lock (m_gate)
            {
                m_notificationPredecessorReady = true;
                submit = m_startScheduled && !m_startSubmissionClaimed;
                if (submit) { m_startSubmissionClaimed = true; }
            }
            if (submit) { Dispatch(InvokeStart, isEnd: false); }
            else { Advance(); }
        }

        private void InvokeStart()
        {
            lock (m_gate)
            {
                if (m_stopping && !m_cursorEffectEntered)
                {
                    m_startCompleted = true;
                    return;
                }
                m_startEntered = true;
            }
            try { m_started(); }
            finally
            {
                ReleaseHeldCursorWrite();
                lock (m_gate) { m_startCompleted = true; }
                Advance();
            }
        }

        private void InvokeEnd()
        {
            lock (m_gate)
            {
                if (!m_startEntered) { return; }
            }
            m_ended();
        }

        private void Dispatch(Action action, bool isEnd)
        {
            Task task;
            try
            {
                task = m_dispatch(action) ?? Task.FromException(new InvalidOperationException("The drag dispatcher returned no task."));
            }
            catch (Exception error)
            {
                FinishDispatch(isEnd, error);
                return;
            }
            if (!isEnd)
            {
                lock (m_gate) { m_startSubmitted = true; }
                Advance();
            }
            _ = ObserveDispatchAsync(task, isEnd);
        }

        private async Task ObserveDispatchAsync(Task task, bool isEnd)
        {
            Exception? failure = null;
            try { await task.ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            FinishDispatch(isEnd, failure);
        }

        private void FinishDispatch(bool isEnd, Exception? failure)
        {
            if (failure != null)
            {
                if (isEnd) { RecordFailure(failure); }
                else
                {
                    ReleaseHeldCursorWrite();
                    RequestStop(failure);
                }
            }
            lock (m_gate)
            {
                m_pendingDispatches--;
                if (isEnd) { m_endCompleted = true; }
            }
            Advance();
        }

        private void RequestStop(Exception? failure = null)
        {
            bool unsubscribe;
            lock (m_gate)
            {
                if (failure != null) { RecordFailureLocked(failure); }
                m_stopping = true;
                unsubscribe = m_subscriptionOwned;
                m_subscriptionOwned = false;
                if (unsubscribe) { m_unsubscribePending = true; }
            }
            if (unsubscribe)
            {
                try { m_workspace.CursorLocationChanged -= OnCursorLocationChanged; }
                catch (Exception error) { RecordFailure(error); }
                finally
                {
                    lock (m_gate) { m_unsubscribePending = false; }
                    Advance();
                }
            }
            else { Advance(); }
        }

        private void FinishEffect()
        {
            lock (m_gate) { m_enteredEffects--; }
            Advance();
        }

        private void RecordFailure(Exception error)
        {
            lock (m_gate) { RecordFailureLocked(error); }
        }

        private void RecordFailureLocked(Exception error)
        {
            if (m_failure == null) { m_failure = error; }
            else if (!ReferenceEquals(m_failure, error)) { (m_laterFailures ??= []).Add(error); }
        }

        private void Advance()
        {
            bool scheduleEnd;
            bool complete;
            Exception? failure;
            lock (m_gate)
            {
                // A terminal notification observes every accepted native write
                // and a completed removal. ModifierWindowMover also delays the
                // next start until this drag has delivered that notification.
                scheduleEnd = m_stopping && !m_unsubscribePending && m_enteredEffects == 0 && !m_endScheduled
                    && ((m_startEntered && m_startCompleted)
                        || (!m_startEntered && m_cursorEffectEntered && m_startSubmitted));
                if (scheduleEnd)
                {
                    m_endScheduled = true;
                    m_pendingDispatches++;
                }

                complete = m_stopping && !m_unsubscribePending && m_notificationPredecessorReady
                    && m_enteredEffects == 0 && m_pendingDispatches == 0
                    && (!m_startEntered || m_endCompleted) && !m_completionSignaled;
                failure = m_failure;
                if (complete)
                {
                    m_completionSignaled = true;
                    if (failure != null && m_laterFailures != null)
                    {
                        failure.Data["WindowDragger.CleanupExceptions"] = new AggregateException(m_laterFailures);
                    }
                }
            }

            if (scheduleEnd) { Dispatch(InvokeEnd, isEnd: true); }
            if (complete)
            {
                if (failure == null) { m_completion.TrySetResult(); }
                else { m_completion.TrySetException(failure); }
            }
        }
    }
}
