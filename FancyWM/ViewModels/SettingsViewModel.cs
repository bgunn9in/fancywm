using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using FancyWM.Models;
using FancyWM.Resources;
using FancyWM.Utilities;

using Serilog;

namespace FancyWM.ViewModels
{
    public record class KeybindingGroup(string GroupKey, IList<KeybindingViewModel> Keybindings);

    public sealed class SettingsViewModel : ViewModelBase
    {
        private static readonly char[] AsciiWhitespaceChars = [' ', '\t', '\r', '\v', '\f', '\n'];

        public IObservableFileEntity<Settings> Model { get; }

        public ActivationHotkey? SelectedActivationHotkey { get => m_activationHotkey; set => SetField(ref m_activationHotkey, value); }

        public ObservableCollection<ActivationHotkey> ActivationHotkeyOptions { get; }
            = new ObservableCollection<ActivationHotkey>(ActivationHotkey.AllowedHotkeys);

        public bool ActivateOnCapsLock { get => m_activateOnCapsLock; set => SetField(ref m_activateOnCapsLock, value); }
        public bool ShowStartupWindow { get => m_showStartupWindow; set => SetField(ref m_showStartupWindow, value); }
        public bool NotifyVirtualDesktopServiceIncompatibility { get => m_notifyVirtualDesktopServiceIncompatibility; set => SetField(ref m_notifyVirtualDesktopServiceIncompatibility, value); }
        public bool AllocateNewPanelSpace { get => m_allocateNewPanelSpace; set => SetField(ref m_allocateNewPanelSpace, value); }
        public bool AutoCollapsePanels { get => m_autoCollapsePanels; set => SetField(ref m_autoCollapsePanels, value); }
        public int AutoSplitCount { get => m_autoSplitCount; set => SetField(ref m_autoSplitCount, value); }

        public bool MasterSatelliteEnabled { get => m_masterSatelliteEnabled; set => SetField(ref m_masterSatelliteEnabled, value); }
        public double MasterRatio { get => m_masterRatio; set => SetField(ref m_masterRatio, value); }
        public MasterSide DefaultMasterSide { get => m_defaultMasterSide; set => SetField(ref m_defaultMasterSide, value); }
        public SatelliteLayoutOrientation DefaultSatelliteOrientation { get => m_defaultSatelliteOrientation; set => SetField(ref m_defaultSatelliteOrientation, value); }
        public int MaxSatellites { get => m_maxSatellites; set => SetField(ref m_maxSatellites, value); }

        public MasterSatelliteOverflowPolicy OverflowPolicy
        {
            get => m_overflowPolicy;
            set
            {
                if (m_overflowPolicy == value)
                {
                    return;
                }

                SetField(ref m_overflowPolicy, value);
                base.NotifyPropertyChanged(nameof(CanConfigureMaxAutoCreatedDesktops));
                base.NotifyPropertyChanged(nameof(CanFollowOverflowWindow));
            }
        }

        public AlgorithmicLayoutDisplayScope AlgorithmicLayoutDisplayScope
        {
            get => m_algorithmicLayoutDisplayScope;
            set => SetField(ref m_algorithmicLayoutDisplayScope, value);
        }

        public int MaxAutoCreatedDesktops { get => m_maxAutoCreatedDesktops; set => SetField(ref m_maxAutoCreatedDesktops, value); }
        public bool FollowOverflowWindow { get => m_followOverflowWindow; set => SetField(ref m_followOverflowWindow, value); }

        public IReadOnlyList<MasterSide> MasterSideOptions { get; } = Enum.GetValues<MasterSide>();
        public IReadOnlyList<SatelliteLayoutOrientation> SatelliteOrientationOptions { get; } = Enum.GetValues<SatelliteLayoutOrientation>();
        public IReadOnlyList<MasterSatelliteOverflowPolicy> OverflowPolicyOptions { get; } = Enum.GetValues<MasterSatelliteOverflowPolicy>();
        public IReadOnlyList<AlgorithmicLayoutDisplayScope> AlgorithmicLayoutDisplayScopeOptions { get; } = Enum.GetValues<AlgorithmicLayoutDisplayScope>();

        public bool CanConfigureMaxAutoCreatedDesktops
            => OverflowPolicy == MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop;

        public bool CanFollowOverflowWindow
            => OverflowPolicy != MasterSatelliteOverflowPolicy.FloatOnCurrentDesktop;

        public ICommand ApplyUwqhdPresetCommand { get; }

        public bool DelayReposition { get => m_delayReposition; set => SetField(ref m_delayReposition, value); }
        public bool AnimateWindowMovement { get => m_animateWindowMovement; set => SetField(ref m_animateWindowMovement, value); }
        public bool MouseAutoFocus
        {
            get => SystemParameters.Instance.ActiveWindowTracking;
            set
            {
                try
                {
                    SystemParameters.Instance.ActiveWindowTracking = value;
                }
                catch (Exception e)
                {
                    var valueStr = value ? "enable" : "disable";
                    m_logger.Error(e, $"Failed to {valueStr} mouse tracking");
                }
                NotifyPropertyChanged(nameof(MouseAutoFocus));
            }
        }

        public bool ModifierMoveWindow { get => m_modifierMoveWindow; set => SetField(ref m_modifierMoveWindow, value); }
        public bool ModifierMoveWindowAutoFocus { get => m_modifierMoveWindowAutoFocus; set => SetField(ref m_modifierMoveWindowAutoFocus, value); }
        public bool ShowContextHints { get => m_showContextHints; set => SetField(ref m_showContextHints, value); }
        public bool RunsAtStartup
        {
            get => m_runsAtStartup;
            set
            {
                if (value)
                {
                    Autostart.EnableAsync()
                        .ContinueWith(t =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                SetField(ref m_runsAtStartup, t.Result, nameof(RunsAtStartup));
                            });
                        });
                }
                else
                {
                    Autostart.DisableAsync()
                        .ContinueWith(t =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                SetField(ref m_runsAtStartup, !t.Result, nameof(RunsAtStartup));
                            });
                        });
                }
            }
        }

        public bool RunsAsAdministrator
        {
            get => m_runsAsAdministrator;
            set
            {
                if (value)
                {
                    File.WriteAllBytes("administrator-mode", []);
                }
                else
                {
                    File.Delete("administrator-mode");
                }
                SetField(ref m_runsAsAdministrator, value);
            }
        }

        public static bool IsAdministrator => (new WindowsPrincipal(WindowsIdentity.GetCurrent()))
                      .IsInRole(WindowsBuiltInRole.Administrator);

        public bool OverrideAccentColor { get => m_customAccentColor; set => SetField(ref m_customAccentColor, value); }

        public Color CustomAccentColor { get => m_accentColor; set => SetField(ref m_accentColor, value); }

        [DerivedProperty(nameof(CustomAccentColor))]
        public byte CustomAccentColorA
        {
            get => m_accentColor.A; set
            {
                CustomAccentColor = Color.FromArgb(value, CustomAccentColorR, CustomAccentColorG, CustomAccentColorB);
            }
        }

        [DerivedProperty(nameof(CustomAccentColor))]
        public byte CustomAccentColorR
        {
            get => m_accentColor.R; set
            {
                CustomAccentColor = Color.FromArgb(CustomAccentColorA, value, CustomAccentColorG, CustomAccentColorB);
            }
        }

        [DerivedProperty(nameof(CustomAccentColor))]
        public byte CustomAccentColorG
        {
            get => m_accentColor.G; set
            {
                CustomAccentColor = Color.FromArgb(CustomAccentColorA, CustomAccentColorR, value, CustomAccentColorB);
            }
        }

        [DerivedProperty(nameof(CustomAccentColor))]
        public byte CustomAccentColorB
        {
            get => m_accentColor.B; set
            {
                CustomAccentColor = Color.FromArgb(CustomAccentColorA, CustomAccentColorR, CustomAccentColorG, value);
            }
        }

        public int PanelHeight
        {
            get => m_panelHeight;
            set => SetField(ref m_panelHeight, value);
        }

        public int PanelFontSize
        {
            get => m_panelFontSize;
            set => SetField(ref m_panelFontSize, value);
        }

        public int WindowPadding
        {
            get => m_windowPadding;
            set => SetField(ref m_windowPadding, value);
        }

        public bool ShowFocus { get => m_showFocus; set => SetField(ref m_showFocus, value); }

        public bool ShowFocusDuringAction { get => m_showFocusDuringAction; set => SetField(ref m_showFocusDuringAction, value); }

        public ObservableCollection<KeybindingViewModel>? Keybindings
        {
            get => m_keybindings; set
            {
                SetField(ref m_keybindings, value);
                base.NotifyPropertyChanged(nameof(KeybindingGroups));
            }
        }

        [DerivedProperty(nameof(Keybindings))]
        public IList<KeybindingGroup>? KeybindingGroups => CreateKeybindingGroups(m_keybindings!);

        public IList<string>? ProcessIgnoreList { get => m_processIgnoreList; set => SetField(ref m_processIgnoreList, value); }

        public IList<string>? ClassIgnoreList { get => m_classIgnoreList; set => SetField(ref m_classIgnoreList, value); }

        public bool MultiMonitorSupport { get => m_multiMonitorSupport; set => SetField(ref m_multiMonitorSupport, value); }

        public bool SoundOnFailure { get => m_soundOnFailure; set => SetField(ref m_soundOnFailure, value); }

        private bool m_runsAtStartup;
        private bool m_runsAsAdministrator;
        private bool m_showStartupWindow;
        private bool m_notifyVirtualDesktopServiceIncompatibility;
        private bool m_allocateNewPanelSpace;
        private bool m_autoCollapsePanels;
        private int m_autoSplitCount;
        private bool m_masterSatelliteEnabled;
        private double m_masterRatio;
        private MasterSide m_defaultMasterSide;
        private SatelliteLayoutOrientation m_defaultSatelliteOrientation;
        private int m_maxSatellites;
        private MasterSatelliteOverflowPolicy m_overflowPolicy;
        private AlgorithmicLayoutDisplayScope m_algorithmicLayoutDisplayScope;
        private int m_maxAutoCreatedDesktops;
        private bool m_followOverflowWindow;
        private bool m_delayReposition;
        private bool m_customAccentColor;
        private bool m_animateWindowMovement;
        private bool m_mouseAutoFocus;
        private bool m_modifierMoveWindow;
        private bool m_modifierMoveWindowAutoFocus;
        private Color m_accentColor;
        private readonly IDisposable m_subscription;
        private bool m_isInit = false;
        private bool m_suppressSave;
        private ObservableCollection<KeybindingViewModel>? m_keybindings;
        private int m_panelHeight;
        private int m_panelFontSize;
        private int m_windowPadding;
        private ActivationHotkey? m_activationHotkey;
        private bool m_activateOnCapsLock;
        private IList<string>? m_processIgnoreList;
        private IList<string>? m_classIgnoreList;
        private bool m_multiMonitorSupport;
        private bool m_showContextHints;
        private bool m_soundOnFailure;
        private bool m_showFocus;
        private bool m_showFocusDuringAction;
        private readonly ILogger m_logger;

        public SettingsViewModel(IObservableFileEntity<Settings> observable)
            : this(observable, App.Current.Logger, Autostart.IsEnabledAsync)
        {
        }

        internal SettingsViewModel(
            IObservableFileEntity<Settings> observable,
            ILogger logger,
            Func<Task<bool>> getAutostartEnabledAsync)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Model = observable;
            m_logger = logger;
            ApplyUwqhdPresetCommand = new DelegateCommand(_ => ApplyUwqhdPreset());
            m_subscription = observable
                .Subscribe(settings =>
            {
                dispatcher.Invoke(() =>
                {
                    m_isInit = false;
                    m_logger.Debug($"{nameof(FancyWM.ViewModels.SettingsViewModel)} received new Settings");

                    SelectedActivationHotkey = settings.ActivationHotkey;
                    ActivateOnCapsLock = settings.ActivateOnCapsLock;
                    ShowStartupWindow = settings.ShowStartupWindow;
                    NotifyVirtualDesktopServiceIncompatibility = settings.NotifyVirtualDesktopServiceIncompatibility;
                    AllocateNewPanelSpace = settings.AllocateNewPanelSpace;
                    AutoCollapsePanels = settings.AutoCollapsePanels;
                    AutoSplitCount = settings.AutoSplitCount;
                    MasterSatelliteEnabled = settings.MasterSatelliteLayout.Enabled;
                    MasterRatio = settings.MasterSatelliteLayout.MasterRatio;
                    DefaultMasterSide = settings.MasterSatelliteLayout.DefaultMasterSide;
                    DefaultSatelliteOrientation = settings.MasterSatelliteLayout.DefaultSatelliteOrientation;
                    MaxSatellites = settings.MasterSatelliteLayout.MaxSatellites;
                    OverflowPolicy = settings.MasterSatelliteLayout.OverflowPolicy;
                    AlgorithmicLayoutDisplayScope = settings.MasterSatelliteLayout.DisplayScope;
                    MaxAutoCreatedDesktops = settings.MasterSatelliteLayout.MaxAutoCreatedDesktops;
                    FollowOverflowWindow = settings.MasterSatelliteLayout.FollowOverflowWindow;
                    DelayReposition = settings.DelayReposition;
                    AnimateWindowMovement = settings.AnimateWindowMovement;
                    ModifierMoveWindow = settings.ModifierMoveWindow;
                    ModifierMoveWindowAutoFocus = settings.ModifierMoveWindowAutoFocus;
                    OverrideAccentColor = settings.OverrideAccentColor;
                    CustomAccentColor = settings.CustomAccentColor;
                    WindowPadding = settings.WindowPadding;
                    PanelHeight = settings.PanelHeight;
                    PanelFontSize = settings.PanelFontSize;
                    ProcessIgnoreList = settings.ProcessIgnoreList;
                    ClassIgnoreList = settings.ClassIgnoreList;
                    MultiMonitorSupport = settings.MultiMonitorSupport;
                    ShowContextHints = settings.ShowContextHints;
                    SoundOnFailure = settings.SoundOnFailure;
                    ShowFocus = settings.ShowFocus;
                    ShowFocusDuringAction = settings.ShowFocusDuringAction;

                    var newKeybindings = KeybindingViewModel.FromDictionary(settings.Keybindings);
                    if (m_keybindings == null)
                    {
                        Keybindings = [.. newKeybindings];
                        foreach (var vm in Keybindings)
                        {
                            vm.PropertyChanged += OnKeybindingPropertyChanged;
                        }
                    }
                    else
                    {
                        Debug.Assert(m_keybindings.Count == newKeybindings.Count);
                        foreach (var vm in newKeybindings)
                        {
                            var existingVm = m_keybindings.First(x => x.Action == vm.Action);
                            existingVm.Pattern = vm.Pattern;
                            existingVm.IsDirectMode = vm.IsDirectMode;
                        }
                    }

                    void completeInitialization(Task<bool> task)
                    {
                        bool? isAutostartEnabled = null;
                        try
                        {
                            isAutostartEnabled = task.GetAwaiter().GetResult();
                        }
                        catch (Exception e)
                        {
                            m_logger.Error(e, "Failed to determine whether FancyWM runs at startup");
                        }

                        dispatcher.Invoke(() =>
                        {
                            if (isAutostartEnabled.HasValue)
                            {
                                SetField(ref m_runsAtStartup, isAutostartEnabled.Value, nameof(RunsAtStartup));
                            }
                            m_isInit = true;
                        });
                    }

                    Task<bool> initializationTask;
                    try
                    {
                        initializationTask = getAutostartEnabledAsync();
                    }
                    catch (Exception e)
                    {
                        m_logger.Error(e, "Failed to start the autostart state query");
                        m_isInit = true;
                        return;
                    }

                    if (initializationTask.IsCompleted)
                    {
                        completeInitialization(initializationTask);
                    }
                    else
                    {
                        _ = initializationTask.ContinueWith(
                            completeInitialization,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                });
            });

            if (File.Exists("administrator-mode"))
            {
                SetField(ref m_runsAsAdministrator, true, nameof(RunsAsAdministrator));
            }
        }

        private void OnKeybindingPropertyChanged(object? c, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(KeybindingViewModel.Pattern) || e.PropertyName == nameof(KeybindingViewModel.IsDirectMode))
            {
                var vm = ((KeybindingViewModel)c!);
                var keybindingSet = new Dictionary<IReadOnlySet<KeyCode>, KeybindingViewModel>(EqualityComparer<KeyCode>.Default.ToSequenceComparer());
                var duplicatesToRemove = Keybindings!
                    .Where(x => x.Pattern != null)
                    .GroupBy(x => x.Pattern, EqualityComparer<KeyCode>.Default.ToSequenceComparer())
                    .Where(g => g.Count() > 1);
                foreach (var duplicates in duplicatesToRemove)
                {
                    foreach (var duplicate in duplicates)
                    {
                        if (duplicate != vm)
                        {
                            duplicate.Pattern = null;
                        }
                    }
                }

                bool isValid = !vm.IsDirectMode || vm.Pattern == null
                    || KeyCodeHelper.CanMapToValidModifiersAndKeyCode(vm.Pattern);

                if (!isValid)
                {
                    vm.HasErrors = true;
                    vm.SetIsDirectModeInternal(false);
                }
                else
                {
                    vm.HasErrors = false;
                    SaveChanges();
                }
            }
        }

        protected override void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            base.NotifyPropertyChanged(propertyName);

            if (!m_isInit || m_suppressSave)
            {
                return;
            }

            SaveChanges();
        }

        public override void Dispose()
        {
            m_subscription?.Dispose();
            if (m_keybindings != null)
            {
                foreach (var keybinding in m_keybindings)
                {
                    keybinding.PropertyChanged -= OnKeybindingPropertyChanged;
                }
            }
            base.Dispose();
        }

        private void SaveChanges()
        {
            var saveTask = Model.SaveAsync(x =>
            {
                m_logger.Debug($"{nameof(SettingsViewModel)} is overwriting existing Settings");

                return x with
                {
                    ActivationHotkey = SelectedActivationHotkey!,
                    ActivateOnCapsLock = ActivateOnCapsLock,
                    ShowStartupWindow = ShowStartupWindow,
                    NotifyVirtualDesktopServiceIncompatibility = NotifyVirtualDesktopServiceIncompatibility,
                    AllocateNewPanelSpace = AllocateNewPanelSpace,
                    AutoCollapsePanels = AutoCollapsePanels,
                    AutoSplitCount = AutoSplitCount,
                    MasterSatelliteLayout = x.MasterSatelliteLayout with
                    {
                        Enabled = MasterSatelliteEnabled,
                        MasterRatio = MasterRatio,
                        DefaultMasterSide = DefaultMasterSide,
                        DefaultSatelliteOrientation = DefaultSatelliteOrientation,
                        MaxSatellites = MaxSatellites,
                        OverflowPolicy = OverflowPolicy,
                        DisplayScope = AlgorithmicLayoutDisplayScope,
                        MaxAutoCreatedDesktops = MaxAutoCreatedDesktops,
                        FollowOverflowWindow = FollowOverflowWindow,
                    },
                    DelayReposition = DelayReposition,
                    AnimateWindowMovement = AnimateWindowMovement,
                    ModifierMoveWindow = ModifierMoveWindow,
                    ModifierMoveWindowAutoFocus = ModifierMoveWindowAutoFocus,
                    CustomAccentColor = CustomAccentColor,
                    OverrideAccentColor = OverrideAccentColor,
                    Keybindings = KeybindingViewModel.ToDictionary(m_keybindings!),
                    WindowPadding = WindowPadding,
                    PanelHeight = PanelHeight,
                    PanelFontSize = PanelFontSize,
                    ShowContextHints = ShowContextHints,
                    ProcessIgnoreList = [.. ProcessIgnoreList!],
                    ClassIgnoreList = [.. ClassIgnoreList!],
                    MultiMonitorSupport = MultiMonitorSupport,
                    SoundOnFailure = SoundOnFailure,
                    ShowFocus = ShowFocus,
                    ShowFocusDuringAction = ShowFocusDuringAction
                };
            });

            _ = saveTask.ContinueWith(
                task => m_logger.Error(task.Exception, "Failed to save settings"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void ApplyUwqhdPreset()
        {
            m_suppressSave = true;
            try
            {
                MasterRatio = 0.60;
                DefaultMasterSide = MasterSide.Left;
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical;
                MaxSatellites = 3;
                OverflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingDesktop;
                FollowOverflowWindow = false;
            }
            finally
            {
                m_suppressSave = false;
            }

            if (m_isInit)
            {
                SaveChanges();
            }
        }

        private static List<KeybindingGroup> CreateKeybindingGroups(IList<KeybindingViewModel> keybindings)
        {
            var buckets = new[]
            {
                ("FancyWM", new HashSet<BindableAction>{
                    BindableAction.ToggleManager,
                    BindableAction.ShowDesktop,
                    BindableAction.RefreshWorkspace,
                    BindableAction.Cancel,
                }),
                (Strings.Keybindings_Panels, new HashSet<BindableAction>{
                    BindableAction.CreateHorizontalPanel,
                    BindableAction.CreateVerticalPanel,
                    BindableAction.CreateStackPanel
                }),
                (Strings.ResourceManager.GetString("Keybindings.MasterSatellite") ?? "Master + Satellites", new HashSet<BindableAction>{
                    BindableAction.ToggleMasterSatelliteLayout,
                    BindableAction.PromoteFocusedWindowToMaster,
                    BindableAction.SwapMasterSide,
                    BindableAction.ToggleSatelliteOrientation,
                    BindableAction.ResetMasterRatio,
                    BindableAction.RebalanceMasterSatelliteLayout,
                }),
                (Strings.Keybindings_Windows, new HashSet<BindableAction>{
                    BindableAction.PullWindowUp,
                    BindableAction.ToggleFloatingMode,
                    BindableAction.MoveLeft,
                    BindableAction.MoveRight,
                    BindableAction.MoveDown,
                    BindableAction.MoveUp,
                    BindableAction.SwapLeft,
                    BindableAction.SwapRight,
                    BindableAction.SwapDown,
                    BindableAction.SwapUp,
                }),
                (Strings.Keybindings_Focus, new HashSet<BindableAction>{
                    BindableAction.MoveFocusLeft,
                    BindableAction.MoveFocusRight,
                    BindableAction.MoveFocusDown,
                    BindableAction.MoveFocusUp,
                }),
                (Strings.Keybindings_Sizing, new HashSet<BindableAction>{
                    BindableAction.IncreaseWidth,
                    BindableAction.IncreaseHeight,
                    BindableAction.DecreaseWidth,
                    BindableAction.DecreaseHeight,
                }),
                (Strings.Keybindings_VirtualDesktops, new HashSet<BindableAction>{
                    BindableAction.SwitchToPreviousDesktop,
                    BindableAction.SwitchToLeftDesktop,
                    BindableAction.SwitchToRightDesktop,
                    BindableAction.MoveToPreviousDesktop,
                    BindableAction.MoveToLeftDesktop,
                    BindableAction.MoveToRightDesktop,
                    BindableAction.SwitchToDesktop1,
                    BindableAction.SwitchToDesktop2,
                    BindableAction.SwitchToDesktop3,
                    BindableAction.SwitchToDesktop4,
                    BindableAction.SwitchToDesktop5,
                    BindableAction.SwitchToDesktop6,
                    BindableAction.SwitchToDesktop7,
                    BindableAction.SwitchToDesktop8,
                    BindableAction.SwitchToDesktop9,
                    BindableAction.MoveToDesktop1,
                    BindableAction.MoveToDesktop2,
                    BindableAction.MoveToDesktop3,
                    BindableAction.MoveToDesktop4,
                    BindableAction.MoveToDesktop5,
                    BindableAction.MoveToDesktop6,
                    BindableAction.MoveToDesktop7,
                    BindableAction.MoveToDesktop8,
                    BindableAction.MoveToDesktop9,
                }),
                (Strings.Keybindings_MultipleDisplays, new HashSet<BindableAction>{
                    BindableAction.SwitchToPreviousDisplay,
                    BindableAction.MoveToPreviousDisplay,
                    BindableAction.SwitchToDisplay1,
                    BindableAction.SwitchToDisplay2,
                    BindableAction.SwitchToDisplay3,
                    BindableAction.SwitchToDisplay4,
                    BindableAction.SwitchToDisplay5,
                    BindableAction.SwitchToDisplay6,
                    BindableAction.SwitchToDisplay7,
                    BindableAction.SwitchToDisplay8,
                    BindableAction.SwitchToDisplay9,
                    BindableAction.MoveToDisplay1,
                    BindableAction.MoveToDisplay2,
                    BindableAction.MoveToDisplay3,
                    BindableAction.MoveToDisplay4,
                    BindableAction.MoveToDisplay5,
                    BindableAction.MoveToDisplay6,
                    BindableAction.MoveToDisplay7,
                    BindableAction.MoveToDisplay8,
                    BindableAction.MoveToDisplay9,
                })
            };

            List<BindableAction> missing = [.. Enum.GetValues(typeof(BindableAction)).OfType<BindableAction>().Except(buckets.SelectMany(x => x.Item2))];
            Debug.Assert(missing.Count == 0);

            return buckets.Select((bucket) =>
            {
                var (group, actions) = bucket;
                return new KeybindingGroup(
                    GroupKey: group,
                    Keybindings: keybindings.Where(x => actions.Contains(x.Action)).ToList());
            }).ToList();
        }
    }
}
