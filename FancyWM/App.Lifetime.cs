using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FancyWM
{
    public partial class App
    {
        internal readonly record struct ShutdownOwner(Action Stop, Func<Task> Completion);

        internal static ShutdownOwner CreateShutdownOwner<TOwner>(
            Func<TOwner> acquire, Action<TOwner> stop, Func<TOwner, Task> completion)
        {
            TOwner owner = default!;
            bool acquired = false;
            return new ShutdownOwner(
                () =>
                {
                    owner = acquire();
                    acquired = true;
                    stop(owner);
                },
                () => acquired ? completion(owner) : Task.CompletedTask);
        }

        // Publish before invoking callbacks so a nested termination observes the
        // same owned operation instead of starting another shutdown sequence.
        internal static Task BeginTermination(ref TaskCompletionSource? current, Func<Task> terminate)
        {
            var existing = Volatile.Read(ref current);
            if (existing != null) { return existing.Task; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            existing = Interlocked.CompareExchange(ref current, completion, null);
            if (existing != null) { return existing.Task; }
            _ = CompleteTerminationAsync(completion, terminate);
            return completion.Task;
        }

        private static async Task CompleteTerminationAsync(TaskCompletionSource completion, Func<Task> terminate)
        {
            try
            {
                await terminate().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
                _ = completion.Task.Exception;
                try { Trace.TraceError($"Application termination failed: {error}"); }
                catch { }
            }
        }

        internal static async Task TerminateOwnedWindowsAsync<TWindow>(
            Func<IEnumerable<TWindow>> windows,
            Action<TWindow> disposeWindow,
            Func<TWindow, Task> shutdownCompletion,
            Action<TWindow> closeWindow,
            Func<Action, Task> dispatch,
            Action disposeMouseHook,
            Action<Exception> reportFailure,
            Action shutdown)
            => await TerminateOwnedWindowsAsync(windows, disposeWindow, shutdownCompletion,
                closeWindow, dispatch,
                [new ShutdownOwner(disposeMouseHook, static () => Task.CompletedTask)],
                reportFailure, shutdown).ConfigureAwait(false);

        internal static async Task TerminateOwnedWindowsAsync<TWindow>(
            Func<IEnumerable<TWindow>> windows,
            Action<TWindow> disposeWindow,
            Func<TWindow, Task> shutdownCompletion,
            Action<TWindow> closeWindow,
            Func<Action, Task> dispatch,
            IEnumerable<ShutdownOwner> shutdownOwners,
            Action<Exception> reportFailure,
            Action shutdown)
        {
            List<Exception> exceptions = [];
            try
            {
                await CloseOwnedWindowsAsync(windows, disposeWindow, shutdownCompletion,
                    closeWindow, dispatch, shutdownOwners).ConfigureAwait(false);
            }
            catch (Exception error) { exceptions.Add(error); }

            try
            {
                await dispatch(() =>
                {
                    if (exceptions.Count != 0)
                    {
                        try { reportFailure(exceptions[0]); }
                        catch (Exception error) { exceptions.Add(error); }
                    }
                    try { shutdown(); }
                    catch (Exception error) { exceptions.Add(error); }
                }).ConfigureAwait(false);
            }
            catch (Exception error) { exceptions.Add(error); }
            if (exceptions.Count > 0)
            {
                throw new AggregateException("Application termination failed!", exceptions);
            }
        }

        internal static async Task CloseOwnedWindowsAsync<TWindow>(
            Func<IEnumerable<TWindow>> windows,
            Action<TWindow> disposeWindow,
            Func<TWindow, Task> shutdownCompletion,
            Action<TWindow> closeWindow,
            Func<Action, Task> dispatch,
            Action disposeMouseHook)
            => await CloseOwnedWindowsAsync(windows, disposeWindow, shutdownCompletion,
                closeWindow, dispatch,
                [new ShutdownOwner(disposeMouseHook, static () => Task.CompletedTask)])
                .ConfigureAwait(false);

        internal static async Task CloseOwnedWindowsAsync<TWindow>(
            Func<IEnumerable<TWindow>> windows,
            Action<TWindow> disposeWindow,
            Func<TWindow, Task> shutdownCompletion,
            Action<TWindow> closeWindow,
            Func<Action, Task> dispatch,
            IEnumerable<ShutdownOwner> shutdownOwners)
        {
            List<Exception> exceptions = [];
            TWindow[] snapshot = [];
            List<(Task Completion, Exception? DisposalFailure)> pending = [];
            List<(Task Completion, Exception? StopFailure)> pendingShutdownOwners = [];
            try
            {
                await dispatch(() =>
                {
                    snapshot = windows().ToArray();
                    foreach (var window in snapshot)
                    {
                        Exception? disposalFailure = null;
                        try { disposeWindow(window); }
                        catch (Exception error) { disposalFailure = error; exceptions.Add(error); }
                        // An initiating Dispose failure must not discard the
                        // already-owned worker/dependent-resource completion.
                        try { pending.Add((shutdownCompletion(window), disposalFailure)); }
                        catch (Exception error)
                        {
                            if (!ReferenceEquals(error, disposalFailure)) { exceptions.Add(error); }
                        }
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception error) { exceptions.Add(error); }

            // Admit every initial owner before waiting. Failure of one worker
            // cannot permit Close/Shutdown while another still uses resources.
            foreach (var item in pending)
            {
                try { await item.Completion.ConfigureAwait(false); }
                catch (Exception error)
                {
                    if (!ReferenceEquals(error, item.DisposalFailure)) { exceptions.Add(error); }
                }
            }

            try
            {
                await dispatch(() =>
                {
                    foreach (var owner in shutdownOwners)
                    {
                        Exception? stopFailure = null;
                        try { owner.Stop(); }
                        catch (Exception error) { stopFailure = error; exceptions.Add(error); }
                        try { pendingShutdownOwners.Add((owner.Completion(), stopFailure)); }
                        catch (Exception error)
                        {
                            if (!ReferenceEquals(error, stopFailure)) { exceptions.Add(error); }
                        }
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception error) { exceptions.Add(error); }

            // Stop every hook before awaiting one. A failed first owner cannot
            // let Shutdown overtake a later callback or message-loop cleanup.
            foreach (var item in pendingShutdownOwners)
            {
                try { await item.Completion.ConfigureAwait(false); }
                catch (Exception error)
                {
                    if (!ReferenceEquals(error, item.StopFailure)) { exceptions.Add(error); }
                }
            }

            // Keep the WPF Dispatcher and its last Window alive until hook
            // callbacks and message-loop cleanup can no longer use them. With
            // the default OnLastWindowClose mode, closing first could start
            // Dispatcher shutdown before this awaited ownership boundary.
            try
            {
                await dispatch(() =>
                {
                    foreach (var window in snapshot)
                    {
                        try { closeWindow(window); }
                        catch (InvalidOperationException) { }
                        catch (Exception error) { exceptions.Add(error); }
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception error) { exceptions.Add(error); }
            if (exceptions.Count > 0)
            {
                throw new AggregateException("Application shutdown failed!", exceptions);
            }
        }

        // Dispatch must invoke its action synchronously, as App.Close's existing
        // direct-call/Dispatcher.Invoke branches do. No App or Window is acquired here.
        internal static void CloseOwnedWindows<TWindow>(
            Func<IEnumerable<TWindow>> windows,
            Action<TWindow> disposeWindow,
            Action<TWindow> closeWindow,
            Action<Action> dispatch,
            Action disposeMouseHook)
        {
            List<Exception> exceptions = [];
            try
            {
                dispatch(() =>
                {
                    // Closing a window mutates Application.Windows. Capture the
                    // complete initial set on its Dispatcher before any mutation.
                    var snapshot = windows().ToArray();
                    foreach (var window in snapshot)
                    {
                        try { disposeWindow(window); }
                        catch (Exception error) { exceptions.Add(error); }

                        try { closeWindow(window); }
                        catch (InvalidOperationException)
                        {
                            // Keep the existing already-closing behavior.
                        }
                        catch (Exception error) { exceptions.Add(error); }
                    }
                });
            }
            catch (Exception error)
            {
                // Includes dispatcher and snapshot-acquisition failures. The
                // existing mouse-hook cleanup still receives its cleanup call.
                exceptions.Add(error);
            }

            try { disposeMouseHook(); }
            catch (Exception error) { exceptions.Add(error); }

            if (exceptions.Count > 0)
            {
                throw new AggregateException("Application shutdown failed!", exceptions);
            }
        }
    }
}
