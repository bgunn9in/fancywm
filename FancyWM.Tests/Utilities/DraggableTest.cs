#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class DraggableTest
    {
        [TestMethod]
        public Task InactiveMouseMovePreservesTransformAndRaisesNoDragEventsAcrossEnableCycles() => RunOnSta(() =>
        {
            var originalTransform = new ScaleTransform(1.5, 1.5);
            var element = new Border { RenderTransform = originalTransform };
            var events = new List<string>();
            Draggable.AddDragStartedHandler(element, (_, _) => events.Add("started"));
            Draggable.AddDraggingHandler(element, (_, _) => events.Add("dragging"));
            Draggable.AddDragCompletedHandler(element, (_, _) => events.Add("completed"));

            for (int cycle = 0; cycle < 100; cycle++)
            {
                Draggable.SetIsDraggable(element, true);
                Draggable.SetIsDraggable(element, true);
                RaiseMouseEvent(element, Mouse.MouseMoveEvent);
                Draggable.SetIsDraggable(element, false);
                RaiseMouseEvent(element, Mouse.MouseMoveEvent);
            }

            Assert.AreSame(originalTransform, element.RenderTransform);
            Assert.AreEqual(0, events.Count);
            Assert.IsFalse(element.IsMouseCaptured);
            Assert.IsNull(Window.GetWindow(element), "This fixture must never create an HWND or touch a desktop window.");
        });

        [TestMethod]
        public Task ActiveDragKeepsOffsetsAndCaptureLossCompletesExactlyOnce() => RunOnSta(() =>
        {
            var transform = new TranslateTransform();
            var element = new Border { RenderTransform = transform };
            Draggable.SetIsDraggable(element, true);
            SeedActiveDrag(element, new Point(100.25, 200.5));
            var moves = new List<Point>();
            int completions = 0;
            Draggable.AddDraggingHandler(element, (_, args) =>
            {
                Assert.AreSame(element, args.Source);
                moves.Add(new Point(transform.X, transform.Y));
            });
            Draggable.AddDragCompletedHandler(element, (_, _) => completions++);

            InvokeUpdate(element, new Point(130.75, 190.25));
            InvokeUpdate(element, new Point(90.25, 240.5));
            CollectionAssert.AreEqual(new[] { new Point(30.5, -10.25), new Point(-10, 40) }, moves);

            RaiseMouseEvent(element, Mouse.LostMouseCaptureEvent);
            RaiseMouseEvent(element, Mouse.LostMouseCaptureEvent);
            InvokeUpdate(element, new Point(900, 800));
            RaiseMouseEvent(element, Mouse.MouseMoveEvent);
            Assert.AreEqual(2, moves.Count);
            Assert.AreEqual(1, completions);
            DrainDispatcher(element.Dispatcher);
            Assert.IsNull(element.RenderTransform);
            Draggable.SetIsDraggable(element, false);
        });

        [TestMethod]
        public Task ActiveDragRetainsInvalidTransformValidation() => RunOnSta(() =>
        {
            var element = new Border { RenderTransform = new ScaleTransform() };
            SeedActiveDrag(element, new Point(1, 2));
            var exception = Assert.ThrowsException<TargetInvocationException>(() => InvokeUpdate(element, new Point(3, 4)));
            Assert.IsInstanceOfType(exception.InnerException, typeof(ArgumentException));
            Assert.IsInstanceOfType(element.RenderTransform, typeof(ScaleTransform));
        });

        private static void RaiseMouseEvent(UIElement element, RoutedEvent routedEvent)
        {
            var args = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent };
            element.RaiseEvent(args);
            Assert.IsFalse(args.Handled, "Drag move/capture handlers must preserve event propagation.");
        }

        private static void SeedActiveDrag(FrameworkElement element, Point initialPosition)
        {
            // CaptureMouse would manipulate the actual desktop. Seed only the
            // existing private state, then exercise production update/cancel code.
            var dataType = typeof(Draggable).GetNestedType("DragData", BindingFlags.NonPublic)!;
            var data = Activator.CreateInstance(dataType, nonPublic: true)!;
            dataType.GetProperty("InitialPosition")!.SetValue(data, initialPosition);
            var table = typeof(Draggable).GetField("s_dragData", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            table.GetType().GetMethod("AddOrUpdate")!.Invoke(table, [element, data]);
        }

        private static void InvokeUpdate(FrameworkElement element, Point position) =>
            typeof(Draggable).GetMethod("UpdateDrag", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [element, position]);

        private static void DrainDispatcher(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
            Dispatcher.PushFrame(frame);
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
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned STA test worker must exit."); }
        }
    }
}
