#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using FancyWM.Layouts.Tiling;
using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class TilingWindowLifetimeTest
    {
        [TestMethod]
        public void ReplacingNodesKeepsOneCursorSubscriptionAndDisposeRemovesIt()
        {
            var workspace = new Mock<IWorkspace>();
            int subscriptions = 0;
            workspace.SetupAdd(value => value.CursorLocationChanged += Moq.It.IsAny<System.EventHandler<CursorLocationChangedEventArgs>>())
                .Callback(() => subscriptions++);
            workspace.SetupRemove(value => value.CursorLocationChanged -= Moq.It.IsAny<System.EventHandler<CursorLocationChangedEventArgs>>())
                .Callback(() => subscriptions--);
            using var model = new TilingWindowViewModel();
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var window = new Mock<IWindow>();
                window.SetupGet(value => value.Workspace).Returns(workspace.Object);
                model.Node = new WindowNode(window.Object);
                Assert.AreEqual(1, subscriptions);
            }
            model.Dispose();
            model.Dispose();
            Assert.AreEqual(0, subscriptions);
        }

        [DataTestMethod]
        [DataRow("icon-removed")]
        [DataRow("window-removed")]
        [DataRow("position-start")]
        [DataRow("position-end")]
        [DataRow("title")]
        [DataRow("cursor")]
        public void DisposeRemovalFailureStillReleasesEveryOwnedHandler(string failedOwner)
        {
            using var fixture = new SubscriptionFixture();
            var primary = new InvalidOperationException($"controlled {failedOwner} removal failure");
            var later = new ApplicationException("controlled later title removal failure");
            var last = new ApplicationException("controlled last cursor removal failure");
            fixture.Failures.Add(failedOwner, primary);
            if (failedOwner == "icon-removed")
            {
                fixture.Failures.Add("title", later);
                fixture.Failures.Add("cursor", last);
            }

            var actual = Assert.ThrowsException<InvalidOperationException>(fixture.Model.Dispose);
            Assert.AreSame(primary, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowRemovalFailure));
            int remainingAfterFirstDispose = fixture.OwnedCount;
            bool fieldsClearedAfterFirstDispose = fixture.FieldsReleased;
            fixture.AssertExternalSubscribersPreserved();
            // Repetition must not be the only way to reach a skipped sibling.
            fixture.Failures.Clear();
            fixture.Model.Dispose();
            fixture.Model.Dispose();

            Assert.AreEqual(0, remainingAfterFirstDispose, "The first Dispose must drain all sibling subscriptions after a committed removal throws.");
            Assert.IsTrue(fieldsClearedAfterFirstDispose, "Dispose must clear public Node, cached node/workspace and commands even when unsubscribe throws.");
            Assert.AreEqual(6, fixture.OwnedRemoveAttempts, "The first Dispose must attempt every owned removal exactly once.");
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            Assert.AreEqual(6, fixture.OwnedAdds, "Repeated Dispose must not acquire handlers.");
            foreach (string owner in SubscriptionFixture.Owners)
            {
                Assert.AreEqual(1, fixture.Releases.GetValueOrDefault(owner), $"Each acquired {owner} handler must be physically removed once.");
            }
            if (failedOwner == "icon-removed")
            {
                var recorded = actual.Data.Values.OfType<AggregateException>()
                    .SelectMany(error => error.InnerExceptions).ToArray();
                CollectionAssert.AreEqual(new Exception[] { later, last }, recorded,
                    "Later cleanup errors must remain attached to the original base-icon failure in release order.");
            }
            fixture.AssertExternalSubscribersPreserved();
        }

        [TestMethod]
        public void DisposedModelCannotReacquireSubscriptionsThroughNodeAssignment()
        {
            using var fixture = new SubscriptionFixture();
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            int adds = fixture.OwnedAdds;
            for (int cycle = 0; cycle < 3; cycle++)
            {
                try { fixture.Model.Node = new WindowNode(fixture.Window.Object); }
                catch (ObjectDisposedException) { }
                Assert.AreEqual(adds, fixture.OwnedAdds, "A disposed model must not reacquire cursor or window handlers.");
                Assert.AreEqual(0, fixture.OwnedCount);
                Assert.IsTrue(fixture.FieldsReleased);
                fixture.Model.Dispose();
            }
            fixture.AssertExternalSubscribersPreserved();
        }

        [TestMethod]
        public void CapturedCallbacksCannotMutateDisposedModelOrTouchWindowSubscriptions()
        {
            using var fixture = new SubscriptionFixture();
            var callbacks = fixture.CaptureOwnedCallbacks();
            fixture.Invoke(callbacks.Single(callback => callback.Group == "position-start"));
            Assert.IsTrue(fixture.ReadField<bool>("m_isMoving"));
            fixture.Model.Title = "disposed title";
            fixture.Model.Dispose();
            fixture.AssertExternalSubscribersPreserved();
            int removals = fixture.OwnedRemoveAttempts;
            bool moving = fixture.ReadField<bool>("m_isMoving");
            foreach (var callback in callbacks)
            {
                fixture.Invoke(callback);
                Assert.AreEqual("disposed title", fixture.Model.Title, $"Late {callback.Group} callback changed Title.");
                Assert.AreEqual(moving, fixture.ReadField<bool>("m_isMoving"), $"Late {callback.Group} callback changed movement state.");
                Assert.AreEqual(removals, fixture.OwnedRemoveAttempts, $"Late {callback.Group} callback touched a disposed window.");
                Assert.AreEqual(0, fixture.OwnedCount);
                Assert.IsTrue(fixture.FieldsReleased);
            }
            fixture.AssertExternalSubscribersPreserved();
        }

        [DataTestMethod]
        [DataRow("icon-removed")]
        [DataRow("window-removed")]
        [DataRow("position-start")]
        [DataRow("position-end")]
        [DataRow("title")]
        [DataRow("cursor")]
        public void ReplacingNodeRemovalFailureDetachesAllOwnedHandlers(string failedOwner)
        {
            using var fixture = new SubscriptionFixture();
            var oldCallbacks = fixture.CaptureOwnedCallbacks();
            var replacement = fixture.CreateWindow();
            var primary = new InvalidOperationException($"controlled old {failedOwner} removal failure");
            fixture.Failures.Add(failedOwner, primary);

            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Model.Node = new WindowNode(replacement.Object));

            Assert.AreSame(primary, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowRemovalFailure));
            AssertFailedReplacementDetaches(fixture, oldCallbacks);
        }

        [DataTestMethod]
        [DataRow("icon-removed", false)]
        [DataRow("icon-removed", true)]
        [DataRow("window-removed", false)]
        [DataRow("window-removed", true)]
        [DataRow("position-start", false)]
        [DataRow("position-start", true)]
        [DataRow("position-end", false)]
        [DataRow("position-end", true)]
        [DataRow("title", false)]
        [DataRow("title", true)]
        [DataRow("cursor", false)]
        [DataRow("cursor", true)]
        public void ReplacingNodeAcquisitionFailureDetachesOldAndPartialNewHandlers(string failedOwner, bool afterEffect)
        {
            using var fixture = new SubscriptionFixture();
            var oldCallbacks = fixture.CaptureOwnedCallbacks();
            var replacement = fixture.CreateWindow();
            object source = failedOwner == "cursor" ? replacement.Object.Workspace : replacement.Object;
            var primary = new InvalidOperationException($"controlled new {failedOwner} add failure afterEffect={afterEffect}");
            fixture.AddFailures.Add((source, failedOwner), (afterEffect, primary));

            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Model.Node = new WindowNode(replacement.Object));

            Assert.AreSame(primary, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowAcquisitionFailure));
            AssertFailedReplacementDetaches(fixture, oldCallbacks);
        }

        [TestMethod]
        public void ReplacingNodeCannotAcquireAfterDisposeInsideCursorAccessor()
        {
            using var fixture = new SubscriptionFixture();
            var oldCallbacks = fixture.CaptureOwnedCallbacks();
            var replacement = fixture.CreateWindow();
            int addsAtDispose = -1;
            fixture.AfterOwnedAdd = (source, owner) =>
            {
                if (!ReferenceEquals(source, replacement.Object.Workspace) || owner != "cursor") { return; }
                fixture.AfterOwnedAdd = null;
                fixture.Model.Dispose();
                addsAtDispose = fixture.OwnedAdds;
            };

            fixture.Model.Node = new WindowNode(replacement.Object);

            Assert.IsTrue(addsAtDispose >= 0, "The synchronous event accessor must actually run Dispose.");
            Assert.AreEqual(addsAtDispose, fixture.OwnedAdds, "An outer Node setter must not acquire handlers after a reentrant Dispose.");
            AssertFailedReplacementDetaches(fixture, oldCallbacks);
        }

        [TestMethod]
        public void ReplacingNodeInSameWorkspaceKeepsCursorAndOnlyNewWindowCallbacksActive()
        {
            using var fixture = new SubscriptionFixture();
            for (int cycle = 0; cycle < 3; cycle++)
            {
                var oldCallbacks = fixture.CaptureOwnedCallbacks().Where(callback => callback.Group != "cursor").ToArray();
                var replacement = fixture.CreateWindow(fixture.Workspace);
                var node = new WindowNode(replacement.Object);
                int adds = fixture.OwnedAdds;

                fixture.Model.Node = node;

                Assert.AreSame(node, fixture.Model.Node);
                Assert.AreSame(node, fixture.ReadField<WindowNode>("m_currentNode"));
                Assert.AreSame(fixture.Workspace.Object, fixture.ReadField<IWorkspace>("m_workspace"));
                Assert.AreEqual(adds + 5, fixture.OwnedAdds, "Same-workspace replacement acquires only the five window handlers.");
                var currentCallbacks = fixture.CaptureOwnedCallbacks();
                Assert.AreEqual(6, currentCallbacks.Length);
                Assert.AreEqual(1, currentCallbacks.Count(callback => callback.Group == "cursor"));
                Assert.AreEqual(5, currentCallbacks.Count(callback => ReferenceEquals(callback.Source, replacement.Object)));
                Assert.AreEqual(0, fixture.Releases.GetValueOrDefault("cursor"), "The existing cursor subscription must be retained.");
                fixture.Model.Title = "current window title";
                int removals = fixture.OwnedRemoveAttempts;
                foreach (var callback in oldCallbacks)
                {
                    fixture.Invoke(callback);
                    Assert.AreEqual("current window title", fixture.Model.Title);
                    Assert.IsFalse(fixture.ReadField<bool>("m_isMoving"));
                    Assert.IsFalse(fixture.IconRemoved, "An old icon callback must not invalidate the replacement icon.");
                    Assert.AreEqual(removals, fixture.OwnedRemoveAttempts);
                    Assert.AreEqual(6, fixture.OwnedCount);
                }
                fixture.Invoke(currentCallbacks.Single(callback => callback.Group == "title"));
                Assert.AreEqual("late title", fixture.Model.Title);
                fixture.Invoke(currentCallbacks.Single(callback => callback.Group == "position-start"));
                Assert.IsTrue(fixture.ReadField<bool>("m_isMoving"));
                fixture.Invoke(currentCallbacks.Single(callback => callback.Group == "position-end"));
                Assert.IsFalse(fixture.ReadField<bool>("m_isMoving"));
                fixture.AssertExternalSubscribersPreserved();
            }
            var finalCallbacks = fixture.CaptureOwnedCallbacks();
            fixture.Model.IsActionActive = true;
            fixture.Invoke(finalCallbacks.Single(callback => callback.Group == "cursor"));
            Assert.AreEqual(System.Windows.Visibility.Visible, fixture.Model.ActionsVisibility);
            fixture.Invoke(finalCallbacks.Single(callback => callback.Group == "removed" && callback.Handler.Method.DeclaringType == typeof(TilingNodeViewModel)));
            Assert.IsTrue(fixture.IconRemoved);
            fixture.Invoke(finalCallbacks.Single(callback => callback.Group == "removed" && callback.Handler.Method.DeclaringType == typeof(TilingWindowViewModel)));
            Assert.AreEqual(1, fixture.OwnedCount, "The current Removed callback releases four window handlers and the cursor subscription.");
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.AssertExternalSubscribersPreserved();
        }

        [TestMethod]
        public void ReplacingNodeCompensatesCursorAddedAfterReentrantDispose()
        {
            using var fixture = new SubscriptionFixture();
            var oldCallbacks = fixture.CaptureOwnedCallbacks();
            var replacement = fixture.CreateWindow();
            int addsAtDispose = -1;
            fixture.BeforeOwnedAdd = (source, owner) =>
            {
                if (!ReferenceEquals(source, replacement.Object.Workspace) || owner != "cursor") { return; }
                fixture.BeforeOwnedAdd = null;
                fixture.Model.Dispose();
                Assert.AreEqual(0, fixture.OwnedCount, "Dispose runs before the accessor physically adds its cursor handler.");
                addsAtDispose = fixture.OwnedAdds;
            };

            fixture.Model.Node = new WindowNode(replacement.Object);

            Assert.IsTrue(addsAtDispose >= 0);
            Assert.AreEqual(addsAtDispose + 1, fixture.OwnedAdds, "Only the already-entered accessor may complete its add after Dispose.");
            AssertFailedReplacementDetaches(fixture, oldCallbacks);
        }

        [DataTestMethod]
        [DataRow("icon-removed")]
        [DataRow("cursor")]
        [DataRow("position-start")]
        public void ReplacingNodeTerminalAddFailurePreservesPrimaryAndCompensationError(string failedOwner)
        {
            using var fixture = new SubscriptionFixture();
            var oldCallbacks = fixture.CaptureOwnedCallbacks();
            var replacement = fixture.CreateWindow();
            object failedSource = failedOwner == "cursor" ? replacement.Object.Workspace : replacement.Object;
            var primary = new InvalidOperationException("controlled add failure after terminal reentry");
            var cleanup = new ApplicationException("controlled compensation removal failure");
            fixture.AddFailures.Add((failedSource, failedOwner), (true, primary));
            int addsAtDispose = -1;
            fixture.BeforeOwnedAdd = (source, owner) =>
            {
                if (!ReferenceEquals(source, failedSource) || owner != failedOwner) { return; }
                fixture.BeforeOwnedAdd = null;
                fixture.Model.Dispose();
                Assert.AreEqual(0, fixture.OwnedCount);
                addsAtDispose = fixture.OwnedAdds;
                fixture.Failures.Add(failedOwner, cleanup);
            };

            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Model.Node = new WindowNode(replacement.Object));

            Assert.IsTrue(addsAtDispose >= 0);
            Assert.AreEqual(addsAtDispose + 1, fixture.OwnedAdds);
            Assert.AreSame(primary, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowAcquisitionFailure));
            const string key = "TilingNodeViewModel.CleanupExceptions";
            Assert.IsInstanceOfType(actual.Data[key], typeof(AggregateException));
            var recorded = ((AggregateException)actual.Data[key]!).InnerExceptions;
            Assert.AreEqual(1, recorded.Count, "Compensation must report its error exactly once under the documented key.");
            Assert.AreSame(cleanup, recorded[0]);
            StringAssert.Contains(recorded[0].StackTrace ?? string.Empty, nameof(ThrowRemovalFailure));
            AssertFailedReplacementDetaches(fixture, oldCallbacks);
        }

        [TestMethod]
        public void CurrentWindowRemovedReleasesBindingAndAllowsFreshReplacement()
        {
            using var fixture = new SubscriptionFixture();
            var oldNode = fixture.Model.Node;
            var releasedCallbacks = fixture.CaptureOwnedCallbacks()
                .Where(callback => callback.Handler.Method.DeclaringType == typeof(TilingWindowViewModel)).ToArray();
            Assert.AreEqual(5, releasedCallbacks.Length);
            fixture.Model.Title = "removed window title";
            fixture.Model.IsActionActive = true;
            fixture.Model.ActionsVisibility = System.Windows.Visibility.Hidden;

            fixture.Invoke(releasedCallbacks.Single(callback => callback.Group == "removed"));

            Assert.AreSame(oldNode, fixture.Model.Node, "Window removal must preserve the public layout node for replacement.");
            Assert.IsNull(fixture.ReadField<WindowNode>("m_currentNode"));
            Assert.IsNull(fixture.ReadField<IWorkspace>("m_workspace"));
            Assert.AreEqual(1, fixture.OwnedCount, "Only the base icon owner remains until Node replacement or Dispose.");
            foreach (string owner in SubscriptionFixture.Owners.Where(owner => owner != "icon-removed"))
            {
                Assert.AreEqual(1, fixture.Releases.GetValueOrDefault(owner), $"Current Removed must release {owner} once.");
            }
            int removals = fixture.OwnedRemoveAttempts;
            bool moving = fixture.ReadField<bool>("m_isMoving");
            foreach (var callback in releasedCallbacks)
            {
                fixture.Invoke(callback);
                Assert.AreEqual("removed window title", fixture.Model.Title);
                Assert.AreEqual(moving, fixture.ReadField<bool>("m_isMoving"));
                Assert.AreEqual(System.Windows.Visibility.Hidden, fixture.Model.ActionsVisibility);
                Assert.AreEqual(removals, fixture.OwnedRemoveAttempts, $"Released {callback.Group} callback must be inert.");
                Assert.AreEqual(1, fixture.OwnedCount);
            }
            var replacement = fixture.CreateWindow();
            var replacementNode = new WindowNode(replacement.Object);
            fixture.Model.Node = replacementNode;
            Assert.AreSame(replacementNode, fixture.Model.Node);
            var currentCallbacks = fixture.CaptureOwnedCallbacks();
            Assert.AreEqual(6, currentCallbacks.Length);
            Assert.AreEqual(5, currentCallbacks.Count(callback => ReferenceEquals(callback.Source, replacement.Object)));
            Assert.AreEqual(1, currentCallbacks.Count(callback => ReferenceEquals(callback.Source, replacement.Object.Workspace)));
            fixture.Invoke(currentCallbacks.Single(callback => callback.Group == "title"));
            Assert.AreEqual("late title", fixture.Model.Title);
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.AssertExternalSubscribersPreserved();
        }

        [TestMethod]
        public void ReplacingNodeStopsAcquisitionAfterRemovedInsideWindowAccessor()
        {
            using var fixture = new SubscriptionFixture();
            var replacement = fixture.CreateWindow();
            var node = new WindowNode(replacement.Object);
            int addsAtRemoval = -1;
            fixture.AfterOwnedAdd = (source, owner) =>
            {
                if (!ReferenceEquals(source, replacement.Object) || owner != "window-removed") { return; }
                fixture.AfterOwnedAdd = null;
                var removed = fixture.CaptureOwnedCallbacks().Single(callback => ReferenceEquals(callback.Source, replacement.Object)
                    && callback.Group == "removed" && callback.Handler.Method.DeclaringType == typeof(TilingWindowViewModel));
                fixture.Invoke(removed);
                Assert.AreEqual(1, fixture.OwnedCount, "The synchronous Removed callback leaves only the base icon owner.");
                addsAtRemoval = fixture.OwnedAdds;
            };

            fixture.Model.Node = node;

            Assert.IsTrue(addsAtRemoval >= 0, "The accessor must actually invoke the newly added model Removed handler.");
            int addsAfterSetter = fixture.OwnedAdds;
            int ownersAfterSetter = fixture.OwnedCount;
            bool bindingCoherent = ReferenceEquals(node, fixture.Model.Node)
                && fixture.ReadField<WindowNode>("m_currentNode") == null
                && fixture.ReadField<IWorkspace>("m_workspace") == null;
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount,
                $"Dispose must release every acquired handler; after setter owners={ownersAfterSetter}, adds after Removed={addsAfterSetter - addsAtRemoval}.");
            Assert.AreEqual(addsAtRemoval, addsAfterSetter, "An outer setter must not acquire the remaining window handlers after synchronous Removed.");
            Assert.AreEqual(1, ownersAfterSetter, "Only the base icon owner may remain attached to the public Node before Dispose.");
            Assert.IsTrue(bindingCoherent);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            fixture.AssertExternalSubscribersPreserved();
        }

        [TestMethod]
        public void ReplacingNodeWithFreshNodeInsideAccessorFailsAndReleasesAllOwners()
        {
            using var fixture = new SubscriptionFixture();
            var replacement = fixture.CreateWindow();
            var reentrant = fixture.CreateWindow();
            bool entered = false;
            fixture.AfterOwnedAdd = (source, owner) =>
            {
                if (!ReferenceEquals(source, replacement.Object) || owner != "window-removed") { return; }
                fixture.AfterOwnedAdd = null;
                entered = true;
                fixture.Model.Node = new WindowNode(reentrant.Object);
            };

            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Model.Node = new WindowNode(replacement.Object));

            Assert.IsTrue(entered);
            StringAssert.Contains(actual.Message, "binding changed");
            StringAssert.Contains(actual.StackTrace ?? string.Empty, "TryAddWindowSubscription");
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.Model.Dispose();
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            fixture.AssertExternalSubscribersPreserved();
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void ReplacingNodeWithFreshNodeInsideBaseIconAddFailsAndReleasesAllOwners(bool afterEffect, bool sameWindow)
        {
            using var fixture = new SubscriptionFixture();
            var replacement = fixture.CreateWindow();
            var reentrant = sameWindow ? replacement : fixture.CreateWindow();
            bool entered = false;
            Action<object, string> replace = (source, owner) =>
            {
                if (!ReferenceEquals(source, replacement.Object) || owner != "icon-removed") { return; }
                fixture.BeforeOwnedAdd = null;
                fixture.AfterOwnedAdd = null;
                entered = true;
                fixture.Model.Node = new WindowNode(reentrant.Object);
            };
            if (afterEffect) { fixture.AfterOwnedAdd = replace; }
            else { fixture.BeforeOwnedAdd = replace; }

            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Model.Node = new WindowNode(replacement.Object));

            Assert.IsTrue(entered);
            Assert.AreEqual("The node binding changed while its icon subscription was being acquired.", actual.Message);
            Assert.IsFalse(actual.Data.Contains("TilingNodeViewModel.CleanupExceptions"));
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.Model.Dispose();
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            fixture.AssertExternalSubscribersPreserved();
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ReplacingNodeWithFreshNodeInsideBaseIconRemoveFailsAndReleasesAllOwners(bool afterEffect)
        {
            using var fixture = new SubscriptionFixture();
            var replacement = fixture.CreateWindow();
            var reentrant = fixture.CreateWindow();
            bool entered = false;
            Action<object, string> replace = (source, owner) =>
            {
                if (!ReferenceEquals(source, fixture.Window.Object) || owner != "icon-removed") { return; }
                fixture.BeforeOwnedRemove = null;
                fixture.AfterOwnedRemove = null;
                entered = true;
                fixture.Model.Node = new WindowNode(reentrant.Object);
            };
            if (afterEffect) { fixture.AfterOwnedRemove = replace; }
            else { fixture.BeforeOwnedRemove = replace; }

            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Model.Node = new WindowNode(replacement.Object));

            Assert.IsTrue(entered);
            Assert.AreEqual("The node binding changed while its icon subscription was being acquired.", actual.Message);
            Assert.IsFalse(actual.Data.Contains("TilingNodeViewModel.CleanupExceptions"));
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.Model.Dispose();
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            fixture.AssertExternalSubscribersPreserved();
        }

        [TestMethod]
        public void BaseIconAddFailureAfterFreshReentryPreservesPrimaryAndCleanupError()
        {
            using var fixture = new SubscriptionFixture();
            var replacement = fixture.CreateWindow();
            var reentrant = fixture.CreateWindow();
            var primary = new InvalidOperationException("controlled icon add failure after fresh replacement");
            var cleanup = new ApplicationException("controlled fresh icon cleanup failure");
            fixture.AddFailures.Add((replacement.Object, "icon-removed"), (true, primary));
            fixture.AfterOwnedAdd = (source, owner) =>
            {
                if (!ReferenceEquals(source, replacement.Object) || owner != "icon-removed") { return; }
                fixture.AfterOwnedAdd = null;
                fixture.Model.Node = new WindowNode(reentrant.Object);
                fixture.Failures.Add("icon-removed", cleanup);
            };

            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Model.Node = new WindowNode(replacement.Object));

            Assert.AreSame(primary, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowAcquisitionFailure));
            const string key = "TilingNodeViewModel.CleanupExceptions";
            Assert.IsInstanceOfType(actual.Data[key], typeof(AggregateException));
            var recorded = ((AggregateException)actual.Data[key]!).InnerExceptions;
            Assert.AreEqual(1, recorded.Count);
            Assert.AreSame(cleanup, recorded[0]);
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            fixture.AssertExternalSubscribersPreserved();
        }

        [TestMethod]
        public void PropertyChangedNodeReentryKeepsTheFreshBinding()
        {
            using var fixture = new SubscriptionFixture();
            var replacement = fixture.CreateWindow();
            var reentrant = fixture.CreateWindow();
            var reentrantNode = new WindowNode(reentrant.Object);
            bool entered = false;
            System.ComponentModel.PropertyChangedEventHandler? observer = null;
            observer = (_, args) =>
            {
                if (entered || args.PropertyName != nameof(TilingWindowViewModel.Node)) { return; }
                entered = true;
                fixture.Model.Node = reentrantNode;
            };
            fixture.Model.PropertyChanged += observer;

            fixture.Model.Node = new WindowNode(replacement.Object);
            fixture.Model.PropertyChanged -= observer;

            Assert.IsTrue(entered);
            Assert.AreSame(reentrantNode, fixture.Model.Node);
            Assert.AreSame(reentrantNode, fixture.ReadField<WindowNode>("m_currentNode"));
            Assert.AreSame(reentrant.Object.Workspace, fixture.ReadField<IWorkspace>("m_workspace"));
            Assert.AreEqual(6, fixture.OwnedCount);
            var callbacks = fixture.CaptureOwnedCallbacks();
            fixture.Invoke(callbacks.Single(callback => callback.Group == "title"));
            Assert.AreEqual("late title", fixture.Model.Title);
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.AssertExternalSubscribersPreserved();
        }

        [TestMethod]
        public void TilingWindowLifetimeCounterScenario()
        {
            string[] metrics = ["cycles", "primary-outcomes", "correct-operation-outcomes", "owned-adds", "owned-remove-attempts",
                "cursor-subscriptions-after-operation", "window-subscriptions-after-operation", "binding-references-after-operation",
                "cursor-subscriptions-after-dispose", "window-subscriptions-after-dispose", "models-needing-fixture-repair",
                "fixture-released-subscriptions", "subscriptions-after-fixture-repair"];
            foreach (string scenario in new[] { "normal", "removal-failure", "replacement-failure", "removed-reentry" })
            {
                var counts = metrics.ToDictionary(metric => metric, _ => 0L);
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    using var fixture = new SubscriptionFixture();
                    var primary = new InvalidOperationException("controlled lifetime counter failure");
                    bool correctOutcome = false;
                    if (scenario == "removal-failure")
                    {
                        fixture.Failures.Add("position-start", primary);
                        var actual = Assert.ThrowsException<InvalidOperationException>(fixture.Model.Dispose);
                        Assert.AreSame(primary, actual);
                        counts["primary-outcomes"]++;
                        correctOutcome = fixture.BindingReleased && fixture.OwnedCount == 0;
                    }
                    else
                    {
                        var replacement = fixture.CreateWindow(scenario == "normal" ? fixture.Workspace : null);
                        var node = new WindowNode(replacement.Object);
                        int addsAtRemoval = -1;
                        if (scenario == "replacement-failure")
                        {
                            fixture.AddFailures.Add((replacement.Object, "position-start"), (true, primary));
                            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Model.Node = node);
                            Assert.AreSame(primary, actual);
                            counts["primary-outcomes"]++;
                            correctOutcome = fixture.BindingReleased && fixture.OwnedCount == 0;
                        }
                        else
                        {
                            if (scenario == "removed-reentry")
                            {
                                fixture.AfterOwnedAdd = (source, owner) =>
                                {
                                    if (!ReferenceEquals(source, replacement.Object) || owner != "window-removed") { return; }
                                    fixture.AfterOwnedAdd = null;
                                    var removed = fixture.CaptureOwnedCallbacks().Single(callback => ReferenceEquals(callback.Source, replacement.Object)
                                        && callback.Group == "removed" && callback.Handler.Method.DeclaringType == typeof(TilingWindowViewModel));
                                    fixture.Invoke(removed);
                                    addsAtRemoval = fixture.OwnedAdds;
                                };
                            }
                            fixture.Model.Node = node;
                            if (scenario == "normal")
                            {
                                var current = fixture.CaptureOwnedCallbacks();
                                fixture.Invoke(current.Single(callback => callback.Group == "title"));
                                correctOutcome = ReferenceEquals(fixture.Model.Node, node)
                                    && ReferenceEquals(fixture.ReadField<WindowNode>("m_currentNode"), node)
                                    && ReferenceEquals(fixture.ReadField<IWorkspace>("m_workspace"), fixture.Workspace.Object)
                                    && current.Count(callback => callback.Group == "cursor") == 1
                                    && current.Count(callback => ReferenceEquals(callback.Source, replacement.Object)) == 5
                                    && fixture.Model.Title == "late title";
                            }
                            else
                            {
                                Assert.IsTrue(addsAtRemoval >= 0, "The counter must exercise synchronous Removed inside the real add accessor.");
                                correctOutcome = ReferenceEquals(fixture.Model.Node, node)
                                    && fixture.ReadField<WindowNode>("m_currentNode") == null
                                    && fixture.ReadField<IWorkspace>("m_workspace") == null
                                    && fixture.OwnedCount == 1 && fixture.OwnedAdds == addsAtRemoval;
                            }
                        }
                    }
                    counts["cycles"]++;
                    if (correctOutcome) { counts["correct-operation-outcomes"]++; }
                    var afterOperation = fixture.CaptureOwnedCallbacks();
                    counts["cursor-subscriptions-after-operation"] += afterOperation.Count(callback => callback.Group == "cursor");
                    counts["window-subscriptions-after-operation"] += afterOperation.Count(callback => callback.Group != "cursor");
                    counts["binding-references-after-operation"] += (fixture.Model.Node == null ? 0 : 1)
                        + (fixture.ReadField<WindowNode>("m_currentNode") == null ? 0 : 1)
                        + (fixture.ReadField<IWorkspace>("m_workspace") == null ? 0 : 1);
                    fixture.Model.Dispose();
                    fixture.Model.Dispose();
                    var afterDispose = fixture.CaptureOwnedCallbacks();
                    counts["owned-adds"] += fixture.OwnedAdds;
                    counts["owned-remove-attempts"] += fixture.OwnedRemoveAttempts;
                    counts["cursor-subscriptions-after-dispose"] += afterDispose.Count(callback => callback.Group == "cursor");
                    counts["window-subscriptions-after-dispose"] += afterDispose.Count(callback => callback.Group != "cursor");
                    if (afterDispose.Length != 0) { counts["models-needing-fixture-repair"]++; }
                    fixture.AssertExternalSubscribersPreserved();
                    // Sample production first. This explicit registry repair is test isolation, not successful production cleanup.
                    fixture.Dispose();
                    counts["fixture-released-subscriptions"] += fixture.FixtureReleasedSubscriptions;
                    counts["subscriptions-after-fixture-repair"] += fixture.OwnedCount;
                    Assert.AreEqual(0, fixture.OwnedCount);
                }
                foreach (string metric in metrics)
                {
                    Console.WriteLine($"PERFCOUNTER tiling-window-lifetime-{scenario} {metric} {counts[metric]}");
                }
            }
        }

        private static void AssertFailedReplacementDetaches(SubscriptionFixture fixture, SubscriptionFixture.Registration[] oldCallbacks)
        {
            Assert.AreEqual(0, fixture.OwnedCount, "A failed replacement must release old and partially acquired new subscriptions.");
            Assert.IsTrue(fixture.BindingReleased, "A failed replacement must not leave public Node and cached binding owners inconsistent.");
            fixture.Model.Title = "replacement failure title";
            bool moving = fixture.ReadField<bool>("m_isMoving");
            int removals = fixture.OwnedRemoveAttempts;
            foreach (var callback in oldCallbacks)
            {
                fixture.Invoke(callback);
                Assert.AreEqual("replacement failure title", fixture.Model.Title, $"Old {callback.Group} callback changed Title.");
                Assert.AreEqual(moving, fixture.ReadField<bool>("m_isMoving"), $"Old {callback.Group} callback changed movement state.");
                Assert.AreEqual(removals, fixture.OwnedRemoveAttempts, $"Old {callback.Group} callback touched a detached binding.");
                Assert.IsTrue(fixture.BindingReleased);
            }
            fixture.Model.Dispose();
            fixture.Model.Dispose();
            Assert.AreEqual(0, fixture.OwnedCount);
            Assert.IsTrue(fixture.FieldsReleased);
            fixture.AssertExternalSubscribersPreserved();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowRemovalFailure(Exception error) => throw error;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowAcquisitionFailure(Exception error) => throw error;

        private sealed class SubscriptionFixture : IDisposable
        {
            public static readonly string[] Owners = ["icon-removed", "window-removed", "position-start", "position-end", "title", "cursor"];
            public sealed record Registration(object Source, string Group, Delegate Handler);
            private readonly List<Registration> m_handlers = [];
            private readonly List<Registration> m_external = [];
            private readonly HashSet<object> m_workspaces = new(ReferenceEqualityComparer.Instance);
            private int m_sourcePairs;
            public TilingWindowViewModel Model { get; } = new();
            public Mock<IWorkspace> Workspace { get; } = new();
            public Mock<IWindow> Window { get; } = new();
            public Dictionary<string, Exception> Failures { get; } = [];
            public Dictionary<(object Source, string Owner), (bool AfterEffect, Exception Error)> AddFailures { get; } = [];
            public Action<object, string>? BeforeOwnedAdd { get; set; }
            public Action<object, string>? AfterOwnedAdd { get; set; }
            public Action<object, string>? BeforeOwnedRemove { get; set; }
            public Action<object, string>? AfterOwnedRemove { get; set; }
            public Dictionary<string, int> Releases { get; } = [];
            public int OwnedAdds { get; private set; }
            public int OwnedRemoveAttempts { get; private set; }
            public int FixtureReleasedSubscriptions { get; private set; }
            public int OwnedCount => m_handlers.Count(value => ReferenceEquals(value.Handler.Target, Model));
            public bool IconRemoved => (bool)typeof(TilingNodeViewModel)
                .GetField("m_iconRemoved", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Model)!;
            public bool BindingReleased => Model.Node == null && ReadField<object?>("m_currentNode") == null
                && ReadField<object?>("m_workspace") == null;
            public bool FieldsReleased => BindingReleased && Model.PrimaryActionCommand == null
                && Model.SecondaryActionCommand == null && Model.CloseCommand == null;

            public SubscriptionFixture()
            {
                ConfigureSources(Window, Workspace);
                Model.PrimaryActionCommand = new EmptyCommand();
                Model.SecondaryActionCommand = new EmptyCommand();
                Model.CloseCommand = new EmptyCommand();
                Model.Node = new WindowNode(Window.Object);
                Assert.AreEqual(6, OwnedCount);
                Assert.AreEqual(6, OwnedAdds);
                AssertExternalSubscribersPreserved();
            }

            public Mock<IWindow> CreateWindow(Mock<IWorkspace>? workspace = null)
            {
                var window = new Mock<IWindow>();
                ConfigureSources(window, workspace ?? new Mock<IWorkspace>());
                return window;
            }

            private void ConfigureSources(Mock<IWindow> window, Mock<IWorkspace> workspace)
            {
                bool newWorkspace = m_workspaces.Add(workspace.Object);
                if (newWorkspace)
                {
                    workspace.SetupAdd(value => value.CursorLocationChanged += It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                        .Callback<EventHandler<CursorLocationChangedEventArgs>>(handler => Add(workspace.Object, "cursor", handler));
                    workspace.SetupRemove(value => value.CursorLocationChanged -= It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                        .Callback<EventHandler<CursorLocationChangedEventArgs>>(handler => Remove(workspace.Object, "cursor", handler));
                }
                window.SetupGet(value => value.Workspace).Returns(workspace.Object);
                window.SetupAdd(value => value.Removed += It.IsAny<EventHandler<WindowChangedEventArgs>>())
                    .Callback<EventHandler<WindowChangedEventArgs>>(handler => Add(window.Object, "removed", handler));
                window.SetupRemove(value => value.Removed -= It.IsAny<EventHandler<WindowChangedEventArgs>>())
                    .Callback<EventHandler<WindowChangedEventArgs>>(handler => Remove(window.Object, "removed", handler));
                window.SetupAdd(value => value.PositionChangeStart += It.IsAny<EventHandler<WindowPositionChangedEventArgs>>())
                    .Callback<EventHandler<WindowPositionChangedEventArgs>>(handler => Add(window.Object, "position-start", handler));
                window.SetupRemove(value => value.PositionChangeStart -= It.IsAny<EventHandler<WindowPositionChangedEventArgs>>())
                    .Callback<EventHandler<WindowPositionChangedEventArgs>>(handler => Remove(window.Object, "position-start", handler));
                window.SetupAdd(value => value.PositionChangeEnd += It.IsAny<EventHandler<WindowPositionChangedEventArgs>>())
                    .Callback<EventHandler<WindowPositionChangedEventArgs>>(handler => Add(window.Object, "position-end", handler));
                window.SetupRemove(value => value.PositionChangeEnd -= It.IsAny<EventHandler<WindowPositionChangedEventArgs>>())
                    .Callback<EventHandler<WindowPositionChangedEventArgs>>(handler => Remove(window.Object, "position-end", handler));
                window.SetupAdd(value => value.TitleChanged += It.IsAny<EventHandler<WindowTitleChangedEventArgs>>())
                    .Callback<EventHandler<WindowTitleChangedEventArgs>>(handler => Add(window.Object, "title", handler));
                window.SetupRemove(value => value.TitleChanged -= It.IsAny<EventHandler<WindowTitleChangedEventArgs>>())
                    .Callback<EventHandler<WindowTitleChangedEventArgs>>(handler => Remove(window.Object, "title", handler));

                // Borrowed subscribers are physical entries scoped to each source.
                int start = m_handlers.Count;
                window.Object.Removed += (_, _) => { };
                window.Object.PositionChangeStart += (_, _) => { };
                window.Object.PositionChangeEnd += (_, _) => { };
                window.Object.TitleChanged += (_, _) => { };
                if (newWorkspace) { workspace.Object.CursorLocationChanged += (_, _) => { }; }
                m_external.AddRange(m_handlers.Skip(start));
                m_sourcePairs++;
            }

            public T? ReadField<T>(string name) => (T?)typeof(TilingWindowViewModel)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Model);
            public Registration[] CaptureOwnedCallbacks() => m_handlers.Where(value => ReferenceEquals(value.Handler.Target, Model)).ToArray();

            public void AssertExternalSubscribersPreserved()
            {
                Assert.AreEqual(m_sourcePairs * 4 + m_workspaces.Count, m_external.Count);
                foreach (var external in m_external)
                {
                    Assert.AreEqual(1, m_handlers.Count(value => ReferenceEquals(value.Source, external.Source)
                        && value.Group == external.Group && value.Handler.Equals(external.Handler)),
                        $"External {external.Group} subscriber must remain registered exactly once.");
                }
            }

            private static string Owner(string group, Delegate handler) => group == "removed"
                ? handler.Method.DeclaringType == typeof(TilingNodeViewModel) ? "icon-removed" : "window-removed"
                : group;

            private void Add(object source, string group, Delegate handler)
            {
                bool owned = ReferenceEquals(handler.Target, Model);
                string owner = Owner(group, handler);
                if (owned) { BeforeOwnedAdd?.Invoke(source, owner); }
                (bool AfterEffect, Exception Error) failure = default;
                bool fail = owned && AddFailures.Remove((source, owner), out failure);
                if (fail && !failure.AfterEffect) { ThrowAcquisitionFailure(failure.Error!); }
                m_handlers.Add(new Registration(source, group, handler));
                if (owned)
                {
                    OwnedAdds++;
                    AfterOwnedAdd?.Invoke(source, owner);
                }
                if (fail) { ThrowAcquisitionFailure(failure.Error!); }
            }

            private void Remove(object source, string group, Delegate handler)
            {
                bool owned = ReferenceEquals(handler.Target, Model);
                string owner = Owner(group, handler);
                if (owned)
                {
                    OwnedRemoveAttempts++;
                    BeforeOwnedRemove?.Invoke(source, owner);
                }
                int index = m_handlers.FindLastIndex(value => ReferenceEquals(value.Source, source)
                    && value.Group == group && value.Handler.Equals(handler));
                if (index < 0) { return; }
                m_handlers.RemoveAt(index);
                if (!owned) { return; }
                Releases[owner] = Releases.GetValueOrDefault(owner) + 1;
                AfterOwnedRemove?.Invoke(source, owner);
                if (Failures.Remove(owner, out var error)) { ThrowRemovalFailure(error); }
            }

            public void Invoke(Registration callback)
            {
                switch (callback.Group)
                {
                    case "removed": ((EventHandler<WindowChangedEventArgs>)callback.Handler)(callback.Source, new WindowChangedEventArgs((IWindow)callback.Source)); break;
                    case "position-start":
                    case "position-end":
                        ((EventHandler<WindowPositionChangedEventArgs>)callback.Handler)(callback.Source,
                            new WindowPositionChangedEventArgs((IWindow)callback.Source, new Rectangle(), new Rectangle())); break;
                    case "title": ((EventHandler<WindowTitleChangedEventArgs>)callback.Handler)(callback.Source,
                        new WindowTitleChangedEventArgs((IWindow)callback.Source, "late title", "disposed title")); break;
                    case "cursor": ((EventHandler<CursorLocationChangedEventArgs>)callback.Handler)(callback.Source,
                        new CursorLocationChangedEventArgs((IWorkspace)callback.Source, new Point(), new Point())); break;
                }
            }

            public void Dispose()
            {
                Failures.Clear();
                AddFailures.Clear();
                BeforeOwnedAdd = null;
                AfterOwnedAdd = null;
                BeforeOwnedRemove = null;
                AfterOwnedRemove = null;
                // Test-only repair keeps deliberately red runs isolated; it is not evidence of production cleanup.
                FixtureReleasedSubscriptions += m_handlers.RemoveAll(value => ReferenceEquals(value.Handler.Target, Model));
                Model.Dispose();
                m_handlers.Clear();
            }
        }

        private sealed class EmptyCommand : System.Windows.Input.ICommand
        {
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) { }
        }
    }
}
