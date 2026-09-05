using System;
using System.Collections.Generic;

using WinMan;

namespace FancyWM
{
    internal static class MultiDisplayActiveDisplaySelector
    {
        public static IDisplay? Select(
            IReadOnlyList<IDisplay> registeredDisplays,
            Point? focusedWindowCenter,
            IDisplay? primaryDisplay)
        {
            if (registeredDisplays == null)
            {
                throw new ArgumentNullException(nameof(registeredDisplays));
            }

            if (focusedWindowCenter is Point center)
            {
                foreach (var display in registeredDisplays)
                {
                    if (display.Bounds.Contains(center))
                    {
                        return display;
                    }
                }
            }

            if (primaryDisplay != null)
            {
                foreach (var display in registeredDisplays)
                {
                    if (ReferenceEquals(display, primaryDisplay)
                        || EqualityComparer<IDisplay>.Default.Equals(display, primaryDisplay))
                    {
                        return display;
                    }
                }
            }

            return registeredDisplays.Count == 0 ? null : registeredDisplays[0];
        }
    }
}
