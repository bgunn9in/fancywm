using System;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    /// <summary>
    /// Creates stable, reference-distinct windows for algorithmic-layout tests.
    /// </summary>
    internal sealed class UniqueWindowMockFactory
    {
        private int m_nextHandle = 100;

        public IWindow Create(
            string title,
            int minimumWidth = 0,
            int minimumHeight = 0,
            int? maximumWidth = null,
            int? maximumHeight = null)
        {
            var handle = new IntPtr(m_nextHandle++);
            var mock = new Mock<IWindow>(MockBehavior.Loose);

            mock.SetupGet(x => x.SyncRoot).Returns(new object());
            mock.SetupGet(x => x.Title).Returns(title);
            mock.SetupGet(x => x.Position).Returns(Rectangle.OffsetAndSize(0, 0, 640, 480));
            mock.SetupGet(x => x.State).Returns(WindowState.Restored);
            mock.SetupGet(x => x.MinSize).Returns(new Point(minimumWidth, minimumHeight));
            mock.SetupGet(x => x.MaxSize).Returns(
                maximumWidth.HasValue && maximumHeight.HasValue
                    ? new Point(maximumWidth.Value, maximumHeight.Value)
                    : null);
            mock.SetupGet(x => x.FrameMargins).Returns(new Rectangle());
            mock.SetupGet(x => x.CanResize).Returns(true);
            mock.SetupGet(x => x.CanMove).Returns(true);
            mock.SetupGet(x => x.CanReorder).Returns(true);
            mock.SetupGet(x => x.CanMinimize).Returns(true);
            mock.SetupGet(x => x.CanMaximize).Returns(true);
            mock.SetupGet(x => x.CanClose).Returns(true);
            mock.SetupGet(x => x.IsAlive).Returns(true);
            mock.SetupGet(x => x.Handle).Returns(handle);
            mock.Setup(x => x.GetHashCode()).Returns(handle.GetHashCode());
            mock.Setup(x => x.Equals(It.IsAny<IWindow>()))
                .Returns((IWindow other) => ReferenceEquals(mock.Object, other));

            return mock.Object;
        }
    }
}
