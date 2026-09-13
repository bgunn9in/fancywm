# PERF-016 native owners and teardown — 2026-09-12

Continuation (2026-09-12): the scopes and receipts below remain preserved.
The subsequent startup graph, three additional native lifetime corrections,
full delivery and remaining GDI/shell criteria are recorded in
[FULLGRAPH_LIFETIME.md](FULLGRAPH_LIFETIME.md), checkpoint
`FWM-PERF016-FULLGRAPH-20260912-R1`. Its criterion mapping supersedes older next
actions; it does not expand the earlier scoped native PASS results.

Current checkpoint: `FWM-PERF016-NATIVE-20260912-R1`. PERF-016 remains
**IN_PROGRESS**. The new results establish the scopes below; they do not accept
the whole ID or a performance stage. `PerformanceClaim=false`,
`StageAccepted=false`, `LedgerAppended=false` throughout this continuation.

The native constructor tests exposed an OverlayHost construction rollback defect.
The retained correction changes exactly `OverlayHost.cs` and
`OverlayHost.OverlayWindow.cs` relative to the inherited nested-notification C2
production baseline. Full correctness delivery C2 passes. No batching, worker,
animation or 250 ms reconciliation design changed.

## Verified receipts

All paths below are under `artifacts/performance/`. Receipts are exclusive-create.
The final checkpoint preserves sorted path/size/SHA256 evidence for successful
and failed runs, source/binary provenance, raw logs, TRX, commands and process order.

| Scope | Immutable evidence | SHA256 of receipt |
|---|---|---|
| Entry checkpoint, original 2,155 production files and old receipts | `FWM-OWNERS-NATIVE-20260912-R0/checkpoint-review.json` | `E8E3BB3D5003831B7370DD29D30329F594DE963973C5DF9F62B1966F906F39E5` |
| Native SettingsWindow/OverlayHost close and retention, before production delta | `FWM-OWNERS-NATIVE-20260912-R7/verification.json` | `32FB55B9124FCF79071546ABFC5DDD71940D35C6755BDBF534BE3638A36364DC` |
| Five constructor reds → corrected candidate, common fixture | `FWM-OWNERS-CONSTRUCTION-20260912-C1R2/verification.json` | `8B5C82DFD8DAE32D9EA731D39F4008364886878A1D68B7A631463B666ADA6CA3` |
| Full correction delivery | `FWM-OWNERS-CONSTRUCTION-20260912-C2/validation/delivery-verification.json` | `58426DF57F7999F1A46F8C43467458DC05820A34136D1974198C06A789310D3F` |
| Auxiliary windows, real hooks, partial graph graceful shutdown and owned crash | `FWM-NATIVE-TEARDOWN-20260912-R2/verification.json` | `07A1E07D155C858DF4331D3CAD248C96DE450CDD2A00FA1038D9E011E5511170` |
| Real Win32Workspace and theme watcher | `FWM-NATIVE-PROVIDERS-20260912-R2/verification.json` | `B89FEE199349BC6352CE70BB8ABE360DB2539E463AF087DB75061E974421B478` |
| Real HelpPage/WebView2, including the framework cache boundary | `FWM-NATIVE-HELP-BROWSER-20260912-R6/verification.json` | `3B46734CBAA2A8AB0BB7D7690150734FA85A6F2AC8C0F583359ABCEDA805ECDC` |

## SettingsWindow and OverlayHost

The earlier managed SettingsWindow tests pass HWND=0; OverlayHostCloseTest
exercises cleanup delegates. The new `NativeOwnerLifetimeTest` partials use
production constructors, nonzero HWNDs and a live STA dispatcher in isolated
testhost processes. A minimal production App provides the WPF resource context.
Its AppState/settings/display/workspace collaborators are controlled: AppState is
initialized through the fixture, settings subscriptions are counted, the view
model uses its existing controlled autostart-query constructor, and providers can
inject precise event-add/remove faults. These runs do not open Win32Workspace,
install input hooks or execute MainWindow/Startup.AppMain. Real providers are
covered separately below.

Seven initial native tests cover normal, repeated, reentrant and stale close
paths; OverlayHost also exercises its supported worker-thread Close. Both
overlay HWNDs are verified, including interactive → non-interactive ownership
and the latter's WPF helper HWND on the same native thread/process. Closing
releases HwndSource, Application.Windows entries, settings/page/view-model
ownership, subscriptions, the active refresh timer and cursor queue. Delayed
provider/settings/cursor/navigation callbacks remain inert after closure.

Settings failures cover Closed notification, view-model disposal, their combined
failure, and page disposal/logger delivery. Overlay failures cover workspace,
display, settings and Closed notification. Only the deliberately injected
exceptions are handled at their actual dispatcher/WM_DESTROY delivery boundary;
normal errors still fail the run. Original exception identity and later cleanup
are checked. Application shutdown closes SettingsWindow after explicit overlay
service closure, while the dispatcher is still alive.

Each owner has 10 warmup lifetimes followed by two cumulative 50-cycle epochs,
with the dispatcher alive during collection and USER/GDI sampling. Settings
tracks five weak objects per cycle; overlay tracks seven, including native WPF
sources. At 100 cycles all 500/700 references are dead, with exact unchanged
USER/GDI. The fixed 1.2-second settling period is applied before every sample.

R7 passes **161 leaves per configuration: 7 native + 154 affected**. The corrected
candidate passes **166 leaves per configuration: 12 native + 154 affected**,
with 68 close/retention/process observations plus 10 constructor observations
across Debug/Release. Its verifier rejects six negative controls, including
missing scenario, surviving HWND, retained owner, duplicate epoch and failed
constructor cleanup. MSTest data-driven parents are excluded from leaf counts:
a leaf is UnitTestResult without InnerResults.

## Constructor defect and retained correction

Common B3 tests reproduce five native failures on the inherited production
source: position read, first display subscription, second surface acquisition,
workspace subscription, and secondary cleanup failure during rollback. Their
surviving HWND counts are **1, 1, 2, 2, 2**; live subscription counts are
**0, 0, 1, 2, 2**. The fixture records these witnesses before cleaning up its own
windows. B3 has five intended red leaves and unchanged common test source.

OverlayHost now records attempted subscription acquisition and rolls back only
acquired/attempted resources, invalidates cursor work, and closes both surfaces
on constructor failure. OverlayWindow joins construction rollback and attaches
secondary close errors to the original constructor exception. Both surfaces
must enter rollback before either closes: native owner destruction can deliver
WM_DESTROY to its owned surface synchronously. C1's 165/166 result exposed that
ordering requirement; C1R2 preserves the original exception and passes 166/166
in Debug and Release. Normal close error delivery remains unchanged.

This is a correctness correction without an A/B performance claim. C2 runs
fresh full Debug **2067/160/31** and Release **2123/160/31** test leaves for the
application/layout/theme projects, both RID-aware restores, GUI builds and
x64/MSIX builds. Dependency validation checks the dirty root without mutation,
then replays pinned Check/Apply/Apply/Check, conflict preservation and a Release
build in an isolated archive. Existing nested-notification measurement evidence
is independently reverified; no new timing/allocation measurement is claimed.
Both MSIX archives have verified AMD64 application payload and x64 package
identity. They were neither installed nor launched nor published.

## Other native owners and application boundary

The standalone `native-teardown` harness uses production App and
Startup.ConfigureServices, real file-backed AppState/settings, public
SettingsViewModel construction and actual registered hook types. Its settings,
theme files, logs and browser profile are under the unique run directory. It
does not execute MainWindow, App.InitializeComponent's StartupUri, or
Startup.AppMain. This is a partial production service graph, not a full startup.

Teardown R2 uses six sequential Debug/Release processes on fresh owned desktops
that are never switched to the input desktop. StartupWindow, AboutWindow,
ErrorMessageBox and MessageBox each pass 10 warmup + 100 post-warmup native
lifetimes, zero weak owners/sources and unchanged USER/GDI. Both real low-level
hook classes pass 110 lifetimes each per configuration, with nonzero native
HHOOKs on the owned desktop, worker completion/join, zero handles/thread IDs
after disposal and zero weak hook/thread references after each epoch.

Actual App.Terminate closes five final native windows and awaits both DI hooks
before Application.Exit; Closed/Exit occur before dispatcher shutdown and the
service provider is subsequently disposed. These are **1,818 observations and
five negative controls**. Application termination is tested separately from
the native toast/minimal-App shutdown tests.

Providers R2 adds two sequential Debug/Release processes and **690 observations,
six negative controls**. Win32Workspace.Open creates its own native message HWND,
three workers, actual display manager and actual Win32VirtualDesktopManager;
the normal reconciliation timer runs before repeated Dispose. Each epoch has
zero weak workspace/worker/display-manager owners, dead native HWND, stopped
workers, zero reported errors and unchanged USER/GDI. ThemeEngineManager uses
its public production Initialize, real FileSystemWatcher, timer, converter and
dispatcher resource application. Each owned CSS mutation is actually applied;
post-disposal mutations are inert. Watcher/subscription/timer fields clear and
the worker completes. Both owners pass 10 warmup + 100 measured lifetimes per
configuration; these are separate owners in one partial graph, not repeated
full application startup.

Hard crash uses only four owned partial-graph processes, each with five live
HWNDs and two native hooks. Parent identity checks precede a child-owned signal.
Environment.FailFast exits with **0x80131623**; self-TerminateProcess uses
**0xE000F016**, in both configurations. The process and all witnessed HWNDs are
gone afterward. No Closed or managed ProcessExit cleanup runs after crash entry.
This verifies OS reclamation after abrupt termination; it does not assert
managed cleanup or exercise the complete App.HandleException/Startup.AppMain
crash handler.

Some EnumDesktopWindows calls return zero with last-error zero. They remain
unsuccessful scans in the raw receipts; an empty row list is not elevated to
proof of an empty desktop. Direct acquired HWND/process identity and termination
provide the cleanup witnesses. See the [native API return contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdesktopwindows).

## HelpPage/WebView2 and observed WPF cache retention

Browser R6 runs two sequential owned Debug/Release processes on the current
desktop. All windows are offscreen, ShowActivated=false, with no hooks or input;
the foreground HWND is identical before/after each process. The installed
WebView2 runtime is **152.0.4191.66**, SDK **1.0.1210.39**. A process-local data
folder override isolates the browser profile. Local about:blank replaces remote
help navigation. Actual SettingsWindow.GoToPage constructs production HelpPage,
creates a real controller, executes document script, navigates General → Help
and verifies cached page identity before repeated close/stale navigation.

A separate live keeper browser shares the runtime during 10 warmup + 100
page/controller lifetimes. After both epochs it closes and the native
BrowserProcessExited event reports Normal on the live dispatcher. This event
covers the [associated runtime process collection](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.browserprocessexited),
not arbitrary browser processes.

The first passive 1.2-second samples in R3–R5 retain exactly the last shown
SettingsWindow and its view model; browser, page and controller weak references
are already dead. R5 preserves an owned full-memory dump. D1's dumpheap/gcroot
and ViewManager field inspection identify `_inactiveViewTables` →
ListCollectionView → PagesList/XAML context → SettingsWindow. This matches the
[WPF inactive-view cache design](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/Data/ViewManager.cs):
non-notifying collections survive two purge cycles. The installed WPF version
is `10.0.12-servicing.26422.108+95017c711e6afc1085133d440e42b4bd78155701`.

R6 preserves the pre-maintenance samples (the same two last-owner references)
and then performs three ordinary public CollectionViewSource.GetDefaultView
activities, each followed by live dispatcher settling. It does not call internal
Purge or edit framework tables. Both cumulative epochs then have **0/300 and
0/600 weak references**, USER **21→21**, GDI **8→8**. The verifier checks **236
observations and six negative controls**. This is a scoped post-maintenance
retention PASS. Immediate collection of the last shown SettingsWindow is not
claimed; changing production to force framework cache cleanup is not justified
by these observations.

## Criterion mapping and exact remaining work

| Criterion | Existing evidence | New evidence / remaining gap | Condition for PASS |
|---|---|---|---|
| Toast native ownership | Pinned toast R2, five isolated tests | Old 2,905-file manifest and receipts reverified; no separate toast rerun | Scoped criterion already PASS; preserve it |
| Settings/overlay native close, failures and retention | Managed helper tests; HWND=0 settings | R7 + construction B3/C1R2 + C2: real handles, failure/order/worker/stale/native retention | PASS for tested constructors, paths and controlled provider boundary |
| Shown Settings/Help browser ownership | Managed page/navigation tests | Browser R6 actual controller/cache/stale close; last-window framework cache observed separately | Native closure and post-maintenance retention PASS; immediate window collection unproven |
| Auxiliary window and input-hook owners | Managed ownership tests | Teardown R2 actual windows/HHOOKs and repeated epochs | PASS for stated native owners without synthesized input delivery |
| Workspace/display/virtual-desktop and theme lifetime | Managed workspace/theme disposal suites | Providers R2 real message loop, COM-backed provider, file watcher and repeated epochs | PASS for separate owner lifetimes; cross-service/full-graph ownership remains separate |
| Graceful App shutdown before dispatcher stop | Managed AppLifetime tests; earlier full-app V19/M8/DPI receipts | Actual App.Terminate in Teardown R2 with real settings/hooks/five windows | Partial graph PASS; whole graph needs all startup owners and workers live |
| Whole-app repeated retention and teardown | Earlier full-app behavior/graceful receipts are retained, not new repeated retention | No new unchanged MainWindow/full startup lifetime epoch | Disposable isolated user/session/VM or an authorized controlled system-parameter seam, then comparable full-graph epochs and shutdown/failure receipts |
| Abrupt termination | Managed shutdown/error tests | Four real owned abrupt exits; native resource reclamation observed | OS cleanup scope PASS; complete application crash-handler/service-graph behavior still needs the isolated full graph |
| Native heap/GPU and presentation | Existing scoped CPU/DPI/behavior evidence | Weak refs and USER/GDI do not supply allocation attribution or physical presentation | Appropriate attributed native heap/GPU/presentation observations; no inference from process exit |

MainWindow.ApplySystemParameters unconditionally writes
`SystemParameters.Instance.WindowArranging=false` (SPI_SETWINARRANGING).
Executing unchanged full startup here would change the user's global window
arranging setting, contrary to this session's constraint. A fresh desktop within
the same user session is insufficient isolation for that setting. No policy,
driver, HWS, display or window-arranging setting was changed by this continuation.
The next full-graph action is to run the pinned source in a disposable isolated
session/VM with native owners and repeated epochs; that external environment is
not supplied here. This is a criterion dependency, not whole-ID BLOCKED.

PERF-010/021 remain governed by EXECUTION-R2 and the already-passed DPI restoration
receipt: complete attributed GPU execution, then candidate GPU A/B, and existing
non-client/full-frame hardware identity. No new contract appeared in this work;
no DPI processes, WPR admission or capture were repeated.

## Preserved failures, integrity and reproduction

All `FWM-OWNERS-NATIVE-20260912-R0` through R7 remain. R1 had a compile-only
fixture error, R2 an invalid StartupUri assignment, R3 a resource namespace
compile error, R4 settling/exception-masking fixture faults, R5 an incorrect
zero-owner assumption; R6 was an intermediate native pass and R7 the completed
initial fixture. The first R7 verifier incorrectly counted data-driven parents;
that failed verifier/log and the corrected exclusive receipt are both retained.

Construction B1/B2/B3, C1 and C1R2 remain: B2's fixture cleanup fault was corrected
before common B3 red evidence; C1 exposed native owner-destruction ordering.
Teardown R1 preserves the failed empty-desktop enumeration assumption. Providers
R1 preserves missing ModernWpf EndInit fixture initialization. Browser R1
preserves real controller admission failure 0x80070578 on a private desktop; R2
preserves the disposed-getter expectation error; R3/R4/R5 and D1 preserve passive
retention and its root diagnosis. No failed artifact is deleted or overwritten.

Exact invocations are saved by each runner. Current commands use fresh IDs:
`Run-NativeOwnerLifetime.py` plus `Verify-NativeOwnerLifetime.py --baseline <B3>`;
`Validate-NativeOwnersDelivery.py --candidate <C1R2>` for a production correction;
`Run-NativeTeardown.py --modes graceful failfast terminate`, `--modes providers`,
or `--modes browser --current-desktop-browser`, followed by the corresponding
strict verifier. Current-desktop mode rejects every mode except browser and
never acquires hooks. Replaying receipts is not a new test run.

The original ledger remains **41,782,945 bytes / 41,325 data rows**, SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
The whole old file is the unchanged prefix; suffix length is zero, SHA256
`E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`.
Correctness/diagnostic/scoped lifetime evidence adds no measurement rows.

HEAD/tree and winman-windows HEAD remain the checkpoint values. The final
checkpoint review verifies all **2,155 production files**, including all **72
winman-windows files**, allowing exactly the two C1R2 correction files. All other
inherited files are byte-exact except explicitly updated live documents and this
session's checkpoint-review script. Final document sizes, lines, SHA256 and
Git/submodule checks are in the final checkpoint's `validation/checkpoint-review.json`.
The old toast receipt/manifest and DPI restoration pinned hashes remain intact.
