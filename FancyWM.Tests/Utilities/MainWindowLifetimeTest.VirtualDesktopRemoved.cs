#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        [TestMethod]
        public void VirtualDesktopRemovedCapturedBeforeDisposeRejectsLateStateReadAndClear()
        {
            var probe = new VirtualDesktopRemovalProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            Action captured = () => probe.Invoke(lifetime);

            lifetime.Dispose();
            captured();

            AssertNoVirtualDesktopRemovalEffects(probe);
        }

        [TestMethod]
        public void VirtualDesktopRemovedRejectsReentrantCallbackDuringOwnedRelease()
        {
            var probe = new VirtualDesktopRemovalProbe();
            MainWindowLifetime? lifetime = null;
            lifetime = new MainWindowLifetime(release => release(() => probe.Invoke(lifetime!)));

            lifetime.Dispose();

            AssertNoVirtualDesktopRemovalEffects(probe);
        }

        [TestMethod]
        public async Task VirtualDesktopRemovedRejectsCapturedCallbackWhileDependentShutdownIsPending()
        {
            var probe = new VirtualDesktopRemovalProbe();
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int dependentReleases = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => operation.Task,
                release => release(() => dependentReleases++));
            Action captured = () => probe.Invoke(lifetime);
            try
            {
                lifetime.Dispose();
                Assert.IsFalse(lifetime.Completion.IsCompleted);
                captured();

                AssertNoVirtualDesktopRemovalEffects(probe);
                Assert.AreEqual(0, dependentReleases);
            }
            finally
            {
                operation.TrySetResult();
                lifetime.Dispose();
                await lifetime.Completion.WaitAsync(VirtualDesktopCallbackGuard);
            }
            Assert.AreEqual(1, dependentReleases);
        }

        [TestMethod]
        public void VirtualDesktopRemovedMatchingReferenceClearsExactlyOnceSynchronously()
        {
            var probe = new VirtualDesktopRemovalProbe();
            using var lifetime = new MainWindowLifetime(_ => { });

            probe.Invoke(lifetime);

            Assert.AreEqual(1, probe.StateReads);
            Assert.AreEqual(1, probe.Clears);
            Assert.IsNull(probe.Previous);
            probe.VerifyNoDesktopMembersRead();
        }

        [TestMethod]
        public void VirtualDesktopRemovedEquivalentDifferentDesktopPreservesExactPreviousReference()
        {
            var removed = CreateEquivalentDesktop();
            var previous = CreateEquivalentDesktop();
            var probe = new VirtualDesktopRemovalProbe(removed, previous.Object);
            using var lifetime = new MainWindowLifetime(_ => { });

            probe.Invoke(lifetime);

            Assert.AreEqual(1, probe.StateReads);
            Assert.AreEqual(0, probe.Clears);
            Assert.AreSame(previous.Object, probe.Previous);
            probe.VerifyNoDesktopMembersRead();
        }

        [TestMethod]
        public void VirtualDesktopRemovedNullPreviousRemainsNullWithoutClear()
        {
            var probe = new VirtualDesktopRemovalProbe(source: null, previous: null);
            using var lifetime = new MainWindowLifetime(_ => { });

            probe.Invoke(lifetime);

            Assert.AreEqual(1, probe.StateReads);
            Assert.AreEqual(0, probe.Clears);
            Assert.IsNull(probe.Previous);
            probe.VerifyNoDesktopMembersRead();
        }

        [TestMethod]
        public void VirtualDesktopRemovedRepeatedNotificationClearsOnlyFirstMatch()
        {
            var probe = new VirtualDesktopRemovalProbe();
            using var lifetime = new MainWindowLifetime(_ => { });

            probe.Invoke(lifetime);
            probe.Invoke(lifetime);

            Assert.AreEqual(2, probe.StateReads);
            Assert.AreEqual(1, probe.Clears);
            Assert.IsNull(probe.Previous);
            probe.VerifyNoDesktopMembersRead();
        }

        [TestMethod]
        public void VirtualDesktopRemovedCapturedActiveCallbackReadsCurrentPreviousReference()
        {
            var initial = new Mock<IVirtualDesktop>(MockBehavior.Strict);
            var probe = new VirtualDesktopRemovalProbe(source: null, previous: initial.Object);
            using var lifetime = new MainWindowLifetime(_ => { });
            Action captured = () => probe.Invoke(lifetime);
            probe.Previous = probe.Source;

            captured();

            Assert.AreEqual(1, probe.StateReads);
            Assert.AreEqual(1, probe.Clears);
            Assert.IsNull(probe.Previous);
            initial.VerifyNoOtherCalls();
            probe.VerifyNoDesktopMembersRead();
        }

        [TestMethod]
        public void VirtualDesktopRemovedPreservesGetterFailureIdentityAndSkipsClear()
        {
            var failure = new InvalidOperationException("Previous desktop read failed.");
            var probe = new VirtualDesktopRemovalProbe { GetterFailure = failure };
            using var lifetime = new MainWindowLifetime(_ => { });

            var observed = Assert.ThrowsException<InvalidOperationException>(() => probe.Invoke(lifetime));

            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, probe.StateReads);
            Assert.AreEqual(0, probe.Clears);
            Assert.AreSame(probe.Source, probe.Previous);
            probe.VerifyNoDesktopMembersRead();
        }

        [TestMethod]
        public void VirtualDesktopRemovedPreservesClearFailureIdentityAndAttemptsOnce()
        {
            var failure = new ApplicationException("Previous desktop clear failed.");
            var probe = new VirtualDesktopRemovalProbe { ClearFailure = failure };
            using var lifetime = new MainWindowLifetime(_ => { });

            var observed = Assert.ThrowsException<ApplicationException>(() => probe.Invoke(lifetime));

            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, probe.StateReads);
            Assert.AreEqual(1, probe.Clears);
            Assert.AreSame(probe.Source, probe.Previous);
            probe.VerifyNoDesktopMembersRead();
        }

        [TestMethod]
        public void VirtualDesktopRemovedGetterAndClearRetainCallerThreadAndContext()
        {
            var probe = new VirtualDesktopRemovalProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            var expectedContext = new SynchronizationContext();
            var previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(expectedContext);
                int expectedThread = Environment.CurrentManagedThreadId;

                probe.Invoke(lifetime);

                CollectionAssert.AreEqual(new[] { expectedThread, expectedThread }, probe.Threads);
                CollectionAssert.AreEqual(new[] { expectedContext, expectedContext }, probe.Contexts);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            probe.VerifyNoDesktopMembersRead();
        }

        [TestMethod]
        public void VirtualDesktopRemovedLifetimeCounterScenario()
        {
            const int cycles = 100;
            int activeStateReads = 0;
            int activeClears = 0;
            int lateStateReads = 0;
            int lateClears = 0;
            int completedCallbacks = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var activeProbe = new VirtualDesktopRemovalProbe();
                using var active = new MainWindowLifetime(_ => { });
                activeProbe.Invoke(active);
                completedCallbacks++;
                activeStateReads += activeProbe.StateReads;
                activeClears += activeProbe.Clears;
                active.Dispose();

                var lateProbe = new VirtualDesktopRemovalProbe();
                using var closing = new MainWindowLifetime(_ => { });
                Action captured = () => lateProbe.Invoke(closing);
                closing.Dispose();
                captured();
                completedCallbacks++;
                lateStateReads += lateProbe.StateReads;
                lateClears += lateProbe.Clears;

                activeProbe.VerifyNoDesktopMembersRead();
                lateProbe.VerifyNoDesktopMembersRead();
            }

            Assert.AreEqual(cycles, activeStateReads);
            Assert.AreEqual(cycles, activeClears);
            Assert.IsTrue(lateStateReads == 0 || lateStateReads == cycles,
                "A comparison variant must consistently reject every late callback or preserve every old callback.");
            Assert.AreEqual(lateStateReads, lateClears);
            Assert.AreEqual(cycles * 2, completedCallbacks);
            Console.WriteLine($"PERFCOUNTER virtual-desktop-removal-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER virtual-desktop-removal-lifetime active-state-reads {activeStateReads}");
            Console.WriteLine($"PERFCOUNTER virtual-desktop-removal-lifetime active-clears {activeClears}");
            Console.WriteLine($"PERFCOUNTER virtual-desktop-removal-lifetime late-state-reads {lateStateReads}");
            Console.WriteLine($"PERFCOUNTER virtual-desktop-removal-lifetime late-clears {lateClears}");
            Console.WriteLine($"PERFCOUNTER virtual-desktop-removal-lifetime completed-callbacks {completedCallbacks}");
        }

        private static Mock<IVirtualDesktop> CreateEquivalentDesktop()
        {
            var desktop = new Mock<IVirtualDesktop>(MockBehavior.Strict);
            desktop.SetupGet(value => value.Name).Returns("Equivalent desktop");
            desktop.SetupGet(value => value.Index).Returns(7);
            desktop.SetupGet(value => value.IsAlive).Returns(true);
            desktop.SetupGet(value => value.IsCurrent).Returns(false);
            return desktop;
        }

        private static void AssertNoVirtualDesktopRemovalEffects(VirtualDesktopRemovalProbe probe)
        {
            Assert.AreEqual(0, probe.StateReads, "A late removal callback must not read previous-desktop state.");
            Assert.AreEqual(0, probe.Clears, "A late removal callback must not clear previous-desktop state.");
            Assert.AreSame(probe.Source, probe.Previous);
            probe.VerifyNoDesktopMembersRead();
        }

        private sealed class VirtualDesktopRemovalProbe
        {
            private readonly List<Mock<IVirtualDesktop>> m_desktops;
            private readonly DesktopChangedEventArgs m_event;

            internal IVirtualDesktop Source => m_event.Source;
            internal IVirtualDesktop? Previous;
            internal int StateReads;
            internal int Clears;
            internal readonly List<int> Threads = [];
            internal readonly List<SynchronizationContext?> Contexts = [];
            internal Exception? GetterFailure;
            internal Exception? ClearFailure;

            internal VirtualDesktopRemovalProbe()
                : this(source: null, previous: null, useSourceAsPrevious: true)
            {
            }

            internal VirtualDesktopRemovalProbe(
                Mock<IVirtualDesktop>? source,
                IVirtualDesktop? previous)
                : this(source, previous, useSourceAsPrevious: false)
            {
            }

            private VirtualDesktopRemovalProbe(
                Mock<IVirtualDesktop>? source,
                IVirtualDesktop? previous,
                bool useSourceAsPrevious)
            {
                source ??= new Mock<IVirtualDesktop>(MockBehavior.Strict);
                m_desktops = [source];
                m_event = new DesktopChangedEventArgs(source.Object);
                Previous = useSourceAsPrevious ? source.Object : previous;
                if (previous != null)
                {
                    var previousMock = Mock.Get(previous);
                    if (!ReferenceEquals(previousMock, source)) { m_desktops.Add(previousMock); }
                }
            }

            internal void Invoke(MainWindowLifetime lifetime)
                => MainWindow.HandleVirtualDesktopRemoved(
                    lifetime,
                    () =>
                    {
                        StateReads++;
                        RecordContext();
                        if (GetterFailure != null) { throw GetterFailure; }
                        return Previous;
                    },
                    m_event,
                    () =>
                    {
                        Clears++;
                        RecordContext();
                        if (ClearFailure != null) { throw ClearFailure; }
                        Previous = null;
                    });

            internal void VerifyNoDesktopMembersRead()
            {
                foreach (var desktop in m_desktops) { desktop.VerifyNoOtherCalls(); }
            }

            private void RecordContext()
            {
                Threads.Add(Environment.CurrentManagedThreadId);
                Contexts.Add(SynchronizationContext.Current);
            }
        }
    }
}
