# Master + Satellites algorithmic layout

## Status

The feature is implemented behind an opt-in setting and is disabled by default. Pure layout, workspace, coordinator, settings, and service-facing components are covered by automated tests. Direct live `TilingService`/WinMan event paths and Windows checks involving UWQHD/DPI combinations, desktop COM integration, and interactive accessibility remain manual acceptance gates; see `IMPLEMENTATION_STATUS.md` for the exact latest build and test results.

## Purpose

Master + Satellites provides a predictable bounded layout using FancyWM's existing tiling tree. One master window occupies the configurable wide region and up to nine satellite windows share the remaining region. The initial defaults target an ultrawide workflow without depending on a particular pixel resolution.

Enable it under **Settings → Layouts → Master + Satellites**. The setting applies immediately to displays selected by the display scope. A pre-existing compatible layout is normalized into the canonical form; unrelated manual layouts on background desktops are left alone.

## Canonical invariant

State is scoped to a `(virtual desktop, display)` pair. Runtime roles, window handles, desktop references, reservations, and transfer intents are never serialized in settings.

The empty layout has no tiled windows. A one-window layout has a horizontal `SplitPanelNode` root containing only the master, which receives the full display `WorkArea`. Once a satellite exists, the same horizontal root contains exactly two children: the master and one satellite `SplitPanelNode`. Their order represents whether the master is on the left or right. The satellite panel contains only ordered `WindowNode` children and is vertical or horizontal according to runtime state.

```text
Master left                         Master right

Root: Horizontal                   Root: Horizontal
├── Master                         ├── Satellites
└── Satellites                     │   ├── S1
    ├── S1                         │   ├── S2
    ├── S2                         │   └── S3
    └── S3                         └── Master
```

Satellite orientation affects only the inner satellite panel. It does not change the logical order:

```text
Vertical satellites                Horizontal satellites

┌──────────────┬──────┐            ┌──────────────┬──┬──┬──┐
│              │  S1  │            │              │S1│S2│S3│
│    Master    ├──────┤            │    Master    │  │  │  │
│              │  S2  │            │              │  │  │  │
│              ├──────┤            │              │  │  │  │
│              │  S3  │            │              │  │  │  │
└──────────────┴──────┘            └──────────────┴──┴──┴──┘
```

The canonical tree contains no nested satellite panels, stacks, placeholders, layout-function nodes, or duplicate windows. Its master and satellite sequence must match runtime state, and the satellite count must not exceed `MaxSatellites`. Every committed mutation must validate this invariant. Recovery must preserve all windows; if safe recovery is impossible, algorithmic mode is disabled only for the affected desktop/display pair.

## Settings and defaults

- Disabled by default.
- Master ratio: 60%, normalized to 50–80%.
- Master side: left.
- Satellite orientation: vertical.
- Maximum satellites: 3, normalized to 1–9; total default capacity is 4 tiled windows.
- Overflow: move to a suitable existing desktop.
- Display scope: primary display.
- Automatically created desktop limit: 1, normalized to 0–9.
- Do not follow an overflowed window by default.

Settings are normalized when loaded, so older `settings.json` files that do not contain the feature receive these defaults. Runtime roles and transfer state are not serialized. Every layout field saves automatically; there is no separate Apply button. **Reset settings to UWQHD preset** selects the 60% ratio, left master, vertical satellites, three satellites, existing-desktop overflow, and no follow. It deliberately preserves the current enabled state, display scope, and automatic-desktop limit, along with unrelated settings. Choose Horizontal after the reset if that is the desired live orientation; changing it updates the current active canonical tree without restarting FancyWM.

The implementation uses each display's `IDisplay.WorkArea`, including its current DPI-adjusted dimensions, spacing, padding, and every tiled window's minimum size. It retains requested and effective master ratios separately when constraints force an adjustment. `AutoSplitCount` is not a capacity setting for this feature.

Display scope choices are:

- **Primary display** — only the current primary display uses the algorithmic layout.
- **Ultrawide displays** — landscape work areas with an aspect ratio of at least 21:9; no pixel resolution is hard-coded.
- **All displays** — each display maintains independent state for every virtual desktop.

Overflow choices are:

- **Float on current desktop** — never initiate an automatic virtual-desktop transfer.
- **Move to an existing desktop** — search one cycle through later desktops for a compatible free slot.
- **Move or create a desktop** — perform that search, then create at most the configured number of desktops when Windows exposes manageable virtual desktops.

The optional **Follow overflow window** setting switches desktops only after a correlated transfer commits successfully.

## Commands and keybindings

All keybindings remain configurable. With FancyWM's activation chord, the defaults added by this feature are:

- `A` — toggle Master + Satellites for the active desktop/display.
- `M` — promote the focused satellite to master.
- `B` — move the master from left to right or back.

Toggle satellite orientation, reset master ratio, and rebalance are exposed as bindable actions without default keys to avoid conflicts. Existing directional move actions reorder satellites along their visible axis. At the inner edge of a horizontal satellite row, moving toward the adjacent master promotes the focused satellite and places the old master in that exact satellite slot; this also covers the visually natural two-window left/right move. Moving a master left/right changes its side. Existing width-resize actions adjust the focused master's ratio within 50–80%. Stack creation and arbitrary nested-panel operations are rejected while the canonical layout is active.

## Window roles and operations

The first tiled window becomes master. The second creates the master/satellite split; later windows append to the satellite panel. Satellites preserve visual order and can be reordered. Promoting a satellite swaps node references atomically: the selected satellite becomes master, while the old master occupies that satellite's exact former slot. Switching master side or satellite orientation preserves the window set and order. Closing or floating the master promotes the first satellite; removing the final satellite restores a full-work-area master.

For example, promoting `C` in `Master=A, Satellites=[B,C,D]` yields `Master=C, Satellites=[B,A,D]`. The old master does not move to the front or end: it occupies `C`'s selected slot.

Mouse drops use a detached preview plan. Dropping a satellite before or after another satellite reorders it; dropping a satellite on the master promotes it; dropping the master on a satellite promotes that satellite; crossing the central boundary changes the master side. Unsupported drops do not mutate the production tree, and the preview is cleared when the drag ends or is cancelled. The current overlay suppresses an impossible preview and explains the rejection after release; it does not yet draw a dedicated red/invalid target. Interactive mouse behavior still requires a live Windows verification pass.

## Overflow contract

Overflow is planned before local tree mutation. A single workspace-wide coordinator searches later virtual desktops cyclically once, preserves the source display, and targets only an empty display layout or an active compatible canonical layout. Capacity includes reservations. Manual layouts and unsafe/corrupt targets are not modified.

The transfer sequence is plan, reserve, create an idempotent pending intent, release all service/coordinator mutation locks, call `IVirtualDesktop.MoveWindow`, correlate source/destination events in either order, register the reserved role, commit, and release the reservation. `MoveWindow` must never execute while a TilingService backend/window/floating/new-window lock or coordinator mutation lock is held. A new Win32 window may become visible before virtual-desktop ownership is queryable; FancyWM keeps it neutral for a bounded six-probe/300 ms Dispatcher window and reconciles the first conclusive ownership observation without querying it a second time. Duplicate callbacks and reused native handles cannot extend or steal that retry generation. Window identity is the exact wrapper plus its captured HWND generation: Destroyed/Added/Removed callbacks may arrive in any order, but an old wrapper cannot reclaim a reused numeric HWND or mutate its replacement. Failure releases all state and leaves the window floating on the user's chosen/source desktop when possible. Successful overflow is informational and must not use the placement-failure sound.

An over-capacity activation or a reduction of `MaxSatellites` considers surplus satellites from the end. Each transfer is planned against a clone before the source tree is changed; the source has an exact restore point until the transfer reaches a terminal state. Increasing the limit does **not** pull floating windows or windows from other desktops back into the layout. Automatic backfill is intentionally outside the current scope.

## Floating and manual moves

Excluded, dialog/transient, pinned, and explicitly floating windows do not consume canonical capacity. A window remains floating when no compatible destination is available, virtual desktops cannot be managed, desktop creation reaches its session limit, a minimum-size constraint makes the requested role impossible, or a transfer fails and cannot be safely recovered.

Manual intent wins over an in-flight automatic transfer. If the user moves the window to another desktop while a transfer is pending, the pending reservation is cancelled and the window is preserved as floating on the desktop chosen by the user; a later duplicate event cannot move it back. Manually moving a window into a full algorithmic desktop likewise leaves it floating there. FancyWM does not silently damage a manual or corrupt destination layout to make room.

## Multi-monitor behavior

Runtime layout state, capacity, reservations, and pending intents are keyed by `(virtual desktop, display)`. Overflow preserves the source display identity; it does not consume a slot on a different monitor. Commands are routed to the display containing the focused window, with a deterministic fallback when no focused window can identify a display. Display removal cancels its pending reservations and removes its runtime state and service subscriptions.

## Minimum-size limitations and recovery

Before committing a placement or role mutation, the engine evaluates a detached canonical tree against the real work area and window constraints. If the requested master/satellite combination cannot fit, it returns a typed rejection without partially mutating the production tree. A newly opened window then follows the configured overflow/floating policy.

`RebalanceMasterSatelliteLayout` rebuilds the local canonical tree from its existing logical roles and redistributes satellite flex values. It never imports floating windows or windows from another desktop and does not change the master unnecessarily. Invariant failures are logged with the layout revision; recovery either restores a valid canonical tree or disables only the affected desktop/display state while keeping its ordinary FancyWM tree usable.

## Disabled behavior

When the feature is disabled or a display is outside its configured scope, FancyWM follows its existing generic panel, auto-split, auto-collapse, floating, exclusion, keyboard, and drag-and-drop paths. Disabling an active algorithmic layout preserves its current canonical tree as an ordinary FancyWM layout; it does not attempt to reconstruct an older manual tree.

## Verification and known manual gates

Automated acceptance requires that an empty desktop gives window A the full work area; B creates the requested/effective 60/40 split when constraints permit; B/C/D are ordered satellites; orientation and master side can change without losing order or ratio; promotion places the old master in the promoted satellite's exact slot; removal promotes/collapses canonically; a fifth default-capacity window never becomes a fifth local tile; overflow uses reservation/correlation to reach the next suitable desktop or leaves the window floating; state is independent per desktop/display; old settings load safely; and disabled mode preserves existing FancyWM behavior.

Debug and Release builds, both existing test projects, new engine/service/coordinator tests, invariant tests, and regression tests must pass. Submodule commit SHAs must remain unchanged.

The following still require hands-on Windows verification and are not implied by automated coverage: 3440×1440 end-to-end behavior, 100/125/150/200% and mixed-monitor DPI, live drag preview/overlay behavior (including the lack of a dedicated invalid-target graphic), settings keyboard navigation and runtime binding diagnostics, physical monitor hot-plug, live virtual-desktop creation across supported Windows builds, elevated-window limitations, and representative UWP/WinUI/non-resizable applications.

## Latest automated verification

The final automated gate uses the same CI-equivalent GUI project build selected by the repository workflow:

```powershell
dotnet build --no-restore --configuration Debug --property:WarningLevel=0 FancyWM.GUI
dotnet build --no-restore --configuration Release --property:WarningLevel=0 FancyWM.GUI
dotnet test FancyWM.Tests/FancyWM.Tests.csproj --configuration Debug --no-restore
dotnet test FancyWM.Tests/FancyWM.Tests.csproj --configuration Release --no-restore
dotnet test FancyWM.Layouts.Tests/FancyWM.Layouts.Tests.csproj --configuration Debug --no-restore
dotnet test FancyWM.Layouts.Tests/FancyWM.Layouts.Tests.csproj --configuration Release --no-restore
```

The exact latest counts and warnings are recorded in `IMPLEMENTATION_STATUS.md`, because they may change as regression coverage is added. No target framework, SDK, package version, or global warning policy was changed for this feature.

## Verified baseline (before production changes)

Environment: .NET SDK 10.0.400 on Windows, commit `947e955d550306c40efbc26532da712a57cbc869`.

```powershell
dotnet restore
dotnet build --no-restore --configuration Debug --property:WarningLevel=0 FancyWM.GUI
dotnet build --no-restore --configuration Release --property:WarningLevel=0 FancyWM.GUI
dotnet test FancyWM.Tests --configuration Debug --no-restore
dotnet test FancyWM.Layouts.Tests --configuration Debug --no-restore
```

Both builds succeeded with 0 errors and 13 pre-existing warnings. `FancyWM.Tests` passed 116/116 and `FancyWM.Layouts.Tests` passed 27/27. Baseline warnings include ModernWpf `MSB4130`, package trimming `NU1510`, package vulnerability `NU1902`/`NU1903`, and solution restore's skipped WAP project `NU1503`; no package or target-framework changes are planned to suppress them.
