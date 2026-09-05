using System;
using System.Collections.Generic;
using System.Linq;

using FancyWM.Models;
using FancyWM.Layouts.Tiling;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal readonly record struct MasterSatelliteRuntimeEntry(
        LayoutStateKey Key,
        MasterSatelliteRuntimeState State);

    internal readonly record struct MasterSatelliteSettingsTransition(
        bool WasEligible,
        bool IsEligible,
        int RemovedStateCount);

    internal sealed record class MasterSatelliteLifecycleResult(
        LayoutStateKey Key,
        bool Eligible,
        bool StatePresent,
        bool StateAdded,
        bool StateRemoved,
        string Action,
        MasterSatelliteOperationResult? Operation,
        MasterSatelliteInvariantResult? Invariant);

    internal enum MasterSatelliteLocalMutationDisposition
    {
        NotActive,
        Placed,
        Removed,
        NotFound,
        Rejected,
    }

    internal sealed record class MasterSatelliteLocalMutationResult(
        MasterSatelliteLocalMutationDisposition Disposition,
        LayoutStateKey? LayoutKey,
        MasterSatelliteOperationResult? Operation,
        MasterSatelliteInvariantResult? Invariant)
    {
        public bool Attempted => Disposition != MasterSatelliteLocalMutationDisposition.NotActive;

        public bool Succeeded => Disposition is MasterSatelliteLocalMutationDisposition.Placed
            or MasterSatelliteLocalMutationDisposition.Removed;
    }

    /// <summary>
    /// Narrow state-store boundary used by a display-scoped tiling service. A later
    /// workspace coordinator can replace this implementation without adding a
    /// second runtime-state dictionary.
    /// </summary>
    internal interface IMasterSatelliteRuntimeRegistry
    {
        int Count { get; }

        bool TryAdd(LayoutStateKey key, MasterSatelliteRuntimeState state);

        bool TryGet(LayoutStateKey key, out MasterSatelliteRuntimeState state);

        bool Remove(LayoutStateKey key);

        int RemoveDisplay(IDisplay display);

        IReadOnlyList<MasterSatelliteRuntimeEntry> SnapshotForDisplay(IDisplay display);

        void Clear();
    }

    internal sealed class MasterSatelliteRuntimeRegistry : IMasterSatelliteRuntimeRegistry
    {
        private readonly Dictionary<LayoutStateKey, MasterSatelliteRuntimeState> m_states
            = new(LayoutStateKeyIdentityComparer.Instance);

        public int Count => m_states.Count;

        public bool TryAdd(LayoutStateKey key, MasterSatelliteRuntimeState state)
        {
            ArgumentNullException.ThrowIfNull(key.VirtualDesktop);
            ArgumentNullException.ThrowIfNull(key.Display);
            ArgumentNullException.ThrowIfNull(state);
            return m_states.TryAdd(key, state);
        }

        public bool TryGet(LayoutStateKey key, out MasterSatelliteRuntimeState state)
        {
            return m_states.TryGetValue(key, out state!);
        }

        public bool Remove(LayoutStateKey key)
        {
            return m_states.Remove(key);
        }

        public int RemoveDisplay(IDisplay display)
        {
            ArgumentNullException.ThrowIfNull(display);
            var keys = m_states.Keys
                .Where(key => MasterSatelliteDisplayEligibility.DisplaysMatch(key.Display, display))
                .ToArray();
            foreach (var key in keys)
            {
                m_states.Remove(key);
            }
            return keys.Length;
        }

        public IReadOnlyList<MasterSatelliteRuntimeEntry> SnapshotForDisplay(IDisplay display)
        {
            ArgumentNullException.ThrowIfNull(display);
            return Array.AsReadOnly(m_states
                .Where(pair => MasterSatelliteDisplayEligibility.DisplaysMatch(pair.Key.Display, display))
                .Select(pair => new MasterSatelliteRuntimeEntry(pair.Key, pair.Value))
                .ToArray());
        }

        public void Clear()
        {
            m_states.Clear();
        }
    }

    /// <summary>
    /// Display-scoped lifecycle seam. It owns the immutable settings snapshot and
    /// delegates all runtime-state storage to exactly one replaceable registry.
    /// </summary>
    internal sealed class MasterSatelliteRuntimeSession
    {
        private readonly IMasterSatelliteRuntimeRegistry m_registry;

        public IDisplay Display { get; }

        public MasterSatelliteLayoutSettings SettingsSnapshot { get; private set; } = new();

        public int StateCount => m_registry.SnapshotForDisplay(Display).Count;

        public MasterSatelliteRuntimeSession(
            IDisplay display,
            IMasterSatelliteRuntimeRegistry? registry = null)
        {
            Display = display ?? throw new ArgumentNullException(nameof(display));
            m_registry = registry ?? new MasterSatelliteRuntimeRegistry();
        }

        public MasterSatelliteSettingsTransition ApplySettings(
            MasterSatelliteLayoutSettings settings,
            IDisplay primaryDisplay)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(primaryDisplay);

            bool wasEligible = IsEligible(primaryDisplay);
            SettingsSnapshot = settings with { };
            bool isEligible = IsEligible(primaryDisplay);
            int removed = isEligible ? 0 : m_registry.RemoveDisplay(Display);
            return new MasterSatelliteSettingsTransition(wasEligible, isEligible, removed);
        }

        public bool IsEligible(IDisplay primaryDisplay)
        {
            return MasterSatelliteDisplayEligibility.IsEligible(
                SettingsSnapshot,
                Display,
                primaryDisplay);
        }

        public LayoutStateKey GetKey(IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            return new LayoutStateKey(desktop, Display);
        }

        public bool TryGetState(
            IVirtualDesktop desktop,
            out MasterSatelliteRuntimeState state)
        {
            return m_registry.TryGet(GetKey(desktop), out state);
        }

        public bool TryCommitState(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            return m_registry.TryAdd(GetKey(desktop), state);
        }

        public bool RemoveDesktop(IVirtualDesktop desktop)
        {
            return m_registry.Remove(GetKey(desktop));
        }

        public IReadOnlyList<MasterSatelliteRuntimeEntry> SnapshotStates()
        {
            return m_registry.SnapshotForDisplay(Display);
        }

        public void Clear()
        {
            m_registry.RemoveDisplay(Display);
        }
    }

    /// <summary>
    /// Headless lifecycle controller shared by TilingService and unit tests. Its
    /// caller serializes calls with the existing backend lock; the controller does
    /// not create a lock, raise events, move windows between desktops, or place new
    /// WindowAdded events.
    /// </summary>
    internal sealed class MasterSatelliteRuntimeLifecycle
    {
        private readonly MasterSatelliteRuntimeSession m_session;

        public MasterSatelliteLayoutSettings SettingsSnapshot => m_session.SettingsSnapshot;

        public int StateCount => m_session.StateCount;

        public MasterSatelliteRuntimeLifecycle(
            IDisplay display,
            IMasterSatelliteRuntimeRegistry? registry = null)
        {
            m_session = new MasterSatelliteRuntimeSession(display, registry);
        }

        public bool IsEligible(IDisplay primaryDisplay)
        {
            return m_session.IsEligible(primaryDisplay);
        }

        public bool TryGetState(
            IVirtualDesktop desktop,
            out MasterSatelliteRuntimeState state)
        {
            return m_session.TryGetState(desktop, out state);
        }

        public bool DeactivateDesktop(IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            return m_session.RemoveDesktop(desktop);
        }

        /// <summary>
        /// Removes an active runtime state after a capacity transition was aborted.
        /// The existing tree is deliberately left untouched as a normal FancyWM
        /// layout, so an invalid background canonical state cannot remain reachable
        /// by commands or placement while the user resolves the failed transition.
        /// </summary>
        public MasterSatelliteLifecycleResult DeactivateCapacityTransitionSource(
            IVirtualDesktop desktop,
            IDisplay primaryDisplay)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(primaryDisplay);
            bool removed = m_session.RemoveDesktop(desktop);
            return new MasterSatelliteLifecycleResult(
                m_session.GetKey(desktop),
                m_session.IsEligible(primaryDisplay),
                StatePresent: false,
                StateAdded: false,
                StateRemoved: removed,
                "CapacityTransitionAborted",
                null,
                null);
        }

        public IReadOnlyList<MasterSatelliteRuntimeEntry> SnapshotStates()
        {
            return m_session.SnapshotStates();
        }

        public MasterSatelliteSettingsTransition CacheSettings(
            MasterSatelliteLayoutSettings settings,
            IDisplay primaryDisplay)
        {
            return m_session.ApplySettings(settings, primaryDisplay);
        }

        public MasterSatelliteLocalMutationResult PlaceWindow(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            IWindow window)
        {
            return PlaceWindow(
                backend,
                desktop,
                window,
                nextSlotReserved: false);
        }

        public MasterSatelliteLocalMutationResult PlaceWindow(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            IWindow window,
            bool nextSlotReserved)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(window);

            if (!m_session.TryGetState(desktop, out var state))
            {
                return new MasterSatelliteLocalMutationResult(
                    MasterSatelliteLocalMutationDisposition.NotActive,
                    m_session.GetKey(desktop),
                    null,
                    null);
            }

            if (nextSlotReserved)
            {
                if (!backend.TryGetMasterSatelliteSnapshot(
                    desktop,
                    state,
                    out var snapshot))
                {
                    return Rejected(
                        m_session.GetKey(desktop),
                        MissingDesktopFailure(state));
                }
                const string message = "The next canonical slot is reserved for a correlated desktop transfer.";
                var reservationInvariant = backend.ValidateMasterSatelliteLayout(
                    desktop,
                    state,
                    m_session.SettingsSnapshot);
                return Rejected(
                    m_session.GetKey(desktop),
                    MasterSatelliteOperationResult.Failure(
                        MasterSatelliteFailureReason.CapacityReached,
                        message,
                        snapshot,
                        reservationInvariant));
            }

            var role = state.Master == null
                ? MasterSatelliteWindowRole.Master
                : MasterSatelliteWindowRole.Satellite;
            int? satelliteIndex = role == MasterSatelliteWindowRole.Satellite
                ? state.Satellites.Count
                : null;
            var preflight = backend.PreflightMasterSatellitePlacement(
                desktop,
                state,
                m_session.SettingsSnapshot,
                window,
                role,
                satelliteIndex);
            if (!preflight.Succeeded)
            {
                return Rejected(m_session.GetKey(desktop), preflight.Operation);
            }

            var placement = role == MasterSatelliteWindowRole.Master
                ? backend.RegisterMaster(
                    desktop,
                    state,
                    m_session.SettingsSnapshot,
                    window)
                : backend.RegisterSatellite(
                    desktop,
                    state,
                    m_session.SettingsSnapshot,
                    window,
                    satelliteIndex!.Value);
            if (!placement.Succeeded)
            {
                return Rejected(m_session.GetKey(desktop), placement.Operation);
            }

            backend.SetFocus(window);
            var invariant = backend.ValidateMasterSatelliteLayout(
                desktop,
                state,
                m_session.SettingsSnapshot);
            return new MasterSatelliteLocalMutationResult(
                MasterSatelliteLocalMutationDisposition.Placed,
                m_session.GetKey(desktop),
                placement.Operation,
                invariant);
        }

        public MasterSatelliteLocalMutationResult RemoveWindow(
            TilingWorkspace backend,
            IWindow window,
            bool preserveOriginalPosition)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(window);

            foreach (var entry in m_session.SnapshotStates())
            {
                var tree = backend.GetTree(entry.Key.VirtualDesktop);
                if (tree?.FindNode(window) == null)
                {
                    continue;
                }
                return RemoveWindow(
                    backend,
                    entry.Key.VirtualDesktop,
                    entry.State,
                    m_session.SettingsSnapshot,
                    window,
                    preserveOriginalPosition);
            }

            return new MasterSatelliteLocalMutationResult(
                MasterSatelliteLocalMutationDisposition.NotActive,
                null,
                null,
                null);
        }

        /// <summary>
        /// Removes a window using the validation snapshot that admitted the
        /// current canonical tree. A capacity transition uses this overload while
        /// the newly cached lower limit is not valid until its tail transfers
        /// finish.
        /// </summary>
        public MasterSatelliteLocalMutationResult RemoveWindow(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings validationSettings,
            IWindow window,
            bool preserveOriginalPosition)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(validationSettings);
            ArgumentNullException.ThrowIfNull(window);
            var key = m_session.GetKey(desktop);
            if (!m_session.TryGetState(desktop, out var currentState)
                || !ReferenceEquals(currentState, state)
                || backend.GetTree(desktop)?.FindNode(window) == null)
            {
                return new MasterSatelliteLocalMutationResult(
                    MasterSatelliteLocalMutationDisposition.NotFound,
                    key,
                    null,
                    null);
            }

            var operation = backend.UnregisterMasterSatelliteWindow(
                desktop,
                state,
                validationSettings,
                window,
                preserveOriginalPosition);
            if (!operation.Succeeded)
            {
                return Rejected(key, operation);
            }
            var invariant = backend.ValidateMasterSatelliteLayout(
                desktop,
                state,
                validationSettings);
            return new MasterSatelliteLocalMutationResult(
                MasterSatelliteLocalMutationDisposition.Removed,
                key,
                operation,
                invariant);
        }

        public MasterSatelliteLifecycleResult ApplySettings(
            TilingWorkspace backend,
            MasterSatelliteLayoutSettings settings,
            IDisplay primaryDisplay,
            IVirtualDesktop currentDesktop)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(primaryDisplay);
            ArgumentNullException.ThrowIfNull(currentDesktop);

            var transition = CacheSettings(settings, primaryDisplay);
            var key = m_session.GetKey(currentDesktop);
            if (!transition.IsEligible)
            {
                return new MasterSatelliteLifecycleResult(
                    key,
                    false,
                    false,
                    false,
                    transition.RemovedStateCount > 0,
                    "SettingsDisabledOrDisplayOutOfScope",
                    null,
                    null);
            }

            return RefreshOrInitializeDesktop(backend, currentDesktop);
        }

        public MasterSatelliteLifecycleResult DesktopAdded(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            IDisplay primaryDisplay)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(primaryDisplay);

            if (!m_session.IsEligible(primaryDisplay))
            {
                return Ineligible(desktop, "DesktopAddedDisplayOutOfScope");
            }
            return RefreshOrInitializeBackgroundDesktop(backend, desktop);
        }

        public bool DesktopRemoved(IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            return m_session.RemoveDesktop(desktop);
        }

        public MasterSatelliteLifecycleResult CurrentDesktopChanged(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            IDisplay primaryDisplay)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(primaryDisplay);

            if (!m_session.IsEligible(primaryDisplay))
            {
                return Ineligible(desktop, "CurrentDesktopDisplayOutOfScope");
            }
            return RefreshOrInitializeDesktop(backend, desktop);
        }

        /// <summary>
        /// Initializes every already-known desktop after the service backend and
        /// settings snapshot are ready. DesktopAdded can run earlier during
        /// TilingService construction, so relying only on that event would leave
        /// background empty desktops without a runtime state and make them fail
        /// destination preflight despite a published empty capacity. Existing
        /// background manual or invalid layouts are classified and skipped; only
        /// the current desktop treats this call as an explicit activation.
        /// </summary>
        public IReadOnlyList<MasterSatelliteLifecycleResult> InitializeExistingDesktops(
            TilingWorkspace backend,
            IEnumerable<IVirtualDesktop> desktops,
            IVirtualDesktop currentDesktop,
            IDisplay primaryDisplay,
            IEnumerable<IVirtualDesktop>? excludedDesktops = null)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktops);
            ArgumentNullException.ThrowIfNull(currentDesktop);
            ArgumentNullException.ThrowIfNull(primaryDisplay);
            var excluded = excludedDesktops?.ToArray()
                ?? Array.Empty<IVirtualDesktop>();
            if (excluded.Any(desktop => desktop == null))
            {
                throw new ArgumentException(
                    "Excluded desktops cannot contain null entries.",
                    nameof(excludedDesktops));
            }

            var ordered = new List<IVirtualDesktop> { currentDesktop };
            foreach (var desktop in desktops)
            {
                ArgumentNullException.ThrowIfNull(desktop);
                if (!ordered.Any(existing =>
                    MasterSatelliteDisplayEligibility.DesktopsMatch(
                        existing,
                        desktop)))
                {
                    ordered.Add(desktop);
                }
            }

            var results = new List<MasterSatelliteLifecycleResult>(ordered.Count);
            foreach (var desktop in ordered)
            {
                if (excluded.Any(candidate =>
                    MasterSatelliteDisplayEligibility.DesktopsMatch(
                        candidate,
                        desktop)))
                {
                    continue;
                }
                results.Add(MasterSatelliteDisplayEligibility.DesktopsMatch(
                        desktop,
                        currentDesktop)
                    ? CurrentDesktopChanged(backend, desktop, primaryDisplay)
                    : DesktopAdded(backend, desktop, primaryDisplay));
            }
            return Array.AsReadOnly(results.ToArray());
        }

        public IReadOnlyList<MasterSatelliteLifecycleResult> WorkAreaChanged(
            TilingWorkspace backend,
            Rectangle workArea)
        {
            ArgumentNullException.ThrowIfNull(backend);
            foreach (var tree in backend.Trees)
            {
                tree.WorkArea = workArea;
            }
            return RelayoutActiveStates(backend, "WorkAreaChanged");
        }

        public IReadOnlyList<MasterSatelliteLifecycleResult> ScalingChanged(
            TilingWorkspace backend,
            Rectangle panelPadding,
            int panelSpacing)
        {
            ArgumentNullException.ThrowIfNull(backend);
            foreach (var panel in backend.Trees
                .Where(tree => tree.Root != null)
                .SelectMany(tree => tree.Root!.Nodes)
                .OfType<PanelNode>())
            {
                panel.Padding = panelPadding;
                panel.Spacing = panelSpacing;
            }
            return RelayoutActiveStates(backend, "ScalingChanged");
        }

        /// <summary>
        /// Applies a changed activation default to the current active layout. The
        /// service decides whether the persisted default actually changed so a
        /// runtime orientation selected through a layout command is not reset by
        /// unrelated settings notifications.
        /// </summary>
        public MasterSatelliteLifecycleResult? ApplySatelliteOrientationSetting(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            MasterSatelliteLayoutSettings validationSettings,
            SatelliteLayoutOrientation orientation)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(validationSettings);
            if (!m_session.TryGetState(desktop, out var state))
            {
                return null;
            }

            var operation = backend.SetMasterSatelliteOrientation(
                desktop,
                state,
                validationSettings,
                orientation);
            return new MasterSatelliteLifecycleResult(
                m_session.GetKey(desktop),
                true,
                true,
                false,
                false,
                operation.Succeeded
                    ? "SettingsOrientationChanged"
                    : "SettingsOrientationRejected",
                operation,
                operation.Invariant);
        }

        public void Clear()
        {
            m_session.Clear();
        }

        private MasterSatelliteLifecycleResult RefreshOrInitializeDesktop(
            TilingWorkspace backend,
            IVirtualDesktop desktop)
        {
            var key = m_session.GetKey(desktop);
            if (!m_session.TryGetState(desktop, out var state))
            {
                return InitializeDesktop(backend, desktop, key);
            }

            var invariant = backend.ValidateMasterSatelliteLayout(
                desktop,
                state,
                m_session.SettingsSnapshot);
            if (invariant.IsValid)
            {
                return new MasterSatelliteLifecycleResult(
                    key,
                    true,
                    true,
                    false,
                    false,
                    "AlreadyActive",
                    null,
                    invariant);
            }

            var rebuild = RebuildDesktop(backend, desktop, state, preferCurrentMaster: true);
            if (!rebuild.Succeeded)
            {
                m_session.RemoveDesktop(desktop);
            }
            return new MasterSatelliteLifecycleResult(
                key,
                true,
                rebuild.Succeeded,
                false,
                !rebuild.Succeeded,
                rebuild.Succeeded ? "Rebuilt" : "RebuildRejected",
                rebuild,
                rebuild.Invariant);
        }

        /// <summary>
        /// Makes an already-known background desktop available to destination
        /// search without treating its existing manual tree as an activation
        /// request. Empty desktops are safe to initialize and an already-active,
        /// valid canonical desktop is safe to retain. Manual or invalid trees are
        /// deliberately left untouched; their subsequently published capacity
        /// classifies them as unavailable to algorithmic overflow.
        /// </summary>
        private MasterSatelliteLifecycleResult RefreshOrInitializeBackgroundDesktop(
            TilingWorkspace backend,
            IVirtualDesktop desktop)
        {
            var key = m_session.GetKey(desktop);
            if (m_session.TryGetState(desktop, out var state))
            {
                var invariant = backend.ValidateMasterSatelliteLayout(
                    desktop,
                    state,
                    m_session.SettingsSnapshot);
                if (invariant.IsValid)
                {
                    return new MasterSatelliteLifecycleResult(
                        key,
                        true,
                        true,
                        false,
                        false,
                        "AlreadyActive",
                        null,
                        invariant);
                }

                return new MasterSatelliteLifecycleResult(
                    key,
                    true,
                    true,
                    false,
                    false,
                    "BackgroundCorruptedLayoutSkipped",
                    null,
                    invariant);
            }

            if (!backend.TryGetLayoutKind(
                    desktop,
                    null,
                    m_session.SettingsSnapshot,
                    out var layoutKind)
                || layoutKind != WorkspaceLayoutKind.Empty)
            {
                return new MasterSatelliteLifecycleResult(
                    key,
                    true,
                    false,
                    false,
                    false,
                    layoutKind == WorkspaceLayoutKind.Manual
                        ? "BackgroundManualLayoutSkipped"
                        : "BackgroundUnsafeLayoutSkipped",
                    null,
                    null);
            }

            return InitializeDesktop(backend, desktop, key);
        }

        private MasterSatelliteLifecycleResult InitializeDesktop(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            LayoutStateKey key)
        {
            var candidate = new MasterSatelliteRuntimeState(m_session.SettingsSnapshot, true);
            if (!m_session.TryCommitState(desktop, candidate))
            {
                bool existingStatePresent = m_session.TryGetState(desktop, out _);
                return new MasterSatelliteLifecycleResult(
                    key,
                    true,
                    existingStatePresent,
                    false,
                    false,
                    "ActivationStateConflict",
                    null,
                    null);
            }

            MasterSatelliteOperationResult activation;
            try
            {
                activation = RebuildDesktop(backend, desktop, candidate, preferCurrentMaster: false);
            }
            catch
            {
                m_session.RemoveDesktop(desktop);
                throw;
            }
            if (!activation.Succeeded)
            {
                m_session.RemoveDesktop(desktop);
                return new MasterSatelliteLifecycleResult(
                    key,
                    true,
                    false,
                    false,
                    false,
                    "ActivationRejected",
                    activation,
                    activation.Invariant);
            }

            return new MasterSatelliteLifecycleResult(
                key,
                true,
                true,
                true,
                false,
                "Activated",
                activation,
                activation.Invariant);
        }

        private MasterSatelliteOperationResult RebuildDesktop(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState state,
            bool preferCurrentMaster)
        {
            if (!backend.TryGetTreeSnapshot(desktop, out var tree))
            {
                return MissingDesktopFailure(state);
            }

            var windows = tree.Root?.Windows
                .Select(node => node.WindowReference)
                .ToList() ?? [];
            IWindow? preferred = preferCurrentMaster && state.Master != null
                && IndexOfWindow(windows, state.Master) >= 0
                    ? state.Master
                    : (backend.GetFocus(desktop) as WindowNode)?.WindowReference;
            int preferredIndex = preferred == null ? -1 : IndexOfWindow(windows, preferred);
            if (preferredIndex > 0)
            {
                windows.RemoveAt(preferredIndex);
                windows.Insert(0, preferred!);
            }

            return backend.ActivateMasterSatelliteLayout(
                desktop,
                state,
                m_session.SettingsSnapshot,
                windows);
        }

        private IReadOnlyList<MasterSatelliteLifecycleResult> RelayoutActiveStates(
            TilingWorkspace backend,
            string action)
        {
            var results = new List<MasterSatelliteLifecycleResult>();
            foreach (var entry in m_session.SnapshotStates())
            {
                var operation = backend.RelayoutMasterSatelliteLayout(
                    entry.Key.VirtualDesktop,
                    entry.State,
                    m_session.SettingsSnapshot);
                var invariant = backend.ValidateMasterSatelliteLayout(
                    entry.Key.VirtualDesktop,
                    entry.State,
                    m_session.SettingsSnapshot);
                results.Add(new MasterSatelliteLifecycleResult(
                    entry.Key,
                    true,
                    true,
                    false,
                    false,
                    action,
                    operation,
                    invariant));
            }
            return Array.AsReadOnly(results.ToArray());
        }

        private MasterSatelliteLifecycleResult Ineligible(
            IVirtualDesktop desktop,
            string action)
        {
            return new MasterSatelliteLifecycleResult(
                m_session.GetKey(desktop),
                false,
                false,
                false,
                false,
                action,
                null,
                null);
        }

        private static MasterSatelliteLocalMutationResult Rejected(
            LayoutStateKey key,
            MasterSatelliteOperationResult operation)
        {
            return new MasterSatelliteLocalMutationResult(
                MasterSatelliteLocalMutationDisposition.Rejected,
                key,
                operation,
                operation.Invariant);
        }

        private static int IndexOfWindow(IReadOnlyList<IWindow> windows, IWindow window)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (ReferenceEquals(windows[i], window)
                    || EqualityComparer<IWindow>.Default.Equals(windows[i], window))
                {
                    return i;
                }
            }
            return -1;
        }

        private static MasterSatelliteOperationResult MissingDesktopFailure(
            MasterSatelliteRuntimeState state)
        {
            const string message = "The virtual desktop is not registered with the tiling workspace.";
            var snapshot = new MasterSatelliteLayoutSnapshot(
                state,
                Rectangle.Empty,
                "<desktop is not registered>");
            return MasterSatelliteOperationResult.Failure(
                MasterSatelliteFailureReason.InvalidArgument,
                message,
                snapshot,
                new MasterSatelliteInvariantResult([message], snapshot.TreeDescription));
        }
    }

    internal sealed class LayoutStateKeyIdentityComparer : IEqualityComparer<LayoutStateKey>
    {
        public static LayoutStateKeyIdentityComparer Instance { get; } = new();

        private LayoutStateKeyIdentityComparer()
        {
        }

        public bool Equals(LayoutStateKey x, LayoutStateKey y)
        {
            return MasterSatelliteDisplayEligibility.DesktopsMatch(x.VirtualDesktop, y.VirtualDesktop)
                && MasterSatelliteDisplayEligibility.DisplaysMatch(x.Display, y.Display);
        }

        public int GetHashCode(LayoutStateKey obj)
        {
            return HashCode.Combine(
                EqualityComparer<IVirtualDesktop>.Default.GetHashCode(obj.VirtualDesktop),
                EqualityComparer<IDisplay>.Default.GetHashCode(obj.Display));
        }
    }

    internal static class MasterSatelliteDisplayEligibility
    {
        /// <summary>
        /// 21:9 is the minimum landscape work-area aspect ratio treated as
        /// ultrawide. Resolution and DPI do not participate in this predicate.
        /// </summary>
        public const double UltrawideMinimumAspectRatio = 21d / 9d;

        public static bool IsEligible(
            MasterSatelliteLayoutSettings settings,
            IDisplay display,
            IDisplay primaryDisplay)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(display);
            ArgumentNullException.ThrowIfNull(primaryDisplay);

            if (!settings.Enabled)
            {
                return false;
            }

            return settings.DisplayScope switch
            {
                AlgorithmicLayoutDisplayScope.PrimaryDisplay => DisplaysMatch(display, primaryDisplay),
                AlgorithmicLayoutDisplayScope.AllDisplays => true,
                AlgorithmicLayoutDisplayScope.UltrawideDisplays => IsUltrawide(display.WorkArea),
                _ => false,
            };
        }

        public static bool IsUltrawide(Rectangle workArea)
        {
            if (workArea.Width <= 0 || workArea.Height <= 0 || workArea.Width < workArea.Height)
            {
                return false;
            }
            return (double)workArea.Width / workArea.Height >= UltrawideMinimumAspectRatio;
        }

        internal static bool DisplaysMatch(IDisplay? left, IDisplay? right)
        {
            return ReferenceEquals(left, right)
                || (left != null && right != null
                    && EqualityComparer<IDisplay>.Default.Equals(left, right));
        }

        internal static bool DesktopsMatch(IVirtualDesktop? left, IVirtualDesktop? right)
        {
            return ReferenceEquals(left, right)
                || (left != null && right != null
                    && EqualityComparer<IVirtualDesktop>.Default.Equals(left, right));
        }
    }
}
