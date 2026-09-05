using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FancyWM.Models;
using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Serilog;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class SettingsViewModelTest
    {
        [TestMethod]
        public void ChangingOneMasterSatelliteFieldPreservesTheRestOfTheNestedSettings()
        {
            var originalLayout = CreateNonDefaultLayout();
            var model = new RecordingSettingsEntity(new Settings
            {
                MasterSatelliteLayout = originalLayout,
                PanelHeight = 37,
                ShowStartupWindow = false,
            });

            using var viewModel = CreateViewModel(model);
            viewModel.MasterRatio = 0.68;

            Assert.AreEqual(1, model.SaveCount);
            Assert.AreEqual(originalLayout with { MasterRatio = 0.68 }, model.Current.MasterSatelliteLayout);
            Assert.AreEqual(37, model.Current.PanelHeight);
            Assert.IsFalse(model.Current.ShowStartupWindow);
        }

        [TestMethod]
        public void UwqhdPresetChangesOnlyItsDocumentedSettingsAndSavesOnce()
        {
            var model = new RecordingSettingsEntity(new Settings
            {
                MasterSatelliteLayout = CreateNonDefaultLayout(),
                PanelHeight = 37,
                ShowStartupWindow = false,
            });

            using var viewModel = CreateViewModel(model);
            viewModel.ApplyUwqhdPreset();

            Assert.AreEqual(1, model.SaveCount);
            Assert.AreEqual(new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.60,
                DefaultMasterSide = MasterSide.Left,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                MaxSatellites = 3,
                OverflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingDesktop,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxAutoCreatedDesktops = 7,
                FollowOverflowWindow = false,
            }, model.Current.MasterSatelliteLayout);
            Assert.AreEqual(37, model.Current.PanelHeight);
            Assert.IsFalse(model.Current.ShowStartupWindow);
        }

        [TestMethod]
        public void OverflowPolicyUpdatesDependentUiState()
        {
            var model = new RecordingSettingsEntity(new Settings());
            using var viewModel = CreateViewModel(model);

            Assert.IsTrue(viewModel.CanFollowOverflowWindow);
            Assert.IsFalse(viewModel.CanConfigureMaxAutoCreatedDesktops);

            viewModel.OverflowPolicy = MasterSatelliteOverflowPolicy.FloatOnCurrentDesktop;
            Assert.IsFalse(viewModel.CanFollowOverflowWindow);
            Assert.IsFalse(viewModel.CanConfigureMaxAutoCreatedDesktops);

            viewModel.OverflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop;
            Assert.IsTrue(viewModel.CanFollowOverflowWindow);
            Assert.IsTrue(viewModel.CanConfigureMaxAutoCreatedDesktops);
        }

        private static SettingsViewModel CreateViewModel(RecordingSettingsEntity model)
        {
            var logger = new LoggerConfiguration().CreateLogger();
            return new SettingsViewModel(model, logger, () => Task.FromResult(false));
        }

        private static MasterSatelliteLayoutSettings CreateNonDefaultLayout()
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.72,
                DefaultMasterSide = MasterSide.Right,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Horizontal,
                MaxSatellites = 8,
                OverflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxAutoCreatedDesktops = 7,
                FollowOverflowWindow = true,
            };
        }

        private sealed class RecordingSettingsEntity : IObservableFileEntity<Settings>
        {
            private readonly List<IObserver<Settings>> m_observers = [];

            public RecordingSettingsEntity(Settings initial)
            {
                Current = initial;
            }

            public Settings Current { get; private set; }

            public int SaveCount { get; private set; }

            public string FullPath => "settings-view-model-test.json";

            public IObservable<Settings> Value => this;

            public IDisposable Subscribe(IObserver<Settings> observer)
            {
                m_observers.Add(observer);
                observer.OnNext(Current);
                return new Subscription(m_observers, observer);
            }

            public Task SaveAsync(Func<Settings, Settings> update)
            {
                Current = update(Current);
                SaveCount++;
                foreach (var observer in m_observers.ToArray())
                {
                    observer.OnNext(Current);
                }
                return Task.CompletedTask;
            }

            private sealed class Subscription(List<IObserver<Settings>> observers, IObserver<Settings> observer) : IDisposable
            {
                public void Dispose()
                {
                    observers.Remove(observer);
                }
            }
        }
    }
}
