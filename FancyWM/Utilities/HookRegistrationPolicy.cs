using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.DllImports;

namespace FancyWM.Utilities
{
    // Shared policy called by the existing keyboard/mouse thread owners.
    internal static class HookRegistrationPolicy
    {
        internal const string CleanupExceptionsDataKey = "HookRegistrationPolicy.CleanupExceptions";

        internal delegate void MessageLoop(ref HHOOK current);

        internal delegate int ReadMessage(out MSG message, out int errorCode);

        internal delegate void DispatchMessage(in MSG message);

        internal static bool ShouldContinueMessageLoop(ThreadLifetime lifetime)
            => !lifetime.IsStopping;

        internal static int ReadNativeMessage(out MSG message, out int errorCode)
        {
            int result = PInvoke.GetMessageStatus(out message, new(0), 0, 0);
            errorCode = result == -1 ? Marshal.GetLastPInvokeError() : 0;
            return result;
        }

        internal static void RunMessageLoop(ref HHOOK current, ThreadLifetime lifetime,
            ReadMessage read, MessageLoop refresh, DispatchMessage dispatch)
        {
            while (ShouldContinueMessageLoop(lifetime))
            {
                int result = read(out MSG message, out int errorCode);
                if (result == -1)
                {
                    // Failed reads have no message to dispatch. Preserve their
                    // captured native error before the owned cleanup runs.
                    throw new Win32Exception(errorCode, "Failed to read a low-level hook message with GetMessage!");
                }
                if (result == 0) { return; }
                if (!ShouldContinueMessageLoop(lifetime)) { return; }
                if (message.message == Constants.WM_TIMER) { refresh(ref current); }
                else { dispatch(in message); }
            }
        }

        // Shared startup/shutdown state for the two existing hook-thread owners.
        internal sealed class ThreadLifetime(Func<uint, bool> postQuit)
        {
            private readonly object m_gate = new();
            private readonly TaskCompletionSource m_completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Func<uint, bool> m_postQuit = postQuit;
            private Exception? m_postFailure;
            private Exception? m_workerFailure;
            private uint m_threadId;
            private int m_stopRequested;
            private int m_enteredCallbacks;
            private bool m_workerComplete;
            private bool m_completionPublished;

            internal Task Completion => m_completion.Task;

            internal bool IsStopping => Volatile.Read(ref m_stopRequested) != 0;

            internal bool PublishQueue(uint threadId)
            {
                if (threadId == 0) { throw new ArgumentOutOfRangeException(nameof(threadId)); }
                lock (m_gate)
                {
                    if (IsStopping || m_workerComplete) { return false; }
                    m_threadId = threadId;
                    return true;
                }
            }

            internal void RequestStop()
            {
                if (Interlocked.Exchange(ref m_stopRequested, 1) != 0) { return; }

                Exception? postFailure = null;
                // Posting is intentionally inside the same short gate as terminal
                // publication. The OS thread ID cannot be cleared/reused while an
                // admitted post is still in flight.
                lock (m_gate)
                {
                    if (!m_workerComplete && m_threadId != 0)
                    {
                        try
                        {
                            if (!m_postQuit(m_threadId))
                            {
                                postFailure = new Win32Exception(
                                    "Failed to post WM_QUIT to the low-level hook thread!");
                            }
                        }
                        catch (Exception error)
                        {
                            postFailure = error;
                        }
                        m_postFailure = postFailure;
                    }
                }

                if (postFailure != null)
                {
                    try { Trace.TraceError($"Low-level hook stop request failed: {postFailure}"); }
                    catch { }
                }
            }

            // A finalizer must never wait for a queue, a managed thread or a
            // potentially blocking native call. A running thread delegate roots
            // its owner; deterministic hook release therefore requires Dispose.
            internal void RequestStopFromFinalizer()
                => Interlocked.Exchange(ref m_stopRequested, 1);

            internal bool TryEnterCallback()
            {
                lock (m_gate)
                {
                    if (IsStopping || m_workerComplete) { return false; }
                    m_enteredCallbacks++;
                    return true;
                }
            }

            internal void ExitCallback()
            {
                Exception? completionFailure = null;
                bool publish = false;
                lock (m_gate)
                {
                    if (m_enteredCallbacks <= 0)
                    {
                        throw new InvalidOperationException("No low-level hook callback is entered.");
                    }
                    m_enteredCallbacks--;
                    if (m_workerComplete && m_enteredCallbacks == 0 && !m_completionPublished)
                    {
                        m_completionPublished = true;
                        completionFailure = BuildCompletionFailure();
                        publish = true;
                    }
                }
                if (publish) { PublishCompletion(completionFailure); }
            }

            internal void Complete(Exception? failure)
            {
                Exception? completionFailure = null;
                bool publish = false;
                lock (m_gate)
                {
                    if (m_workerComplete) { return; }
                    // Serialized with RequestStop's actual PostThreadMessage call.
                    m_threadId = 0;
                    m_workerFailure = failure;
                    m_workerComplete = true;
                    if (m_enteredCallbacks == 0)
                    {
                        m_completionPublished = true;
                        completionFailure = BuildCompletionFailure();
                        publish = true;
                    }
                }
                if (publish) { PublishCompletion(completionFailure); }
            }

            private Exception? BuildCompletionFailure()
            {
                if (m_workerFailure == null) { return m_postFailure; }
                if (m_postFailure == null || ReferenceEquals(m_workerFailure, m_postFailure))
                {
                    return m_workerFailure;
                }

                try
                {
                    List<Exception> cleanup = [];
                    if (m_workerFailure.Data[CleanupExceptionsDataKey] is AggregateException existing)
                    {
                        cleanup.AddRange(existing.InnerExceptions);
                    }
                    cleanup.Add(m_postFailure);
                    m_workerFailure.Data[CleanupExceptionsDataKey] = new AggregateException(cleanup);
                }
                catch { }
                return m_workerFailure;
            }

            private void PublishCompletion(Exception? failure)
            {
                if (failure == null) { m_completion.TrySetResult(); }
                else
                {
                    m_completion.TrySetException(failure);
                    _ = m_completion.Task.Exception;
                    try { Trace.TraceError($"Low-level hook worker failed: {failure}"); }
                    catch { }
                }
            }
        }

        internal static void RunLoop(ref HHOOK current, Func<HHOOK> install,
            Func<nuint> createTimer, MessageLoop runLoop, Func<nuint, bool> killTimer,
            Func<HHOOK, bool> unhook, string installFailureMessage, string unhookFailureMessage)
        {
            ExceptionDispatchInfo? failure = null;
            List<Exception>? cleanupFailures = null;
            nuint timerId = 0;

            try
            {
                current = install();
                if (current == IntPtr.Zero)
                {
                    throw new Win32Exception(installFailureMessage);
                }

                timerId = createTimer();
                if (timerId == 0)
                {
                    throw new Win32Exception("Failed to set the low-level hook watchdog timer!");
                }

                try
                {
                    runLoop(ref current);
                }
                catch (ThreadInterruptedException)
                {
                }
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                if (timerId != 0)
                {
                    try
                    {
                        if (!killTimer(timerId))
                        {
                            throw new Win32Exception("Failed to unset the low-level hook watchdog timer!");
                        }
                    }
                    catch (Exception error)
                    {
                        CaptureCleanup(error);
                    }
                }

                if (current != IntPtr.Zero)
                {
                    HHOOK owned = current;
                    try
                    {
                        if (!unhook(owned))
                        {
                            throw new Win32Exception(unhookFailureMessage);
                        }
                        current = default;
                    }
                    catch (Exception error)
                    {
                        CaptureCleanup(error);
                    }
                }
            }

            if (failure != null)
            {
                if (cleanupFailures != null)
                {
                    failure.SourceException.Data[CleanupExceptionsDataKey] =
                        new AggregateException(cleanupFailures);
                }
                failure.Throw();
            }

            void CaptureCleanup(Exception error)
            {
                if (failure == null)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                }
                else
                {
                    (cleanupFailures ??= []).Add(error);
                }
            }
        }

        internal static void RefreshIfIdle(ref HHOOK current, ref DateTime lastActive,
            TimeSpan idleInterval, Func<DateTime> utcNow, Func<HHOOK> install,
            Func<HHOOK, bool> unhook)
        {
            if (utcNow() - lastActive > idleInterval)
            {
                lastActive = utcNow();
                Replace(ref current, install, unhook);
            }
        }

        internal static void Replace(ref HHOOK current, Func<HHOOK> install, Func<HHOOK, bool> unhook)
        {
            HHOOK previous = current;
            HHOOK replacement = install();
            if (replacement == IntPtr.Zero) return;
            current = replacement;
            if (previous != IntPtr.Zero) unhook(previous);
        }
    }
}
