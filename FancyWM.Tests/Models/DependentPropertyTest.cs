using System.Collections.Generic;

using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class DependentPropertyTest
    {
        [TestMethod]
        public void EveryDependentPropertyIsNotifiedOnceAfterItsSource()
        {
            using var model = new DependentModel();
            var notifications = new List<string>();
            model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            model.Value = 42;
            CollectionAssert.AreEqual(new[] { "Value", "Double", "Text" }, notifications);
            model.Value = 42;
            Assert.AreEqual(3, notifications.Count);
        }

        [TestMethod]
        public void OverlayRectanglesNotifyVisibilityOnShowAndClear()
        {
            using var model = new TilingOverlayViewModel();
            var notifications = new List<string>();
            model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            Assert.IsFalse(model.IsFocusRectangleVisible);
            Assert.IsFalse(model.IsPreviewRectangleVisible);
            model.FocusRectangle = Rectangle.OffsetAndSize(20, 30, 100, 200);
            model.PreviewRectangle = model.FocusRectangle;
            Assert.IsTrue(model.IsFocusRectangleVisible);
            Assert.IsTrue(model.IsPreviewRectangleVisible);
            model.FocusRectangle = default;
            model.PreviewRectangle = default;
            Assert.IsFalse(model.IsFocusRectangleVisible);
            Assert.IsFalse(model.IsPreviewRectangleVisible);
            CollectionAssert.AreEqual(new[]
            {
                "FocusRectangle", "IsFocusRectangleVisible", "PreviewRectangle", "IsPreviewRectangleVisible",
                "FocusRectangle", "IsFocusRectangleVisible", "PreviewRectangle", "IsPreviewRectangleVisible",
            }, notifications);
        }

        private sealed class DependentModel : ViewModelBase
        {
            private int m_value;
            public int Value { get => m_value; set => SetField(ref m_value, value); }
            [DerivedProperty(nameof(Value))]
            public int Double => Value * 2;
            [DerivedProperty(nameof(Value))]
            public string Text => Value.ToString();
        }
    }
}
