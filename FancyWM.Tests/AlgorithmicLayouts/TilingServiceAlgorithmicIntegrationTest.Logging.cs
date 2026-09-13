#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FancyWM.Layouts.Tiling;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [DataTestMethod]
        [DataRow(LogEventLevel.Information, 0)]
        [DataRow(LogEventLevel.Debug, 1)]
        public void RepositionDiagnosticsRespectConfiguredLevel(LogEventLevel level, int expectedCalls)
        {
            var sink = new DiagnosticSink();
            using var logger = new LoggerConfiguration().MinimumLevel.Is(level).WriteTo.Sink(sink).CreateLogger();
            using var fixture = new ServiceFixture(EnabledSettings(false), logger: logger);
            sink.Events.Clear();
            var window = DiagnosticWindow();
            var node = new WindowNode(window.Object);
            var targets = (List<TransitionTarget>)typeof(TilingService).GetMethod("CalculateRepositionTargets", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(fixture.Service, [new[] { node }])!;
            Assert.AreEqual(1, targets.Count);
            Assert.AreSame(window.Object, targets[0].Window);
            Assert.AreEqual(node.ComputedRectangle, targets[0].ComputedPosition);
            window.Verify(item => item.GetProcess(), Times.Exactly(expectedCalls));
            Assert.AreEqual(expectedCalls, sink.Events.Count);
            if (expectedCalls != 0)
            {
                Assert.AreEqual(LogEventLevel.Debug, sink.Events[0].Level);
                Assert.AreEqual("Updating position of window {Window}", sink.Events[0].MessageTemplate.Text);
                Assert.AreEqual("00000042=Dead process(0)", ((ScalarValue)sink.Events[0].Properties["Window"]).Value);
            }
        }

        [DataTestMethod]
        [DataRow(LogEventLevel.Information, 0)]
        [DataRow(LogEventLevel.Verbose, 1)]
        public void DirtyCheckDiagnosticsRespectConfiguredLevel(LogEventLevel level, int expectedCalls)
        {
            var sink = new DiagnosticSink();
            using var logger = new LoggerConfiguration().MinimumLevel.Is(level).WriteTo.Sink(sink).CreateLogger();
            using var fixture = new ServiceFixture(EnabledSettings(false), logger: logger);
            sink.Events.Clear();
            var window = DiagnosticWindow();
            window.SetupGet(item => item.State).Returns(WindowState.Minimized);
            bool changed = (bool)typeof(TilingService).GetMethod("DetectChanges", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(fixture.Service, [window.Object, false, null])!;
            Assert.IsFalse(changed);
            window.Verify(item => item.GetProcess(), Times.Exactly(expectedCalls));
            Assert.AreEqual(expectedCalls, sink.Events.Count);
            if (expectedCalls != 0)
                Assert.AreEqual("Dirty checking for changes with window {Window}", sink.Events[0].MessageTemplate.Text);
        }

        [DataTestMethod]
        [DataRow(LogEventLevel.Warning, 0)]
        [DataRow(LogEventLevel.Information, 1)]
        public void PositionApplicationPreservesInformationAndNativeEffect(LogEventLevel level, int expectedCalls)
        {
            var sink = new DiagnosticSink();
            using var logger = new LoggerConfiguration().MinimumLevel.Is(level).WriteTo.Sink(sink).CreateLogger();
            using var fixture = new ServiceFixture(EnabledSettings(false), logger: logger);
            sink.Events.Clear();
            var window = DiagnosticWindow();
            var node = new WindowNode(window.Object);
            var operation = (Task)typeof(TilingService).GetMethod("UpdateWindowPositionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(fixture.Service, [new[] { node }, false])!;
            // The fake adapter completes on the pool. Keep this fixture's disposal on
            // the thread that owns its Dispatcher/coordinator, with a bounded test wait.
            operation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            window.Verify(item => item.SetPosition(node.ComputedRectangle), Times.Once);
            window.Verify(item => item.GetProcess(), Times.Exactly(expectedCalls));
            Assert.AreEqual(expectedCalls, sink.Events.Count);
            if (expectedCalls != 0)
            {
                Assert.AreEqual(LogEventLevel.Information, sink.Events[0].Level);
                Assert.AreEqual("Relocating window {Window} from {OriginalPosition} to {ComputedPosition}", sink.Events[0].MessageTemplate.Text);
                Assert.AreEqual(3, sink.Events[0].Properties.Count);
            }
        }

        [TestMethod]
        public void RepositionFailureKeepsWarningAndErrorContext()
        {
            var sink = new DiagnosticSink();
            using var logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(sink).CreateLogger();
            using var fixture = new ServiceFixture(EnabledSettings(false), logger: logger);
            sink.Events.Clear();
            var warning = DiagnosticWindow();
            warning.SetupGet(item => item.CanResize).Returns(false);
            var failed = DiagnosticWindow();
            var failure = new System.ComponentModel.Win32Exception(5);
            failed.SetupGet(item => item.Position).Throws(failure);
            var method = typeof(TilingService).GetMethod("CalculateRepositionTargets", BindingFlags.NonPublic | BindingFlags.Instance)!;
            method.Invoke(fixture.Service, [new[] { new WindowNode(warning.Object), new WindowNode(failed.Object) }]);
            Assert.AreEqual(2, sink.Events.Count);
            Assert.AreEqual("Unresizable window {Window} will be moved only", sink.Events[0].MessageTemplate.Text);
            Assert.AreEqual("Failed to calculate reposition targets", sink.Events[1].MessageTemplate.Text);
            Assert.AreSame(failure, sink.Events[1].Exception);
            Assert.AreEqual(LogEventLevel.Warning, sink.Events[0].Level);
            Assert.AreEqual(LogEventLevel.Error, sink.Events[1].Level);
        }

        [TestMethod]
        public void DisabledLoggingCounterScenario()
        {
            var method = typeof(TilingService).GetMethod("CalculateRepositionTargets", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (int count in new[] { 1, 4, 10, 25, 50 })
            {
                using var logger = new LoggerConfiguration().MinimumLevel.Information().CreateLogger();
                using var fixture = new ServiceFixture(EnabledSettings(false), logger: logger);
                int handles = 0;
                var nodes = new WindowNode[count];
                for (int index = 0; index < count; index++)
                {
                    var window = DiagnosticWindow();
                    window.SetupGet(item => item.Handle).Returns(() => { handles++; return new IntPtr(0x42); });
                    nodes[index] = new WindowNode(window.Object);
                }
                for (int warmup = 0; warmup < 20; warmup++) method.Invoke(fixture.Service, [nodes]);
                handles = 0;
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    var result = (List<TransitionTarget>)method.Invoke(fixture.Service, [nodes])!;
                    Assert.AreEqual(count, result.Count);
                    for (int index = 0; index < count; index++)
                    {
                        Assert.AreSame(nodes[index].WindowReference, result[index].Window);
                        Assert.AreEqual(nodes[index].ComputedRectangle, result[index].ComputedPosition);
                    }
                }
                TestContext.WriteLine($"PERFCOUNTER disabled-logging-{count} diagnostic-handle-reads {handles}");
                TestContext.WriteLine($"PERFCOUNTER disabled-logging-{count} target-count {100 * count}");
            }
        }

        private static Mock<IWindow> DiagnosticWindow()
        {
            var window = new Mock<IWindow>();
            window.SetupGet(item => item.Handle).Returns(new IntPtr(0x42));
            window.Setup(item => item.GetProcess()).Throws(new InvalidOperationException("Synthetic dead process"));
            window.SetupGet(item => item.Position).Returns(new Rectangle(1, 1, 101, 101));
            window.SetupGet(item => item.CanResize).Returns(true);
            window.SetupGet(item => item.State).Returns(WindowState.Restored);
            return window;
        }

        private sealed class DiagnosticSink : ILogEventSink
        {
            public List<LogEvent> Events { get; } = [];
            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }
    }
}
