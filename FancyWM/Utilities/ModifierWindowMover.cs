using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows.Input;

using Serilog;

using WinMan;

namespace FancyWM.Utilities
{
    internal class ModifierWindowMover : IDisposable
    {
        public bool IsEnabled { get; set; }

        public bool AutoFocus { get; set; }

        private readonly ILogger m_logger;
        private readonly Action<LowLevelMouseHook.ButtonStateChangedEventHandler> m_unsubscribe;
        private readonly IWorkspace m_workspace;
        private readonly Func<IWindow, WindowDragger> m_createDragger;
        private readonly Func<bool> m_moveModifierPressed;
        private readonly Func<bool> m_activateModifierPressed;
        private readonly object m_gate = new();
        private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task m_notificationTail = Task.CompletedTask;
        private WindowDragger? m_windowDragger = null;
        private bool m_hookOwned;
        private bool m_disposed;
        private bool m_unsubscribePending;
        private bool m_completionSignaled;
        private int m_enteredCallbacks;
        private int m_pendingDrags;
        private int m_dragReservations;
        private Exception? m_failure;
        private List<Exception>? m_laterFailures;

        internal Task Completion => m_completion.Task;

        public ModifierWindowMover(IWorkspace workspace, LowLevelMouseHook mshk)
            : this(workspace, handler => mshk.ButtonStateChanged += handler,
                handler => mshk.ButtonStateChanged -= handler, static window => new WindowDragger(window),
                IsMoveModifierPressed, IsMoveActivateModifierPressed, App.Current.Logger)
        {
        }

        internal ModifierWindowMover(IWorkspace workspace,
            Action<LowLevelMouseHook.ButtonStateChangedEventHandler> subscribe,
            Action<LowLevelMouseHook.ButtonStateChangedEventHandler> unsubscribe,
            Func<IWindow, WindowDragger> createDragger, Func<bool> moveModifierPressed,
            Func<bool> activateModifierPressed, ILogger logger)
        {
            m_workspace = workspace;
            m_unsubscribe = unsubscribe;
            m_createDragger = createDragger;
            m_moveModifierPressed = moveModifierPressed;
            m_activateModifierPressed = activateModifierPressed;
            m_logger = logger;
            try { subscribe(OnMouseButtonStateChanged); }
            catch (Exception error)
            {
                try { unsubscribe(OnMouseButtonStateChanged); }
                catch (Exception cleanupError)
                {
                    error.Data["ModifierWindowMover.ConstructionCleanupException"] = cleanupError;
                }
                throw;
            }
            m_hookOwned = true;
        }

        private void OnMouseButtonStateChanged(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
        {
            e.Handled = false;
            if (!TryEnterCallback()) { return; }
            try
            {
                if (!IsEnabled || e.Button != LowLevelMouseHook.MouseButton.Left)
                {
                    return;
                }

                WindowDragger? windowDragger;
                lock (m_gate)
                {
                    if (m_disposed) { return; }
                    windowDragger = m_windowDragger;
                    if (windowDragger != null) { m_windowDragger = null; }
                }
                if (windowDragger != null)
                {
                    e.Handled = true;
                    windowDragger.End();
                    return;
                }
                if (!e.IsPressed || !CanContinue() || !m_moveModifierPressed() || !CanContinue())
                {
                    return;
                }

                if (!TryReserveDrag()) { return; }
                bool reservationOwned = true;
                try
                {
                    var window = m_workspace.FindWindowFromPoint(new(e.X, e.Y));
                    if (window == null || !CanContinue())
                    {
                        return;
                    }

                    windowDragger = m_createDragger(window);
                    bool begin = TrackAndPublish(windowDragger);
                    reservationOwned = false;
                    if (!begin)
                    {
                        windowDragger.End();
                        return;
                    }

                    bool activateWindow = AutoFocus || m_activateModifierPressed();
                    if (!IsCurrentAndActive(windowDragger))
                    {
                        windowDragger.End();
                        return;
                    }
                    try
                    {
                        windowDragger.Begin(activateWindow);
                    }
                    catch (Exception)
                    {
                        windowDragger.End();
                        ClearCurrent(windowDragger);
                    }
                    e.Handled = true;
                }
                catch (Exception ex)
                {
                    m_logger.Error(ex, "Ocurred during drag mouse hook!");
                }
                finally
                {
                    if (reservationOwned) { ReleaseDragReservation(); }
                }
            }
            finally
            {
                ExitCallback();
            }
        }

        private bool TryEnterCallback()
        {
            lock (m_gate)
            {
                if (m_disposed) { return false; }
                m_enteredCallbacks++;
                return true;
            }
        }

        private void ExitCallback()
        {
            lock (m_gate) { m_enteredCallbacks--; }
            Advance();
        }

        private bool CanContinue()
        {
            lock (m_gate) { return !m_disposed; }
        }

        private bool TryReserveDrag()
        {
            lock (m_gate)
            {
                // Retain at most the draining drag and one current successor.
                if (m_disposed || m_windowDragger != null || m_dragReservations != 0 || m_pendingDrags >= 2)
                {
                    return false;
                }
                m_dragReservations = 1;
                return true;
            }
        }

        private void ReleaseDragReservation()
        {
            lock (m_gate) { m_dragReservations--; }
            Advance();
        }

        private bool TrackAndPublish(WindowDragger windowDragger)
        {
            bool begin;
            Task completion = windowDragger.Completion;
            lock (m_gate)
            {
                windowDragger.SetNotificationPredecessor(m_notificationTail);
                m_dragReservations--;
                m_notificationTail = completion;
                m_pendingDrags++;
                begin = !m_disposed && m_windowDragger == null;
                if (begin) { m_windowDragger = windowDragger; }
            }
            _ = ObserveDraggerAsync(windowDragger);
            return begin;
        }

        private bool IsCurrentAndActive(WindowDragger windowDragger)
        {
            lock (m_gate) { return !m_disposed && ReferenceEquals(m_windowDragger, windowDragger); }
        }

        private void ClearCurrent(WindowDragger windowDragger)
        {
            lock (m_gate)
            {
                if (ReferenceEquals(m_windowDragger, windowDragger)) { m_windowDragger = null; }
            }
        }

        private async Task ObserveDraggerAsync(WindowDragger windowDragger)
        {
            Task completion = windowDragger.Completion;
            Exception? failure = null;
            try { await completion.ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            Exception? loggingFailure = null;
            if (failure != null)
            {
                try { m_logger.Error(failure, "Occurred while completing modifier window drag"); }
                catch (Exception error) { loggingFailure = error; }
            }
            lock (m_gate)
            {
                // Ordinary drag failures are operational events and must not be
                // retained for the lifetime of the MainWindow. Only a drag that
                // is still owned by shutdown contributes to shutdown failure.
                if (failure != null && m_disposed) { RecordFailureLocked(failure); }
                if (loggingFailure != null && m_disposed) { RecordFailureLocked(loggingFailure); }
                if (ReferenceEquals(m_notificationTail, completion)) { m_notificationTail = Task.CompletedTask; }
                m_pendingDrags--;
            }
            Advance();
        }

        private static bool IsMoveModifierPressed()
        {
            static bool GetState() => Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
            if (App.Current.Dispatcher.CheckAccess())
            {
                return GetState();
            }
            else
            {
                return App.Current.Dispatcher.Invoke(GetState, System.Windows.Threading.DispatcherPriority.Send);
            }
        }

        private static bool IsMoveActivateModifierPressed()
        {
            static bool GetState() => Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            if (App.Current.Dispatcher.CheckAccess())
            {
                return GetState();
            }
            else
            {
                return App.Current.Dispatcher.Invoke(GetState, System.Windows.Threading.DispatcherPriority.Send);
            }
        }

        public void Dispose()
        {
            bool unsubscribe;
            WindowDragger? windowDragger;
            lock (m_gate)
            {
                if (m_disposed) { return; }
                m_disposed = true;
                unsubscribe = m_hookOwned;
                m_hookOwned = false;
                m_unsubscribePending = unsubscribe;
                windowDragger = m_windowDragger;
                m_windowDragger = null;
            }

            Exception? synchronousFailure = null;
            if (unsubscribe)
            {
                try { m_unsubscribe(OnMouseButtonStateChanged); }
                catch (Exception error)
                {
                    synchronousFailure = error;
                    RecordFailure(error);
                }
                finally
                {
                    lock (m_gate) { m_unsubscribePending = false; }
                }
            }
            try { windowDragger?.End(); }
            catch (Exception error)
            {
                synchronousFailure ??= error;
                RecordFailure(error);
            }
            Advance();
            if (synchronousFailure != null) { ExceptionDispatchInfo.Capture(synchronousFailure).Throw(); }
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
            bool complete;
            Exception? failure;
            lock (m_gate)
            {
                complete = m_disposed && !m_unsubscribePending && m_enteredCallbacks == 0 && m_dragReservations == 0
                    && m_pendingDrags == 0 && !m_completionSignaled;
                failure = m_failure;
                if (complete)
                {
                    m_completionSignaled = true;
                    if (failure != null && m_laterFailures != null)
                    {
                        failure.Data["ModifierWindowMover.CleanupExceptions"] = new AggregateException(m_laterFailures);
                    }
                }
            }
            if (!complete) { return; }
            if (failure == null) { m_completion.TrySetResult(); }
            else { m_completion.TrySetException(failure); }
        }
    }
}
