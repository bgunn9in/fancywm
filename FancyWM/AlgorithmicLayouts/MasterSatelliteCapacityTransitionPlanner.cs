using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    /// <summary>
    /// Immutable plan for reducing a desktop to a Master + Satellites capacity.
    /// Extras are deliberately returned last-to-first so every canonical removal
    /// is a tail removal and therefore cannot change the master or retained slots.
    /// </summary>
    internal sealed record MasterSatelliteCapacityTransitionPlan(
        IWindow? Master,
        IReadOnlyList<IWindow> RetainedSatellites,
        IReadOnlyList<IWindow> AdmittedWindows,
        IReadOnlyList<IWindow> ExtrasFromEnd)
    {
        public bool RequiresOverflow => ExtrasFromEnd.Count > 0;
    }

    /// <summary>
    /// Pure capacity-transition planner. It neither mutates a layout nor moves a
    /// window; the service can therefore finish target preflight/reservation before
    /// applying each planned source mutation.
    /// </summary>
    internal static class MasterSatelliteCapacityTransitionPlanner
    {
        public static IWindow? ResolveActivationFocus(
            IReadOnlyList<IWindow> visualOrder,
            IWindow? layoutFocusedWindow,
            IWindow? workspaceFocusedWindow)
        {
            ArgumentNullException.ThrowIfNull(visualOrder);
            ValidateWindows(visualOrder);
            if (layoutFocusedWindow != null
                && IndexOfWindow(visualOrder, layoutFocusedWindow) >= 0)
            {
                return layoutFocusedWindow;
            }
            return workspaceFocusedWindow != null
                && IndexOfWindow(visualOrder, workspaceFocusedWindow) >= 0
                    ? workspaceFocusedWindow
                    : null;
        }

        public static MasterSatelliteCapacityTransitionPlan PlanActivation(
            IReadOnlyList<IWindow> visualOrder,
            IWindow? focusedWindow,
            int maxSatellites)
        {
            ArgumentNullException.ThrowIfNull(visualOrder);
            ValidateLimit(maxSatellites);
            ValidateWindows(visualOrder);

            var ordered = visualOrder.ToList();
            int focusedIndex = focusedWindow == null
                ? -1
                : IndexOfWindow(ordered, focusedWindow);
            if (focusedIndex > 0)
            {
                var focused = ordered[focusedIndex];
                ordered.RemoveAt(focusedIndex);
                ordered.Insert(0, focused);
            }

            return CreatePlan(ordered, maxSatellites);
        }

        public static MasterSatelliteCapacityTransitionPlan PlanShrink(
            IWindow? master,
            IReadOnlyList<IWindow> satellites,
            int maxSatellites)
        {
            ArgumentNullException.ThrowIfNull(satellites);
            ValidateLimit(maxSatellites);
            ValidateWindows(satellites);
            if (master != null && satellites.Any(window => WindowsMatch(window, master)))
            {
                throw new ArgumentException(
                    "The master cannot also appear in the satellite list.",
                    nameof(satellites));
            }

            var ordered = new List<IWindow>(satellites.Count + (master == null ? 0 : 1));
            if (master != null)
            {
                ordered.Add(master);
            }
            ordered.AddRange(satellites);
            return CreatePlan(ordered, maxSatellites);
        }

        private static MasterSatelliteCapacityTransitionPlan CreatePlan(
            IReadOnlyList<IWindow> ordered,
            int maxSatellites)
        {
            int admittedCount = Math.Min(ordered.Count, maxSatellites + 1);
            var admitted = ordered.Take(admittedCount).ToArray();
            var extras = ordered.Skip(admittedCount).Reverse().ToArray();
            var retainedSatellites = admitted.Skip(1).ToArray();
            return new MasterSatelliteCapacityTransitionPlan(
                admitted.FirstOrDefault(),
                new ReadOnlyCollection<IWindow>(retainedSatellites),
                new ReadOnlyCollection<IWindow>(admitted),
                new ReadOnlyCollection<IWindow>(extras));
        }

        private static void ValidateLimit(int maxSatellites)
        {
            if (maxSatellites < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSatellites));
            }
        }

        private static void ValidateWindows(IReadOnlyList<IWindow> windows)
        {
            if (windows.Any(window => window == null))
            {
                throw new ArgumentException(
                    "The window order cannot contain null entries.",
                    nameof(windows));
            }
            for (int index = 0; index < windows.Count; index++)
            {
                for (int other = index + 1; other < windows.Count; other++)
                {
                    if (WindowsMatch(windows[index], windows[other]))
                    {
                        throw new ArgumentException(
                            "The window order cannot contain duplicates.",
                            nameof(windows));
                    }
                }
            }
        }

        private static int IndexOfWindow(IReadOnlyList<IWindow> windows, IWindow candidate)
        {
            for (int index = 0; index < windows.Count; index++)
            {
                if (WindowsMatch(windows[index], candidate))
                {
                    return index;
                }
            }
            return -1;
        }

        private static bool WindowsMatch(IWindow left, IWindow right)
        {
            return ReferenceEquals(left, right)
                || EqualityComparer<IWindow>.Default.Equals(left, right);
        }
    }
}
