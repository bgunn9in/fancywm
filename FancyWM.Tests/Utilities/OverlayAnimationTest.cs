#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using FancyWM.Controls;
using FancyWM.ThemeEngine.Wpf;
using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class OverlayAnimationTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task UnrelatedNotificationsDoNotAllocateEasingOrStartAnimations(bool nonHitTestable) => RunOnSta(() =>
        {
            var control = CreateControl(nonHitTestable);
            var handler = GetHandler(control);
            var args = new PropertyChangedEventArgs(nameof(TilingOverlayViewModel.FocusRectangle));
            NotifyRepeatedly(handler, args, 10000);
            long before = GC.GetAllocatedBytesForCurrentThread();
            NotifyRepeatedly(handler, args, 10000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(allocated < 1024, $"Unrelated notifications allocated {allocated} bytes.");
            Assert.IsFalse(control.HasAnimatedProperties);
            Assert.AreEqual(1d, control.Opacity);
            Assert.IsNull(Window.GetWindow(control));
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task VisibilityStillStartsFadeAndUpdatesHitTesting(bool nonHitTestable) => RunOnSta(() =>
        {
            var control = CreateControl(nonHitTestable);
            using var viewModel = new TilingOverlayViewModel();
            if (control is TilingOverlay hit) { hit.ViewModel = viewModel; }
            else { ((NonHitTestableTilingOverlay)control).ViewModel = viewModel; }
            viewModel.OverlayVisibility = Visibility.Hidden;
            Assert.IsTrue(control.HasAnimatedProperties);
            Assert.IsFalse(control.IsHitTestVisible);
            viewModel.OverlayVisibility = Visibility.Visible;
            Assert.IsTrue(control.HasAnimatedProperties);
            Assert.AreEqual(!nonHitTestable, control.IsHitTestVisible);
            control.BeginAnimation(UIElement.OpacityProperty, null);
            Assert.IsFalse(control.HasAnimatedProperties);
            Assert.AreEqual(1d, control.Opacity);
        });

        [TestMethod]
        public Task OverlayAnimationCounterScenario() => RunOnSta(() =>
        {
            foreach (bool nonHitTestable in new[] { false, true })
            {
                var control = CreateControl(nonHitTestable);
                var handler = GetHandler(control);
                var args = new PropertyChangedEventArgs(nameof(TilingOverlayViewModel.FocusRectangle));
                NotifyRepeatedly(handler, args, 10000);
                long before = GC.GetAllocatedBytesForCurrentThread();
                NotifyRepeatedly(handler, args, 10000);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.IsFalse(control.HasAnimatedProperties);
                Assert.AreEqual(1d, control.Opacity);
                string name = nonHitTestable ? "nonhit" : "hit";
                Console.WriteLine($"PERFCOUNTER overlay-animation-{name} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER overlay-animation-{name} updates 10000");
            }
        });

        private static UserControl CreateControl(bool nonHitTestable)
        {
            if (!nonHitTestable) { return new TilingOverlay(); }
            // The isolated fixture has no Application and cannot call ApplyTheme.
            // Supply a real dictionary only while XAML resolves its resources.
            var field = typeof(CssManager).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previous = field.GetValue(null);
            try
            {
                field.SetValue(null, new Dictionary<string, CssValue>(new CssToWpfResourceConverter()
                    .Convert("<fixture></fixture>", "fixture { color: red; }")));
                return new NonHitTestableTilingOverlay();
            }
            finally { field.SetValue(null, previous); }
        }
        private static Action<object?, PropertyChangedEventArgs> GetHandler(UserControl control) =>
            control.GetType().GetMethod("OnDataContextPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<object?, PropertyChangedEventArgs>>(control);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NotifyRepeatedly(Action<object?, PropertyChangedEventArgs> handler, PropertyChangedEventArgs args, int count)
        {
            // Warm the same loop that is measured, including its runtime setup.
            for (int update = 0; update < count; update++) { handler(null, args); }
        }

        private static async Task RunOnSta(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned STA test worker must exit."); }
        }
    }
}
