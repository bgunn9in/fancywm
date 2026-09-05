using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests
{
    [TestClass]
    public class MultiDisplayActiveDisplaySelectorTest
    {
        [TestMethod]
        public void FocusedWindowOnSecondDisplayTakesPrecedenceOverPrimary()
        {
            var primary = CreateDisplay(new Rectangle(0, 0, 99, 99));
            var second = CreateDisplay(new Rectangle(100, 0, 199, 99));

            var selected = MultiDisplayActiveDisplaySelector.Select(
                new[] { primary, second },
                new Point(150, 50),
                primary);

            Assert.AreSame(second, selected);
        }

        [TestMethod]
        public void FocusOutsideRegisteredBoundsFallsBackToPrimary()
        {
            var first = CreateDisplay(new Rectangle(0, 0, 99, 99));
            var primary = CreateDisplay(new Rectangle(100, 0, 199, 99));

            var selected = MultiDisplayActiveDisplaySelector.Select(
                new[] { first, primary },
                new Point(300, 300),
                primary);

            Assert.AreSame(primary, selected);
        }

        [TestMethod]
        public void NoFocusedWindowFallsBackToRegisteredPrimary()
        {
            var first = CreateDisplay(new Rectangle(0, 0, 99, 99));
            var primary = CreateDisplay(new Rectangle(100, 0, 199, 99));

            var selected = MultiDisplayActiveDisplaySelector.Select(
                new[] { first, primary },
                null,
                primary);

            Assert.AreSame(primary, selected);
        }

        [TestMethod]
        public void UnregisteredPrimaryFallsBackToDeterministicFirstDisplay()
        {
            var first = CreateDisplay(new Rectangle(0, 0, 99, 99));
            var second = CreateDisplay(new Rectangle(100, 0, 199, 99));
            var removedPrimary = CreateDisplay(new Rectangle(200, 0, 299, 99));

            var selected = MultiDisplayActiveDisplaySelector.Select(
                new[] { first, second },
                null,
                removedPrimary);

            Assert.AreSame(first, selected);
        }

        [TestMethod]
        public void SharedBoundarySelectsFirstRegisteredContainingDisplay()
        {
            var first = CreateDisplay(new Rectangle(0, 0, 100, 100));
            var second = CreateDisplay(new Rectangle(100, 0, 200, 100));

            var selected = MultiDisplayActiveDisplaySelector.Select(
                new[] { first, second },
                new Point(100, 50),
                second);

            Assert.AreSame(first, selected);
        }

        [TestMethod]
        public void NoRegisteredDisplaysReturnsNull()
        {
            var selected = MultiDisplayActiveDisplaySelector.Select(
                System.Array.Empty<IDisplay>(),
                new Point(10, 10),
                CreateDisplay(new Rectangle(0, 0, 100, 100)));

            Assert.IsNull(selected);
        }

        private static IDisplay CreateDisplay(Rectangle bounds)
        {
            var mock = new Mock<IDisplay>(MockBehavior.Loose);
            mock.SetupGet(display => display.Bounds).Returns(bounds);
            return mock.Object;
        }
    }
}
