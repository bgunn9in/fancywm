using System;

using FancyWM.Resources;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal readonly record struct AlgorithmicLayoutNotification(
        string? Message,
        string? Hint,
        bool PlayFailureSound)
    {
        public bool ShouldShow => !string.IsNullOrWhiteSpace(Message);
    }

    /// <summary>
    /// Pure presentation mapping for typed algorithmic events. Internal exception
    /// text is deliberately not an input, so it cannot leak into a notification.
    /// </summary>
    internal static class AlgorithmicLayoutNotificationFormatter
    {
        public static bool ShouldPlayFailureSound(
            AlgorithmicLayoutNotification notification,
            bool soundOnFailure)
        {
            return notification.ShouldShow
                && notification.PlayFailureSound
                && soundOnFailure;
        }

        public static AlgorithmicLayoutNotification Format(
            AlgorithmicLayoutEvent layoutEvent,
            string fallbackWindowTitle,
            Func<IVirtualDesktop?, string> desktopLabel)
        {
            ArgumentNullException.ThrowIfNull(layoutEvent);
            ArgumentException.ThrowIfNullOrWhiteSpace(fallbackWindowTitle);
            ArgumentNullException.ThrowIfNull(desktopLabel);

            string windowTitle = string.IsNullOrWhiteSpace(layoutEvent.WindowTitle)
                ? fallbackWindowTitle
                : layoutEvent.WindowTitle;
            return layoutEvent.Kind switch
            {
                AlgorithmicLayoutEventKind.LayoutEnabled => Show(
                    Get(layoutEvent.MessageKey, "Master + Satellites enabled")),
                AlgorithmicLayoutEventKind.LayoutDisabled => Show(
                    Get(layoutEvent.MessageKey, "Master + Satellites disabled")),
                AlgorithmicLayoutEventKind.WindowMovedToDesktop => Show(
                    string.Format(
                        Get(layoutEvent.MessageKey, "\"{0}\" moved to {1}."),
                        windowTitle,
                        desktopLabel(layoutEvent.TargetDesktop)),
                    FormatCapacity(layoutEvent, desktopLabel)),
                AlgorithmicLayoutEventKind.WindowLeftFloating => Show(
                    string.Format(
                        Get(layoutEvent.MessageKey, "{0} left floating"),
                        windowTitle),
                    FormatFloatingHint(layoutEvent),
                    playFailureSound: true),
                AlgorithmicLayoutEventKind.LayoutRecovered => Show(
                    Get(layoutEvent.MessageKey, "Master + Satellites layout recovered")),
                AlgorithmicLayoutEventKind.OperationRejected
                    when layoutEvent.MessageKey.EndsWith(
                        ".CommandHandled",
                        StringComparison.Ordinal) => default,
                AlgorithmicLayoutEventKind.OperationRejected => Show(
                    Get(layoutEvent.MessageKey, "Master + Satellites operation rejected"),
                    playFailureSound: true),
                AlgorithmicLayoutEventKind.TransferFailed
                    or AlgorithmicLayoutEventKind.TransferCancelled => default,
                _ => default,
            };
        }

        private static string? FormatCapacity(
            AlgorithmicLayoutEvent layoutEvent,
            Func<IVirtualDesktop?, string> desktopLabel)
        {
            if (layoutEvent.SourceOccupiedSlots is not int occupied
                || layoutEvent.SourceTotalCapacity is not int total)
            {
                return null;
            }
            if (layoutEvent.Kind == AlgorithmicLayoutEventKind.WindowMovedToDesktop)
            {
                return string.Format(
                    Get(
                        "AlgorithmicLayout.CapacityReachedHint",
                        "{0} reached its {1}-window limit."),
                    desktopLabel(layoutEvent.SourceDesktop),
                    total);
            }
            return string.Format(
                Get("AlgorithmicLayout.CapacityHint", "Source capacity: {0}/{1}."),
                occupied,
                total);
        }

        private static string FormatFloatingHint(AlgorithmicLayoutEvent layoutEvent)
        {
            string noCapacity = Get(
                "AlgorithmicLayout.NoCapacityHint",
                "Master + Satellites is full on all available desktops.");
            if (layoutEvent.SourceTotalCapacity is not int total)
            {
                return noCapacity;
            }
            return string.Join(
                Environment.NewLine,
                noCapacity,
                string.Format(
                    Get(
                        "AlgorithmicLayout.CapacityPerDesktopHint",
                        "Capacity: {0} windows per desktop."),
                    total));
        }

        private static AlgorithmicLayoutNotification Show(
            string message,
            string? hint = null,
            bool playFailureSound = false)
        {
            return new AlgorithmicLayoutNotification(message, hint, playFailureSound);
        }

        private static string Get(string key, string fallback)
        {
            return Strings.ResourceManager.GetString(key) ?? fallback;
        }
    }
}
