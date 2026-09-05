using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;

using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class MasterSatelliteLayoutSettingsTest
    {
        private string m_tempDir = null!;
        private string m_settingsPath = null!;

        [TestInitialize]
        public void Setup()
        {
            m_tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(m_tempDir);
            m_settingsPath = Path.Combine(m_tempDir, "settings.json");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(m_tempDir))
            {
                Directory.Delete(m_tempDir, recursive: true);
            }
        }

        [TestMethod]
        public void DefaultsAreExact()
        {
            var settings = new MasterSatelliteLayoutSettings();

            Assert.IsFalse(settings.Enabled);
            Assert.AreEqual(0.60, settings.MasterRatio);
            Assert.AreEqual(MasterSide.Left, settings.DefaultMasterSide);
            Assert.AreEqual(SatelliteLayoutOrientation.Vertical, settings.DefaultSatelliteOrientation);
            Assert.AreEqual(3, settings.MaxSatellites);
            Assert.AreEqual(MasterSatelliteOverflowPolicy.MoveToExistingDesktop, settings.OverflowPolicy);
            Assert.AreEqual(AlgorithmicLayoutDisplayScope.PrimaryDisplay, settings.DisplayScope);
            Assert.AreEqual(1, settings.MaxAutoCreatedDesktops);
            Assert.IsFalse(settings.FollowOverflowWindow);
        }

        [TestMethod]
        public async Task OldJsonWithoutSectionLoadsDefaultsAndPreservesCommentsOnSave()
        {
            File.WriteAllText(m_settingsPath, """
                {
                    // keep this legacy settings comment
                    "AutoSplitCount": 5
                }
                """);
            var state = new AppState(m_settingsPath);

            var loaded = await state.Settings.Value.FirstAsync();

            Assert.AreEqual(5, loaded.AutoSplitCount);
            Assert.AreEqual(new MasterSatelliteLayoutSettings(), loaded.MasterSatelliteLayout);

            await state.Settings.SaveAsync(x => x with
            {
                MasterSatelliteLayout = x.MasterSatelliteLayout with { Enabled = true }
            });

            var savedJson = File.ReadAllText(m_settingsPath);
            StringAssert.Contains(savedJson, "keep this legacy settings comment");
            using var document = ParseSavedJson(savedJson);
            Assert.IsTrue(document.RootElement
                .GetProperty(nameof(Settings.MasterSatelliteLayout))
                .GetProperty(nameof(MasterSatelliteLayoutSettings.Enabled))
                .GetBoolean());
        }

        [TestMethod]
        public async Task ExplicitNullSectionLoadsDefaultsWithoutDiscardingOtherSettings()
        {
            var loaded = await ReadSettingsAsync("""
                {
                    "AutoSplitCount": 7,
                    "MasterSatelliteLayout": null
                }
                """);

            Assert.AreEqual(7, loaded.AutoSplitCount);
            Assert.AreEqual(new MasterSatelliteLayoutSettings(), loaded.MasterSatelliteLayout);
        }

        [TestMethod]
        public void EveryEnumValueSerializesAsAStringAndRoundTrips()
        {
            var options = AppState.CreateSettingsJsonSerializerOptions();

            AssertEnumRoundTrip(MasterSide.Left, "\"left\"", options);
            AssertEnumRoundTrip(MasterSide.Right, "\"right\"", options);
            AssertEnumRoundTrip(SatelliteLayoutOrientation.Vertical, "\"vertical\"", options);
            AssertEnumRoundTrip(SatelliteLayoutOrientation.Horizontal, "\"horizontal\"", options);
            AssertEnumRoundTrip(MasterSatelliteOverflowPolicy.FloatOnCurrentDesktop, "\"floatOnCurrentDesktop\"", options);
            AssertEnumRoundTrip(MasterSatelliteOverflowPolicy.MoveToExistingDesktop, "\"moveToExistingDesktop\"", options);
            AssertEnumRoundTrip(MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop, "\"moveToExistingOrCreateDesktop\"", options);
            AssertEnumRoundTrip(AlgorithmicLayoutDisplayScope.PrimaryDisplay, "\"primaryDisplay\"", options);
            AssertEnumRoundTrip(AlgorithmicLayoutDisplayScope.UltrawideDisplays, "\"ultrawideDisplays\"", options);
            AssertEnumRoundTrip(AlgorithmicLayoutDisplayScope.AllDisplays, "\"allDisplays\"", options);
        }

        [TestMethod]
        public async Task AllValuesRoundTripThroughProductionSettingsEntity()
        {
            var expected = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.73,
                DefaultMasterSide = MasterSide.Right,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Horizontal,
                MaxSatellites = 8,
                OverflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxAutoCreatedDesktops = 7,
                FollowOverflowWindow = true,
            };
            var state = new AppState(m_settingsPath);

            await state.Settings.SaveAsync(x => x with { MasterSatelliteLayout = expected });

            var reloadedState = new AppState(m_settingsPath);
            var reloaded = await reloadedState.Settings.Value.FirstAsync();
            Assert.AreEqual(expected, reloaded.MasterSatelliteLayout);
        }

        [TestMethod]
        public async Task ValuesBelowMinimumAreClampedWhenDeserialized()
        {
            var loaded = await ReadSettingsAsync("""
                {
                    "MasterSatelliteLayout": {
                        "MasterRatio": 0.10,
                        "MaxSatellites": -10,
                        "MaxAutoCreatedDesktops": -10
                    }
                }
                """);

            Assert.AreEqual(0.50, loaded.MasterSatelliteLayout.MasterRatio);
            Assert.AreEqual(1, loaded.MasterSatelliteLayout.MaxSatellites);
            Assert.AreEqual(0, loaded.MasterSatelliteLayout.MaxAutoCreatedDesktops);
        }

        [TestMethod]
        public async Task ValuesAboveMaximumAreClampedWhenDeserialized()
        {
            var loaded = await ReadSettingsAsync("""
                {
                    "MasterSatelliteLayout": {
                        "MasterRatio": 0.90,
                        "MaxSatellites": 10,
                        "MaxAutoCreatedDesktops": 10
                    }
                }
                """);

            Assert.AreEqual(0.80, loaded.MasterSatelliteLayout.MasterRatio);
            Assert.AreEqual(9, loaded.MasterSatelliteLayout.MaxSatellites);
            Assert.AreEqual(9, loaded.MasterSatelliteLayout.MaxAutoCreatedDesktops);
        }

        [TestMethod]
        public void NonFiniteRatiosUseTheDefault()
        {
            Assert.AreEqual(0.60, new MasterSatelliteLayoutSettings { MasterRatio = double.NaN }.MasterRatio);
            Assert.AreEqual(0.60, new MasterSatelliteLayoutSettings { MasterRatio = double.PositiveInfinity }.MasterRatio);
            Assert.AreEqual(0.60, new MasterSatelliteLayoutSettings { MasterRatio = double.NegativeInfinity }.MasterRatio);
        }

        [TestMethod]
        public async Task UndefinedNumericEnumValuesUseSafeDefaults()
        {
            var loaded = await ReadSettingsAsync("""
                {
                    "AutoSplitCount": 6,
                    "MasterSatelliteLayout": {
                        "DefaultMasterSide": 999,
                        "DefaultSatelliteOrientation": 999,
                        "OverflowPolicy": 999,
                        "DisplayScope": 999
                    }
                }
                """);

            Assert.AreEqual(6, loaded.AutoSplitCount);
            Assert.AreEqual(MasterSide.Left, loaded.MasterSatelliteLayout.DefaultMasterSide);
            Assert.AreEqual(SatelliteLayoutOrientation.Vertical, loaded.MasterSatelliteLayout.DefaultSatelliteOrientation);
            Assert.AreEqual(MasterSatelliteOverflowPolicy.MoveToExistingDesktop, loaded.MasterSatelliteLayout.OverflowPolicy);
            Assert.AreEqual(AlgorithmicLayoutDisplayScope.PrimaryDisplay, loaded.MasterSatelliteLayout.DisplayScope);
        }

        [TestMethod]
        public async Task MalformedJsonFallsBackToDefaultsWithoutObservableError()
        {
            var loaded = await ReadSettingsAsync("{ invalid json }");

            Assert.AreEqual(new MasterSatelliteLayoutSettings(), loaded.MasterSatelliteLayout);
        }

        [TestMethod]
        public async Task TopLevelNullFallsBackToDefaultsWithoutObservableError()
        {
            var loaded = await ReadSettingsAsync("null");

            Assert.AreEqual(new MasterSatelliteLayoutSettings(), loaded.MasterSatelliteLayout);
        }

        [TestMethod]
        public async Task CommentsAndUnknownRootAndNestedPropertiesSurviveSave()
        {
            File.WriteAllText(m_settingsPath, """
                {
                    // root comment marker
                    "UnknownRoot": {
                        "Keep": true
                    },
                    "MasterSatelliteLayout": {
                        // nested comment marker
                        "Enabled": false,
                        "MasterRatio": 0.65,
                        "UnknownNested": "keep me"
                    }
                }
                """);
            var state = new AppState(m_settingsPath);
            var loaded = await state.Settings.Value.FirstAsync();

            Assert.AreEqual(0.65, loaded.MasterSatelliteLayout.MasterRatio);
            await state.Settings.SaveAsync(x => x with
            {
                MasterSatelliteLayout = x.MasterSatelliteLayout with { FollowOverflowWindow = true }
            });

            var savedJson = File.ReadAllText(m_settingsPath);
            StringAssert.Contains(savedJson, "root comment marker");
            StringAssert.Contains(savedJson, "nested comment marker");

            using var document = ParseSavedJson(savedJson);
            Assert.IsTrue(document.RootElement
                .GetProperty("UnknownRoot")
                .GetProperty("Keep")
                .GetBoolean());
            var nested = document.RootElement.GetProperty(nameof(Settings.MasterSatelliteLayout));
            Assert.AreEqual("keep me", nested.GetProperty("UnknownNested").GetString());
            Assert.IsTrue(nested
                .GetProperty(nameof(MasterSatelliteLayoutSettings.FollowOverflowWindow))
                .GetBoolean());
        }

        private async Task<Settings> ReadSettingsAsync(string json)
        {
            File.WriteAllText(m_settingsPath, json);
            var state = new AppState(m_settingsPath);
            return await state.Settings.Value.FirstAsync();
        }

        private static JsonDocument ParseSavedJson(string json)
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }

        private static void AssertEnumRoundTrip<T>(T value, string expectedJson, JsonSerializerOptions options)
            where T : struct, Enum
        {
            var json = JsonSerializer.Serialize(value, options);
            Assert.AreEqual(expectedJson, json);
            Assert.AreEqual(value, JsonSerializer.Deserialize<T>(json, options));
        }
    }
}
