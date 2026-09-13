#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

using FancyWM.ThemeEngine.Wpf;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class ThemeDictionaryThreadAffinityTest
    {
        [TestMethod]
        public async Task WorkerConvertedDictionariesTransferOnceToStaWithIndependentLazyValues()
        {
            const string html = "<panel><panel-bar><panel-bar-button></panel-bar-button></panel-bar></panel><window class='preview'></window>";
            var previousBrushes = new List<(SolidColorBrush Brush, Color Expected)>();

            for (int cycle = 0; cycle < 8; cycle++)
            {
                bool alternate = cycle % 2 != 0;
                int top = cycle + 2;
                string defaultBackground = alternate ? "#E0D0C0" : "#102030";
                string customColor = alternate ? "#CCBBAA" : "#AABBCC";
                string customBackground = alternate ? "#966432" : "#326496";
                string defaultCss = $$"""
                    panel-bar { border-style: solid; border-width: 1px 2px 3px 4px; background-color: {{defaultBackground}}; }
                    panel-bar-button { color: red; background-color: #203040; }
                    panel-bar-button:hover { color: lime; }
                    panel-bar-button:active { color: fuchsia; }
                    window.preview { background-color: #405060; }
                    """;
                string customCss = $$"""
                    panel-bar { border-width: {{top}}px {{top + 1}}px {{top + 2}}px {{top + 3}}px; }
                    panel-bar-button { color: {{customColor}}; background-color: {{customBackground}}; }
                    panel-bar-button:focus { color: cyan; }
                    """;

                var produced = await Task.Run(() =>
                {
                    Assert.AreEqual(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
                    var resources = new CssToWpfResourceConverter().Convert(html, defaultCss + "\n" + customCss);
                    return (Resources: resources, ThreadId: Environment.CurrentManagedThreadId);
                }).WaitAsync(TimeSpan.FromSeconds(10));

                var brush = await RunOnSta(() =>
                {
                    Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                    Assert.AreNotEqual(produced.ThreadId, Environment.CurrentManagedThreadId);

                    // The shorthand is materialized lazily from the worker-created
                    // declaration; all dictionary and CssValue cache access stays here.
                    const string shorthand = "panel-bar/border-width";
                    Assert.IsFalse(produced.Resources.ContainsKey(shorthand));
                    int countBeforeLookup = produced.Resources.Count;
                    var borderWidth = produced.Resources[shorthand];
                    Assert.AreEqual(countBeforeLookup + 1, produced.Resources.Count);
                    Assert.AreSame(borderWidth, produced.Resources[shorthand]);
                    Assert.AreEqual(new Thickness(top + 3, top, top + 1, top + 2), borderWidth.As<Thickness>());
                    Assert.AreSame(borderWidth.As(typeof(Thickness)), borderWidth.As(typeof(Thickness)));
                    Assert.AreEqual((double)top, produced.Resources["panel-bar/border-top-width"].As<double>());

                    Assert.AreEqual(alternate ? Color.FromRgb(0xE0, 0xD0, 0xC0) : Color.FromRgb(0x10, 0x20, 0x30),
                        produced.Resources["panel-bar/background-color"].As<Color>());
                    Assert.AreEqual(alternate ? Color.FromRgb(0xCC, 0xBB, 0xAA) : Color.FromRgb(0xAA, 0xBB, 0xCC),
                        produced.Resources["panel-bar-button/color"].As<Color>());
                    Assert.AreEqual(Colors.Lime, produced.Resources["panel-bar-button:hover/color"].As<Color>());
                    Assert.AreEqual(Colors.Fuchsia, produced.Resources["panel-bar-button:active/color"].As<Color>());
                    Assert.AreEqual(Colors.Cyan, produced.Resources["panel-bar-button:focus/color"].As<Color>());
                    Assert.AreEqual(Color.FromRgb(0x40, 0x50, 0x60),
                        produced.Resources["window.preview/background-color"].As<Color>());

                    var background = produced.Resources["panel-bar-button/background-color"];
                    var converted = background.As<Brush>();
                    Assert.IsInstanceOfType(converted, typeof(SolidColorBrush));
                    Assert.IsTrue(converted.IsFrozen);
                    Assert.AreSame(converted, background.As<Brush>());
                    return (SolidColorBrush)converted;
                });

                Color expected = alternate ? Color.FromRgb(0x96, 0x64, 0x32) : Color.FromRgb(0x32, 0x64, 0x96);
                Assert.IsTrue(brush.CheckAccess(), "Only the frozen result may leave its STA consumer.");
                Assert.AreEqual(expected, brush.Color);
                foreach (var previous in previousBrushes)
                {
                    Assert.AreNotSame(previous.Brush, brush);
                    Assert.IsTrue(previous.Brush.IsFrozen);
                    Assert.AreEqual(previous.Expected, previous.Brush.Color, "Later themes cannot mutate an earlier result.");
                }
                previousBrushes.Add((brush, expected));
            }
        }

        private static async Task<T> RunOnSta<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(action()); }
                catch (Exception exception) { completion.SetException(exception); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned STA consumer must exit.");
            }
        }
    }
}
