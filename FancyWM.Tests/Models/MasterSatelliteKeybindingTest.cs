using System;
using System.Linq;
using System.Reflection;

using FancyWM.Models;
using FancyWM.Utilities;
using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class MasterSatelliteKeybindingTest
    {
        [TestMethod]
        public void DefaultsContainEveryActionAndMasterSatelliteBindingsAreConflictFree()
        {
            var defaults = new KeybindingDictionary(useDefaults: true);
            var actions = Enum.GetValues<BindableAction>();

            CollectionAssert.AreEquivalent(actions, defaults.Keys.ToArray());
            AssertBinding(defaults, BindableAction.ToggleMasterSatelliteLayout, KeyCode.A);
            AssertBinding(defaults, BindableAction.PromoteFocusedWindowToMaster, KeyCode.M);
            AssertBinding(defaults, BindableAction.SwapMasterSide, KeyCode.B);
            Assert.IsNull(defaults[BindableAction.ToggleSatelliteOrientation]);
            Assert.IsNull(defaults[BindableAction.ResetMasterRatio]);
            Assert.IsNull(defaults[BindableAction.RebalanceMasterSatelliteLayout]);

            var duplicates = defaults
                .Where(pair => pair.Value != null)
                .GroupBy(pair => string.Join(",", pair.Value!.Keys.OrderBy(key => key)))
                .Where(group => group.Count() > 1)
                .ToArray();
            Assert.AreEqual(0, duplicates.Length,
                $"Duplicate default bindings: {string.Join("; ", duplicates.Select(group => group.Key))}");
        }

        [TestMethod]
        public void UiGroupsOrderingAndNeutralResourcesCoverMasterSatelliteActions()
        {
            var expected = new[]
            {
                BindableAction.ToggleMasterSatelliteLayout,
                BindableAction.PromoteFocusedWindowToMaster,
                BindableAction.SwapMasterSide,
                BindableAction.ToggleSatelliteOrientation,
                BindableAction.ResetMasterRatio,
                BindableAction.RebalanceMasterSatelliteLayout,
            };
            var defaults = new KeybindingDictionary(useDefaults: true);
            var viewModels = KeybindingViewModel.FromDictionary(defaults);
            var createGroups = typeof(SettingsViewModel).GetMethod(
                "CreateKeybindingGroups",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(createGroups);
            var groups = (System.Collections.Generic.IList<KeybindingGroup>)createGroups.Invoke(
                null,
                new object[] { viewModels })!;
            var groupedActions = groups.SelectMany(group => group.Keybindings)
                .Select(viewModel => viewModel.Action)
                .ToArray();

            CollectionAssert.AreEquivalent(Enum.GetValues<BindableAction>(), groupedActions);
            var masterSatelliteGroup = groups.Single(group =>
                group.Keybindings.Any(viewModel =>
                    viewModel.Action == BindableAction.ToggleMasterSatelliteLayout));
            CollectionAssert.AreEquivalent(
                expected,
                masterSatelliteGroup.Keybindings.Select(viewModel => viewModel.Action).ToArray());

            var orderingField = typeof(Controls.KeybindingList).GetField(
                "Ordering",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(orderingField);
            var ordering = (BindableAction[])orderingField.GetValue(null)!;
            Assert.IsTrue(expected.All(ordering.Contains));

            foreach (var action in expected)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(
                    Resources.Strings.ResourceManager.GetString($"Keybinding.{action}.Caption")));
                Assert.IsFalse(string.IsNullOrWhiteSpace(
                    Resources.Strings.ResourceManager.GetString($"Keybinding.{action}.Description")));
            }
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                Resources.Strings.ResourceManager.GetString("Keybindings.MasterSatellite")));
        }

        private static void AssertBinding(
            KeybindingDictionary defaults,
            BindableAction action,
            KeyCode key)
        {
            var binding = defaults[action];
            Assert.IsNotNull(binding);
            Assert.IsFalse(binding.IsDirectMode);
            Assert.AreEqual(1, binding.Keys.Count);
            Assert.IsTrue(binding.Keys.Contains(key));
        }
    }
}
