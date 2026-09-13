#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

using FancyWM.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    // Uses actual XAML/control behavior and a bound-list stand-in with no
    // settings file, App, hook or browser.
    [TestClass]
    public class StringsListBoxTest
    {
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(10)]
        [DataRow(50)]
        public Task UnchangedLostFocusKeepsSourceAndEditors(int count) => RunOnSta(() =>
        {
            var values = Values(count);
            var host = new ListHost(values);
            var control = Create(host);
            var presenters = Presenters(control);
            var editors = presenters.Select(Editor).ToArray();
            editors[0].Select(1, 2);
            editors[0].RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Materialize(control);
            Assert.AreSame(values, host.Items);
            Assert.AreEqual(0, host.Updates);
            CollectionAssert.AreEqual(presenters, Presenters(control));
            CollectionAssert.AreEqual(editors, Presenters(control).Select(Editor).ToArray());
            Assert.AreEqual(1, editors[0].SelectionStart);
            Assert.AreEqual(2, editors[0].SelectionLength);
            AssertContents(control, values);
            Release(control);
        });

        [TestMethod]
        public Task ChangedDuplicateSlotStillUpdatesBoundSourceExactlyOnce() => RunOnSta(() =>
        {
            var host = new ListHost(new[] { "same", "middle", "same" });
            var control = Create(host);
            var editor = Editor(Presenters(control)[2]);
            editor.Text = "changed";
            editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Materialize(control);
            Assert.AreEqual(1, host.Updates);
            CollectionAssert.AreEqual(new[] { "same", "middle", "changed" }, host.Items.ToArray());
            AssertContents(control, host.Items);
            Release(control);
        });

        [DataTestMethod]
        [DataRow("detached-presenter")]
        [DataRow("orphan-editor")]
        [DataRow("foreign-parent")]
        [DataRow("source-shrunk")]
        public Task LateEditorEventsCannotOverwriteTheCurrentList(string ownership) => RunOnSta(() =>
        {
            var host = new ListHost(new List<string> { "first", "second" });
            var control = Create(host);
            var presenter = Presenters(control)[0];
            var editor = Editor(presenter);
            editor.Text = "obsolete edit";
            if (ownership == "source-shrunk")
            {
                host.Items.Clear();
            }
            else if (ownership == "orphan-editor")
            {
                ((Grid)VisualTreeHelper.GetParent(editor)).Children.Remove(editor);
            }
            else
            {
                Items(control).Children.Remove(presenter);
                if (ownership == "foreign-parent") new StackPanel().Children.Add(presenter);
            }
            var current = host.Items;
            var expected = current.ToArray();
            editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.AreSame(current, host.Items);
            Assert.AreEqual(0, host.Updates);
            CollectionAssert.AreEqual(expected, host.Items.ToArray());
            Release(control);
        });

        [DataTestMethod]
        [DataRow("detached-presenter")]
        [DataRow("orphan-button")]
        [DataRow("foreign-parent")]
        [DataRow("source-shrunk")]
        [DataRow("source-replaced")]
        public Task LateDeleteEventsCannotMutateTheCurrentList(string ownership) => RunOnSta(() =>
        {
            var host = new ListHost(new List<string> { "first", "second" });
            var control = Create(host);
            try
            {
                var presenter = Presenters(control)[0];
                var button = DeleteButton(presenter);
                if (ownership == "source-shrunk")
                {
                    host.Items.Clear();
                }
                else if (ownership == "source-replaced")
                {
                    host.Items = new[] { "replacement", "current" };
                    Materialize(control);
                    host.Updates = 0;
                }
                else if (ownership == "orphan-button")
                {
                    ((Grid)VisualTreeHelper.GetParent(button)).Children.Remove(button);
                }
                else
                {
                    Items(control).Children.Remove(presenter);
                    if (ownership == "foreign-parent") new StackPanel().Children.Add(presenter);
                }
                var current = host.Items;
                var expected = current.ToArray();
                var currentPresenters = Presenters(control);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreSame(current, host.Items, "An obsolete delete must not replace the current source.");
                Assert.AreEqual(0, host.Updates);
                CollectionAssert.AreEqual(expected, host.Items.ToArray());
                CollectionAssert.AreEqual(currentPresenters, Presenters(control));
                AssertNoWindow(control);
            }
            finally { Release(control); }
        });

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public Task DeleteDuplicateSlotStillUpdatesBoundSourceExactlyOnce(int index) => RunOnSta(() =>
        {
            var values = new[] { "same", "middle", "same" };
            var expected = index switch
            {
                0 => new[] { "middle", "same" },
                1 => new[] { "same", "same" },
                2 => new[] { "same", "middle" },
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            var host = new ListHost(values);
            var control = Create(host);
            try
            {
                var button = DeleteButton(Presenters(control)[index]);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Materialize(control);
                Assert.AreEqual(1, host.Updates);
                AssertContents(control, expected);
                CollectionAssert.AreEqual(new[] { "same", "middle", "same" }, values,
                    "Removing one slot must not mutate the caller's original array.");
                var current = host.Items;
                var currentPresenters = Presenters(control);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreSame(current, host.Items);
                Assert.AreEqual(1, host.Updates, "A second event from the removed button must not save another field change.");
                CollectionAssert.AreEqual(currentPresenters, Presenters(control));
                AssertContents(control, expected);
                AssertNoWindow(control);
            }
            finally { Release(control); }
        });

        [TestMethod]
        public Task RepeatedDeleteAndStaleClickKeepOneUpdatePerCycle() => RunOnSta(() =>
        {
            var host = new ListHost(new[] { "value0" });
            var control = Create(host);
            try
            {
                int deleteUpdates = 0;
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    if (cycle != 0)
                    {
                        host.Items = new[] { $"value{cycle}" };
                        Materialize(control);
                        host.Updates = 0;
                    }
                    var button = DeleteButton(Presenters(control)[0]);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Materialize(control);
                    Assert.AreEqual(1, host.Updates);
                    AssertContents(control, Array.Empty<string>());
                    var current = host.Items;
                    for (int lateEvent = 0; lateEvent < 5; lateEvent++)
                    {
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    Assert.AreSame(current, host.Items);
                    Assert.AreEqual(1, host.Updates);
                    deleteUpdates += host.Updates;
                }
                Assert.AreEqual(100, deleteUpdates);
                AssertNoWindow(control);
            }
            finally { Release(control); }
        });

        [TestMethod]
        public Task StringsListBoxCounterScenario() => RunOnSta(() =>
        {
            foreach (int count in new[] { 1, 2, 10, 50 })
            {
                var values = Values(count);
                var host = new ListHost(values);
                var control = Create(host);
                for (int warmup = 0; warmup < 20; warmup++)
                {
                    Editor(Presenters(control)[0]).RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                    Materialize(control);
                }
                host.Updates = 0;
                var oldPresenters = Presenters(control);
                var events = Enumerable.Range(0, 100)
                    .Select(_ => new RoutedEventArgs(UIElement.LostFocusEvent)).ToArray();
                long bytes = 0;
                long ticks = 0;
                int replacements = 0;
                for (int pass = 0; pass < events.Length; pass++)
                {
                    var editor = Editor((ContentPresenter)Items(control).Children[0]);
                    long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                    long beforeTicks = Stopwatch.GetTimestamp();
                    editor.RaiseEvent(events[pass]);
                    Materialize(control);
                    ticks += Stopwatch.GetTimestamp() - beforeTicks;
                    bytes += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                    for (int index = 0; index < count; index++)
                    {
                        var current = (ContentPresenter)Items(control).Children[index];
                        if (!ReferenceEquals(oldPresenters[index], current)) replacements++;
                        oldPresenters[index] = current;
                    }
                }
                AssertContents(control, values);
                AssertNoWindow(control);
                string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", host.Items))));
                Console.WriteLine($"PERFCOUNTER settings-strings-{count} allocated-bytes {bytes}");
                Console.WriteLine($"PERFCOUNTER settings-strings-{count} elapsed-ticks {ticks}");
                Console.WriteLine($"PERFCOUNTER settings-strings-{count} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER settings-strings-{count} presenter-replacements {replacements}");
                Console.WriteLine($"PERFCOUNTER settings-strings-{count} source-updates {host.Updates}");
                Console.WriteLine($"PERFCOUNTER settings-strings-{count} lost-focus-events {events.Length}");
                Console.WriteLine($"PERFCOUNTER settings-strings-{count} content-digest {digest}");
                Release(control);
            }
        });

        private sealed class ListHost(IList<string> values) : INotifyPropertyChanged
        {
            private IList<string> m_items = values;
            public int Updates;
            public IList<string> Items
            {
                get => m_items;
                set { m_items = value; Updates++; PropertyChanged?.Invoke(this, new(nameof(Items))); }
            }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private static StringsListBox Create(ListHost host)
        {
            var control = new StringsListBox();
            BindingOperations.SetBinding(control, StringsListBox.ItemsSourceProperty,
                new Binding(nameof(ListHost.Items)) { Source = host, Mode = BindingMode.TwoWay });
            Materialize(control);
            AssertNoWindow(control);
            host.Updates = 0;
            return control;
        }

        private static string[] Values(int count) => count switch
        {
            1 => new[] { "Taskmgr" },
            2 => new[] { "OperationStatusWindow", "RAIL_WINDOW" },
            _ => Enumerable.Range(0, count).Select(index => $"Rule{index:D3}").ToArray(),
        };
        private static StackPanel Items(StringsListBox control) => (StackPanel)control.FindName("ItemsBox");
        private static ContentPresenter[] Presenters(StringsListBox control) => Items(control).Children.Cast<ContentPresenter>().ToArray();
        private static TextBox Editor(ContentPresenter presenter) =>
            ((Grid)VisualTreeHelper.GetChild(presenter, 0)).Children.OfType<TextBox>().Single();
        private static Button DeleteButton(ContentPresenter presenter) =>
            ((Grid)VisualTreeHelper.GetChild(presenter, 0)).Children.OfType<Button>().Single();
        private static void AssertContents(StringsListBox control, IList<string> values)
        {
            CollectionAssert.AreEqual(values.ToArray(), control.ItemsSource.ToArray());
            CollectionAssert.AreEqual(values.ToArray(), Presenters(control).Select(item => Editor(item).Text).ToArray());
        }
        private static void Materialize(StringsListBox control)
        {
            control.ApplyTemplate();
            control.Measure(new Size(640, double.PositiveInfinity));
            control.Arrange(new Rect(new Point(), control.DesiredSize));
            control.UpdateLayout();
        }
        private static void AssertNoWindow(StringsListBox control)
        {
            Assert.IsNull(Window.GetWindow(control));
            Assert.IsNull(PresentationSource.FromVisual(control));
        }
        private static void Release(StringsListBox control)
        {
            BindingOperations.ClearBinding(control, StringsListBox.ItemsSourceProperty);
            control.ItemsSource = Array.Empty<string>();
        }
        private static async Task RunOnSta(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Owned STA did not exit."); }
        }
    }
}
