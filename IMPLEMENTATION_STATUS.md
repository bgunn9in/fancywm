# Master + Satellites implementation status

## Published branch and base commits

- Published root branch: `feature/master-satellite-layout`, based on `main@947e955d550306c40efbc26532da712a57cbc869` (`Set version to 2.19.1-alpha`). The branch was created only after the preserved feature tree had been reviewed and committed, so no dirty-file checkout was required.
- The feature commit and branch were published only after the user's explicit commit/push request. User-owned `.codex/config.toml`, `BEGIN_PROMT.md`, and `CONTINUE_PROMT.md` are deliberately excluded.
- Submodule commits are `ModernWpf@0ab73b38c604cade2b344537202e195762e03412`, `winman@b2faabe377386d73b0df1371e61933e103a3ba65`, and `winman-windows@adb9f55b84b567db9f6e0ee8df4c33d11a12d91f`.
- The required `winman-windows` commit is published on `bgunn9in/winman-windows:feature/master-satellite-layout`; `.gitmodules` points to that reachable fork.

## Current stage

Continuation from base `main@947e955d550306c40efbc26532da712a57cbc869` is published on `feature/master-satellite-layout` and remains in Stage 13, Mouse interaction, because the physical mouse/UI gates cannot be completed automatically. Live `r3` testing on the 3440×1440 display confirmed Horizontal satellites and a correlated fifth-window overflow to Desktop 2. It then exposed a direct-command boundary defect: `MoveRight` rejected the sole left satellite even though its actual neighbour was the right-side master. The command now treats that active-axis boundary crossing as the existing atomic promotion operation. The fully validated `r4` portable build is ready for the exact physical direct-hotkey confirmation.

## Current TODO item

`TODO.md` recommended order item 13, `[~] Mouse interaction`, remains open for physical confirmation. The immediate unchecked gate is the Stage 5 direct-hotkey check: run `r4` with a right-side master and one left-side satellite, then move the satellite right across the adjacent master.

Current checklist totals, excluding the status legend: 631 `[x]`, 5 `[~]`, 59 `[ ]`, and 0 `[!]`.

Factually discovered state: root and `winman-windows` `git diff --check` both succeed. The reviewed feature sources are published together with the reachable `winman-windows` gitlink; only the three documented local workflow files remain outside version control. The live `r3` session had the feature enabled with Horizontal satellites, right-side master, ratio 0.6, capacity three, `MoveToExistingDesktop`, and ultrawide-display scope. Logs and screenshots showed Firefox in the left 40% satellite region, Zed in the right 60% master region, a second Snipping Tool satellite laid out horizontally, and an earlier Visual Studio overflow committed from Desktop 1 to Desktop 2. The rejected `MoveRight` had no floating, ownership, or transfer failure; it was the controller's last-satellite reorder boundary. The replacement `r4` archive contains 508 fully readable files and has SHA-256 `6CC7D56D877991CD3A1298434EFE8A729EEA3E6294F73DC3B05768ED5844EB46`.

## Completed in this session

- Extended active-axis satellite movement across an actually adjacent master to use the existing atomic promotion path. The focused satellite becomes master, the old master occupies the selected satellite's exact slot, focus/side/orientation are preserved, and perpendicular or outer-edge moves remain rejected.
- Added four controller cases covering one and three satellites with the master on either side, including outer-edge rejection and invariant validation, plus a direct `TilingService` regression matching the reported two-window Horizontal/right-master `MoveRight` path and asserting that no rejection/failure event is emitted.
- Inspected the live `r3` settings, screenshots, and Info log. This confirmed the 3440×1440, Horizontal, and fifth-window existing-desktop overflow gates, which are now marked `[x]`; direct-hotkey verification remains open for `r4`.
- Published and fully stream-validated `FancyWM-Master-Satellites-win-x64-20260905-r4.zip`: 508 files, 74,544,523 compressed bytes, SHA-256 `6CC7D56D877991CD3A1298434EFE8A729EEA3E6294F73DC3B05768ED5844EB46`.
- Hardened HWND generation ownership across Destroyed, replacement Added, delayed old Removed, duplicate old Added, queued position callbacks, incoming ownership probes, and post-`MoveWindow` reconciliation. Retired wrappers use weak tombstones; a dead current wrapper remains correlated just long enough for a replacement to purge the old canonical node safely.
- Corrected the Removed-before-Added manual-move regression found by the expanded gate. An absent generation is no longer confused with a different registered wrapper: reconciliation is allowed only when the exact object remains in both the workspace snapshot and the service's reference-identity tracked set. Both event orders stay on the user-selected desktop without starting another overflow.
- Made capacity-shrink transitions source-desktop stable even if the user changes desktops. Direct keyboard/mouse/float mutations are rejected during the temporary over-capacity interval, lifecycle removals validate against the source settings, and reentrant settings changes are deferred until finalization.
- Applied a changed default orientation to all empty background canonical states while preserving nonempty per-desktop customization. A rejected live orientation keeps the current valid tree but saves the desired default for new/empty layouts and raises a specific localized explanation.
- Added/expanded direct regressions for active-tree orientation, rejected orientation, background defaults, delayed shrink plus desktop switching/close/commands, HWND reuse, destroyed/dead generations, duplicate queued callbacks, and both manual/automatic event orders. A final blocker/high race review found no remaining issue in these paths.
- Published and fully stream-validated `FancyWM-Master-Satellites-win-x64-20260905-r3.zip`: 508 files, 74,544,540 compressed bytes, SHA-256 `336B91DCF5A27BB7946F39EC9B580AB1C803D8B1CBD17298FA0167393E234E52`.
- Added `ChangingDefaultOrientationUpdatesTheActiveCanonicalLayout` and routed a changed persisted default through the existing clone/preflight/rollback orientation mutation. The current canonical master, satellite order, capacity, and state identity are preserved; the revision advances exactly once. A combined capacity-shrink/orientation regression also passes.
- Renamed the misleading settings action to `Reset settings to UWQHD preset` and documented in-page that every field auto-saves and the preset deliberately resets orientation to Vertical.
- Added a bounded, Dispatcher-based ownership retry for incoming windows whose virtual desktop is temporarily unavailable. The successful probe passes its observed desktop into reconciliation without a second COM query, so alternating visibility cannot start unbounded retry generations.
- Changed discovery and refresh reconciliation to permit policy overflow while suppressing it for correlated destination events and observed manual desktop moves. A newly resizable tracked window in a full source layout now moves exactly once to an eligible existing desktop instead of becoming permanently floating.
- Replaced the incoming-probe HWND set with generation objects keyed by handle. Timer/removal cleanup requires both the captured generation and wrapper identity, stable-handle cache reads no longer let an old wrapper reclaim a reused HWND, and an explicit reused-HWND regression passes.
- Raised no-destination candidate diagnostics to Information level so a live run records each examined desktop's disposition/reason. The UI now explains that an existing destination must be empty or already canonical with a free slot.
- Current focused gates: complete direct-service integration 46/46 and transfer-event tracker 12/12 pass in Debug; the final seven exact-generation tests pass 7/7, and the manual-move plus HWND-reuse group passes 8/8.
- Reproduced the live `Dispatcher.VerifyAccess` crash deterministically: a floating-window drag with `PositionChangeEnd` raised on a worker thread failed through `DoWindowMove` → `MasterSatelliteCommandController.IsActive` → `AlgorithmicLayoutCoordinator.TryGet` before the production fix.
- Added Dispatcher boundaries to WinMan `PositionChangeStart`, `PositionChangeEnd`, and `LostFocus` callbacks. The latter is part of the same ordering fix: an immediate off-thread focus-loss reset could otherwise overtake queued pointer callbacks and accidentally revive a cancelled interaction.
- Added `MouseDropEndFromWinManWorkerUsesTilingDispatcher`, which verifies absence of the exception plus actual placement, runtime state/capacity, floating-registry cleanup, and preview cleanup. It fails with the reported stack before the fix and passes afterward.
- Re-ran the full current gates: FancyWM tests 496/496 Debug and 497/497 Release, Layouts 33/33 in each configuration, and ThemeEngine 16/16 in each configuration. Full Visual Studio 18 Debug and Release solution builds, including MSIX creation, also succeeded.
- Published and fully stream-validated the self-contained x64 portable replacement `FancyWM-Master-Satellites-win-x64-20260905-r2.zip` (500 files, SHA-256 `B1B746A575FA5283821AEF70CBE3AEBC6811B7AB20FA4B211A04B3295B2D0E16`). Its `FancyWM.dll` hash differs from the original crashing artifact.
- Recovered the continuation context and reproduced the existing pure event gate (9 passed). Added an injected logger/overlay construction seam without changing the production constructor, then directly instantiated `TilingService` in tests without opening WPF windows.
- Verified settings and command paths publish `LayoutEnabled`/`LayoutDisabled` with the correct desktop/display/message context, and verified service disposal unregisters its coordinator participant and disposes the overlay. The focused direct-service gate passes 2/2 and `TODO.md` lines 489/498 are now `[x]`.
- Extended the direct fixture through public float/unfloat and real workspace `WindowAdded` callbacks. It now proves master promotion, empty-layout unfloat, excluded/dialog/pinned capacity filtering, and full-layout floating fallback with duplicate-event notification deduplication (6/6 before the current two-desktop extension).
- Added exact destination restore-point retention for new-window overflow. Reject/exception/commit-reject paths restore both backend endpoints and runtime state; restore tokens survive a failed restore attempt, are removed before post-restore publication, and successful commit cleanup follows the backend-lock contract.
- Fixed late first-ownership reconciliation by comparing an unremembered OS destination with the already attached backend desktop. Both Added→Removed and Removed→Added manual moves into a full desktop now remain floating on the user-selected desktop, never start overflow, never return to the source, and deduplicate repeated events.
- Expanded the transfer lock diagnostic to include backend/window/new-window/saved-location/ignore-reposition/coordinator and workspace-wide floating-registry locks. Direct success, post-ownership `MoveWindow` failure, publication failure, and commit-rejection tests all observe both target and source `MoveWindow` calls outside every tracked lock.
- Verified direct no-destination, VDM-unavailable, auto-create-limit, desktop-creation-exception, and capacity-shrink fallback paths. Every rejected window remains floating at the intended source, pending intents/reservations drain to zero, and repeated events/settings do not duplicate user events.
- Added a controlled `MultiDisplayTilingService` child-service factory without changing its public constructor. Direct tests prove all six algorithmic command pairs follow focused-window geometry, display add/remove is idempotent, inherited properties reach new children, active-display removal reroutes safely, and every removed child stops/disposes exactly once.
- Raised `DesktopAdded` against two real `TilingService` instances sharing one coordinator and proved the created desktop receives independent `(desktop, display)` runtime states/capacities in both services; disposing one service removes only its participant/state.
- Exercised the real service PositionChangeStart/Changed/End path in an STA fixture. Floating-to-free-slot, full-layout floating fallback, full-layout existing-desktop overflow, and accepted/rejected/cancelled preview cleanup all pass; no production mouse change was required.
- Added a pure notification sound policy and wired the real `MainWindow` event path through it. Success never beeps; rejection and floating fallback respect `SoundOnFailure=true/false` (10/10 event tests).
- Corrected Rebalance no-op semantics: a balanced equivalent canonical allocation preserves the same root and revision and emits no event; a real repair still emits one visible `LayoutRecovered` event without failure sound (31/31 command/invariant tests).
- Added legacy `TilingWorkspace` regressions proving `AutoSplitCount` still bounds top-level generic layout width and `AutoCollapsePanels` still controls single-child cleanup outside algorithmic mode.
- Completed full x64 Debug and Release solution builds with the installed Visual Studio 18 Desktop Bridge, including MSIX production, plus complete Debug/Release FancyWM, Layouts, and ThemeEngine test projects.
- Read the complete `TODO.md`, inspected the required architecture and event/lock paths, preserved the initially dirty worktree, initialized submodules, and recorded a clean baseline.
- Added persisted settings and safe legacy JSON defaults, a complete Settings view-model/page with UWQHD preset and preview, bindable commands/keybindings, resources, README, changelog, and the layout guide.
- Implemented the pure canonical `MasterSatelliteLayoutEngine` on existing `DesktopTree`/`SplitPanelNode`/`Flex`, including atomic clone-first mutations, min-size preflight, master promotion, ordering, side/orientation/ratio changes, removal, normalization, and invariant recovery.
- Integrated the engine through `TilingWorkspace` and existing `TilingService` paths without a second tiling backend. Disabled/out-of-scope displays retain FancyWM's generic behavior.
- Added one Dispatcher-affine coordinator per `IWorkspace`, keyed all state/capacity by `(IVirtualDesktop, IDisplay)`, and shared it across every single-/multi-display service.
- Implemented exact-slot reservations, idempotent pending transfer intents, cyclic existing-desktop search, bounded desktop creation, both `WindowAdded`/`WindowRemoved` orders, duplicate-event handling, rollback, timeout, close/settings/display cleanup, and sequential over-capacity shrink from the satellite tail.
- Kept every `IVirtualDesktop.MoveWindow` and `CreateDesktop` call outside service and coordinator mutation locks. Added stable-HWND lifetime tracking and a workspace-wide floating registry so cross-display/manual intent survives wrapper changes and stale event broadcasts.
- Integrated canonical keyboard and mouse controller paths. Detached preview plans and atomic drop behavior are unit-tested; live overlay behavior remains a manual gate.
- Added typed algorithmic events, safe localized presentation messages, deduplication, no failure sound on successful overflow, and structured correlation/capacity/reservation/recovery logging.
- Extended the existing WinMan Windows backend only where required to expose the platform's desktop-creation operation; its gitlink SHA was not changed.
- Added broad settings, engine, workspace, lifecycle, placement, command, coordinator, overflow, creation, multi-monitor, event, mouse-controller, capacity-transition, lifetime, and floating-registry tests.
- Reconciled `TODO.md` strictly: automated items with direct evidence are complete; live/service/UI gaps remain `[ ]` or `[~]`; several previously overclaimed items were downgraded.

## Changed files in the preserved feature tree

The complete feature-related inventory is below. User-owned `.codex/config.toml`, `BEGIN_PROMT.md`, and `CONTINUE_PROMT.md` also appear as untracked but were not changed and are deliberately excluded.

- `CHANGELOG.md` (modified)
- `FancyWM.Layouts.Tests/FlexTest.cs` (modified)
- `FancyWM.Layouts.Tests/Tiling/SplitPanelNodeTest.cs` (modified)
- `FancyWM.Layouts/Flex.cs` (modified)
- `FancyWM.Layouts/Tiling/SplitPanelNode.cs` (modified)
- `FancyWM.Tests/TilingWorkspaceTest.cs` (modified)
- `FancyWM/Controls/KeybindingList.xaml.cs` (modified)
- `FancyWM/ITilingService.cs` (modified)
- `FancyWM/MainWindow.xaml.cs` (modified)
- `FancyWM/Models/AppState.cs` (modified)
- `FancyWM/Models/BindableAction.cs` (modified)
- `FancyWM/Models/Entities.cs` (modified)
- `FancyWM/Models/KeybindingDictionary.cs` (modified)
- `FancyWM/Models/Settings.cs` (modified)
- `FancyWM/MultiDisplayTilingService.cs` (modified)
- `FancyWM/Resources/Strings.resx` (modified)
- `FancyWM/TilingOverlayRenderer.cs` (modified)
- `FancyWM/TilingService.Private.cs` (modified)
- `FancyWM/TilingService.cs` (modified)
- `FancyWM/TilingWorkspace.cs` (modified)
- `FancyWM/Utilities/DebugLock.cs` (modified)
- `FancyWM/ViewModels/SettingsViewModel.cs` (modified)
- `FancyWM/Windows/SettingsWindow.xaml` (modified)
- `README.md` (modified)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicAutoCreatedDesktopTransferIntegrationTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicDesktopCreationOrchestratorTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicDesktopCreationTrackerTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicLayoutCoordinatorTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicLayoutDestinationSearchTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicLayoutEventTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicLayoutMultiMonitorTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicWindowTransferEventTrackerTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicWindowTransferOrchestratorTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/AlgorithmicWindowTransferWorkspaceIntegrationTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteArrangeFailureTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteCapacityTransitionPlannerTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteCommandControllerTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteDropControllerTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteExistingWindowOverflowIntegrationTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteLayoutEngineConstructionTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteLayoutEngineInvariantTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteLayoutEngineOperationTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteLayoutEngineRatioTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteLocalPlacementTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteRuntimeLifecycleTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/MultiDisplayTilingServiceIntegrationTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.cs` (new)
- `FancyWM.Tests/AlgorithmicLayouts/UniqueWindowMockFactory.cs` (new)
- `FancyWM.Tests/LegacyTilingWorkspaceCompatibilityTest.cs` (new)
- `FancyWM.Tests/Models/MasterSatelliteKeybindingTest.cs` (new)
- `FancyWM.Tests/Models/MasterSatelliteLayoutSettingsTest.cs` (new)
- `FancyWM.Tests/Models/MasterSatellitePreviewPlanTest.cs` (new)
- `FancyWM.Tests/Models/SettingsViewModelTest.cs` (new)
- `FancyWM.Tests/MultiDisplayActiveDisplaySelectorTest.cs` (new)
- `FancyWM.Tests/TilingWorkspaceMasterSatelliteTest.cs` (new)
- `FancyWM.Tests/WorkspaceFloatingWindowRegistryTest.cs` (new)
- `FancyWM/AlgorithmicLayouts/AlgorithmicDesktopCreationOrchestrator.cs` (new)
- `FancyWM/AlgorithmicLayouts/AlgorithmicDesktopCreationTracker.cs` (new)
- `FancyWM/AlgorithmicLayouts/AlgorithmicLayoutCoordinator.Destinations.cs` (new)
- `FancyWM/AlgorithmicLayouts/AlgorithmicLayoutCoordinator.cs` (new)
- `FancyWM/AlgorithmicLayouts/AlgorithmicLayoutEvent.cs` (new)
- `FancyWM/AlgorithmicLayouts/AlgorithmicLayoutNotificationFormatter.cs` (new)
- `FancyWM/AlgorithmicLayouts/AlgorithmicWindowTransferEventTracker.cs` (new)
- `FancyWM/AlgorithmicLayouts/AlgorithmicWindowTransferOrchestrator.cs` (new)
- `FancyWM/AlgorithmicLayouts/LayoutStateKey.cs` (new)
- `FancyWM/AlgorithmicLayouts/MasterSatelliteArrangeFailure.cs` (new)
- `FancyWM/AlgorithmicLayouts/MasterSatelliteCapacityTransitionPlanner.cs` (new)
- `FancyWM/AlgorithmicLayouts/MasterSatelliteCommandController.cs` (new)
- `FancyWM/AlgorithmicLayouts/MasterSatelliteDropController.cs` (new)
- `FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs` (new)
- `FancyWM/AlgorithmicLayouts/MasterSatelliteResults.cs` (new)
- `FancyWM/AlgorithmicLayouts/MasterSatelliteRuntimeRegistry.cs` (new)
- `FancyWM/AlgorithmicLayouts/MasterSatelliteRuntimeState.cs` (new)
- `FancyWM/AlgorithmicLayouts/PendingWindowTransfer.cs` (new)
- `FancyWM/Models/MasterSatelliteLayoutSettings.cs` (new)
- `FancyWM/MultiDisplayActiveDisplaySelector.cs` (new)
- `FancyWM/Pages/Settings/LayoutsPage.xaml` (new)
- `FancyWM/Pages/Settings/LayoutsPage.xaml.cs` (new)
- `FancyWM/Pages/Settings/MasterSatellitePreviewPlan.cs` (new)
- `FancyWM/TilingService.MasterSatellite.Commands.cs` (new)
- `FancyWM/TilingService.MasterSatellite.cs` (new)
- `FancyWM/TilingWorkspace.Algorithmic.cs` (new)
- `FancyWM/WorkspaceFloatingWindowRegistry.cs` (new)
- `IMPLEMENTATION_STATUS.md` (new)
- `TODO.md` (new)
- `docs/master-satellite-layout.md` (new)
- `.gitmodules` (modified so the custom submodule commit is reachable)
- `winman-windows/src/WinMan.Windows/Windows/Com/BuildGeneric.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/FaultTolerantWin32VirtualDesktopService.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/IWin32VirtualDesktopService.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktopManager.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktopService17661.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktopService22000.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktopService22449.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktopService22621R2215.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktopService22631R3085.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktopService26100.cs` (published in submodule commit `adb9f55`)
- `winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktopServiceGeneric.cs` (published in submodule commit `adb9f55`)

## Last successful build

- `dotnet build FancyWM.Tests/FancyWM.Tests.csproj -c Debug --no-restore --property:Platform=AnyCPU` — succeeded, 0 errors.
- `dotnet build FancyWM.Tests/FancyWM.Tests.csproj -c Release --no-restore --property:Platform=AnyCPU` — succeeded, 0 errors.
- Visual Studio 18 `MSBuild.exe FancyWM.sln /t:Build /p:Configuration=Debug /p:Platform=x64 /m /nologo /verbosity:minimal` — succeeded, 0 errors, and produced the Debug x64 MSIX.
- Visual Studio 18 `MSBuild.exe FancyWM.sln /t:Build /p:Configuration=Release /p:Platform=x64 /p:GenerateTemporaryStoreCertificate=False /m /nologo /verbosity:minimal` — succeeded, 0 errors, and produced the Release x64 MSIX/MSIX upload.
- `dotnet publish FancyWM.GUI/FancyWM.GUI.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --property:Platform=AnyCPU --property:PublishSingleFile=false --property:PublishTrimmed=false --output artifacts/portable/FancyWM-Master-Satellites-win-x64-20260905-r4` — succeeded. Its ZIP was opened and every one of 508 file streams was read successfully.
- The source project, target frameworks, SDK selection, package versions, and repository warning policy were not changed to obtain these results.

## Last successful tests

- `MasterSatelliteCommandControllerTest` — 18/18; `TilingServiceAlgorithmicIntegrationTest` — 46/46, including the exact public two-window `MoveRight` regression.
- `dotnet test FancyWM.Tests/FancyWM.Tests.csproj --configuration Debug` — 501 passed, 0 failed, 0 skipped.
- `dotnet test FancyWM.Tests/FancyWM.Tests.csproj --configuration Release` — 502 passed, 0 failed, 0 skipped. The extra case is the Release-only invariant-recovery test.
- `FancyWM.Layouts.Tests` Debug and Release — 33/33 in each configuration.
- `FancyWM.ThemeEngine.Tests` Debug and Release — 16/16 in each configuration.
- Root and `winman-windows` `git diff --check` — exit 0; only line-ending conversion warnings were emitted.

## Baseline failures

- No baseline build or test failures.
- Before production changes, Debug and Release `FancyWM.GUI` builds both succeeded with 0 errors and 13 warnings; `FancyWM.Tests` passed 116/116 and `FancyWM.Layouts.Tests` passed 27/27.
- Baseline warnings remain: ModernWpf `MSB4130`; `NU1510` for `System.Drawing.Common`/`Microsoft.Win32.Registry`; `NU1902` for AngleSharp 0.17.0; `NU1903` for Newtonsoft.Json 9.0.1; `CS0162`, `CS0169`, `CA2022`, x64 `MSB3270`, and packaging restore `NU1701`/`NU1503` diagnostics.
- Plain `dotnet build FancyWM.sln` cannot import the Visual Studio-only Desktop Bridge targets. Visual Studio 18 MSBuild is required. Its first x64 build needed a normal RID restore; its default Release packaging cleanup also hits `RemoveDisposableSigningCertificate` with a missing thumbprint after successfully creating the package. Passing `GenerateTemporaryStoreCertificate=False` at build time avoids that environment/tooling cleanup defect without changing source.

## New failures

- None remaining in automated reproduction. The live `System.InvalidOperationException` from `Dispatcher.VerifyAccess` in `MasterSatelliteCommandController.IsActive` was a new implementation defect, not a baseline failure; it has a red-before/green-after worker-thread regression and remains fixed. The `r2` stale-orientation and premature-floating reports were also new implementation defects; their automated reproductions and `r3` physical checks are green. The `r3` two-window `MoveRight` rejection was a command-semantics defect; controller and direct-service regressions pass and the correction is packaged in `r4` pending physical confirmation.
- The expanded direct-service gate briefly exposed a Removed-before-Added manual-move regression (56/57 passed before the correction). Its root cause was fixed rather than suppressed; the affected parameterized test now passes 2/2 and the complete direct-service class, including the later directional-move regression, passes 46/46.
- Earlier continuation defects remain fixed with passing regression tests. Fixture-only corrections included replacing an inaccessible Moq proxy with a concrete animation fake, expecting the event model's `Guid.Empty` default correlation, recognizing that an active empty target is canonical, and preserving a canonical window's actual dimensions so a simulated drag is classified as moving rather than resizing.

## Architecture decisions

- The canonical tree uses only existing split panels and Flex; `LayoutFunctionNode`/`RatioLayout` are rejected by the invariant and are not the implementation path.
- `MaxSatellites` is independent of `AutoSplitCount`; geometry comes from each display's live `WorkArea`.
- Runtime roles, reservations, HWNDs, window references, and desktop references are never serialized.
- The coordinator owns the only runtime registry and transfer state for its workspace. Services publish immutable physical capacity snapshots; the coordinator never calls service/backend code while holding its mutation lock.
- Overflow is plan → preflight → reserve → pending intent → unlocked OS move → correlated materialization → commit/release. Failures are bounded and idempotent.
- A reserved automatic target may roll back to source after failure. Positive manual evidence (an unexpected third desktop, a retained manual marker, or shared floating ownership) wins and prevents a later terminal notification from moving the window back.
- Existing-window shrink keeps an exact source restore point until terminal success/failure. Increasing capacity performs no automatic backfill.
- HWND generation boundaries are explicit and reference-based. Destroy tombstones the exact wrapper and retains its mapping only until delayed removal or replacement Added can identify/purge it; a replacement clears handle-scoped state, while later old-wrapper callbacks cannot reclaim the numeric HWND.
- Incoming ownership uncertainty is neutral for at most six 50 ms Dispatcher probes. A probe generation is tied to the exact wrapper and HWND; resolution is reconciled using the already observed desktop, while timeout produces exactly one floating fallback.
- Capacity transitions retain their original source desktop and validation settings until every move reaches a terminal state. Commands, mouse drops, and float/unfloat mutations cannot interleave with that temporary over-capacity state; reentrant settings updates are processed afterward.
- Rebalance compares canonical allocation semantics before commit; an equivalent tree is a true no-op, while invalid or uneven allocation still follows the atomic recovery path.
- A directional satellite move normally reorders within the active axis. If the next physical neighbour on that axis is the master, the boundary crossing delegates to atomic promotion; this preserves the canonical tree and the old master's exact selected slot without creating a second movement architecture.

## Known limitations

- `TilingService` is now instantiated directly against deterministic WinMan event doubles; real Windows shell/COM timing, physical pointer input, and actual WPF toast rendering remain manual/integration gates.
- `r3` physically confirmed 3440×1440, Horizontal satellites, and an existing-desktop overflow. `r4` has not yet received the exact direct-key `MoveRight` confirmation. No live verification was performed for 100/125/150/200% or mixed DPI, physical monitor hot-plug, runtime WPF binding diagnostics, live mouse overlay, real COM desktop creation, elevated windows, UWP/WinUI, or representative non-resizable/min-size applications.
- During Firefox startup, one live trace showed Firefox overwrite its first requested X position after registration; the next focus/position event made FancyWM reapply the correct canonical slot. This transient real-application timing case is not yet covered by a dedicated bounded post-add geometry retry.
- The overlay suppresses an impossible target and explains rejection after release, but does not draw a dedicated red invalid-target graphic.
- Minimize/restore preserves original geometry but restores into the next available canonical role rather than guaranteeing the former master/satellite index.
- The repository already contained unrelated `async void`, unobserved Dispatcher/task calls, blocking waits, and exhaustive/default `NotImplementedException` paths. No new feature path adds those patterns; the literal whole-repository quality checkboxes remain open.
- The feature branch was created after the reviewed implementation was committed. User files `.codex/`, `BEGIN_PROMT.md`, and `CONTINUE_PROMT.md` remain local and were not changed or removed.
- `winman-windows` desktop-creation support is committed at `adb9f55b84b567db9f6e0ee8df4c33d11a12d91f` in the reachable user fork; real COM desktop creation across supported Windows builds remains a manual gate.

## Next exact action

Launch `FancyWM-Master-Satellites-win-x64-20260905-r4`, select Horizontal with the master on the right, leave exactly one satellite on the left, focus that satellite, and invoke `MoveRight`. Verify that it becomes the right-side 60% master, the old master becomes the left-side 40% satellite, focus stays on the moved window, and no rejection toast appears. If Firefox initially overwrites its assigned position again, capture `%APPDATA%\FancyWM\fancywm.log`, the screenshot time, and whether the slot corrects without an extra focus change.
