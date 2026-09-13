using System.Linq;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

using FancyWM.ThemeEngine.Wpf;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.ThemeEngine.Tests
{
    [TestClass]
    public class CssToWpfResourceConverterTest
    {
        private const string FullHtml = """
            <panel><panel-bar><panel-bar-header><panel-bar-handle></panel-bar-handle>
            <panel-bar-button></panel-bar-button></panel-bar-header><panel-bar-tab></panel-bar-tab>
            </panel-bar><window><window-actions></window-actions></window><window class='preview'></window></panel>
            <panel class='preview'></panel><custom class='accent extra'></custom>
            """;

        private const string FullCss = """
            panel-bar { color: white; border-style: solid; border-width: 1px 2px 3px 4px;
                border-radius: 4px; background-color: #102030; filter: drop-shadow(0px 2px 2px rgba(0,0,0,.2)); }
            panel-bar-tab { color: white; }
            panel-bar-handle, panel-bar-button { color: black; background-color: #99AABB; border-radius: 4px; }
            panel-bar-button:hover { background-color: #AABBCC; }
            panel-bar-button:active { background-color: #778899; }
            window:focus, window.preview, panel.preview { border-color: #99AABB; border-width: 2px; border-radius: 8px; }
            window.preview, panel.preview { background-color: rgba(153,170,187,.1); }
            custom.accent { color: red; background-color: #010203; }
            custom.accent:hover { color: lime; }
            custom.accent:active { color: fuchsia; }
            custom.accent:focus { color: cyan; }
            """;

        [TestMethod]
        public void FullDictionaryRetainsCustomAndPseudoSelectorsAcrossConversions()
        {
            var converter = new CssToWpfResourceConverter();
            var variants = new[]
            {
                FullCss,
                FullCss + "custom.accent { color: blue; } custom.accent:hover { color: yellow; }",
                FullCss + "custom.accent:hover:focus { color: navy; } custom:not(:hover) { opacity: .75; }",
                FullCss + "@media all { custom.accent { color: purple; } } @supports (color: red) { panel { color: red; } }",
                "custom.accent, panel { color: #123456; }",
                string.Empty,
            };
            var originals = variants.Select(css => converter.Convert(FullHtml, css)).ToArray();
            var fingerprints = originals.Select(Fingerprint).ToArray();
            // Every eager public key/raw value captured on the archived R0 converter,
            // including compound pseudo selectors and rules nested in at-rules.
            CollectionAssert.AreEqual(new[]
            {
                "C97B0AC28E4245F19736969DCFA580BD0B8E4A758174045609402082FC0DC780",
                "C78541B04106452EBF041A8AD04810264FAFC14D618768DA5552084E6AD8E0FB",
                "D90B79E97ABB830E45091C107DBAF0DF1588C607EE78AE8A48C951787E9A2EC8",
                "1EEC2FA3EEAA52F8B269DAB850DB90E6FB2680E7FA7AAE4A4A3D3997E6141EE2",
                "6B75779D84F560C6FC0D3DF17D6BC76F6A97E5733DFE5BA3F4330A5CF12C0494",
                "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            }, fingerprints);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                for (int i = variants.Length - 1; i >= 0; i--)
                {
                    var resources = converter.Convert(FullHtml, variants[i]);
                    Assert.AreEqual(fingerprints[i], Fingerprint(resources), $"Variant {i}, cycle {cycle}");
                    Assert.AreEqual(fingerprints[i], Fingerprint(originals[i]), "Later parses cannot change prior dictionaries.");
                    Assert.AreNotSame(originals[i], resources);
                }
            }
            Assert.AreEqual(Colors.Red, originals[0]["custom.accent.extra/color"].As<Color>());
            Assert.AreEqual(Colors.Lime, originals[0]["custom.accent.extra:hover/color"].As<Color>());
            Assert.AreEqual(Colors.Fuchsia, originals[0]["custom.accent.extra:active/color"].As<Color>());
            Assert.AreEqual(Colors.Cyan, originals[0]["custom.accent.extra:focus/color"].As<Color>());
            Assert.AreEqual(Colors.Blue, originals[1]["custom.accent.extra/color"].As<Color>());
            Assert.AreEqual(Colors.Yellow, originals[1]["custom.accent.extra:hover/color"].As<Color>());
            Assert.IsFalse(originals[0].ContainsKey("panel-bar/border-width"));
            Assert.AreEqual(new Thickness(4, 1, 2, 3), originals[0]["panel-bar/border-width"].As<Thickness>());
        }

        [TestMethod]
        public async Task ConcurrentConversionsDoNotShareMutableRules()
        {
            var converter = new CssToWpfResourceConverter();
            var inputs = Enumerable.Range(0, 8)
                .Select(i => FullCss + FormattableString.Invariant($"custom.accent {{ opacity: {i / 10d:0.0}; }}")).ToArray();
            var expected = inputs.Select(css => Fingerprint(converter.Convert(FullHtml, css))).ToArray();
            var results = await Task.WhenAll(inputs.Select(css => Task.Run(async () =>
                Fingerprint(await converter.ConvertAsync(FullHtml, css)))));
            CollectionAssert.AreEqual(expected, results);
        }

        [TestMethod]
        public void CurrentXamlResourcePathsKeepLazyConversions()
        {
            var resources = new CssToWpfResourceConverter().Convert(FullHtml, FullCss + """
                panel-bar { border-color: rgba(0,0,0,.1); }
                window:focus, window.preview, panel.preview { filter: drop-shadow(0px 2px 2px rgba(0,0,0,.2)); }
                """);
            var paths = new Dictionary<string, Type>
            {
                ["panel-bar/border-radius"] = typeof(CornerRadius),
                ["panel-bar/background"] = typeof(Brush),
                ["panel-bar/filter"] = typeof(Effect),
                ["panel-bar/border-width"] = typeof(Thickness),
                ["panel-bar/border-color"] = typeof(Brush),
                ["panel-bar-button/background"] = typeof(Brush),
                ["panel-bar-button/border-radius"] = typeof(CornerRadius),
                ["panel-bar-button:hover/background"] = typeof(Brush),
                ["panel-bar-button:active/background"] = typeof(Brush),
                ["panel-bar-handle/border-radius"] = typeof(CornerRadius),
                ["panel-bar-handle/background"] = typeof(Brush),
                ["panel-bar-handle/color"] = typeof(Brush),
                ["panel-bar-button/color"] = typeof(Color),
                ["panel-bar-tab/color"] = typeof(Brush),
            };
            foreach (var selector in new[] { "window:focus", "window.preview", "panel.preview" })
            {
                paths.Add(selector + "/border-color", typeof(Brush));
                paths.Add(selector + "/border-width", typeof(double));
                paths.Add(selector + "/border-radius", typeof(double));
                paths.Add(selector + "/filter", typeof(Effect));
            }
            paths.Add("panel.preview/background", typeof(Brush));
            Assert.AreEqual(27, paths.Count);
            foreach (var (path, type) in paths)
            {
                Assert.IsTrue(resources.TryGetValue(path, out var value), path);
                var converted = value.As(type);
                Assert.IsInstanceOfType(converted, type, path);
                Assert.AreSame(converted, value.As(type), "Existing per-type value reuse remains intact: " + path);
                if (converted is Freezable freezable) Assert.IsTrue(freezable.IsFrozen, path);
            }
        }

        private static string Fingerprint(IReadOnlyDictionary<string, CssValue> resources)
        {
            var field = typeof(CssValue).GetField("m_value", BindingFlags.Instance | BindingFlags.NonPublic);
            var rows = resources.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
                x.Key + "=" + ((AngleSharp.Css.Dom.ICssValue)field.GetValue(x.Value))?.CssText);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
        }

        [TestMethod]
        public void TestSpecificity()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<a><button></button><button class='primary'></button></a>";
            var cssText = @"
                button { color: red; }
                button.primary { color: blue; }
                button:hover { color: lime; }
            ";

            var resources = converter.Convert(htmlTemplate, cssText);

            Assert.AreEqual(Colors.Red, resources["button/color"].As<Color>());
            Assert.AreEqual(Colors.Lime, resources["button:hover/color"].As<Color>());
            Assert.AreEqual(Colors.Blue, resources["button.primary/color"].As<Color>());
            Assert.AreEqual(Colors.Lime, resources["button.primary:hover/color"].As<Color>());
        }

        [TestMethod]
        public void TestPrecedence()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = @"
                a, button { color: blue; }
                button { color: red; }
            ";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual(Colors.Red, resources["button/color"].As<Color>());
        }

        [TestMethod]
        public void TestColorProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { color: #AABBCC; }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual(Color.FromRgb(0xAA, 0xBB, 0xCC), resources["button/color"].As<Color>());
        }

        [TestMethod]
        public void TestBackgroundColorProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { background-color: rgba(10, 20, 30, 0.5); }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual(Color.FromArgb(128, 10, 20, 30), resources["button/background-color"].As<Color>());
        }

        [TestMethod]
        public void TestBackgroundImageProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { background-image: url('file:///C:/Windows/Web/Wallpaper/Windows/img0.jpg'); }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.IsInstanceOfType(resources["button/background-image"].As<Brush>(), typeof(ImageBrush));
        }

        [TestMethod]
        public void TestBackgroundProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { background: rgba(10, 20, 30, 0.5); }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual(Color.FromArgb(128, 10, 20, 30), resources["button/background-color"].As<Color>());
        }

        [TestMethod]
        public void TestBorderColorProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { border-color: red; }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual(Colors.Red, resources["button/border-top-color"].As<Color>());
        }

        [TestMethod]
        public void TestBorderWidthProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { border-width: 1px 2px 3px 4px; }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual(new Thickness(4, 1, 2, 3), resources["button/border-width"].As<Thickness>());
            Assert.AreEqual(1, resources["button/border-top-width"].As<double>());
        }


        [TestMethod]
        public void TestBorderRadiusProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { border-radius: 1px 2px 3px 4px; }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual(new CornerRadius(1, 2, 3, 4), resources["button/border-radius"].As<CornerRadius>());
        }

        [TestMethod]
        public void TestFilterDropShadowProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { filter: drop-shadow(2px 2px rgba(0,0,0,.2)); }";

            var resources = converter.Convert(htmlTemplate, cssText);
            var filter = resources["button/filter"].As<Effect>();
            Assert.IsInstanceOfType(filter, typeof(DropShadowEffect));
        }

        [TestMethod]
        public void TestFontWeightProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { font-weight: 800; }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual(FontWeight.FromOpenTypeWeight(800), resources["button/font-weight"].As<FontWeight>());
        }

        [TestMethod]
        public void TestFontFamilityProperty()
        {
            var converter = new CssToWpfResourceConverter();
            var htmlTemplate = "<button></button>";
            var cssText = "button { font-family: 'Segoe UI'; }";

            var resources = converter.Convert(htmlTemplate, cssText);
            Assert.AreEqual("Segoe UI", (resources["button/font-family"].As<FontFamily>())?.FamilyNames.First().Value);
        }
    }
}
