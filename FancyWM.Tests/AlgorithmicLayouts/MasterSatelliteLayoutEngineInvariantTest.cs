using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteLayoutEngineInvariantTest
    {
        private delegate MasterSatelliteInvariantResult ValidateInvariantWithDescription(
            MasterSatelliteLayoutEngine engine,
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            string capturedTreeDescription);

        private static readonly ValidateInvariantWithDescription s_validateInvariantWithDescription =
            (ValidateInvariantWithDescription)typeof(MasterSatelliteLayoutEngine)
                .GetMethod(
                    "ValidateInvariant",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    types:
                    [
                        typeof(DesktopTree),
                        typeof(MasterSatelliteRuntimeState),
                        typeof(MasterSatelliteLayoutSettings),
                        typeof(string),
                    ],
                    modifiers: null)!
                .CreateDelegate(typeof(ValidateInvariantWithDescription));

        private readonly MasterSatelliteLayoutEngine m_engine = new();
        private readonly UniqueWindowMockFactory m_windows = new();

        [TestMethod]
        public void ValidateInvariantAcceptsCanonicalTree()
        {
            var (tree, state, settings, _) = Build(4);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsTrue(invariant.IsValid, invariant.Description);
            Assert.AreEqual(0, invariant.Violations.Count);
            Assert.IsFalse(string.IsNullOrWhiteSpace(invariant.TreeDescription));
        }

        [TestMethod]
        public void ValidateInvariantDetectsWrongRootOrientation()
        {
            var (tree, state, settings, _) = Build(2);
            AssertRoot(tree).Orientation = PanelOrientation.Vertical;

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "root split must be horizontal");
        }

        [TestMethod]
        public void ValidateInvariantDetectsNestedPanel()
        {
            var (tree, state, settings, _) = Build(3);
            var satellitePanel = AssertSatellitePanel(tree);
            var first = satellitePanel.Children[0];
            satellitePanel.Detach(first);
            var nested = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
            satellitePanel.Attach(0, nested);
            nested.Attach(first);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "Nested panels");
        }

        [TestMethod]
        public void ValidateInvariantDetectsStack()
        {
            var (tree, state, settings, _) = Build(3);
            var satellitePanel = AssertSatellitePanel(tree);
            var first = satellitePanel.Children[0];
            satellitePanel.Detach(first);
            var stack = new StackPanelNode();
            satellitePanel.Attach(0, stack);
            stack.Attach(first);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "StackPanelNode");
        }

        [TestMethod]
        public void ValidateInvariantDetectsPlaceholder()
        {
            var (tree, state, settings, _) = Build(2);
            AssertSatellitePanel(tree).Attach(new PlaceholderNode());

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "PlaceholderNode");
        }

        [TestMethod]
        public void ValidateInvariantDetectsDuplicateWindowNode()
        {
            var (tree, state, settings, windows) = Build(2);
            var satellitePanel = AssertSatellitePanel(tree);
            var mutableChildren = (IList<TilingNode>)satellitePanel.Children;
            mutableChildren.Add(new WindowNode(windows[1]));

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "duplicate WindowNode");
        }

        [DataTestMethod]
        [DataRow("distinct")]
        [DataRow("duplicate")]
        [DataRow("late-hash")]
        [DataRow("late-equality")]
        public void DuplicateGroupingPreservesCompleteCallbacksAndLateFailures(string scenario)
        {
            var (tree, state, settings, windows) = BuildMasterCountProbe(4);
            state.Master = null;

            var calls = new List<string>();
            var failure = new InvalidOperationException("controlled late grouping failure");
            bool recording = false;
            int earlyMatches = 0;
            long revision = state.Revision;
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;

            for (int index = 0; index < windows.Length; index++)
            {
                int current = index;
                var mock = Mock.Get(windows[current]);
                mock.Setup(window => window.GetHashCode()).Returns(() =>
                {
                    if (recording)
                    {
                        calls.Add($"hash:{current}");
                        if (scenario == "late-hash" && current == 2)
                        {
                            throw failure;
                        }
                    }

                    return current < 2 || (scenario == "late-equality" && current == 3)
                        ? 101
                        : 200 + current;
                });
                mock.Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        int otherIndex = Array.FindIndex(
                            windows,
                            candidate => ReferenceEquals(candidate, other));
                        if (recording)
                        {
                            calls.Add($"equals:{current}:{otherIndex}");
                            if (scenario == "late-equality" && current == 0 && otherIndex == 3)
                            {
                                throw failure;
                            }
                        }

                        bool equal = current == otherIndex
                            || (scenario != "distinct"
                                && current < 2
                                && otherIndex >= 0
                                && otherIndex < 2);
                        if (recording && equal && current == 0 && otherIndex == 1)
                        {
                            earlyMatches++;
                        }
                        return equal;
                    });
            }

            MasterSatelliteInvariantResult invariant = null;
            recording = true;
            try
            {
                if (scenario == "late-hash" || scenario == "late-equality")
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(
                        () => m_engine.ValidateInvariant(tree, state, settings));
                    Assert.AreSame(failure, actual);
                }
                else
                {
                    invariant = m_engine.ValidateInvariant(tree, state, settings);
                }
            }
            finally
            {
                recording = false;
            }

            string[] expectedCalls = scenario switch
            {
                "late-hash" =>
                [
                    "hash:0", "hash:1", "equals:0:1", "hash:2",
                ],
                "late-equality" =>
                [
                    "hash:0", "hash:1", "equals:0:1",
                    "hash:2", "hash:3", "equals:0:3",
                ],
                _ =>
                [
                    "hash:0", "hash:1", "equals:0:1", "hash:2", "hash:3",
                ],
            };
            CollectionAssert.AreEqual(expectedCalls, calls);
            Assert.AreEqual(scenario == "distinct" ? 0 : 1, earlyMatches);

            if (scenario == "distinct" || scenario == "duplicate")
            {
                Assert.IsNotNull(invariant);
                const string duplicate =
                    "The tree contains duplicate WindowNode references for a logical window.";
                const string nonempty =
                    "An empty runtime state requires an empty root.";
                CollectionAssert.AreEqual(
                    scenario == "duplicate"
                        ? new[] { duplicate, nonempty }
                        : new[] { nonempty },
                    invariant!.Violations.ToArray());
                Assert.AreEqual(description, invariant.TreeDescription);
            }

            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreEqual(description, m_engine.CreateSnapshot(tree, state).TreeDescription);

            var root = AssertRoot(tree);
            Assert.AreEqual(windows.Length, root.Children.Count);
            for (int index = 0; index < windows.Length; index++)
            {
                Assert.AreSame(
                    windows[index],
                    ((WindowNode)root.Children[index]).WindowReference);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DuplicateGroupingMasksHashSignBitAndPreservesLateEquality(
            bool failAfterDuplicate)
        {
            int windowCount = failAfterDuplicate ? 3 : 2;
            var (tree, state, settings, windows) = BuildMasterCountProbe(windowCount);
            state.Master = null;

            var calls = new List<string>();
            var failure = new InvalidOperationException(
                "controlled equality failure after an earlier duplicate");
            bool recording = false;
            long revision = state.Revision;
            const string description = "duplicate-sign-bit-hash-probe";

            for (int index = 0; index < windows.Length; index++)
            {
                int current = index;
                var mock = Mock.Get(windows[current]);
                mock.Setup(window => window.GetHashCode()).Returns(() =>
                {
                    if (recording)
                    {
                        calls.Add($"hash:{current}");
                    }

                    if (current == windowCount - 1)
                    {
                        return unchecked(int.MinValue | 101);
                    }
                    return 101;
                });
                mock.Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        int otherIndex = Array.FindIndex(
                            windows,
                            candidate => ReferenceEquals(candidate, other));
                        if (recording)
                        {
                            calls.Add($"equals:{current}:{otherIndex}");
                            if (failAfterDuplicate && current == 0 && otherIndex == 2)
                            {
                                throw failure;
                            }
                        }

                        return failAfterDuplicate && current == 0 && otherIndex == 1;
                    });
            }

            MasterSatelliteInvariantResult invariant = null;
            recording = true;
            try
            {
                if (failAfterDuplicate)
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                        s_validateInvariantWithDescription(
                            m_engine,
                            tree,
                            state,
                            settings,
                            description));
                    Assert.AreSame(failure, actual);
                }
                else
                {
                    invariant = s_validateInvariantWithDescription(
                        m_engine,
                        tree,
                        state,
                        settings,
                        description);
                }
            }
            finally
            {
                recording = false;
            }

            CollectionAssert.AreEqual(
                failAfterDuplicate
                    ? new[]
                    {
                        "hash:0", "hash:1", "equals:0:1",
                        "hash:2", "equals:0:2",
                    }
                    : new[] { "hash:0", "hash:1", "equals:0:1" },
                calls);

            if (!failAfterDuplicate)
            {
                Assert.IsNotNull(invariant);
                CollectionAssert.AreEqual(
                    new[] { "An empty runtime state requires an empty root." },
                    invariant!.Violations.ToArray());
                Assert.AreEqual(description, invariant.TreeDescription);
            }

            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(revision, state.Revision);
            var root = AssertRoot(tree);
            Assert.AreEqual(windowCount, root.Children.Count);
            for (int index = 0; index < windowCount; index++)
            {
                Assert.AreSame(
                    windows[index],
                    ((WindowNode)root.Children[index]).WindowReference);
            }
        }

        [TestMethod]
        public void DuplicateGroupingHandlesNullWithoutWindowCallbacks()
        {
            var settings = CreateSettings();
            var root = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
                Root = root,
            };
            var state = m_engine.CreateState(settings, true);
            var first = m_windows.Create("null-group-first");
            var second = m_windows.Create("null-group-second");
            var calls = new List<string>();
            bool recording = false;
            const string description = "duplicate-null-key-probe";

            Mock.Get(first).Setup(window => window.GetHashCode()).Returns(() =>
            {
                if (recording)
                {
                    calls.Add("hash:first");
                }
                return 0;
            });
            Mock.Get(second).Setup(window => window.GetHashCode()).Returns(() =>
            {
                if (recording)
                {
                    calls.Add("hash:second");
                }
                return 0;
            });
            Mock.Get(first).Setup(window => window.Equals(It.IsAny<IWindow>()))
                .Returns((IWindow other) =>
                {
                    if (recording)
                    {
                        calls.Add("equals:first:second");
                    }
                    Assert.AreSame(second, other);
                    return false;
                });
            Mock.Get(second).Setup(window => window.Equals(It.IsAny<IWindow>()))
                .Returns((IWindow _) =>
                {
                    Assert.Fail("The later window must not be the equality receiver.");
                    return false;
                });

            var mutableChildren = (IList<TilingNode>)root.Children;
            mutableChildren.Add(new WindowNode(null!));
            mutableChildren.Add(new WindowNode(first));
            mutableChildren.Add(new WindowNode(null!));
            mutableChildren.Add(new WindowNode(second));
            var originalChildren = root.Children.ToArray();
            long revision = state.Revision;

            recording = true;
            MasterSatelliteInvariantResult invariant;
            try
            {
                invariant = s_validateInvariantWithDescription(
                    m_engine,
                    tree,
                    state,
                    settings,
                    description);
            }
            finally
            {
                recording = false;
            }

            CollectionAssert.AreEqual(
                new[] { "hash:first", "hash:second", "equals:first:second" },
                calls);
            CollectionAssert.AreEqual(
                new[]
                {
                    "The tree contains duplicate WindowNode references for a logical window.",
                    "An empty runtime state requires an empty root.",
                },
                invariant.Violations.ToArray());
            Assert.AreEqual(description, invariant.TreeDescription);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(revision, state.Revision);
            CollectionAssert.AreEqual(originalChildren, root.Children.ToArray());
        }

        [TestMethod]
        public void DuplicateGroupingPreservesCollisionOrderAcrossResize()
        {
            const int distinctCount = 8;
            var (tree, state, settings, windows) = BuildMasterCountProbe(distinctCount);
            state.Master = null;
            var root = AssertRoot(tree);
            ((IList<TilingNode>)root.Children).Add(new WindowNode(windows[0]));

            var calls = new List<string>();
            bool recording = false;
            const string description = "duplicate-collision-resize-probe";
            for (int index = 0; index < windows.Length; index++)
            {
                int current = index;
                var mock = Mock.Get(windows[current]);
                mock.Setup(window => window.GetHashCode()).Returns(() =>
                {
                    if (recording)
                    {
                        calls.Add($"hash:{current}");
                    }
                    return 101;
                });
                mock.Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        int otherIndex = Array.FindIndex(
                            windows,
                            candidate => ReferenceEquals(candidate, other));
                        if (recording)
                        {
                            calls.Add($"equals:{current}:{otherIndex}");
                        }
                        return ReferenceEquals(windows[current], other);
                    });
            }

            var expectedCalls = new List<string>();
            for (int current = 0; current < distinctCount; current++)
            {
                expectedCalls.Add($"hash:{current}");
                for (int existing = current - 1; existing >= 0; existing--)
                {
                    expectedCalls.Add($"equals:{existing}:{current}");
                }
            }
            expectedCalls.Add("hash:0");
            for (int existing = distinctCount - 1; existing >= 0; existing--)
            {
                expectedCalls.Add($"equals:{existing}:0");
            }

            recording = true;
            MasterSatelliteInvariantResult invariant;
            try
            {
                invariant = s_validateInvariantWithDescription(
                    m_engine,
                    tree,
                    state,
                    settings,
                    description);
            }
            finally
            {
                recording = false;
            }

            CollectionAssert.AreEqual(expectedCalls, calls);
            CollectionAssert.AreEqual(
                new[]
                {
                    "The tree contains duplicate WindowNode references for a logical window.",
                    "An empty runtime state requires an empty root.",
                },
                invariant.Violations.ToArray());
            Assert.AreEqual(description, invariant.TreeDescription);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(distinctCount + 1, root.Children.Count);
            for (int index = 0; index < distinctCount; index++)
            {
                Assert.AreSame(
                    windows[index],
                    ((WindowNode)root.Children[index]).WindowReference);
            }
            Assert.AreSame(
                windows[0],
                ((WindowNode)root.Children[^1]).WindowReference);
        }

        [DataTestMethod]
        [DataRow("success")]
        [DataRow("late-hash")]
        public void WindowNodeSnapshotPreservesTypeOrderAndGroupingBoundary(string scenario)
        {
            var calls = new List<string>();
            var root = new SnapshotCapturingPanel(calls) { Orientation = PanelOrientation.Horizontal };
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
                Root = root,
            };
            var settings = CreateSettings();
            var state = m_engine.CreateState(settings, true);
            var windows = Enumerable.Range(0, 5)
                .Select(index => m_windows.Create($"window-node-snapshot-{index}"))
                .ToArray();
            WindowNode[] windowNodes =
            [
                new WindowNode(windows[0]),
                new DerivedInvariantWindowNode(windows[1]),
                new WindowNode(windows[2]),
                new DerivedInvariantWindowNode(windows[3]),
            ];
            var nonWindow = new SplitPanelNode();
            var injectedNode = new WindowNode(windows[4]);
            TilingNode[] sourceNodes =
                [root, windowNodes[2], nonWindow, windowNodes[1], windowNodes[0], windowNodes[3]];
            root.SourceNodes = sourceNodes;
            var failure = new InvalidOperationException("controlled late window-node snapshot hash failure");
            bool recording = false;
            int mutationCount = 0;

            for (int index = 0; index < windows.Length; index++)
            {
                int current = index;
                var mock = Mock.Get(windows[current]);
                mock.Setup(window => window.GetHashCode()).Returns(() =>
                {
                    if (recording)
                    {
                        calls.Add($"hash:{current}");
                        if (current == 2 && mutationCount == 0)
                        {
                            var capturedNodes = root.CopiedArray;
                            Assert.AreNotSame(sourceNodes, capturedNodes);
                            Assert.AreEqual(0, root.CopiedOffset);
                            Assert.AreEqual(sourceNodes.Length, capturedNodes.Length);
                            for (int nodeIndex = 0; nodeIndex < sourceNodes.Length; nodeIndex++)
                            {
                                Assert.AreSame(sourceNodes[nodeIndex], capturedNodes[nodeIndex]);
                            }

                            // Alter the materialized nodes list, not the live tree. The
                            // complete WindowNode snapshot must already be independent.
                            capturedNodes[3] = injectedNode;
                            capturedNodes[4] = root;
                            capturedNodes[5] = windowNodes[2];
                            mutationCount++;
                            calls.Add("mutate:nodes");
                        }
                        if (scenario == "late-hash" && current == 3)
                        {
                            throw failure;
                        }
                    }
                    return current is 1 or 2 ? 101 : 200 + current;
                });
                mock.Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        if (!recording)
                        {
                            return ReferenceEquals(windows[current], other);
                        }
                        int otherIndex = Array.FindIndex(
                            windows,
                            candidate => ReferenceEquals(candidate, other));
                        calls.Add($"equals:{current}:{otherIndex}");
                        return current == otherIndex || (current == 2 && otherIndex == 1);
                    });
            }

            root.Attach(windowNodes[0]);
            root.Attach(windowNodes[1]);
            root.Attach(nonWindow);
            root.Attach(windowNodes[2]);
            root.Attach(windowNodes[3]);
            var originalChildren = root.Children.ToArray();
            long revision = state.Revision;
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;
            MasterSatelliteInvariantResult invariant = null;
            recording = true;
            try
            {
                if (scenario == "late-hash")
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(
                        () => m_engine.ValidateInvariant(tree, state, settings));
                    Assert.AreSame(failure, actual);
                }
                else
                {
                    invariant = m_engine.ValidateInvariant(tree, state, settings);
                }
            }
            finally
            {
                recording = false;
            }

            CollectionAssert.AreEqual(
                new[]
                {
                    "nodes:count", "nodes:copy", "hash:2", "mutate:nodes",
                    "hash:1", "equals:2:1", "hash:0", "hash:3",
                },
                calls);
            Assert.AreEqual(1, mutationCount);
            if (scenario == "success")
            {
                Assert.IsNotNull(invariant);
                Assert.IsFalse(invariant.IsValid);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "The tree contains duplicate WindowNode references for a logical window.",
                        "An empty runtime state requires an empty root.",
                    },
                    invariant.Violations.ToArray());
                Assert.AreEqual(description, invariant.TreeDescription);
            }

            Assert.AreSame(root, tree.Root);
            Assert.IsNull(root.Parent);
            Assert.AreEqual(originalChildren.Length, root.Children.Count);
            for (int index = 0; index < originalChildren.Length; index++)
            {
                Assert.AreSame(originalChildren[index], root.Children[index]);
                Assert.AreSame(root, originalChildren[index].Parent);
                Assert.AreSame(tree, originalChildren[index].Desktop);
            }
            for (int index = 0; index < windowNodes.Length; index++)
            {
                Assert.AreSame(windows[index], windowNodes[index].WindowReference);
                Assert.AreSame(windowNodes[index], tree.FindNode(windows[index]));
            }
            Assert.IsNull(injectedNode.Parent);
            Assert.IsNull(tree.FindNode(windows[4]));
            Assert.AreSame(windowNodes[1], sourceNodes[3]);
            Assert.AreSame(windowNodes[0], sourceNodes[4]);
            Assert.AreSame(windowNodes[3], sourceNodes[5]);
            Assert.AreSame(injectedNode, root.CopiedArray[3]);
            Assert.AreSame(root, root.CopiedArray[4]);
            Assert.AreSame(windowNodes[2], root.CopiedArray[5]);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(revision, state.Revision);
            Assert.IsTrue(state.IsActive);
            Assert.IsFalse(state.IsRecovering);
            Assert.AreEqual(description, m_engine.CreateSnapshot(tree, state).TreeDescription);
        }

        [DataTestMethod]
        [DataRow("distinct")]
        [DataRow("duplicate")]
        [DataRow("late-hash")]
        [DataRow("late-equality")]
        [DataRow("null-single")]
        [DataRow("null-duplicate")]
        [DataRow("mutate-hash")]
        [DataRow("mutate-equality")]
        public void RuntimeSatelliteDistinctPreservesCallbacksNullsAndMutation(string scenario)
        {
            var (tree, state, settings, _) = Build(0);
            settings = settings with { MaxSatellites = 4 };
            var windows = Enumerable.Range(0, 4)
                .Select(index => m_windows.Create($"runtime-distinct-{index}"))
                .ToArray();
            IWindow[] satellites = scenario switch
            {
                "null-single" => [null, windows[0], windows[2], windows[3]],
                "null-duplicate" => [null, windows[0], null, windows[2]],
                _ => windows,
            };
            state.MutableSatellites.AddRange(satellites);

            // The tree has no windows and the master is null, so only the
            // runtime distinct-count check can invoke these window callbacks.
            var root = AssertRoot(tree);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, root.Children.Count);
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;
            long revision = state.Revision;
            var calls = new List<string>();
            var failure = new InvalidOperationException("controlled late runtime distinct failure");
            bool recording = false;
            bool logicalDuplicate = scenario is "duplicate" or "late-hash" or "late-equality"
                or "mutate-hash" or "mutate-equality";
            bool mutates = scenario is "mutate-hash" or "mutate-equality";
            int earlyMatches = 0;
            int mutationCount = 0;

            for (int index = 0; index < windows.Length; index++)
            {
                int current = index;
                var mock = Mock.Get(windows[current]);
                mock.Setup(window => window.GetHashCode()).Returns(() =>
                {
                    if (recording)
                    {
                        calls.Add($"hash:{current}");
                        if (scenario == "late-hash" && current == 2)
                        {
                            throw failure;
                        }
                        if (scenario == "mutate-hash" && current == 2)
                        {
                            state.MutableSatellites.RemoveAt(3);
                            mutationCount++;
                        }
                    }

                    return current < 2 || (scenario == "late-equality" && current == 3)
                        ? 101
                        : 200 + current;
                });
                mock.Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        if (!recording)
                        {
                            return ReferenceEquals(windows[current], other);
                        }

                        int otherIndex = Array.FindIndex(
                            windows,
                            candidate => ReferenceEquals(candidate, other));
                        calls.Add($"equals:{current}:{otherIndex}");
                        if (scenario == "late-equality" && current == 0 && otherIndex == 3)
                        {
                            throw failure;
                        }

                        bool equal = current == otherIndex
                            || (logicalDuplicate && current < 2 && otherIndex >= 0 && otherIndex < 2);
                        if (equal && current == 0 && otherIndex == 1)
                        {
                            earlyMatches++;
                            if (scenario == "mutate-equality")
                            {
                                state.MutableSatellites.RemoveAt(3);
                                mutationCount++;
                            }
                        }
                        return equal;
                    });
            }

            MasterSatelliteInvariantResult invariant = null;
            recording = true;
            try
            {
                if (scenario is "late-hash" or "late-equality" or "mutate-hash" or "mutate-equality")
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(
                        () => m_engine.ValidateInvariant(tree, state, settings));
                    if (!mutates)
                    {
                        Assert.AreSame(failure, actual);
                    }
                }
                else
                {
                    invariant = m_engine.ValidateInvariant(tree, state, settings);
                }
            }
            finally
            {
                recording = false;
            }

            string[] expectedCalls = scenario switch
            {
                "late-hash" or "mutate-hash" =>
                ["hash:0", "hash:1", "equals:0:1", "hash:2"],
                "late-equality" =>
                ["hash:0", "hash:1", "equals:0:1", "hash:2", "hash:3", "equals:0:3"],
                "mutate-equality" => ["hash:0", "hash:1", "equals:0:1"],
                "null-single" => ["hash:0", "hash:2", "hash:3"],
                "null-duplicate" => ["hash:0", "hash:2"],
                _ => ["hash:0", "hash:1", "equals:0:1", "hash:2", "hash:3"],
            };
            CollectionAssert.AreEqual(expectedCalls, calls);
            Assert.AreEqual(logicalDuplicate ? 1 : 0, earlyMatches);
            Assert.AreEqual(mutates ? 1 : 0, mutationCount);

            if (scenario is "distinct" or "duplicate" or "null-single" or "null-duplicate")
            {
                Assert.IsNotNull(invariant);
                const string duplicate = "The runtime satellite list contains duplicates.";
                const string missingMaster = "Satellites cannot exist without a master.";
                CollectionAssert.AreEqual(
                    scenario is "duplicate" or "null-duplicate"
                        ? new[] { duplicate, missingMaster }
                        : new[] { missingMaster },
                    invariant!.Violations.ToArray());
                Assert.IsFalse(invariant.IsValid);
                Assert.AreEqual(description, invariant.TreeDescription);
            }

            Assert.AreSame(root, tree.Root);
            Assert.AreEqual(0, root.Children.Count);
            Assert.IsNull(state.Master);
            Assert.IsTrue(state.IsActive);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreEqual(mutates ? 3 : satellites.Length, state.Satellites.Count);
            for (int index = 0; index < state.Satellites.Count; index++)
            {
                if (satellites[index] == null)
                {
                    Assert.IsNull(state.Satellites[index]);
                }
                else
                {
                    Assert.AreSame(satellites[index], state.Satellites[index]);
                }
            }
            Assert.AreEqual(description, m_engine.CreateSnapshot(tree, state).TreeDescription);
        }

        [DataTestMethod]
        [DataRow("no-match")]
        [DataRow("first-match")]
        [DataRow("late-exception")]
        [DataRow("master-replacement")]
        [DataRow("master-to-null")]
        [DataRow("initially-null")]
        [DataRow("final-hash-clears-master")]
        public void ValidateInvariantSatelliteMasterScanPreservesCallbacksAndState(string scenario)
        {
            var (tree, state, settings, _) = Build(0);
            var root = AssertRoot(tree);
            var master = m_windows.Create("satellite-scan-master");
            var replacementMaster = m_windows.Create("satellite-scan-replacement-master");
            var windows = Enumerable.Range(0, 3)
                .Select(index => m_windows.Create($"satellite-scan-{index}"))
                .ToArray();
            IWindow[] satellites = scenario == "master-to-null"
                ? [windows[0], windows[1], null!]
                : windows;
            state.Master = scenario == "initially-null" ? null : master;
            state.MutableSatellites.AddRange(satellites);
            var satelliteView = state.Satellites;
            var before = state.Clone();
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;
            var calls = new List<string>();
            var failure = new InvalidOperationException("controlled late satellite/master equality failure");
            bool recording = false;
            int masterMutations = 0;

            // An empty tree and distinct satellite hashes isolate the target
            // equality scan from GroupBy, HashSet equality and later tree checks.
            for (int index = 0; index < windows.Length; index++)
            {
                int current = index;
                var mock = Mock.Get(windows[current]);
                mock.Setup(window => window.GetHashCode()).Returns(() =>
                {
                    if (recording)
                    {
                        calls.Add($"hash:{current}");
                        if (scenario == "final-hash-clears-master" && current == 2)
                        {
                            state.Master = null;
                            masterMutations++;
                        }
                    }
                    return 101 + current;
                });
                mock.Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        if (!recording)
                        {
                            return ReferenceEquals(windows[current], other);
                        }

                        string target = ReferenceEquals(other, master) ? "master"
                            : ReferenceEquals(other, replacementMaster) ? "replacement" : "unexpected";
                        calls.Add($"equals:{current}:{target}");
                        Assert.AreSame(
                            scenario == "master-replacement" && current != 0 ? replacementMaster : master,
                            other);
                        switch (scenario)
                        {
                            case "no-match":
                                return false;
                            case "first-match":
                                Assert.AreEqual(0, current, "The first match must stop later comparisons.");
                                return true;
                            case "late-exception":
                                if (current == 2)
                                {
                                    throw failure;
                                }
                                return false;
                            case "master-replacement":
                                if (current == 0)
                                {
                                    state.Master = replacementMaster;
                                    masterMutations++;
                                    return false;
                                }
                                Assert.AreEqual(1, current, "The replacement-master match must stop the scan.");
                                return true;
                            case "master-to-null":
                                Assert.AreEqual(0, current, "Null comparisons must use the default comparer.");
                                state.Master = null;
                                masterMutations++;
                                return false;
                            default:
                                Assert.Fail("A null master at the guard must skip the equality scan.");
                                return false;
                        }
                    });
            }

            MasterSatelliteInvariantResult invariant = null;
            recording = true;
            try
            {
                if (scenario == "late-exception")
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(
                        () => m_engine.ValidateInvariant(tree, state, settings));
                    Assert.AreSame(failure, actual);
                }
                else
                {
                    invariant = m_engine.ValidateInvariant(tree, state, settings);
                }
            }
            finally
            {
                recording = false;
            }

            string[] expectedCalls = scenario switch
            {
                "no-match" or "late-exception" =>
                    ["hash:0", "hash:1", "hash:2", "equals:0:master", "equals:1:master", "equals:2:master"],
                "first-match" => ["hash:0", "hash:1", "hash:2", "equals:0:master"],
                "master-replacement" =>
                    ["hash:0", "hash:1", "hash:2", "equals:0:master", "equals:1:replacement"],
                "master-to-null" => ["hash:0", "hash:1", "equals:0:master"],
                _ => ["hash:0", "hash:1", "hash:2"],
            };
            CollectionAssert.AreEqual(expectedCalls, calls);
            Assert.AreEqual(
                scenario is "master-replacement" or "master-to-null" or "final-hash-clears-master" ? 1 : 0,
                masterMutations);

            if (scenario != "late-exception")
            {
                const string duplicate = "The master also appears in the satellite list.";
                const string missingMaster = "Satellites cannot exist without a master.";
                const string windowCount = "The tree has 0 windows; runtime state expects 4.";
                const string masterCount = "The tree must contain exactly one node for the runtime master.";
                const string rootShape = "A master-plus-satellites tree must have exactly two root children.";
                string[] expectedViolations = scenario switch
                {
                    "first-match" or "master-replacement" => [duplicate, windowCount, masterCount, rootShape],
                    "master-to-null" => [duplicate, missingMaster],
                    "initially-null" or "final-hash-clears-master" => [missingMaster],
                    _ => [windowCount, masterCount, rootShape],
                };
                Assert.IsNotNull(invariant);
                Assert.IsFalse(invariant.IsValid);
                Assert.AreEqual(description, invariant.TreeDescription);
                CollectionAssert.AreEqual(expectedViolations, invariant.Violations.ToArray());
            }

            IWindow? expectedMaster = scenario switch
            {
                "master-replacement" => replacementMaster,
                "master-to-null" or "initially-null" or "final-hash-clears-master" => null,
                _ => master,
            };
            Assert.AreSame(satelliteView, state.Satellites);
            AssertSatelliteMasterScanIdentity(tree, root, state, before, expectedMaster, satellites, description);
        }

        [DataTestMethod]
        [DataRow("clear", false)]
        [DataRow("clear", true)]
        [DataRow("replace", false)]
        [DataRow("replace", true)]
        public void ValidateInvariantSatelliteMasterScanPreservesMutationBoundary(string mutation, bool matches)
        {
            var (tree, state, settings, _) = Build(0);
            var root = AssertRoot(tree);
            var master = m_windows.Create("satellite-mutation-master");
            var replacement = m_windows.Create("satellite-mutation-replacement");
            var satellites = Enumerable.Range(0, 3)
                .Select(index => m_windows.Create($"satellite-mutation-{index}"))
                .ToArray();
            state.Master = master;
            state.MutableSatellites.AddRange(satellites);
            var satelliteView = state.Satellites;
            var before = state.Clone();
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;
            var calls = new List<string>();
            bool recording = false;
            int mutationCount = 0;

            for (int index = 0; index < satellites.Length; index++)
            {
                int current = index;
                var mock = Mock.Get(satellites[current]);
                mock.Setup(window => window.GetHashCode()).Returns(() =>
                {
                    if (recording)
                    {
                        calls.Add($"hash:{current}");
                    }
                    return 101 + current;
                });
                mock.Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        if (!recording)
                        {
                            return ReferenceEquals(satellites[current], other);
                        }
                        calls.Add($"equals:{current}:master");
                        Assert.AreSame(master, other);
                        Assert.AreEqual(0, current, "Mutation must prevent any later comparison.");
                        if (mutation == "clear")
                        {
                            state.MutableSatellites.Clear();
                        }
                        else
                        {
                            state.MutableSatellites[1] = replacement;
                        }
                        mutationCount++;
                        return matches;
                    });
            }

            MasterSatelliteInvariantResult invariant = null;
            recording = true;
            try
            {
                if (matches)
                {
                    // Any returns without another MoveNext, even after Clear.
                    invariant = m_engine.ValidateInvariant(tree, state, settings);
                }
                else
                {
                    // The next MoveNext checks the version before the new Count.
                    Assert.ThrowsException<InvalidOperationException>(
                        () => m_engine.ValidateInvariant(tree, state, settings));
                }
            }
            finally
            {
                recording = false;
            }

            CollectionAssert.AreEqual(
                new[] { "hash:0", "hash:1", "hash:2", "equals:0:master" }, calls);
            Assert.AreEqual(1, mutationCount);
            if (matches)
            {
                Assert.IsNotNull(invariant);
                Assert.IsFalse(invariant.IsValid);
                Assert.AreEqual(description, invariant.TreeDescription);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "The master also appears in the satellite list.",
                        mutation == "clear"
                            ? "The tree has 0 windows; runtime state expects 1."
                            : "The tree has 0 windows; runtime state expects 4.",
                        "The tree must contain exactly one node for the runtime master.",
                        mutation == "clear"
                            ? "A master-only tree must contain exactly the master as the root's sole child."
                            : "A master-plus-satellites tree must have exactly two root children.",
                    },
                    invariant.Violations.ToArray());
            }

            IWindow[] expectedSatellites = mutation == "clear" ? [] : [satellites[0], replacement, satellites[2]];
            Assert.AreSame(satelliteView, state.Satellites);
            AssertSatelliteMasterScanIdentity(tree, root, state, before, master, expectedSatellites, description);
        }

        [DataTestMethod]
        [DataRow("left")]
        [DataRow("right")]
        [DataRow("logical-short-circuit")]
        [DataRow("state-after-snapshot")]
        [DataRow("state-before-snapshot-null")]
        [DataRow("state-before-snapshot-clear")]
        [DataRow("state-before-snapshot-reorder")]
        [DataRow("state-before-snapshot-replacement")]
        [DataRow("state-before-snapshot-null-satellite")]
        [DataRow("first-untracked")]
        [DataRow("late-equality")]
        public void MembershipScanPreservesSnapshotsOrderAndFailureBoundaries(string scenario)
        {
            var (tree, state, settings, windows) = Build(4);
            if (scenario == "right")
            {
                var operation = m_engine.SetMasterSide(tree, state, settings, MasterSide.Right);
                Assert.IsTrue(operation.Succeeded, operation.Message);
            }

            var root = AssertRoot(tree);
            var satellitePanel = AssertSatellitePanel(tree);
            var originalNodes = root.Windows.ToArray();
            var originalSatellites = state.Satellites.ToArray();
            var replacement = m_windows.Create("membership-replacement");
            if (scenario == "first-untracked")
            {
                root.Orientation = PanelOrientation.Vertical;
            }
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;
            long revision = state.Revision;
            var failure = new InvalidOperationException("controlled late membership equality failure");
            var precedingCalls = new List<string>();
            var membershipCalls = new List<string>();
            bool recording = false;
            bool membershipStarted = false;
            int snapshotBoundaryCount = 0;
            int mutationCount = 0;

            for (int index = 0; index < windows.Length; index++)
            {
                int current = index;
                Mock.Get(windows[current])
                    .Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        if (!recording)
                        {
                            return ReferenceEquals(windows[current], other);
                        }

                        int otherIndex = Array.FindIndex(
                            windows,
                            candidate => ReferenceEquals(candidate, other));
                        string call = $"{current}:{otherIndex}";
                        if (!membershipStarted)
                        {
                            precedingCalls.Add(call);
                            // The final visual-slot equality is immediately before
                            // construction of the runtime membership snapshot.
                            if (current == 3 && otherIndex == 3)
                            {
                                snapshotBoundaryCount++;
                                membershipStarted = true;
                                if (scenario == "state-before-snapshot-null")
                                {
                                    state.Master = null;
                                    mutationCount++;
                                }
                                else if (scenario == "state-before-snapshot-clear")
                                {
                                    state.MutableSatellites.Clear();
                                    mutationCount++;
                                }
                                else if (scenario == "state-before-snapshot-reorder")
                                {
                                    state.MutableSatellites.Reverse();
                                    mutationCount++;
                                }
                                else if (scenario == "state-before-snapshot-replacement")
                                {
                                    state.MutableSatellites[1] = replacement;
                                    mutationCount++;
                                }
                                else if (scenario == "state-before-snapshot-null-satellite")
                                {
                                    state.MutableSatellites[1] = null;
                                    mutationCount++;
                                }
                            }
                            return ReferenceEquals(windows[current], other);
                        }

                        membershipCalls.Add(call);
                        if (scenario == "state-after-snapshot" && membershipCalls.Count == 1)
                        {
                            state.Master = replacement;
                            state.MutableSatellites.Clear();
                            state.MutableSatellites.Add(replacement);
                            mutationCount++;
                        }
                        if (scenario == "late-equality" && current == 2 && otherIndex == 3)
                        {
                            throw failure;
                        }
                        if (scenario == "first-untracked" && otherIndex == 1)
                        {
                            return false;
                        }
                        return ReferenceEquals(windows[current], other)
                            || (scenario == "logical-short-circuit" && current == 0 && otherIndex == 1);
                    });
            }

            Mock.Get(replacement)
                .Setup(window => window.Equals(It.IsAny<IWindow>()))
                .Returns((IWindow other) =>
                {
                    if (recording)
                    {
                        Assert.IsTrue(membershipStarted);
                        int otherIndex = Array.FindIndex(
                            windows,
                            candidate => ReferenceEquals(candidate, other));
                        membershipCalls.Add($"4:{otherIndex}");
                    }
                    return ReferenceEquals(replacement, other);
                });

            for (int index = 0; index <= windows.Length; index++)
            {
                int current = index;
                var window = index < windows.Length ? windows[index] : replacement;
                int hashCode = window.GetHashCode();
                Mock.Get(window)
                    .Setup(candidate => candidate.GetHashCode())
                    .Returns(() =>
                    {
                        if (recording && membershipStarted)
                        {
                            // Snapshot copying and membership must not hash windows.
                            membershipCalls.Add($"hash:{current}");
                        }
                        return hashCode;
                    });
            }

            MasterSatelliteInvariantResult invariant = null;
            recording = true;
            try
            {
                if (scenario == "late-equality")
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(
                        () => m_engine.ValidateInvariant(tree, state, settings));
                    Assert.AreSame(failure, actual);
                }
                else
                {
                    invariant = m_engine.ValidateInvariant(tree, state, settings);
                }
            }
            finally
            {
                recording = false;
            }

            string[] expectedPrecedingCalls = scenario == "right"
                ? ["1:0", "2:0", "3:0", "1:0", "2:0", "3:0", "0:0", "0:0", "1:1", "2:2", "3:3"]
                : ["1:0", "2:0", "3:0", "0:0", "1:0", "2:0", "3:0", "0:0", "1:1", "2:2", "3:3"];
            string[] expectedMembershipCalls = scenario switch
            {
                "right" =>
                ["0:1", "1:1", "0:2", "1:2", "2:2", "0:3", "1:3", "2:3", "3:3", "0:0"],
                "logical-short-circuit" =>
                ["0:0", "0:1", "0:2", "1:2", "2:2", "0:3", "1:3", "2:3", "3:3"],
                "state-before-snapshot-null" => ["1:0", "2:0", "3:0"],
                "state-before-snapshot-clear" => ["0:0", "0:1"],
                "state-before-snapshot-reorder" =>
                ["0:0", "0:1", "3:1", "2:1", "1:1", "0:2", "3:2", "2:2", "0:3", "3:3"],
                "state-before-snapshot-replacement" =>
                ["0:0", "0:1", "1:1", "0:2", "1:2", "4:2", "3:2"],
                "state-before-snapshot-null-satellite" =>
                ["0:0", "0:1", "1:1", "0:2", "1:2", "3:2"],
                "first-untracked" => ["0:0", "0:1", "1:1", "2:1", "3:1"],
                "late-equality" =>
                ["0:0", "0:1", "1:1", "0:2", "1:2", "2:2", "0:3", "1:3", "2:3"],
                _ =>
                ["0:0", "0:1", "1:1", "0:2", "1:2", "2:2", "0:3", "1:3", "2:3", "3:3"],
            };
            Assert.AreEqual(1, snapshotBoundaryCount);
            CollectionAssert.AreEqual(expectedPrecedingCalls, precedingCalls);
            CollectionAssert.AreEqual(expectedMembershipCalls, membershipCalls);

            if (scenario != "late-equality")
            {
                Assert.IsNotNull(invariant);
                const string untracked =
                    "The tree contains a window that is not tracked by runtime state.";
                string[] expectedViolations = scenario switch
                {
                    "first-untracked" => ["The root split must be horizontal.", untracked],
                    "state-before-snapshot-null" => [untracked],
                    "state-before-snapshot-clear" => [untracked],
                    "state-before-snapshot-replacement" => [untracked],
                    "state-before-snapshot-null-satellite" => [untracked],
                    _ => [],
                };
                CollectionAssert.AreEqual(expectedViolations, invariant!.Violations.ToArray());
                Assert.AreEqual(expectedViolations.Length == 0, invariant.IsValid);
                Assert.AreEqual(description, invariant.TreeDescription);
            }

            Assert.AreEqual(
                scenario is "state-after-snapshot" or "state-before-snapshot-null"
                    or "state-before-snapshot-clear" or "state-before-snapshot-reorder"
                    or "state-before-snapshot-replacement" or "state-before-snapshot-null-satellite" ? 1 : 0,
                mutationCount);
            if (scenario == "state-after-snapshot")
            {
                Assert.AreSame(replacement, state.Master);
                Assert.AreEqual(1, state.Satellites.Count);
                Assert.AreSame(replacement, state.Satellites[0]);
            }
            else if (scenario is "state-before-snapshot-clear" or "state-before-snapshot-reorder"
                or "state-before-snapshot-replacement" or "state-before-snapshot-null-satellite")
            {
                Assert.AreSame(windows[0], state.Master);
                IWindow[] expectedSatellites = scenario switch
                {
                    "state-before-snapshot-clear" => [],
                    "state-before-snapshot-reorder" => [windows[3], windows[2], windows[1]],
                    "state-before-snapshot-replacement" => [windows[1], replacement, windows[3]],
                    "state-before-snapshot-null-satellite" => [windows[1], null, windows[3]],
                    _ => throw new InvalidOperationException($"Unexpected scenario: {scenario}."),
                };
                Assert.AreEqual(expectedSatellites.Length, state.Satellites.Count);
                for (int index = 0; index < expectedSatellites.Length; index++)
                {
                    Assert.AreSame(expectedSatellites[index], state.Satellites[index]);
                }
            }
            else
            {
                if (scenario == "state-before-snapshot-null")
                {
                    Assert.IsNull(state.Master);
                }
                else
                {
                    Assert.AreSame(windows[0], state.Master);
                }
                CollectionAssert.AreEqual(originalSatellites, state.Satellites.ToArray());
            }
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(satellitePanel, AssertSatellitePanel(tree));
            CollectionAssert.AreEqual(originalNodes, root.Windows.ToArray());
            Assert.AreEqual(description, m_engine.CreateSnapshot(tree, state).TreeDescription);
        }

#if !DEBUG
        [DataTestMethod]
        [DataRow(0, 808L)]
        [DataRow(1, 1352L)]
        [DataRow(4, 2856L)]
        public void ValidateInvariantStaysWithinDuplicateGroupingAllocationBudget(
            int windowCount,
            long maximumBytesPerCall)
        {
            AssertInvariantAllocationBudget(windowCount, maximumBytesPerCall, "duplicate-group-allocation");
        }

        [TestMethod]
        public void ValidateInvariantStaysWithinMembershipScanAllocationBudget()
        {
            AssertInvariantAllocationBudget(4, 2432, "membership-scan-allocation");
        }

        [TestMethod]
        public void ValidateInvariantStaysWithinRuntimeSatelliteDistinctAllocationBudget()
        {
            AssertInvariantAllocationBudget(4, 2400, "runtime-satellite-distinct-allocation");
        }

        [TestMethod]
        public void ValidateInvariantStaysWithinMembershipSnapshotAllocationBudget()
        {
            AssertInvariantAllocationBudget(4, 2320, "membership-snapshot-allocation");
        }

        [DataTestMethod]
        [DataRow(1, 1240L)]
        [DataRow(4, 2256L)]
        public void ValidateInvariantStaysWithinNestedPanelFilterAllocationBudget(
            int windowCount,
            long maximumBytesPerCall)
        {
            AssertInvariantAllocationBudget(windowCount, maximumBytesPerCall, "nested-panel-filter-allocation");
        }

        [DataTestMethod]
        [DataRow(0, 656L)]
        [DataRow(1, 1112L)]
        [DataRow(4, 2128L)]
        public void ValidateInvariantStaysWithinWindowNodeSnapshotAllocationBudget(
            int windowCount,
            long maximumBytesPerCall)
        {
            AssertInvariantAllocationBudget(windowCount, maximumBytesPerCall, "window-node-snapshot-allocation");
        }

        [DataTestMethod]
        [DataRow(0, 656L)]
        [DataRow(1, 1048L)]
        [DataRow(4, 2064L)]
        public void ValidateInvariantStaysWithinMasterCountScanAllocationBudget(
            int windowCount,
            long maximumBytesPerCall)
        {
            AssertInvariantAllocationBudget(windowCount, maximumBytesPerCall, "master-count-scan-allocation");
        }

        [DataTestMethod]
        [DataRow(0, 648L)]
        [DataRow(1, 976L)]
        [DataRow(4, 1992L)]
        public void ValidateInvariantStaysWithinSatelliteMasterScanAllocationBudget(
            int windowCount,
            long maximumBytesPerCall)
        {
            AssertInvariantAllocationBudget(windowCount, maximumBytesPerCall, "satellite-master-scan-allocation");
        }

        [DataTestMethod]
        [DataRow(0, 512L)]
        [DataRow(1, 824L)]
        [DataRow(4, 1664L)]
        public void ValidateInvariantStaysWithinDuplicateHashSetScanAllocationBudget(
            int windowCount,
            long maximumBytesPerCall)
        {
            AssertInvariantAllocationBudget(windowCount, maximumBytesPerCall, "duplicate-hashset-scan-allocation");
        }

        [DataTestMethod]
        [DataRow(0, 480L)]
        [DataRow(1, 728L)]
        [DataRow(4, 1568L)]
        public void ValidateInvariantStaysWithinCapturingNestedPanelScanAllocationBudget(
            int windowCount,
            long maximumBytesPerCall)
        {
            AssertInvariantAllocationBudget(windowCount, maximumBytesPerCall, "capturing-nested-panel-scan-allocation");
        }

        private void AssertInvariantAllocationBudget(
            int windowCount,
            long maximumBytesPerCall,
            string counterName)
        {
            var (tree, state, settings) = BuildAllocationProbe(windowCount);
            const int warmupCount = 2048;
            const int sampleCount = 512;
            const string capturedTreeDescription = "invariant-allocation-probe";
            MasterSatelliteInvariantResult last = null;
            for (int index = 0; index < warmupCount; index++)
            {
                last = s_validateInvariantWithDescription(
                    m_engine,
                    tree,
                    state,
                    settings,
                    capturedTreeDescription);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int validCount = 0;
            int digest = 17;
            for (int index = 0; index < sampleCount; index++)
            {
                last = s_validateInvariantWithDescription(
                    m_engine,
                    tree,
                    state,
                    settings,
                    capturedTreeDescription);
                validCount += last.IsValid ? 1 : 0;
                digest = unchecked((digest * 31) + last.TreeDescription.Length);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(last);

            Assert.AreEqual(sampleCount, validCount);
            Assert.AreNotEqual(17, digest);
            Assert.IsTrue(
                allocated <= maximumBytesPerCall * sampleCount,
                $"PERFCOUNTER {counterName} windows={windowCount} "
                + $"total={allocated} bytes samples={sampleCount} "
                + $"per-call={(double)allocated / sampleCount:F3} bytes "
                + $"ceiling={maximumBytesPerCall} bytes/call.");
        }
#endif

        [TestMethod]
        public void ValidateInvariantDetectsDuplicateRuntimeSatellite()
        {
            var (tree, state, settings, _) = Build(3);
            state.MutableSatellites.Add(state.Satellites[0]);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "runtime satellite list contains duplicates");
        }

        [TestMethod]
        public void ValidateInvariantDetectsRuntimeVisualOrderMismatch()
        {
            var (tree, state, settings, _) = Build(4);
            state.MutableSatellites.Reverse();

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "runtime visual order");
        }

        [TestMethod]
        public void ValidateInvariantPreservesCompleteViolationOrderWithMultipleNestedPanels()
        {
            var (tree, state, settings, _) = Build(4);
            var root = AssertRoot(tree);
            var satellitePanel = AssertSatellitePanel(tree);
            root.Orientation = PanelOrientation.Vertical;
            satellitePanel.Orientation = PanelOrientation.Horizontal;
            var firstSatellite = satellitePanel.Children[0];
            satellitePanel.Detach(firstSatellite);
            var outer = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
            var inner = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            satellitePanel.Attach(0, outer);
            outer.Attach(inner);
            inner.Attach(firstSatellite);
            state.MutableSatellites.Reverse();

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            CollectionAssert.AreEqual(
                new[]
                {
                    "The root split must be horizontal.",
                    "The satellite panel orientation does not match runtime state.",
                    "Every satellite panel child must be a direct WindowNode.",
                    "Nested panels are not allowed below the canonical satellite panel.",
                    "Satellite slot 0 does not match runtime visual order.",
                    "Satellite slot 2 does not match runtime visual order.",
                },
                invariant.Violations.ToArray());
        }

        [TestMethod]
        public void ValidateInvariantRejectsDerivedNestedPanelInMasterOnlyTreeWithoutEqualityCallbacks()
        {
            var (tree, state, settings, windows) = Build(1);
            var root = AssertRoot(tree);
            var masterNode = (WindowNode)root.Children[0];
            var nested = new EqualityRecordingPanel { Orientation = PanelOrientation.Vertical };
            root.Attach(nested);
            long revision = state.Revision;
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;
            Assert.AreEqual(0, nested.EqualityCallCount);
            Assert.AreEqual(0, nested.HashCallCount);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            CollectionAssert.AreEqual(
                new[]
                {
                    "A master-only tree must contain exactly the master as the root's sole child.",
                    "A master-only tree cannot contain a nested panel.",
                },
                invariant.Violations.ToArray());
            Assert.AreEqual(description, invariant.TreeDescription);
            Assert.AreSame(root, tree.Root);
            Assert.IsNull(root.Parent);
            Assert.AreEqual(2, root.Children.Count);
            Assert.AreSame(masterNode, root.Children[0]);
            Assert.AreSame(nested, root.Children[1]);
            Assert.AreSame(root, masterNode.Parent);
            Assert.AreSame(root, nested.Parent);
            Assert.AreSame(tree, masterNode.Desktop);
            Assert.AreSame(tree, nested.Desktop);
            Assert.AreEqual(0, nested.Children.Count);
            Assert.AreSame(windows[0], masterNode.WindowReference);
            Assert.AreSame(masterNode, tree.FindNode(windows[0]));
            Assert.AreSame(windows[0], state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(revision, state.Revision);
            Assert.IsTrue(state.IsActive);
            Assert.IsFalse(state.IsRecovering);
            Assert.AreEqual(description, m_engine.CreateSnapshot(tree, state).TreeDescription);
            Assert.AreEqual(0, nested.EqualityCallCount);
            Assert.AreEqual(0, nested.HashCallCount);
        }

        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void ValidateInvariantRejectsDerivedNestedPanelInMasterSatelliteTreeWithoutEqualityCallbacks(
            MasterSide masterSide)
        {
            var (tree, state, settings, windows) = Build(3);
            if (masterSide != state.MasterSide)
            {
                var operation = m_engine.SetMasterSide(tree, state, settings, masterSide);
                Assert.IsTrue(operation.Succeeded, operation.Message);
            }

            var root = AssertRoot(tree);
            var satellitePanel = AssertSatellitePanel(tree);
            var firstSatellite = satellitePanel.Children[0];
            satellitePanel.Detach(firstSatellite);
            var nested = new EqualityRecordingPanel { Orientation = PanelOrientation.Vertical };
            satellitePanel.Attach(0, nested);
            nested.Attach(firstSatellite);
            var rootChildren = root.Children.ToArray();
            var satelliteChildren = satellitePanel.Children.ToArray();
            var before = state.Clone();
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;
            Assert.AreEqual(0, nested.EqualityCallCount);
            Assert.AreEqual(0, nested.HashCallCount);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            CollectionAssert.AreEqual(
                new[]
                {
                    "Every satellite panel child must be a direct WindowNode.",
                    "Nested panels are not allowed below the canonical satellite panel.",
                    "Satellite slot 0 does not match runtime visual order.",
                },
                invariant.Violations.ToArray());
            Assert.AreEqual(description, invariant.TreeDescription);
            Assert.AreSame(root, tree.Root);
            Assert.IsNull(root.Parent);
            CollectionAssert.AreEqual(rootChildren, root.Children.ToArray());
            CollectionAssert.AreEqual(satelliteChildren, satellitePanel.Children.ToArray());
            Assert.AreSame(satellitePanel, nested.Parent);
            Assert.AreSame(nested, firstSatellite.Parent);
            Assert.AreSame(tree, nested.Desktop);
            Assert.AreSame(tree, firstSatellite.Desktop);
            Assert.AreSame(windows[0], state.Master);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            Assert.AreEqual(before.Revision, state.Revision);
            Assert.AreEqual(before.IsActive, state.IsActive);
            Assert.AreEqual(before.IsRecovering, state.IsRecovering);
            Assert.AreEqual(before.MasterSide, state.MasterSide);
            Assert.AreEqual(before.RequestedMasterRatio, state.RequestedMasterRatio);
            Assert.AreEqual(before.EffectiveMasterRatio, state.EffectiveMasterRatio);
            Assert.AreEqual(before.SatelliteOrientation, state.SatelliteOrientation);
            Assert.AreEqual(description, m_engine.CreateSnapshot(tree, state).TreeDescription);
            Assert.AreEqual(0, nested.EqualityCallCount);
            Assert.AreEqual(0, nested.HashCallCount);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void ValidateInvariantCountsAllMasterMatchesInWindowOrder(int matchingCount)
        {
            var (tree, state, settings, windows) = BuildMasterCountProbe(3);
            var calls = new List<int>();
            for (int index = 0; index < windows.Length; index++)
            {
                int capturedIndex = index;
                Mock.Get(windows[index])
                    .Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        Assert.AreSame(state.Master, other);
                        calls.Add(capturedIndex);
                        return capturedIndex < matchingCount;
                    });
            }

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, calls);
            Assert.AreEqual(
                matchingCount != 1,
                invariant.Violations.Contains("The tree must contain exactly one node for the runtime master."));
        }

        [TestMethod]
        public void ValidateInvariantReadsCurrentMasterForEveryCountPredicate()
        {
            var (tree, state, settings, windows) = BuildMasterCountProbe(3);
            var firstMaster = state.Master!;
            var replacementMaster = m_windows.Create("replacement-master");
            var calls = new List<string>();
            Mock.Get(windows[0])
                .Setup(window => window.Equals(It.IsAny<IWindow>()))
                .Returns((IWindow other) =>
                {
                    calls.Add(ReferenceEquals(other, firstMaster) ? "0:first" : "0:unexpected");
                    state.Master = replacementMaster;
                    return false;
                });
            Mock.Get(windows[1])
                .Setup(window => window.Equals(It.IsAny<IWindow>()))
                .Returns((IWindow other) =>
                {
                    calls.Add(ReferenceEquals(other, replacementMaster) ? "1:replacement" : "1:unexpected");
                    return ReferenceEquals(other, replacementMaster);
                });
            Mock.Get(windows[2])
                .Setup(window => window.Equals(It.IsAny<IWindow>()))
                .Returns((IWindow other) =>
                {
                    calls.Add(ReferenceEquals(other, replacementMaster) ? "2:replacement" : "2:unexpected");
                    return false;
                });

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            CollectionAssert.AreEqual(new[] { "0:first", "1:replacement", "2:replacement" }, calls);
            Assert.AreSame(replacementMaster, state.Master);
            Assert.IsFalse(invariant.Violations.Contains(
                "The tree must contain exactly one node for the runtime master."));
        }

        [TestMethod]
        public void ValidateInvariantMasterCountRetainsNullMatchesAfterMasterClears()
        {
            var calls = new List<string>();
            var root = new SnapshotCapturingPanel(calls) { Orientation = PanelOrientation.Horizontal };
            var tree = new DesktopTree { Root = root };
            var settings = CreateSettings();
            var state = m_engine.CreateState(settings, true);
            state.Master = m_windows.Create("master-before-null-count");
            var before = state.Clone();
            var window = m_windows.Create("first-counted-window");
            var firstNode = new WindowNode(window);
            var nullNode = new WindowNode(null!);
            var layoutMarker = new SplitPanelNode();
            bool recording = false;

            Mock.Get(window).Setup(value => value.GetHashCode()).Returns(() =>
            {
                if (recording)
                {
                    calls.Add("hash:first");
                }
                return 101; // Keep GroupBy's nonnull key separate from its null key.
            });
            Mock.Get(window).Setup(value => value.Equals(It.IsAny<IWindow>()))
                .Returns((IWindow other) =>
                {
                    if (!recording)
                    {
                        return ReferenceEquals(window, other);
                    }
                    calls.Add("equals:first:master");
                    Assert.AreSame(before.Master, other);
                    state.Master = null;
                    return false;
            });
            root.Attach(firstNode);
            root.Attach(layoutMarker);
            // Null keys cannot be registered; expose the damaged node through the
            // existing controlled Nodes source without changing live registration.
            root.SourceNodes = [root, firstNode, nullNode];
            string description = m_engine.CreateSnapshot(tree, state).TreeDescription;
            MasterSatelliteInvariantResult invariant;
            recording = true;
            try
            {
                invariant = m_engine.ValidateInvariant(tree, state, settings);
            }
            finally
            {
                recording = false;
            }

            CollectionAssert.AreEqual(
                new[] { "nodes:count", "nodes:copy", "hash:first", "equals:first:master" },
                calls);
            CollectionAssert.AreEqual(
                new[]
                {
                    "The tree has 2 windows; runtime state expects 1.",
                    "A master-only tree must contain exactly the master as the root's sole child.",
                },
                invariant.Violations.ToArray());
            Assert.AreEqual(description, invariant.TreeDescription);
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(tree, root.Desktop);
            Assert.IsNull(root.Parent);
            Assert.AreEqual(2, root.Children.Count);
            Assert.AreSame(firstNode, root.Children[0]);
            Assert.AreSame(layoutMarker, root.Children[1]);
            Assert.AreSame(root, firstNode.Parent);
            Assert.AreSame(tree, firstNode.Desktop);
            Assert.AreSame(root, layoutMarker.Parent);
            Assert.AreSame(tree, layoutMarker.Desktop);
            Assert.AreSame(window, firstNode.WindowReference);
            Assert.AreSame(firstNode, tree.FindNode(window));
            Assert.IsNull(nullNode.WindowReference);
            Assert.IsNull(nullNode.Parent);
            Assert.IsNull(nullNode.Desktop);
            CollectionAssert.AreEqual(root.SourceNodes, root.CopiedArray);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(before.Revision, state.Revision);
            Assert.AreEqual(before.IsActive, state.IsActive);
            Assert.AreEqual(before.IsRecovering, state.IsRecovering);
            Assert.AreEqual(before.MasterSide, state.MasterSide);
            Assert.AreEqual(before.RequestedMasterRatio, state.RequestedMasterRatio);
            Assert.AreEqual(before.EffectiveMasterRatio, state.EffectiveMasterRatio);
            Assert.AreEqual(before.SatelliteOrientation, state.SatelliteOrientation);
            Assert.AreEqual(description, m_engine.CreateSnapshot(tree, state).TreeDescription);
        }

        [TestMethod]
        public void ValidateInvariantPropagatesLateMasterEqualityExceptionWithoutShortCircuit()
        {
            var (tree, state, settings, windows) = BuildMasterCountProbe(4);
            var expected = new InvalidOperationException("late master equality failure");
            var calls = new List<int>();
            for (int index = 0; index < windows.Length; index++)
            {
                int capturedIndex = index;
                Mock.Get(windows[index])
                    .Setup(window => window.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow _) =>
                    {
                        calls.Add(capturedIndex);
                        if (capturedIndex == 2)
                        {
                            throw expected;
                        }
                        return capturedIndex < 2;
                    });
            }

            var actual = Assert.ThrowsException<InvalidOperationException>(
                () => m_engine.ValidateInvariant(tree, state, settings));

            Assert.AreSame(expected, actual);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, calls);
        }

        [TestMethod]
        public void InvariantResultOwnsOrderedViolationSnapshotForEmptyAndNonEmptyLists()
        {
            var violations = new List<string> { "first", "duplicate", "duplicate", "last" };
            var result = new MasterSatelliteInvariantResult(violations, "captured-tree");
            var emptyViolations = new List<string>();
            var emptyResult = new MasterSatelliteInvariantResult(emptyViolations, "empty-tree");
            var secondEmptyResult = new MasterSatelliteInvariantResult(new List<string>(), "empty-tree");

            violations[0] = "mutated";
            violations.RemoveAt(1);
            violations.Add("added");
            emptyViolations.Add("late");

            CollectionAssert.AreEqual(
                new[] { "first", "duplicate", "duplicate", "last" },
                result.Violations.ToArray());
            Assert.AreEqual(
                string.Join(Environment.NewLine, "first", "duplicate", "duplicate", "last"),
                result.Description);
            Assert.AreEqual("captured-tree", result.TreeDescription);
            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(0, emptyResult.Violations.Count);
            Assert.AreEqual("empty-tree", emptyResult.TreeDescription);
            Assert.IsTrue(emptyResult.IsValid);
            Assert.AreNotSame(emptyResult.Violations, secondEmptyResult.Violations);
            Assert.AreNotEqual(emptyResult, secondEmptyResult);
            Assert.ThrowsException<NotSupportedException>(
                () => ((IList<string>)result.Violations).Add("forbidden"));
            Assert.ThrowsException<NotSupportedException>(
                () => ((IList<string>)result.Violations)[0] = "forbidden");
        }

        [TestMethod]
        public void InvariantResultPreservesCustomEnumerationOrderDisposalAndException()
        {
            var successful = new RecordingViolationEnumerable(["one", "two"]);

            var result = new MasterSatelliteInvariantResult(successful, "tree");

            CollectionAssert.AreEqual(new[] { "one", "two" }, result.Violations.ToArray());
            CollectionAssert.AreEqual(
                new[] { "get-enumerator", "yield:one", "yield:two", "end", "dispose" },
                successful.Events);

            var expected = new InvalidOperationException("late violation enumeration failure");
            var throwing = new RecordingViolationEnumerable(["one", "two", "three"], 2, expected);

            var actual = Assert.ThrowsException<InvalidOperationException>(
                () => new MasterSatelliteInvariantResult(throwing, "tree"));

            Assert.AreSame(expected, actual);
            CollectionAssert.AreEqual(
                new[] { "get-enumerator", "yield:one", "yield:two", "throw:three", "dispose" },
                throwing.Events);
        }

        [TestMethod]
        public void InvariantResultPreservesDerivedListCollectionCallbacks()
        {
            var violations = new RecordingViolationList { "one", "two" };

            var result = new MasterSatelliteInvariantResult(violations, "tree");

            CollectionAssert.AreEqual(new[] { "one", "two" }, result.Violations.ToArray());
            CollectionAssert.AreEqual(new[] { "count", "copy" }, violations.Events);
        }

        [TestMethod]
        public void NormalizeRepairsSupportedCanonicalDamageAndPreservesWindows()
        {
            var (tree, state, settings, windows) = Build(4);
            var originalMaster = state.Master;
            var originalSatelliteOrder = state.Satellites.ToArray();
            AssertRoot(tree).Orientation = PanelOrientation.Vertical;

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsTrue(operation.Changed);
            Assert.AreEqual(PanelOrientation.Horizontal, AssertRoot(tree).Orientation);
            Assert.AreSame(originalMaster, state.Master);
            CollectionAssert.AreEqual(originalSatelliteOrder, state.Satellites.ToArray());
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
            Assert.IsFalse(state.IsRecovering);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void NormalizeLogsBeforeAndAfterTreeSnapshots()
        {
            var (tree, state, settings, _) = Build(2);
            AssertRoot(tree).Orientation = PanelOrientation.Vertical;
            var messages = new List<string>();
            var engine = new MasterSatelliteLayoutEngine(messages.Add);

            var operation = engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(1, messages.Count);
            StringAssert.Contains(messages[0], "before:");
            StringAssert.Contains(messages[0], "after:");
            StringAssert.Contains(messages[0], nameof(PanelOrientation.Vertical));
            StringAssert.Contains(messages[0], nameof(PanelOrientation.Horizontal));
        }

        [TestMethod]
        public void NormalizeRepairsNestedPanelWithoutLosingWindows()
        {
            var (tree, state, settings, windows) = Build(3);
            var satellitePanel = AssertSatellitePanel(tree);
            var first = satellitePanel.Children[0];
            satellitePanel.Detach(first);
            var nested = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            satellitePanel.Attach(0, nested);
            nested.Attach(first);

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void NormalizeRestoresPanelCollapsedByGenericCleanup()
        {
            var (tree, state, settings, windows) = Build(2);
            var panel = AssertSatellitePanel(tree);
            panel.CollapseIfSingle();
            Assert.IsFalse(m_engine.ValidateInvariant(tree, state, settings).IsValid);

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(1, AssertSatellitePanel(tree).Children.Count);
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void NormalizeRestoresRuntimeSatelliteOrderAfterVisualMove()
        {
            var (tree, state, settings, windows) = Build(4);
            AssertSatellitePanel(tree).Move(0, 2);
            Assert.IsFalse(m_engine.ValidateInvariant(tree, state, settings).IsValid);

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            CollectionAssert.AreEqual(
                windows.Skip(1).ToArray(),
                AssertSatellitePanel(tree).Children.Cast<WindowNode>().Select(x => x.WindowReference).ToArray());
        }

        [TestMethod]
        public void RecoveryFailureDisablesOnlyStateAndDoesNotRemoveWindows()
        {
            var settings = CreateSettings();
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var state = m_engine.CreateState(settings, true);
            var windows = new[]
            {
                m_windows.Create("A", minimumWidth: 400, minimumHeight: 10),
                m_windows.Create("B", minimumWidth: 400, minimumHeight: 10),
            };
            var build = m_engine.BuildLayout(tree, state, settings, windows);
            Assert.IsTrue(build.Succeeded, build.Message);
            var originalRoot = tree.Root;
            var revision = state.Revision;
            tree.WorkArea = Rectangle.OffsetAndSize(0, 0, 700, 600);

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsFalse(operation.Succeeded);
            Assert.IsTrue(operation.Changed);
            Assert.AreEqual(MasterSatelliteFailureReason.RecoveryFailed, operation.FailureReason);
            Assert.IsFalse(state.IsActive);
            Assert.IsFalse(state.IsRecovering);
            Assert.AreEqual(revision + 1, state.Revision);
            Assert.AreSame(originalRoot, tree.Root);
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
        }

        [TestMethod]
        public void RecoveryCapacityFailureDoesNotDeleteOverflowingTreeWindow()
        {
            var (tree, state, settings, windows) = Build(4);
            var overflow = m_windows.Create("E");
            AssertSatellitePanel(tree).Attach(new WindowNode(overflow));
            var allWindows = windows.Append(overflow).ToArray();
            var originalRoot = tree.Root;

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.RecoveryFailed, operation.FailureReason);
            Assert.IsFalse(state.IsActive);
            Assert.AreSame(originalRoot, tree.Root);
            CollectionAssert.AreEquivalent(allWindows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
        }

        [TestMethod]
        public void RemovingToOneSatelliteKeepsCanonicalPanel()
        {
            var (tree, state, settings, windows) = Build(3);

            var operation = m_engine.RemoveWindow(tree, state, settings, windows[2]);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(1, state.Satellites.Count);
            Assert.AreEqual(1, AssertSatellitePanel(tree).Children.Count);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void SnapshotCopiesSatelliteListInsteadOfExposingMutableRuntimeState()
        {
            var (tree, state, _, windows) = Build(3);
            var snapshot = m_engine.CreateSnapshot(tree, state);

            state.MutableSatellites.Reverse();

            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), snapshot.Satellites.ToArray());
        }

#if !DEBUG
        [TestMethod]
        public void ReleaseMutationRecoversSupportedDamageBeforeContinuing()
        {
            var (tree, state, settings, _) = Build(3);
            AssertRoot(tree).Orientation = PanelOrientation.Vertical;

            var operation = m_engine.SetMasterSide(tree, state, settings, MasterSide.Right);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(MasterSide.Right, state.MasterSide);
            Assert.AreEqual(PanelOrientation.Horizontal, AssertRoot(tree).Orientation);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }
#endif

        private (DesktopTree Tree, MasterSatelliteRuntimeState State, MasterSatelliteLayoutSettings Settings,
            IWindow[] Windows) Build(int count)
        {
            var settings = CreateSettings();
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var state = m_engine.CreateState(settings, true);
            var windows = Enumerable.Range(0, count)
                .Select(x => m_windows.Create(((char)('A' + x)).ToString()))
                .ToArray();
            var operation = m_engine.BuildLayout(tree, state, settings, windows);
            Assert.IsTrue(operation.Succeeded, operation.Message);
            return (tree, state, settings, windows);
        }

        private void AssertSatelliteMasterScanIdentity(
            DesktopTree tree,
            SplitPanelNode root,
            MasterSatelliteRuntimeState state,
            MasterSatelliteRuntimeState before,
            IWindow expectedMaster,
            IWindow[] expectedSatellites,
            string description)
        {
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(tree, root.Desktop);
            Assert.IsNull(root.Parent);
            Assert.AreEqual(PanelOrientation.Horizontal, root.Orientation);
            Assert.AreEqual(0, root.Children.Count);
            if (expectedMaster == null)
            {
                Assert.IsNull(state.Master);
            }
            else
            {
                Assert.AreSame(expectedMaster, state.Master);
                Assert.IsNull(tree.FindNode(expectedMaster));
            }
            Assert.AreEqual(expectedSatellites.Length, state.Satellites.Count);
            for (int index = 0; index < expectedSatellites.Length; index++)
            {
                if (expectedSatellites[index] == null)
                {
                    Assert.IsNull(state.Satellites[index]);
                }
                else
                {
                    Assert.AreSame(expectedSatellites[index], state.Satellites[index]);
                    Assert.IsNull(tree.FindNode(expectedSatellites[index]));
                }
            }
            Assert.AreEqual(before.Revision, state.Revision);
            Assert.AreEqual(before.IsActive, state.IsActive);
            Assert.AreEqual(before.IsRecovering, state.IsRecovering);
            Assert.AreEqual(before.MasterSide, state.MasterSide);
            Assert.AreEqual(before.RequestedMasterRatio, state.RequestedMasterRatio);
            Assert.AreEqual(before.EffectiveMasterRatio, state.EffectiveMasterRatio);
            Assert.AreEqual(before.SatelliteOrientation, state.SatelliteOrientation);
            Assert.AreEqual(description, m_engine.CreateSnapshot(tree, state).TreeDescription);
        }

        private (DesktopTree Tree, MasterSatelliteRuntimeState State, MasterSatelliteLayoutSettings Settings,
            IWindow[] Windows) BuildMasterCountProbe(int windowCount)
        {
            var settings = CreateSettings();
            var root = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
                Root = root,
            };
            var state = m_engine.CreateState(settings, true);
            state.Master = m_windows.Create("master-sentinel");
            var windows = Enumerable.Range(0, windowCount)
                .Select(index => m_windows.Create($"probe-{index}"))
                .ToArray();
            foreach (var window in windows)
            {
                root.Attach(new WindowNode(window));
            }
            return (tree, state, settings, windows);
        }

        private (DesktopTree Tree, MasterSatelliteRuntimeState State,
            MasterSatelliteLayoutSettings Settings) BuildAllocationProbe(int windowCount)
        {
            var settings = CreateSettings();
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var state = m_engine.CreateState(settings, true);
            var windows = Enumerable.Range(0, windowCount)
                .Select(index => (IWindow)new InvariantAllocationWindow(index + 1))
                .ToArray();
            var operation = m_engine.BuildLayout(tree, state, settings, windows);
            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
            return (tree, state, settings);
        }

        private static MasterSatelliteLayoutSettings CreateSettings()
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.60,
                DefaultMasterSide = MasterSide.Left,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                MaxSatellites = 3,
            };
        }

        private static SplitPanelNode AssertRoot(DesktopTree tree)
        {
            Assert.IsInstanceOfType(tree.Root, typeof(SplitPanelNode));
            return (SplitPanelNode)tree.Root!;
        }

        private static SplitPanelNode AssertSatellitePanel(DesktopTree tree)
        {
            var panel = AssertRoot(tree).Children.OfType<SplitPanelNode>().SingleOrDefault();
            Assert.IsNotNull(panel);
            return panel;
        }

        private sealed class DerivedInvariantWindowNode(IWindow window) : WindowNode(window)
        {
        }

        private sealed class SnapshotCapturingPanel(List<string> calls) : SplitPanelNode, ICollection<TilingNode>
        {
            public TilingNode[] SourceNodes { get; set; } = [];
            public TilingNode[] CopiedArray { get; private set; } = [];
            public int CopiedOffset { get; private set; }
            public override IEnumerable<TilingNode> Nodes => this;

            int ICollection<TilingNode>.Count
            {
                get
                {
                    calls.Add("nodes:count");
                    return SourceNodes.Length;
                }
            }

            bool ICollection<TilingNode>.IsReadOnly => true;

            void ICollection<TilingNode>.CopyTo(TilingNode[] array, int arrayIndex)
            {
                calls.Add("nodes:copy");
                SourceNodes.CopyTo(array, arrayIndex);
                CopiedArray = array;
                CopiedOffset = arrayIndex;
            }

            IEnumerator<TilingNode> IEnumerable<TilingNode>.GetEnumerator()
            {
                calls.Add("nodes:enumerate");
                return ((IEnumerable<TilingNode>)SourceNodes).GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<TilingNode>)this).GetEnumerator();
            bool ICollection<TilingNode>.Contains(TilingNode item)
                => Array.Exists(SourceNodes, node => ReferenceEquals(node, item));
            void ICollection<TilingNode>.Add(TilingNode item) => throw new NotSupportedException();
            void ICollection<TilingNode>.Clear() => throw new NotSupportedException();
            bool ICollection<TilingNode>.Remove(TilingNode item) => throw new NotSupportedException();
        }

        private sealed class EqualityRecordingPanel : SplitPanelNode
        {
            public int EqualityCallCount { get; private set; }
            public int HashCallCount { get; private set; }

            public override bool Equals(object obj)
            {
                EqualityCallCount++;
                return ReferenceEquals(this, obj);
            }

            public override int GetHashCode()
            {
                HashCallCount++;
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
            }
        }

        private sealed class RecordingViolationEnumerable : IEnumerable<string>
        {
            private readonly IReadOnlyList<string> m_values;
            private readonly int? m_throwIndex;
            private readonly Exception m_exception;

            public RecordingViolationEnumerable(
                IReadOnlyList<string> values,
                int? throwIndex = null,
                Exception exception = null)
            {
                m_values = values;
                m_throwIndex = throwIndex;
                m_exception = exception;
            }

            public List<string> Events { get; } = [];

            public IEnumerator<string> GetEnumerator()
            {
                Events.Add("get-enumerator");
                return Enumerate().GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            private IEnumerable<string> Enumerate()
            {
                try
                {
                    for (int index = 0; index < m_values.Count; index++)
                    {
                        if (index == m_throwIndex)
                        {
                            Events.Add($"throw:{m_values[index]}");
                            throw m_exception!;
                        }

                        Events.Add($"yield:{m_values[index]}");
                        yield return m_values[index];
                    }
                    Events.Add("end");
                }
                finally
                {
                    Events.Add("dispose");
                }
            }
        }

        private sealed class RecordingViolationList : List<string>, ICollection<string>
        {
            public List<string> Events { get; } = [];

            int ICollection<string>.Count
            {
                get
                {
                    Events.Add("count");
                    return Count;
                }
            }

            void ICollection<string>.CopyTo(string[] array, int arrayIndex)
            {
                Events.Add("copy");
                CopyTo(array, arrayIndex);
            }
        }

        private sealed class InvariantAllocationWindow : IWindow
        {
            private readonly int m_identity;

            public InvariantAllocationWindow(int identity)
            {
                m_identity = identity;
            }

            public object SyncRoot { get; } = new();
            public IWorkspace Workspace => throw UnsupportedOperation();
            public string Title => $"Invariant allocation window {m_identity}";
            public Rectangle Position => Rectangle.OffsetAndSize(0, 0, 640, 480);
            public WindowState State => WindowState.Restored;
            public Point? MinSize => new Point(0, 0);
            public Point? MaxSize => null;
            public Rectangle FrameMargins => new();
            public bool CanResize => true;
            public bool CanMove => true;
            public bool CanReorder => true;
            public bool CanMinimize => true;
            public bool CanMaximize => true;
            public bool CanClose => true;
            public bool IsTopmost => false;
            public bool IsFocused => false;
            public bool IsAlive => true;
            public IntPtr Handle => new(m_identity);
            public event EventHandler<WindowPositionChangedEventArgs> PositionChangeStart { add { } remove { } }
            public event EventHandler<WindowPositionChangedEventArgs> PositionChangeEnd { add { } remove { } }
            public event EventHandler<WindowPositionChangedEventArgs> PositionChanged { add { } remove { } }
            public event EventHandler<WindowStateChangedEventArgs> StateChanged { add { } remove { } }
            public event EventHandler<WindowTopmostChangedEventArgs> TopmostChanged { add { } remove { } }
            public event EventHandler<WindowFocusChangedEventArgs> GotFocus { add { } remove { } }
            public event EventHandler<WindowFocusChangedEventArgs> LostFocus { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs> Added { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs> Removed { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs> Destroyed { add { } remove { } }
            public event EventHandler<WindowTitleChangedEventArgs> TitleChanged { add { } remove { } }
            public bool Equals(IWindow other) => ReferenceEquals(this, other);
            public override bool Equals(object obj) => ReferenceEquals(this, obj);
            public override int GetHashCode() => m_identity;
            public System.Diagnostics.Process GetProcess() => throw UnsupportedOperation();
            public IWindow GetPreviousWindow() => throw UnsupportedOperation();
            public IWindow GetNextWindow() => throw UnsupportedOperation();
            public void Close() => throw UnsupportedOperation();
            public void SetPosition(Rectangle newLocation) => throw UnsupportedOperation();
            public void SetState(WindowState state) => throw UnsupportedOperation();
            public void SetTopmost(bool topmost) => throw UnsupportedOperation();
            public void InsertAfter(IWindow other) => throw UnsupportedOperation();
            public void SendToBack() => throw UnsupportedOperation();
            public void BringToFront() => throw UnsupportedOperation();
            public bool RequestFocus() => throw UnsupportedOperation();

            private static NotSupportedException UnsupportedOperation()
                => new("The invariant allocation fixture must not perform native or desktop mutations.");
        }
    }
}
