#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Windows.Threading;
using System.Threading.Tasks;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using Serilog;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public partial class MultiDisplayTilingServiceIntegrationTest
    {
        [DataTestMethod]
        [DataRow(true, false, false, true)]
        [DataRow(false, false, false, false)]
        [DataRow(true, true, true, true)]
        [DataRow(false, true, true, true)]
        [DataRow(false, false, true, true)]
        public void DiscoveryVisitsEveryDisplayExactlyOnce(
            bool firstResult, bool secondResult, bool thirdResult, bool expectedResult)
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var thirdDisplay = fixture.CreateDisplay(new Rectangle(2000, 0, 2999, 999));
            fixture.AddDisplay(thirdDisplay);
            var services = new[]
            {
                fixture.GetService(fixture.PrimaryDisplay),
                fixture.GetService(fixture.SecondDisplay),
                fixture.GetService(thirdDisplay),
            };
            var results = new[] { firstResult, secondResult, thirdResult };
            var calls = new List<int>();
            for (int index = 0; index < services.Length; index++)
            {
                int capturedIndex = index;
                services[index].Discover = () =>
                {
                    calls.Add(capturedIndex);
                    return results[capturedIndex];
                };
            }

            Assert.AreEqual(expectedResult, fixture.Service.DiscoverWindows());
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, calls);
        }

        [TestMethod]
        public void DiscoveryPropagatesChildExceptionWithoutVisitingLaterDisplays()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var calls = new List<int>();
            var failure = new InvalidOperationException("discovery failed");
            fixture.GetService(fixture.PrimaryDisplay).Discover = () => throw failure;
            fixture.GetService(fixture.SecondDisplay).Discover = () => { calls.Add(1); return true; };

            Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(
                () => fixture.Service.DiscoverWindows()));
            Assert.AreEqual(0, calls.Count);
        }

        [TestMethod]
        public void MasterSatelliteCommandsFollowFocusedWindowAcrossDisplays()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var primary = fixture.GetService(fixture.PrimaryDisplay);
            var second = fixture.GetService(fixture.SecondDisplay);

            fixture.FocusDisplay(fixture.SecondDisplay);
            InvokeMasterSatelliteCommands(fixture.Service);

            CollectionAssert.AreEqual(ExpectedMasterSatelliteCalls, second.CommandCalls);
            Assert.AreEqual(0, primary.CommandCalls.Count);

            second.CommandCalls.Clear();
            fixture.FocusDisplay(fixture.PrimaryDisplay);
            InvokeMasterSatelliteCommands(fixture.Service);

            CollectionAssert.AreEqual(ExpectedMasterSatelliteCalls, primary.CommandCalls);
            Assert.AreEqual(0, second.CommandCalls.Count);
            Assert.AreEqual(2, second.AutoRegisterWindowsValues.Count);
            Assert.AreEqual(3, primary.AutoRegisterWindowsValues.Count);
            Assert.AreEqual(2, primary.RefreshCount);
            Assert.AreEqual(1, second.RefreshCount);
        }

        [TestMethod]
        public void DisplayAddedAndRemovedRegistersRoutesAndDisposesExactlyOnce()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: false);
            var primary = fixture.GetService(fixture.PrimaryDisplay);
            var matchers = new IWindowMatcher[]
            {
                new ByProcessNameMatcher("excluded-test-process"),
            };
            fixture.Service.ShowPreviewFocus = true;
            fixture.Service.ExclusionMatchers = matchers;
            fixture.FocusDisplay(fixture.SecondDisplay);

            fixture.AddDisplay(fixture.SecondDisplay);

            Assert.AreEqual(2, fixture.CreatedServiceCount);
            var second = fixture.GetService(fixture.SecondDisplay);
            Assert.AreEqual(1, second.StartCount);
            Assert.IsTrue(second.ShowPreviewFocus);
            Assert.AreSame(matchers, second.ExclusionMatchers);
            CollectionAssert.AreEqual(new[] { true }, second.AutoRegisterWindowsValues);
            Assert.AreEqual(1, second.RefreshCount);

            fixture.Service.ToggleMasterSatelliteLayout();
            CollectionAssert.AreEqual(
                new[] { nameof(ITilingService.ToggleMasterSatelliteLayout) },
                second.CommandCalls);
            Assert.AreEqual(0, primary.CommandCalls.Count);

            fixture.AddDisplay(fixture.SecondDisplay);
            Assert.AreEqual(2, fixture.CreatedServiceCount);
            Assert.AreEqual(1, second.StartCount);

            fixture.FocusDisplay(null);
            fixture.RemoveDisplay(fixture.SecondDisplay);

            Assert.AreEqual(1, second.StopCount);
            Assert.AreEqual(1, second.DisposeCount);
            fixture.Service.ToggleMasterSatelliteLayout();
            CollectionAssert.AreEqual(
                new[] { nameof(ITilingService.ToggleMasterSatelliteLayout) },
                primary.CommandCalls);

            fixture.RemoveDisplay(fixture.SecondDisplay);
            Assert.AreEqual(1, second.StopCount);
            Assert.AreEqual(1, second.DisposeCount);

            fixture.Service.Dispose();
            Assert.AreEqual(1, primary.StopCount);
            Assert.AreEqual(1, primary.DisposeCount);

            fixture.AddDisplay(fixture.SecondDisplay);
            Assert.AreEqual(2, fixture.CreatedServiceCount);
        }

        [TestMethod]
        public void UnknownAndDuplicateDisplayRemovalsDoNotRequestGarbageCollection()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var unknown = fixture.CreateDisplay(new Rectangle(2000, 0, 2999, 999));
            fixture.RemoveDisplay(unknown);
            Assert.AreEqual(0, fixture.GarbageCollectionRequests);
            var removed = fixture.GetService(fixture.SecondDisplay);
            fixture.RemoveDisplay(fixture.SecondDisplay);
            Assert.AreEqual(1, fixture.GarbageCollectionRequests);
            for (int index = 0; index < 100; index++)
            {
                fixture.RemoveDisplay(unknown);
                fixture.RemoveDisplay(fixture.SecondDisplay);
            }
            Assert.AreEqual(1, fixture.GarbageCollectionRequests);
            Assert.AreEqual(1, removed.StopCount);
            Assert.AreEqual(1, removed.DisposeCount);
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public void RemovedOwnerStillRequestsOneCollectionAfterCleanupFailures(bool failStop, bool failDispose)
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var removed = fixture.GetService(fixture.SecondDisplay);
            removed.OnStop = () => { if (failStop) throw new InvalidOperationException("stop"); };
            removed.OnDispose = () => { if (failDispose) throw new InvalidOperationException("dispose"); };
            fixture.RemoveDisplay(fixture.SecondDisplay);
            fixture.RemoveDisplay(fixture.SecondDisplay);
            Assert.AreEqual(1, removed.StopCount);
            Assert.AreEqual(1, removed.DisposeCount);
            Assert.AreEqual(1, fixture.GarbageCollectionRequests);
        }

        [TestMethod]
        public void ReentrantDuplicateDuringStopDoesNotRequestAnotherCollection()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var removed = fixture.GetService(fixture.SecondDisplay);
            removed.OnStop = () => fixture.RemoveDisplay(fixture.SecondDisplay);
            fixture.RemoveDisplay(fixture.SecondDisplay);
            Assert.AreEqual(1, removed.StopCount);
            Assert.AreEqual(1, removed.DisposeCount);
            Assert.AreEqual(1, fixture.GarbageCollectionRequests);
        }

        [TestMethod]
        public void CapturedDisplayRemovalAfterDisposeDoesNotRequestCollection()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var method = typeof(MultiDisplayTilingService).GetMethod("OnDisplayRemoved", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var callback = method.CreateDelegate<Action<object?, DisplayChangedEventArgs>>(fixture.Service);
            fixture.Service.Dispose();
            for (int index = 0; index < 100; index++) callback(null, new DisplayChangedEventArgs(fixture.SecondDisplay));
            Assert.AreEqual(0, fixture.GarbageCollectionRequests);
            Assert.AreEqual(1, fixture.GetService(fixture.SecondDisplay).DisposeCount);
        }

        [TestMethod]
        public void ReaddedDisplayHasANewOwnerAndOneCollectionForEachRemoval()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var first = fixture.GetService(fixture.SecondDisplay);
            fixture.RemoveDisplay(fixture.SecondDisplay);
            fixture.AddDisplay(fixture.SecondDisplay);
            var second = fixture.GetService(fixture.SecondDisplay);
            Assert.AreNotSame(first, second);
            fixture.RemoveDisplay(fixture.SecondDisplay);
            fixture.RemoveDisplay(fixture.SecondDisplay);
            Assert.AreEqual(2, fixture.GarbageCollectionRequests);
            Assert.AreEqual(1, first.StopCount);
            Assert.AreEqual(1, first.DisposeCount);
            Assert.AreEqual(1, second.StopCount);
            Assert.AreEqual(1, second.DisposeCount);
        }

        [TestMethod]
        public void DisplayRemovalGarbageCollectionCounterScenario()
        {
            int requests = 0;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var fixture = new ServiceFixture(includeSecondDisplay: true);
                var removed = fixture.GetService(fixture.SecondDisplay);
                fixture.RemoveDisplay(fixture.SecondDisplay);
                for (int duplicate = 0; duplicate < 100; duplicate++) fixture.RemoveDisplay(fixture.SecondDisplay);
                requests += fixture.GarbageCollectionRequests;
                Assert.AreEqual(1, removed.StopCount);
                Assert.AreEqual(1, removed.DisposeCount);
                fixture.Service.ToggleMasterSatelliteLayout();
                CollectionAssert.AreEqual(new[] { nameof(ITilingService.ToggleMasterSatelliteLayout) }, fixture.GetService(fixture.PrimaryDisplay).CommandCalls);
                fixture.Service.Dispose();
                fixture.RemoveDisplay(fixture.SecondDisplay);
                Assert.AreEqual(1, fixture.GetService(fixture.PrimaryDisplay).DisposeCount);
            }
            Console.WriteLine($"PERFCOUNTER display-removal-gc collection-requests {requests}");
            Console.WriteLine("PERFCOUNTER display-removal-gc removed-owners 100");
            Console.WriteLine("PERFCOUNTER display-removal-gc duplicate-events 10000");
            Console.WriteLine("PERFCOUNTER display-removal-gc cycles 100");
        }

        private static readonly string[] ExpectedMasterSatelliteCalls =
        [
            nameof(ITilingService.CanToggleMasterSatelliteLayout),
            nameof(ITilingService.ToggleMasterSatelliteLayout),
            nameof(ITilingService.CanPromoteFocusedWindowToMaster),
            nameof(ITilingService.PromoteFocusedWindowToMaster),
            nameof(ITilingService.CanSwapMasterSide),
            nameof(ITilingService.SwapMasterSide),
            nameof(ITilingService.CanToggleSatelliteOrientation),
            nameof(ITilingService.ToggleSatelliteOrientation),
            nameof(ITilingService.CanResetMasterRatio),
            nameof(ITilingService.ResetMasterRatio),
            nameof(ITilingService.CanRebalanceMasterSatelliteLayout),
            nameof(ITilingService.RebalanceMasterSatelliteLayout),
        ];

        private static void InvokeMasterSatelliteCommands(ITilingService service)
        {
            Assert.IsTrue(service.CanToggleMasterSatelliteLayout());
            service.ToggleMasterSatelliteLayout();
            Assert.IsTrue(service.CanPromoteFocusedWindowToMaster());
            service.PromoteFocusedWindowToMaster();
            Assert.IsTrue(service.CanSwapMasterSide());
            service.SwapMasterSide();
            Assert.IsTrue(service.CanToggleSatelliteOrientation());
            service.ToggleSatelliteOrientation();
            Assert.IsTrue(service.CanResetMasterRatio());
            service.ResetMasterRatio();
            Assert.IsTrue(service.CanRebalanceMasterSatelliteLayout());
            service.RebalanceMasterSatelliteLayout();
        }

        private sealed class ServiceFixture : IDisposable
        {
            private readonly Mock<IWorkspace> m_workspace = new(MockBehavior.Loose);
            private readonly Mock<IDisplayManager> m_displayManager = new(MockBehavior.Loose);
            private readonly List<IDisplay> m_displays = new();
            private readonly Dictionary<IDisplay, FakeTilingService> m_services = new();
            private IWindow? m_focusedWindow;
            private bool m_disposed;

            public IDisplay PrimaryDisplay { get; }
            public IDisplay SecondDisplay { get; }
            public int CreatedServiceCount { get; private set; }
            public int GarbageCollectionRequests { get; private set; }
            public AlgorithmicLayoutCoordinator Coordinator { get; }
            public MultiDisplayTilingService Service { get; }
            public Action<FakeTilingService>? OnServiceCreated { get; set; }
            public Action<ITilingService>? OnAutoRegisterWindows { get; set; }

            public ServiceFixture(bool includeSecondDisplay, Action? onGarbageCollectionRequest = null)
            {
                PrimaryDisplay = CreateDisplay(new Rectangle(0, 0, 999, 999));
                SecondDisplay = CreateDisplay(new Rectangle(1000, 0, 1999, 999));
                m_displays.Add(PrimaryDisplay);
                if (includeSecondDisplay)
                {
                    m_displays.Add(SecondDisplay);
                }

                m_displayManager.SetupGet(item => item.Workspace)
                    .Returns(m_workspace.Object);
                m_displayManager.SetupGet(item => item.PrimaryDisplay)
                    .Returns(PrimaryDisplay);
                m_displayManager.SetupGet(item => item.Displays)
                    .Returns(() => m_displays.ToArray());

                m_workspace.SetupGet(item => item.DisplayManager)
                    .Returns(m_displayManager.Object);
                m_workspace.SetupGet(item => item.FocusedWindow)
                    .Returns(() => m_focusedWindow);

                Coordinator = new AlgorithmicLayoutCoordinator(
                    m_workspace.Object,
                    Dispatcher.CurrentDispatcher);
                Service = new MultiDisplayTilingService(
                    m_workspace.Object,
                    new FakeAnimationThread(),
                    Observable.Return<ITilingServiceSettings>(new Settings()),
                    Coordinator,
                    new Mock<ILogger>(MockBehavior.Loose).Object,
                    CreateTilingService,
                    (service, value) =>
                    {
                        ((FakeTilingService)service).AutoRegisterWindowsValues.Add(value);
                        OnAutoRegisterWindows?.Invoke(service);
                    },
                    () =>
                    {
                        GarbageCollectionRequests++;
                        onGarbageCollectionRequest?.Invoke();
                    });
            }

            public FakeTilingService GetService(IDisplay display)
            {
                return m_services[display];
            }

            public void FocusDisplay(IDisplay? display)
            {
                m_focusedWindow = display == null
                    ? null
                    : CreateWindow(display.Bounds);
            }

            public void AddDisplay(IDisplay display)
            {
                if (!m_displays.Contains(display))
                {
                    m_displays.Add(display);
                }
                m_displayManager.Raise(
                    item => item.Added += null,
                    new DisplayChangedEventArgs(display));
            }

            public void RemoveDisplay(IDisplay display)
            {
                m_displays.Remove(display);
                m_displayManager.Raise(
                    item => item.Removed += null,
                    new DisplayChangedEventArgs(display));
            }

            public void Dispose()
            {
                if (m_disposed)
                {
                    return;
                }
                m_disposed = true;
                Service.Dispose();
                Coordinator.Dispose();
            }

            private ITilingService CreateTilingService(IDisplay display)
            {
                CreatedServiceCount++;
                var service = new FakeTilingService(m_workspace.Object, display);
                m_services[display] = service;
                OnServiceCreated?.Invoke(service);
                return service;
            }

            public IDisplay CreateDisplay(Rectangle bounds)
            {
                var display = new Mock<IDisplay>(MockBehavior.Loose);
                display.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
                display.SetupGet(item => item.Bounds).Returns(bounds);
                display.SetupGet(item => item.WorkArea).Returns(bounds);
                display.Setup(item => item.Equals(It.IsAny<IDisplay>()))
                    .Returns((IDisplay other) => ReferenceEquals(display.Object, other));
                return display.Object;
            }

            private IWindow CreateWindow(Rectangle position)
            {
                var window = new Mock<IWindow>(MockBehavior.Loose);
                window.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
                window.SetupGet(item => item.Position).Returns(position);
                return window.Object;
            }
        }

        private sealed class FakeTilingService(
            IWorkspace workspace,
            IDisplay display) : ITilingService
        {
            public event EventHandler<TilingFailedEventArgs>? PlacementFailed
            {
                add { }
                remove { }
            }

            public event EventHandler<AlgorithmicLayoutEvent>? AlgorithmicLayoutChanged
            {
                add { }
                remove { }
            }

            public event EventHandler<EventArgs>? PendingIntentChanged
            {
                add { }
                remove { }
            }

            public bool Active { get; private set; }
            public IWorkspace Workspace { get; } = workspace;
            public ITilingServiceIntent? PendingIntent { get; set; }
            public bool ShowPreviewFocus { get; set; }
            public IReadOnlyCollection<IWindowMatcher> ExclusionMatchers { get; set; } = [];
            public List<bool> AutoRegisterWindowsValues { get; } = new();
            public List<string> CommandCalls { get; } = new();
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }
            public int DisposeCount { get; private set; }
            public int RefreshCount { get; private set; }
            public Action? OnStop { get; set; }
            public Action? OnDispose { get; set; }
            public Action? OnStart { get; set; }
            public Func<Task> OnPrepareForShutdown { get; set; } = () => Task.CompletedTask;
            public int PrepareForShutdownCount { get; private set; }

            public bool CanSplit(bool vertical) => true;
            public void Split(bool vertical) { }
            public bool CanStack() => true;
            public void Stack() { }
            public Func<bool> Discover { get; set; } = () => false;
            public bool DiscoverWindows() => Discover();
            public void Refresh() => RefreshCount++;
            public bool CanFloat() => true;
            public void Float() { }
            public void ToggleDesktop() { }
            public bool CanMoveFocus(TilingDirection direction) => true;
            public void MoveFocus(TilingDirection direction) { }
            public bool CanPullUp() => true;
            public void PullUp() { }
            public bool CanSwapFocus(TilingDirection direction) => true;
            public void SwapFocus(TilingDirection direction) { }
            public bool CanMoveWindow(TilingDirection direction) => true;
            public void MoveWindow(TilingDirection direction) { }
            public bool CanResize(PanelOrientation orientation, double displayPercentage) => true;
            public void Resize(PanelOrientation orientation, double displayPercentage) { }

            public bool CanToggleMasterSatelliteLayout()
                => RecordCapability(nameof(CanToggleMasterSatelliteLayout));

            public void ToggleMasterSatelliteLayout()
                => Record(nameof(ToggleMasterSatelliteLayout));

            public bool CanPromoteFocusedWindowToMaster()
                => RecordCapability(nameof(CanPromoteFocusedWindowToMaster));

            public void PromoteFocusedWindowToMaster()
                => Record(nameof(PromoteFocusedWindowToMaster));

            public bool CanSwapMasterSide()
                => RecordCapability(nameof(CanSwapMasterSide));

            public void SwapMasterSide()
                => Record(nameof(SwapMasterSide));

            public bool CanToggleSatelliteOrientation()
                => RecordCapability(nameof(CanToggleSatelliteOrientation));

            public void ToggleSatelliteOrientation()
                => Record(nameof(ToggleSatelliteOrientation));

            public bool CanResetMasterRatio()
                => RecordCapability(nameof(CanResetMasterRatio));

            public void ResetMasterRatio()
                => Record(nameof(ResetMasterRatio));

            public bool CanRebalanceMasterSatelliteLayout()
                => RecordCapability(nameof(CanRebalanceMasterSatelliteLayout));

            public void RebalanceMasterSatelliteLayout()
                => Record(nameof(RebalanceMasterSatelliteLayout));

            public void Start()
            {
                StartCount++;
                Active = true;
                OnStart?.Invoke();
            }

            public Task PrepareForShutdownAsync()
            {
                PrepareForShutdownCount++;
                Active = false;
                return OnPrepareForShutdown();
            }

            public void Stop()
            {
                StopCount++;
                Active = false;
                OnStop?.Invoke();
            }

            public IWindow? GetFocus() => null;
            public Rectangle GetBounds() => display.Bounds;
            public IWindow? FindClosest(Point center) => null;
            public void Dispose()
            {
                DisposeCount++;
                OnDispose?.Invoke();
            }

            private bool RecordCapability(string command)
            {
                Record(command);
                return true;
            }

            private void Record(string command)
            {
                CommandCalls.Add(command);
            }
        }

        private sealed class FakeAnimationThread : IAnimationThread
        {
            public void Start(IAnimationJob job) { }
            public void Dispose() { }
        }
    }
}
