#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Tests.TestUtilities;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class TasksTest
    {
        private const string FaultedCancellationChild = "FANCYWM_TASKS_FAULTED_CANCELLATION_CHILD";
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public async Task EmptySuccessAndEnumerationSnapshotCompleteOnce()
        {
            await Tasks.WhenAllIgnoreCancelled(Array.Empty<Task>());

            int enumerations = 0;
            IEnumerable<Task> Enumerate()
            {
                enumerations++;
                yield return Task.CompletedTask;
                yield return Task.CompletedTask;
            }

            await Tasks.WhenAllIgnoreCancelled(Enumerate());
            Assert.AreEqual(1, enumerations);

            var owned = new List<Task> { Task.CompletedTask, Task.CompletedTask };
            await Tasks.WhenAllIgnoreCancelled(owned);
            Assert.AreEqual(2, owned.Count, "The helper must not mutate its caller-owned collection.");
        }

        [TestMethod]
        public async Task ActualCancellationIsIgnoredWithSuccessfulPeers()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var tasks = new List<Task>
            {
                Task.CompletedTask,
                Task.FromCanceled(cancellation.Token),
                Task.CompletedTask,
            };

            await Tasks.WhenAllIgnoreCancelled(tasks);

            Assert.AreEqual(3, tasks.Count);
            Assert.AreEqual(TaskStatus.Canceled, tasks[1].Status);
        }

        [TestMethod]
        public async Task CancellationDoesNotReleaseCompletionBeforePendingPeer()
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Task completion = Tasks.WhenAllIgnoreCancelled(
                [Task.FromCanceled(cancellation.Token), pending.Task]);

            Assert.IsFalse(completion.IsCompleted);
            pending.SetResult();
            await completion.WaitAsync(Guard);
        }

        [TestMethod]
        public async Task CallerMutationDoesNotChangeCapturedCompletionSet()
        {
            var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var addedLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new List<Task> { initial.Task };

            Task completion = Tasks.WhenAllIgnoreCancelled(tasks);
            tasks.Add(addedLater.Task);

            Assert.IsFalse(completion.IsCompleted);
            initial.SetResult();
            await completion.WaitAsync(Guard);
            Assert.IsFalse(addedLater.Task.IsCompleted,
                "Tasks added after invocation must not extend the captured completion set.");
            addedLater.SetResult();
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FaultWaitsForPendingPeerAndKeepsPriorityOverCancellation(bool faultFirst)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var error = new InvalidOperationException("transition completion failure");
            Task[] settled = faultFirst
                ? [Task.FromException(error), Task.FromCanceled(cancellation.Token)]
                : [Task.FromCanceled(cancellation.Token), Task.FromException(error)];
            Task completion = Tasks.WhenAllIgnoreCancelled([settled[0], pending.Task, settled[1]]);

            Assert.IsFalse(completion.IsCompleted);
            pending.SetResult();
            var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => completion.WaitAsync(Guard));
            Assert.AreSame(error, observed);
            Assert.IsTrue(completion.IsFaulted);
        }

        [TestMethod]
        public async Task FaultedOperationCanceledExceptionRetainsCancellationSemantics()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var error = new OperationCanceledException("adapter cancellation", cancellation.Token);
            Task completion = Tasks.WhenAllIgnoreCancelled([Task.FromException(error)]);

            var observed = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => completion.WaitAsync(Guard));
            Assert.AreEqual(cancellation.Token, observed.CancellationToken);
            Assert.IsTrue(completion.IsCanceled);
        }

        [TestMethod]
        public async Task FaultedTaskCanceledExceptionTerminatesInsteadOfSpinning()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(FaultedCancellationChild), "1", StringComparison.Ordinal))
            {
                await IsolatedTestProcess.RunAsync(TestContext, typeof(TasksTest),
                    typeof(TasksTest).FullName + "." + nameof(FaultedTaskCanceledExceptionTerminatesInsteadOfSpinning),
                    FaultedCancellationChild, "1", "tasks-faulted-cancellation");
                return;
            }

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var error = new TaskCanceledException("faulted cancellation");
            Task invocation = Task.Run(() =>
            {
                started.SetResult();
                return Tasks.WhenAllIgnoreCancelled([Task.FromException(error)]);
            });
            await started.Task.WaitAsync(Guard);
            Task completed = await Task.WhenAny(invocation, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.AreSame(invocation, completed,
                "A faulted TaskCanceledException must not enter a synchronous retry loop.");
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => invocation);
            Assert.IsTrue(invocation.IsCanceled);
        }

#if !DEBUG
        [DataTestMethod]
        [DataRow("success", 0, 16)]
        [DataRow("success", 1, 48)]
        [DataRow("success", 10, 192)]
        [DataRow("success", 50, 512)]
        [DataRow("cancelled", 0, 16)]
        [DataRow("cancelled", 1, 528)]
        [DataRow("cancelled", 10, 1080)]
        [DataRow("cancelled", 50, 2216)]
        [DataRow("mixed", 0, 16)]
        [DataRow("mixed", 1, 528)]
        [DataRow("mixed", 10, 1000)]
        [DataRow("mixed", 50, 1752)]
        public async Task TransitionCompletionAvoidsBaselineWrapperAllocation(
            string mode, int count, int maximumBytesPerCompletion)
        {
            List<Task> tasks = CreateCompletedTasks(count, mode);
            for (int warmup = 0; warmup < 1_000; warmup++)
                await Tasks.WhenAllIgnoreCancelled(tasks);

            Task synchronousCompletion = Tasks.WhenAllIgnoreCancelled(tasks);
            Assert.IsTrue(synchronousCompletion.IsCompleted,
                "The allocation counter requires the completed-input path to remain synchronous.");
            await synchronousCompletion;

            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 10_000; iteration++)
                await Tasks.WhenAllIgnoreCancelled(tasks);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

            Assert.IsTrue(allocated <= maximumBytesPerCompletion * 10_000L,
                $"{mode}/{count} allocated {allocated / 10_000d:F3} B/completion; budget {maximumBytesPerCompletion} B.");
            Assert.AreEqual(count, tasks.Count);
        }
#endif

        [TestMethod]
        public async Task TransitionCompletionCounterScenario()
        {
            var scenarios = new List<(string Mode, int Count, List<Task> Tasks)>();
            foreach (int count in new[] { 0, 1, 4, 10, 25, 50 })
            {
                foreach (string mode in new[] { "success", "cancelled", "mixed" })
                    scenarios.Add((mode, count, CreateCompletedTasks(count, mode)));
            }

            for (int warmup = 0; warmup < 1_000; warmup++)
                foreach (var scenario in scenarios)
                    await Tasks.WhenAllIgnoreCancelled(scenario.Tasks);

            foreach (var scenario in scenarios)
            {
                string mode = scenario.Mode;
                int count = scenario.Count;
                List<Task> tasks = scenario.Tasks;
                Task synchronousCompletion = Tasks.WhenAllIgnoreCancelled(tasks);
                Assert.IsTrue(synchronousCompletion.IsCompleted,
                    "The current-thread allocation counter requires synchronous completion.");
                await synchronousCompletion;

                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int iteration = 0; iteration < 10_000; iteration++)
                    await Tasks.WhenAllIgnoreCancelled(tasks);
                long elapsed = Stopwatch.GetTimestamp() - started;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

                Assert.AreEqual(count, tasks.Count);
                Assert.IsTrue(tasks.TrueForAll(task => task.IsCompleted));
                int cancelled = tasks.FindAll(task => task.IsCanceled).Count;
                Console.WriteLine($"PERFCOUNTER transition-completion-{mode}-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER transition-completion-{mode}-{count} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER transition-completion-{mode}-{count} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER transition-completion-{mode}-{count} completions 10000");
                Console.WriteLine($"PERFCOUNTER transition-completion-{mode}-{count} input-tasks {count * 10_000}");
                Console.WriteLine($"PERFCOUNTER transition-completion-{mode}-{count} cancelled-inputs {cancelled * 10_000}");
                GC.KeepAlive(tasks);
            }
        }

        private static List<Task> CreateCompletedTasks(int count, string mode)
        {
            var tasks = new List<Task>(count);
            for (int index = 0; index < count; index++)
            {
                bool cancel = mode == "cancelled" || mode == "mixed" && index % 2 == 0;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (cancel) completion.SetCanceled();
                else completion.SetResult();
                tasks.Add(completion.Task);
            }
            return tasks;
        }
    }
}
