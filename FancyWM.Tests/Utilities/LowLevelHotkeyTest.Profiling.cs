#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class LowLevelHotkeyTest
    {
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(10)]
        [DataRow(50)]
        public void UnmatchedCallbacksDoNotAllocateAfterWarmup(int count)
        {
            RunSta(() =>
            {
                var hook = CreateNativeFreeHook();
                var hotkeys = CreateSpecifications(count).Select(specification => CreateHotkey(hook, specification)).ToArray();
                try
                {
                    var subscribers = Subscribers(hook);
                    for (int warmup = 0; warmup < 100; warmup++)
                    {
                        var pressed = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.F24, true);
                        var released = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.F24, false);
                        subscribers?.Invoke(hook, ref pressed);
                        subscribers?.Invoke(hook, ref released);
                    }
                    bool handled = false;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int index = 0; index < 1000; index++)
                    {
                        var pressed = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.F24, true);
                        var released = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.F24, false);
                        subscribers?.Invoke(hook, ref pressed);
                        subscribers?.Invoke(hook, ref released);
                        handled |= pressed.Handled || released.Handled;
                    }
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.IsFalse(handled);
                    Assert.AreEqual(0L, bytes, "Unmatched production subscriber callbacks have no notification or state snapshot to allocate.");
                }
                finally
                {
                    foreach (var hotkey in hotkeys) hotkey.Dispose();
                    Assert.IsNull(Subscribers(hook));
                }
            });
        }

        [TestMethod]
        public void HotkeyCallbackCounterScenario()
        {
            foreach (int count in new[] { 0, 10, 50 })
            {
                RunSta(() => ProfileCallbacks(count, matching: false));
                RunSta(() => ProfileCallbacks(count, matching: true));
            }
        }

        private static void ProfileCallbacks(int count, bool matching)
        {
            var specifications = CreateSpecifications(count);
            var inputs = matching ? MatchingInputs : new[] { new KeyInput(KeyCode.F24, true), new KeyInput(KeyCode.F24, false) };
            int iterations = matching ? 500 : 10000;
            var expected = Observe(specifications, inputs);
            int[] expectedOrder = !matching || count == 0 ? []
                : count == 10 ? [3, 3, 1, 0, 4, 5] : [3, 3, 31, 1, 0, 4, 5];
            CollectionAssert.AreEqual(expectedOrder, expected.Emissions.Select(emission => emission.Binding).ToArray());
            Assert.AreEqual(!matching || count == 0 ? 0 : count == 10 ? 9 : 11,
                expected.Handled.Count(value => value));
            var hook = CreateNativeFreeHook();
            var hotkeys = new List<LowLevelHotkey>();
            var emissions = new List<int>(expected.Emissions.Length * iterations);
            try
            {
                for (int index = 0; index < specifications.Length; index++)
                {
                    int binding = index;
                    var hotkey = CreateHotkey(hook, specifications[index]);
                    hotkey.Pressed += (_, _) => emissions.Add(binding);
                    hotkeys.Add(hotkey);
                }
                // Reflection and multicast snapshotting occur outside measurement.
                // Invoke the exact production subscriber list; do not call native
                // HookProc, marshal KBDLLHOOKSTRUCT, or alter the keyboard/desktop.
                var subscribers = Subscribers(hook);
                var handled = new bool[iterations * inputs.Length];
                var samples = new long[handled.Length];
                long allocated = 0;
                RunNativeFreeProducer(drain =>
                {
                    for (int warmup = 0; warmup < 20; warmup++)
                    {
                        foreach (var input in inputs)
                        {
                            var args = new LowLevelKeyboardHook.KeyStateChangedEventArgs(input.Key, input.Pressed);
                            subscribers?.Invoke(hook, ref args);
                        }
                        drain();
                    }
                    // The completed drain barrier makes the list quiescent.
                    emissions.Clear();
                    int offset = 0;
                    for (int iteration = 0; iteration < iterations; iteration++)
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        for (int input = 0; input < inputs.Length; input++)
                        {
                            var args = new LowLevelKeyboardHook.KeyStateChangedEventArgs(inputs[input].Key, inputs[input].Pressed);
                            long start = Stopwatch.GetTimestamp();
                            subscribers?.Invoke(hook, ref args);
                            samples[offset] = Stopwatch.GetTimestamp() - start;
                            handled[offset++] = args.Handled;
                        }
                        allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                        // Cross-thread posting is real; owner-Dispatcher draining
                        // and producer barriers are outside both metric regions.
                        // Queues are bounded to one input cycle in this fixture.
                        if (matching) drain();
                    }
                });
                Assert.AreEqual(expected.Emissions.Length * iterations, emissions.Count);
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    for (int input = 0; input < inputs.Length; input++)
                    {
                        Assert.AreEqual(expected.Handled[input], handled[iteration * inputs.Length + input]);
                    }
                    for (int emitted = 0; emitted < expected.Emissions.Length; emitted++)
                    {
                        Assert.AreEqual(expected.Emissions[emitted].Binding, emissions[iteration * expected.Emissions.Length + emitted]);
                    }
                }
                var digestInput = new StringBuilder(handled.Length + emissions.Count * 3);
                foreach (bool suppressed in handled) digestInput.Append(suppressed ? '1' : '0');
                digestInput.Append('|');
                foreach (int emitted in emissions) digestInput.Append(emitted).Append(',');
                string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput.ToString())));
                long elapsed = samples.Sum();
                Array.Sort(samples);
                long Percentile(double percentile) => samples[Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1)];
                string scenario = $"hotkey-{(matching ? "matching" : "quiet")}-{count}";
                Console.WriteLine($"PERFCOUNTER {scenario} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER {scenario} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER {scenario} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER {scenario} input-events {handled.Length}");
                Console.WriteLine($"PERFCOUNTER {scenario} suppressed-events {handled.Count(value => value)}");
                Console.WriteLine($"PERFCOUNTER {scenario} emitted-events {emissions.Count}");
                Console.WriteLine($"PERFCOUNTER {scenario} p50-ticks {Percentile(0.50)}");
                Console.WriteLine($"PERFCOUNTER {scenario} p95-ticks {Percentile(0.95)}");
                Console.WriteLine($"PERFCOUNTER {scenario} p99-ticks {Percentile(0.99)}");
                Console.WriteLine($"PERFCOUNTER {scenario} event-digest {digest}");
            }
            finally
            {
                foreach (var hotkey in hotkeys) hotkey.Dispose();
                Assert.IsNull(Subscribers(hook));
            }
        }

        private static void RunNativeFreeProducer(Action<Action> action)
        {
            using var drainRequested = new AutoResetEvent(false);
            using var drainCompleted = new AutoResetEvent(false);
            using var completed = new ManualResetEvent(false);
            Exception? failure = null;
            var producer = new Thread(() =>
            {
                try
                {
                    action(() =>
                    {
                        drainRequested.Set();
                        if (!drainCompleted.WaitOne(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException("The controlled owner Dispatcher did not drain its callbacks.");
                    });
                }
                catch (Exception exception) { failure = exception; }
                finally { completed.Set(); }
            }) { IsBackground = true };
            producer.SetApartmentState(ApartmentState.STA);
            producer.Start();
            try
            {
                WaitHandle[] signals = [drainRequested, completed];
                while (true)
                {
                    int signal = WaitHandle.WaitAny(signals, TimeSpan.FromSeconds(30));
                    if (signal == 1) break;
                    Assert.AreEqual(0, signal, "Controlled hook producer did not complete.");
                    DrainDispatcher();
                    drainCompleted.Set();
                }
            }
            finally
            {
                drainCompleted.Set();
                Assert.IsTrue(producer.Join(TimeSpan.FromSeconds(10)), "Controlled producer did not exit.");
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static Specification[] CreateSpecifications(int count)
        {
            var result = new Specification[count];
            for (int index = 0; index < count; index++)
            {
                // Actual MainWindow modes: the CapsLock trigger, two release
                // activation variants and direct mode modifier/main bindings.
                result[index] = index switch
                {
                    0 => new([], KeyCode.CapsLock, ClearModifiersOnMiss: true),
                    1 => new([KeyCode.LeftAlt], KeyCode.LeftShift, ScanOnRelease: true,
                        HideKeyPress: false, ClearModifiersOnMiss: true, SideAgnostic: true),
                    2 => new([KeyCode.LeftShift], KeyCode.LeftAlt, ScanOnRelease: true,
                        HideKeyPress: false, ClearModifiersOnMiss: true, SideAgnostic: true),
                    _ => new([index < 29 ? KeyCode.LeftCtrl : KeyCode.RightAlt], (KeyCode)((int)KeyCode.A + (index - 3) % 26)),
                };
            }
            return result;
        }

        private static readonly KeyInput[] MatchingInputs =
        [
            new(KeyCode.F24, true), new(KeyCode.F24, false),
            new(KeyCode.LeftCtrl, true), new(KeyCode.A, true), new(KeyCode.A, true),
            new(KeyCode.A, false), new(KeyCode.LeftCtrl, false),
            new(KeyCode.RightCtrl, true), new(KeyCode.A, true), new(KeyCode.A, false), new(KeyCode.RightCtrl, false),
            new(KeyCode.RightAlt, true), new(KeyCode.C, true), new(KeyCode.C, false), new(KeyCode.RightAlt, false),
            new(KeyCode.LeftAlt, true), new(KeyCode.LeftShift, true), new(KeyCode.LeftShift, false), new(KeyCode.LeftAlt, false),
            new(KeyCode.CapsLock, true), new(KeyCode.CapsLock, false),
            new(KeyCode.LeftCtrl, true), new(KeyCode.B, true), new(KeyCode.C, true),
            new(KeyCode.B, false), new(KeyCode.C, false), new(KeyCode.LeftCtrl, false),
        ];
    }
}
