using System;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal enum AlgorithmicLayoutEventKind
    {
        LayoutEnabled,
        LayoutDisabled,
        WindowMovedToDesktop,
        WindowLeftFloating,
        OperationRejected,
        LayoutRecovered,
        TransferFailed,
        TransferCancelled,
    }

    /// <summary>
    /// Immutable, presentation-neutral description of a user-visible
    /// Master + Satellites event. Window titles are captured by the producer so
    /// a later toast never has to dereference a dead IWindow wrapper.
    /// </summary>
    internal sealed class AlgorithmicLayoutEvent : EventArgs
    {
        public AlgorithmicLayoutEventKind Kind { get; }

        public IntPtr WindowHandle { get; }

        public string? WindowTitle { get; }

        public IVirtualDesktop? SourceDesktop { get; }

        public IVirtualDesktop? TargetDesktop { get; }

        public IDisplay Display { get; }

        public Guid CorrelationId { get; }

        public string Reason { get; }

        public string MessageKey { get; }

        public int? SourceOccupiedSlots { get; }

        public int? SourceTotalCapacity { get; }

        public AlgorithmicLayoutEvent(
            AlgorithmicLayoutEventKind kind,
            IDisplay display,
            string reason,
            string messageKey,
            IntPtr windowHandle = default,
            string? windowTitle = null,
            IVirtualDesktop? sourceDesktop = null,
            IVirtualDesktop? targetDesktop = null,
            Guid correlationId = default,
            int? sourceOccupiedSlots = null,
            int? sourceTotalCapacity = null)
        {
            ArgumentNullException.ThrowIfNull(display);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }
            if (sourceOccupiedSlots < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceOccupiedSlots));
            }
            if (sourceTotalCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceTotalCapacity));
            }
            if (sourceOccupiedSlots.HasValue != sourceTotalCapacity.HasValue)
            {
                throw new ArgumentException(
                    "Source occupancy and total capacity must be supplied together.",
                    nameof(sourceOccupiedSlots));
            }
            Kind = kind;
            Display = display;
            Reason = reason;
            MessageKey = messageKey;
            WindowHandle = windowHandle;
            WindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle;
            SourceDesktop = sourceDesktop;
            TargetDesktop = targetDesktop;
            CorrelationId = correlationId;
            SourceOccupiedSlots = sourceOccupiedSlots;
            SourceTotalCapacity = sourceTotalCapacity;
        }
    }
}
