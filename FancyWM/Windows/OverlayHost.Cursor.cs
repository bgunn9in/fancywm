using System;
using System.Threading.Tasks;
using System.Windows;

using FancyWM.DllImports;
using FancyWM.Utilities;

namespace FancyWM.Windows
{
    partial class OverlayHost
    {
        internal static (Action Invalidate, IDisposable Lifetime) CreateCursorInput(
            Action<Action> schedule,
            Func<long> getActiveGeneration,
            Func<Point> getCursor,
            Func<Point, Point> screenToLocal,
            Func<Point, bool> hitTest,
            Func<int> readStyle,
            Action<int> writeStyle,
            Action<Exception> reportError)
        {
            var queue = new LayoutInvalidationQueue(schedule, () => getActiveGeneration() != 0, () =>
            {
                long generation = getActiveGeneration();
                if (generation == 0) { return Task.CompletedTask; }
                var screenPoint = getCursor();
                if (getActiveGeneration() != generation) { return Task.CompletedTask; }
                bool wasHit;
                try
                {
                    var point = screenToLocal(screenPoint);
                    if (getActiveGeneration() != generation) { return Task.CompletedTask; }
                    wasHit = hitTest(point);
                }
                catch (InvalidOperationException)
                {
                    // This Visual is not connected to a PresentationSource.
                    wasHit = false;
                }
                if (getActiveGeneration() != generation) { return Task.CompletedTask; }
                var oldValue = readStyle();
                if (getActiveGeneration() != generation) { return Task.CompletedTask; }
                var newValue = wasHit
                    ? oldValue & ~(int)WINDOWS_STYLE.WS_DISABLED
                    : oldValue | (int)WINDOWS_STYLE.WS_DISABLED;
                if (oldValue != newValue) { writeStyle(newValue); }
                return Task.CompletedTask;
            }, reportError);
            return (queue.Invalidate, queue);
        }
    }
}
