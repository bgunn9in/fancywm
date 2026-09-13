#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using FancyWM.Pages.Settings;
using FancyWM.Windows;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class SettingsPageNavigationTest
    {
        [TestMethod]
        public void IdleNavigationBurstConstructsOnlyTheLatestPage()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                for (int i = 0; i < 100; i++) fixture.Owner.Navigate(i % 2 == 0 ? typeof(GeneralPage) : typeof(HelpPage));
                Assert.AreEqual(0, fixture.Pages.Count, "Superseded idle navigation must not create browser pages.");
                Drain();
                Assert.AreEqual(1, fixture.Pages.Count);
                Assert.AreEqual(typeof(HelpPage), fixture.Visible!.Kind);
                Assert.AreSame(fixture.Pages[0], fixture.Visible);
            });
        }

        [TestMethod]
        public void HelpRetainsHistoryAcrossNavigationAndDisposesOnceAtClose()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                var help = fixture.Visible!;
                help.History = "selected help section";
                for (int i = 0; i < 100; i++)
                {
                    fixture.Owner.Navigate(typeof(GeneralPage));
                    Drain();
                    Assert.AreEqual(0, help.DisposeCount);
                    var general = fixture.Visible!;
                    fixture.Owner.Navigate(typeof(HelpPage));
                    Drain();
                    Assert.AreSame(help, fixture.Visible);
                    Assert.AreEqual("selected help section", help.History);
                    Assert.AreEqual(1, general.DisposeCount);
                }
                Assert.AreEqual(1, fixture.Pages.Count(page => page.Kind == typeof(HelpPage)));
                fixture.Owner.Dispose();
                fixture.Owner.Dispose();
                Assert.AreEqual(1, help.DisposeCount);
                Assert.IsNull(fixture.Visible);
                Assert.IsTrue(fixture.Pages.All(page => page.DisposeCount == 1));
            });
        }

        [TestMethod]
        public void ClosingBeforeIdleCancelsCreationAndRejectsLateNavigation()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.Owner.Navigate(typeof(HelpPage));
                fixture.Owner.Dispose();
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.AreEqual(0, fixture.Pages.Count);
                Assert.IsNull(fixture.Visible);
            });
        }

        [TestMethod]
        public void NavigationDuringCreationDisposesTheObsoletePage()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.OnCreate = kind => { if (kind == typeof(HelpPage)) fixture.Owner.Navigate(typeof(GeneralPage)); };
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.AreEqual(typeof(GeneralPage), fixture.Visible!.Kind);
                Assert.AreEqual(1, fixture.Pages.Single(page => page.Kind == typeof(HelpPage)).DisposeCount);
                Assert.IsFalse(fixture.Displays.Any(page => page?.Kind == typeof(HelpPage)));
            });
        }

        [TestMethod]
        public void CloseDuringCreationReleasesTheReturnedPageWithoutDisplayingIt()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.OnCreate = _ => fixture.Owner.Dispose();
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.AreEqual(1, fixture.Pages.Single().DisposeCount);
                Assert.IsNull(fixture.Visible);
                Assert.IsFalse(fixture.Displays.Any(page => page != null));
            });
        }

        [TestMethod]
        public void FailedCreationRetainsTheCurrentPageAndCanRetry()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.Owner.Navigate(typeof(GeneralPage));
                Drain();
                var original = fixture.Visible;
                fixture.OnCreate = _ => throw new InvalidOperationException("create failure");
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.AreSame(original, fixture.Visible);
                Assert.AreEqual(0, original!.DisposeCount);
                Assert.AreEqual(1, fixture.Errors.Count);
                fixture.OnCreate = null;
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.AreEqual(typeof(HelpPage), fixture.Visible!.Kind);
                Assert.AreEqual(1, original.DisposeCount);
            });
        }

        [TestMethod]
        public void FailedDisplayRestoresCurrentPageAndDisposesNewResources()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.Owner.Navigate(typeof(GeneralPage));
                Drain();
                var original = fixture.Visible;
                fixture.OnDisplay = page => { if (page?.Kind == typeof(HelpPage)) throw new InvalidOperationException("display failure"); };
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.AreSame(original, fixture.Visible);
                Assert.AreEqual(1, fixture.Errors.Count);
                Assert.AreEqual(1, fixture.Pages.Single(page => page.Kind == typeof(HelpPage)).DisposeCount);
                Assert.AreEqual(0, original!.DisposeCount);
                fixture.OnDisplay = null;
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.AreEqual(2, fixture.Pages.Count(page => page.Kind == typeof(HelpPage)));
                Assert.AreEqual(1, original.DisposeCount);
            });
        }

        [TestMethod]
        public void DisposeFailureStillReleasesAllOwnedPagesExactlyOnce()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                fixture.Visible!.FailDispose = true;
                fixture.Owner.Navigate(typeof(GeneralPage));
                Drain();
                Assert.ThrowsException<AggregateException>(() => fixture.Owner.Dispose());
                fixture.Owner.Dispose();
                Assert.IsNull(fixture.Visible);
                Assert.IsTrue(fixture.Pages.All(page => page.DisposeCount == 1));
            });
        }

        [TestMethod]
        public void RepeatedSettingsLifetimesLeaveNoOwnedPagesOrLateCallbacks()
        {
            RunOnSta(() =>
            {
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    using var fixture = new Fixture();
                    fixture.Owner.Navigate(typeof(HelpPage));
                    Drain();
                    fixture.Owner.Navigate(typeof(GeneralPage));
                    Drain();
                    fixture.Owner.Navigate(typeof(HelpPage));
                    fixture.Owner.Dispose();
                    Drain();
                    Assert.IsNull(fixture.Visible);
                    Assert.AreEqual(2, fixture.Pages.Count);
                    Assert.IsTrue(fixture.Pages.All(page => page.DisposeCount == 1));
                    Assert.AreEqual(0, fixture.Errors.Count);
                }
            });
        }

        [TestMethod]
        public void ReturningToCurrentPageCancelsAnUncreatedNavigation()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.Owner.Navigate(typeof(GeneralPage));
                Drain();
                var current = fixture.Visible;
                fixture.Owner.Navigate(typeof(HelpPage));
                fixture.Owner.Navigate(typeof(GeneralPage));
                Drain();
                Assert.AreSame(current, fixture.Visible);
                Assert.AreEqual(1, fixture.Pages.Count);
            });
        }

        [TestMethod]
        public void ClosingDuringDisplayReleasesCurrentAndPreviousPages()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.Owner.Navigate(typeof(GeneralPage));
                Drain();
                fixture.OnDisplay = page => { if (page?.Kind == typeof(HelpPage)) fixture.Owner.Dispose(); };
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.IsTrue(fixture.Pages.All(page => page.DisposeCount == 1));
                Assert.AreEqual(2, fixture.Pages.Count);
                Assert.IsNull(fixture.Visible);
                Assert.AreEqual(0, fixture.Errors.Count);
            });
        }

        [TestMethod]
        public void ReplacedPageDisposeFailureIsObservedWithoutDroppingNavigation()
        {
            RunOnSta(() =>
            {
                using var fixture = new Fixture();
                fixture.Owner.Navigate(typeof(GeneralPage));
                Drain();
                var previous = fixture.Visible!;
                previous.FailDispose = true;
                fixture.Owner.Navigate(typeof(HelpPage));
                Drain();
                Assert.AreEqual(typeof(HelpPage), fixture.Visible!.Kind);
                Assert.AreEqual(1, previous.DisposeCount);
                Assert.AreEqual(1, fixture.Errors.Count);
                fixture.Owner.Dispose();
                Assert.AreEqual(1, previous.DisposeCount);
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void HelpPageReleasesItsBrowserOnTheOwningThreadOnce(bool fail)
        {
            RunOnSta(() =>
            {
                var browser = new Page(typeof(HelpPage)) { FailDispose = fail };
                var help = new HelpPage(browser) { DataContext = new object(), Content = new Border() };
                if (fail) Assert.ThrowsException<InvalidOperationException>(() => help.Dispose());
                else help.Dispose();
                help.Dispose();
                Assert.AreEqual(1, browser.DisposeCount);
                Assert.IsNull(help.Content);
                Assert.IsNull(help.DataContext);
            });
        }

        private static void Drain()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.InvokeAsync(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
            Dispatcher.PushFrame(frame);
        }

        private static void RunOnSta(Action action)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "The isolated STA must finish without an application window.");
            failure?.Throw();
        }

        private sealed class Fixture : IDisposable
        {
            public readonly List<Page> Pages = [];
            public readonly List<Page?> Displays = [];
            public readonly List<Exception> Errors = [];
            public readonly SettingsWindow.PageNavigation Owner;
            public Action<Type>? OnCreate;
            public Action<Page?>? OnDisplay;
            public Page? Visible;

            public Fixture()
            {
                Owner = new SettingsWindow.PageNavigation(Dispatcher.CurrentDispatcher, kind =>
                {
                    OnCreate?.Invoke(kind);
                    var page = new Page(kind);
                    Pages.Add(page);
                    return page;
                }, page =>
                {
                    Visible = (Page?)page;
                    Displays.Add(Visible);
                    // WPF property callbacks run after the new local value is set.
                    OnDisplay?.Invoke(Visible);
                }, Errors.Add);
            }
            public void Dispose() => Owner.Dispose();
        }

        private sealed class Page(Type kind) : Border, IDisposable
        {
            public readonly Type Kind = kind;
            public string? History;
            public int DisposeCount;
            public bool FailDispose;
            public void Dispose()
            {
                Dispatcher.VerifyAccess();
                DisposeCount++;
                if (FailDispose) throw new InvalidOperationException("dispose failure");
            }
        }
    }
}
