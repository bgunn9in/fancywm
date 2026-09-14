# Performance implementation status

## Idle CPU observation complete, no application change — 2026-09-14

The already running ordinary portable **2.19.1.9** (PID 34116) completed three
idle intervals after 15 s stabilization, using a separate PowerShell observer
(PID 39472). One ordinary visible, non-minimized, non-cloaked window remained:
WindowsTerminal, excluded from tiling by the user's settings. Settings select
master right, Horizontal with mixed satellites enabled; this observation does
**not** establish idle costs with a populated master/satellite or mixed layout.

| Idle interval | Actual duration | TotalProcessorTime delta |
|---|---|---|
| 1 | 30.064 s | 218.75 ms |
| 2 | 30.190 s | 156.25 ms |
| 3 | 30.091 s | 218.75 ms |

Total: **593.75 ms process CPU over 90.345 s**, equivalent to 0.657% of one
logical processor's capacity (individual intervals 0.518–0.728%). This is total
FancyWM process CPU, not DiscoverWindows cost or whole-computer utilization.
Allocations/GC and command/frame latency were not measured. No concrete redundant
operation or justified optimization was established; no code experiment or
before/after improvement is claimed. The bounded pass ends without expanding
the investigation or repeating the existing desktop-snapshot optimization.

All 357 external polls observed unchanged last-input timestamps, visible HWNDs/
rectangles and foreground; window snapshots also match between intervals.
Polling was requested every 250 ms; this is sampled observation, not a native
event trace. Receipt assertions, separate-process identity and unchanged settings
SHA-256 checks pass. Only one FancyWM instance ran and remains running.

Earlier attempts below remain **excluded from idle results**. Counts are ordinary
visible native windows at the start, not necessarily tiled windows; an input
timestamp change alone does not identify its producer.

| Attempt | Duration | TotalProcessorTime delta | Windows at start | Activity detected |
|---|---|---|---|---|
| Initial | 30.146 s | 437.5 ms | 4 | Input |
| After the user's free-interval reply | 30.235 s | 343.75 ms | 5 | Input |
| Retry after a stable 10 s input/cursor probe | 30.113 s | 2562.5 ms | 5 | Input, windows and foreground |

Application code/settings, 250 ms reconciliation, dependency patches and the
portable are unchanged. Documentation `git diff --check` passes; no regression
or rebuild was needed. Local observation files remain ignored under
`artifacts/performance/FWM-IDLE-20260914/`; accepted receipt: `idle-202157.json`.
Historical unfinished criteria retain their status.

## PERF-010/021 four-window measurement complete — 2026-09-14

The current practical PERFORMANCE iteration is **complete**. Further changes
require a concrete reproducible problem or a measured bottleneck. Historical
results and remaining IN_PROGRESS criteria retain their existing status.

At `0b625b7`, the existing Release full-app host completed `run-7`: four real
WinForms HWNDs at a time, Horizontal/Vertical/Mixed, two warmup cycles and three
measured cycles per layout. **78 measured operations** (including 18 automatic
drags); 52 warmup operations excluded. Native geometry, exact slots/master roles,
operation focus, visible WPF preview and drop cleanup pass. Input now runs off
the Dispatcher so native hook callbacks can consult it; owned Alt/button releases
are protected across awaits. Admission, ownership and interference guards remain.

Median per-cycle process CPU: H 1453 ms, V 625 ms, M 547 ms; allocations:
4.963 / 4.869 / 8.746 MiB. These include in-process fixture work and background
application work, not command-exclusive costs. Fixed waits/native polling are
observation intervals, not frame latency. **0 model additions and 0 child Reset
notifications** across measured operations. CPU spread and un-attributed mixed
allocations do not establish a specific redundant production operation: no
optimization selected. [Short table, method and limits](docs/performance/FOUR_WINDOW_INTERACTION.md).

Runs 1–6 are excluded, including all partial warmups; repeated occupied-input
symptoms cannot be attributed to the user alone. Release build, run-7 assertions,
independent receipt checks and diff checks pass. Twelve target HWNDs across three
phases and both processes are gone; no pressed input remains. User JSON, existing
window rectangles and display geometry match; WindowArranging restored normally.
Production is unchanged, so the [current portable](docs/portable.md) is retained;
no new portable, full regression, ETW/GPU work or second candidate. Raw results
remain ignored under `artifacts/performance/FWM-FOUR-WINDOWS-20260914/`.

## PERF-017 periodic candidate checked — 2026-09-14

At `9011e2f`, the three hook/policy files and directly related tests show no
safe, useful local removal of repeated work. Tick delegates are already reused;
the admitted attempt's second clock read preserves the independently tested
retry timestamp before installation. **Candidate not implemented; application
and tests unchanged.** The 1 s watchdogs and strict >5 s recovery remain intact.

Existing scenarios produce the same results in Debug/Release:

| Scope | Measured result in each configuration |
|---|---|
| 1,000 warmed not-due RefreshIfIdle calls | 0 managed allocation bytes; 0 install/unhook adapter calls |
| 100 cycles: failed attempt, not-due tick, successful retry | 300 ticks; 500 clock reads; 200 install attempts |
| Replacement and fixture cleanup | 100 replacement + 100 final unhooks; 100 old handles preserved on failure; 0 remaining fake handles |

These are managed policy/adapter counts, not native API measurements. No
before/after implementation exists and no performance gain is claimed. CPU,
native wakeups, input latency and interactive recovery were not measured.
Targeted **97 Debug + 97 Release** cases pass, zero failures/skips, including
retry cadence, clock rollback, failure cleanup, message/callback order and
keyboard/mouse startup/disposal. Existing compiler warnings remain. Raw TRX/logs
are ignored under `artifacts/performance/FWM-PERF017-PERIODIC-20260914/`.

No full regression, interactive UI, new packaging or second candidate. Reuse
portable **2.19.1.9**, source `2dc08a8`, archive
`artifacts/portable/FancyWM-Portable-win-x64-20260914-160522-654.zip`;
SHA-256 rechecked as
`0A2EDEF843820E9330368A2209FDFAF0CAD335629CA25363AD265E496196C8FE`.
[Archive and instructions](docs/portable.md). Only these two status documents
changed; dependency patches/gitlink and unrelated files are preserved. The
bounded pass is complete; further hook work needs a concrete input/recovery
defect. Historical native evidence limits remain unchanged.

## User-confirmed test portable — 2026-09-14

User result: **«Все работает как надо»** for the requested manual check of
portable **2.19.1.7**, source `4477212` (application code through `e69c96c`).
The manual verification stage is complete. No repeat UI/test/build run is pending
without a concrete reason; unspecified monitors, DPI and applications remain
outside that confirmation. Previous measured improvements and test results are
unchanged. This documentation closeout performed no tests or rebuild.

## Discovery candidate checked; current portable ready — 2026-09-14

PERF-002 selected candidate is **already optimized**, so production remains at
`e69c96c`. Provider snapshot counter: two eligible windows → one call, none
eligible → zero, initial snapshot failure → three calls including required
per-window retries. Ten targeted Release cases pass, including snapshot mutation
and missed-event reconciliation. No new optimization or before/after gain.
[Bounded result](docs/performance/DISCOVERY_SNAPSHOT_CHECK.md).

Fresh Release win-x64 self-contained portable **2.19.1.6**, source `e69c96c`:
511 files, 72,338,952-byte ZIP; all entry hashes/CRC, 669 dependency asset
references, x64 apphosts/runtime and both extracted EXEs' help/version pass.
[Archive and instructions](docs/portable.md). Existing dependency patches match
their manifest; gitlink/local changes are preserved. Old builds are retained.
The earlier 758 Debug / 800 Release results were not repeated. No interactive
UI, push, installation or external publication. This bounded pass is complete.

## Preview conversion follow-up complete — 2026-09-14

After `43dcfcc`, the authorized follow-up removed one enumerator allocation
while creating PreviewWindows sets from accepted plans. Fresh sets preserve
current hash/equality semantics and independent ownership; no cache was added.

Release before/after: **208 → 176 B per conversion**, or
**11,232,000 → 9,504,000 B** over 54,000 conversions. Debug also improves in all
18 cases: **11,232,000 → 9,504,048 B** in aggregate.
This is a small allocation reduction only, not a measured application speedup.
[Counter, tests and limitations](docs/performance/PREVIEW_WINDOW_SET.md).

Final affected regression: **758 Debug + 800 Release = 1558** passed leaf cases,
zero failures/skips; both feature suites and overlay/preview checks included.
Both configurations compiled. Raw receipts remain ignored under
`artifacts/performance/FWM-PREVIEW-WINDOW-SET-20260914/`. No new interactive UI,
push, installation or publication. Existing feature confirmation and all
historical research limits remain unchanged; this bounded follow-up is complete.

## One local optimization complete — 2026-09-14

Both new features are complete and manually confirmed by the user; feature
implementation, tests and closeout docs are in local commit `a40cf6d`. That UI
confirmation does not extend to unspecified monitors, DPI or applications.
No repeat UI pass is pending without a concrete reason.

PERF-010/021 local result: panel child collection updates now retain surviving
tabs during permutations and membership changes. In 72 comparable updates
(Horizontal/Vertical/mixed, both master sides), newly created WPF tabs fell
**228 → 48**, resets **96 → 0**, collection notifications **324 → 183**.
Only these counts are claimed; whole-app speed, allocation bytes and GPU work
were not measured. [Method, scope and receipts](docs/performance/OVERLAY_CHILD_ORDER.md).

Final affected regression passed **756 Debug + 798 Release = 1554** leaf cases,
with zero failures/skips. The runs include both feature suites and overlay
recovery/lifetime tests; the production projects also compiled in both modes.
Raw logs/TRX are ignored under `artifacts/performance/FWM-OVERLAY-REORDER-20260914/`.
No interactive application launch, push, installation or publication occurred.
The one-optimization scope is complete. Historical whole-ID IN_PROGRESS states
and deferred GPU/memory/provider work below are unchanged.

## Practical stage complete — 2026-09-13

The user confirmed **«Работает»**; no new issue was reported. Use the working
version and fix concrete new bugs next. There is no pending UI-run permission
step. Previous successful Debug/Release tests, GUI/x64/MSIX builds and scoped
automated UI checks retain their results; no regression or application launch
was repeated for this closeout. The checked Release x64 portable and launch
instructions are in [docs/portable.md](docs/portable.md).

General confirmation does not separately verify DPI, cross-monitor/mixed-DPI,
the exact MoveRight scenario, Firefox placement or physical focus/drag. These
remain optional checks; multiple displays and mixed-DPI coverage need a second
display. GPU, memory attribution and further optimization remain deferred.
Historical evidence and individual research statuses below are preserved.

## Historical checkpoints before user confirmation

Real-UI follow-up (2026-09-13): current-source portable publish and command-line
entry checks pass in `FWM-USER-UI-20260913-R2`; production is unchanged.
The real-UI scenario is prepared in [the current plan](PERFORMANCE_TODO.md).
At that checkpoint ordinary launch awaited permission for the app's WindowArranging write; physical
MoveRight/focus/drag, Firefox placement and visual checks are still unverified.
No full regression/MSIX or previously passed UI scenario was repeated.

Current scope (2026-09-13): practical closeout per the
[current plan](PERFORMANCE_TODO.md#current-scope--2026-09-13-practical-closeout).
GPU/presentation, deeper memory attribution and speculative variants are deferred;
earlier research next actions below are historical. Implemented optimizations,
existing scoped evidence and IN_PROGRESS statuses are preserved. Closeout is
verified in `FWM-PRACTICAL-CLOSEOUT-20260913-R1/R2`: Debug/Release regression,
x64 packages and scoped automated UI pass. No new production change or reproduced
defect; manual focus/drag remains unperformed. See the current plan for receipts
and limits. No active research next step; resume for a concrete defect or a
separately scheduled manual UI pass.

Latest verified continuation (2026-09-13): `FWM-PERF016-MEMCOUNTERS-20260913-R1` —
[native GPU and process memory accounting](docs/performance/DXGI_PROCESS_MEMORY.md).
Real Debug/Release controls calibrate DXGI process usage by adapter LUID/node/
segment and GetProcessMemoryInfo private commit. F3 uses the same native DLLs
without PSS, heap tracing or D3D interception. Each full graph completes 100
post-warmup lifetimes, clears 9,483 workload weak owners and closes all graph
HWND generations before graceful provider teardown. F3's sampled DXGI usage
is stable across live epochs and zero after shutdown in both configurations;
private commit grows in both, and USER/GDI fails Release. F2's Release LOCAL
growth and F1's missing Application.Windows checkpoints remain preserved.
The fixture correction is verified by new F2/F3 runs, not a weakened verifier.
These counters do not identify physical allocations, logical owners or
presentation; CPU and GPU values are not an atomic/additive total. Production,
five inherited corrections, PERF-002 250 ms and ledger are unchanged. No new
TRX/crash/DPI/ETL run, performance claim or stage acceptance. PERF-016 remains
IN_PROGRESS; the report maps remaining criteria and supersedes historical
next actions where applicable.

Latest verified continuation (2026-09-13): `FWM-PERF016-D3DMAP-20260913-R1` —
[actual D3D mapped points and PSS boundaries](docs/performance/D3D_MAPPED_PSS.md).
Debug/Release native controls reproduce and correct missing hardware vertex/
index method coverage. New F1 graphs verify 165,618 registered private-owner
generations, 233,598 map pairs, zero of 9,483 workload weak owners per process,
100 post-warmup lifetimes and graceful native teardown. Fourteen snapshots
inside actual Lock lifetimes identify eight graph vertex-buffer points absent
from PSS and six present surface points. Separate native shared-section controls
explain a calibrated reserved-to-committed snapshot boundary; the original
strict all-VA failure remains preserved, with WholeCloneVaStable=false.
Private-memory and USER/GDI no-growth remain false. Region extent is not resource
size; private-data release is not physical GPU release. Failed fixtures and all
earlier receipts are retained. Production, five inherited corrections, PERF-002
250 ms and ledger are unchanged. No performance/stage claim or new TRX/crash/
dedicated DPI/ETL run. PERF-016 remains IN_PROGRESS; the linked criterion mapping
supersedes historical next actions and records the remaining evidence contracts.

Latest verified continuation (2026-09-13): `FWM-PERF016-D3D-20260913-R1` —
[native D3D ownership observations](docs/performance/D3D_NATIVE_OWNERS.md).
Calibrated Debug/Release D3D11 destruction and D3D9 private-data controls now
lead into F2 full startup graphs with the same native observer DLLs. Each graph
completes 100 post-warmup lifetimes, zero of 9,483 workload weak owners after
WPF maintenance, all native graph HWND closures and graceful provider teardown.
Registered D3D9 private-owner counts remain stable across live epochs and reach
zero before process exit. Nested runtime map calls have independent native
entry/exit evidence. This does not establish complete COM/GPU resource lifetime,
physical memory release or presentation. PSS and USER/GDI no-growth remain
false; diagnostic observer metadata is included in process memory. All failed
fixtures and earlier receipts are preserved. Production, five inherited deltas,
PERF-002 250 ms and ledger are unchanged; no new TRX/crash/DPI/ETL run or stage
acceptance. PERF-016 remains IN_PROGRESS. Earlier next actions are historical
where superseded; the linked report gives the exact remaining evidence contracts.

Latest verified continuation (2026-09-13): `FWM-PERF016-PSS-20260913-R1` —
[PSS native memory and coverage boundaries](docs/performance/PSS_NATIVE_MEMORY.md).
F3 Debug/Release completes 100 post-warmup lifetimes per graph, zero of 9,483
workload weak owners after WPF maintenance, all graph HWND generations and
native graceful teardown. PSS clone maps and independent page/interval counts
are verified; clone-private no-growth is false in both configurations, and
USER/GDI passes Debug but fails Release. Native controls verify exact ordinary
private bytes and live-source clone release, then reproduce 12 mapped D3D11
buffers absent from PSS despite committed MEM_PRIVATE source mappings.
Protection alone is insufficient: all 48 ordinary VirtualAlloc controls copy.
Toolhelp heap enumeration is denied on own clones. Complete process memory,
allocator/logical ownership, unmodified shell and GPU/presentation remain open.
All fixture failures and earlier receipts are preserved. Production, five
inherited corrections, PERF-002 250 ms and ledger remain unchanged. No new
TRX/crash/dedicated DPI/ETL run, performance claim or stage acceptance.
PERF-016 remains IN_PROGRESS. Earlier next actions are historical where
superseded; the report maps each remaining criterion to its evidence contract.

Latest verified continuation (2026-09-13): `FWM-PERF016-CLR-20260913-R1` —
[CLR/native caller attribution and heap boundary evidence](docs/performance/CLR_NATIVE_HEAP_ATTRIBUTION.md).
New Debug/Release controls verify 96 traced allocation/realloc/free chains,
collectible code unload/address reuse and PID filtering. New F1 full startup
graphs retain zero of 9,483 workload weak owners per process and complete
native graceful teardown. D2/D3 verify 8,358,470 full native stacks and
61,824 managed caller witnesses with exact code-generation/QPC bounds.
D1's strict baseline heap replay failure is preserved. Native L1 reproduces
12 small allocations completing while HeapLock is held; F1 has no atomic
whole-heap replay PASS. Older finite snapshot equalities remain preserved.
Heap no-growth/logical ownership, unmodified shell/startup and broader GUI/
GPU/presentation criteria remain open; PERF-016 stays IN_PROGRESS. Production,
five inherited deltas, PERF-002 250 ms and ledger are unchanged. No performance
claim, stage acceptance, new TRX/crash or dedicated DPI run. All 32 ETLs are
rehashed with the 26 old files unchanged. Earlier next actions are historical
where superseded; see the report for exact remaining evidence contracts.

Latest verified continuation (2026-09-13): `FWM-PERF016-COHORT-20260913-R1` —
[complete baseline heap replay](docs/performance/NATIVE_HEAP_COHORTS.md).
The retained F4 Debug/Release traces now reproduce every busy default-heap
address/size from locked epoch 0 through epochs 1/2/shutdown, including initially
untraced blocks. Native control covers 48 moved realloc pairs; independent
witness checks cover 771,654 phase rows and 73,811 realloc successors.
At epoch 2, Debug retires 2,123 original generations and has 3,498 new live
generations; Release retires 2,867 and has 5,889 new live generations.
This is scoped baseline inventory/lifetime replay PASS. Heap no-growth remains
false, and native generation retirement does not prove logical owner cleanup.
Zero new native application/TRX/crash/DPI/ETL runs; only offline analysis and
receipt verification. Production, inherited five deltas, PERF-002 250 ms and
ledger are unchanged. No performance claim or stage acceptance.
PERF-016 stays IN_PROGRESS. Unmodified startup/membership/wallpaper requires
an appropriate isolated shell/user environment; original allocation stacks,
logical ownership, broader GUI no-growth and GPU/presentation remain unproved.
Earlier next actions are historical where superseded.

Latest verified continuation (2026-09-13): `FWM-PERF016-HEAP-20260913-R1` —
[native heap and graph HWND generations](docs/performance/NATIVE_HEAP_LIFETIME.md).
F3/F4 Debug/Release verify every acquired/closed graph HWND generation and
graceful startup-graph teardown, with zero of 9,483 workload weak owners and
100 post-warmup lifetimes per process on the live dispatcher. Existing isolation
adapters remain. F4 verifies 6,790,750 alloc/realloc/free rows and 163,322,214
stack PCs against independent decoders and locked native heap snapshots.
This is scoped post-attach attribution PASS: default-heap no-growth is false;
new GUI no-growth gates are mixed/false and are preserved. Prior F11 scoped
GUI PASS is not broadened. No complete native-heap/GPU leak-freedom claim.
Six new graph executions include two F1 verifier failures; F2's build failure,
admission/decoder failures and all old evidence remain archived. Production,
the five inherited deltas, PERF-002 250 ms and ledger are unchanged. No new
TRX/crash/DPI run, production candidate, delivery gate or stage acceptance.
PERF-016 stays IN_PROGRESS. Unmodified startup/membership/wallpaper needs an
appropriate isolated shell/user environment. Broader heap owner lifetime and
changing-display GUI no-growth remain unproved; PERF-010/021 still needs new
GPU/presentation contracts. Earlier next actions are historical where superseded.

Latest verified continuation (2026-09-13): [GUI counter identity and native attribution](docs/performance/GUI_RESOURCE_ATTRIBUTION.md).
F11 Debug/Release pass the unchanged strict USER/GDI no-growth gate after
45-second quiescence and first-stable-window admission: USER 44/43/44 and
44/44/44, GDI=11 throughout, zero of 9,483 workload weak owners per process.
Native raw events, 48 HIMC control pairs and live creator-thread IDs explain
the earlier USER variation. Twelve complete new full-graph processes, four
failed native fixtures and one failed fixture build are preserved. F5's changing
baseline remains rejected. The final compact targets use the production native
NoMonitor 1024x768 fallback while their actual HWND DPI is 192; this is no
physical-presentation or unchanged-shell claim. Shared PERF-010 target source
is restored byte-exact. Production, inherited five deltas, PERF-002 250 ms and
ledger remain unchanged. PERF-016 is IN_PROGRESS; no stage acceptance or
performance claim. Next evidence needs an isolated shell/startup environment;
PERF-010/021 retain their existing GPU/presentation contracts.
Earlier next actions are superseded; original receipts are preserved.


Latest checkpoint (2026-09-12): `FWM-PERF016-FULLGRAPH-20260912-R1` —
[full startup graph lifetime and exact remaining criteria](docs/performance/FULLGRAPH_LIFETIME.md).
Actual Startup/AppMain/DI graph runs on owned non-input desktops with explicit
build-copy adapters for data/BAML/global-setting isolation. Native Mica shutdown
and Debug idle-worker payload reds justify three additional production corrections;
common-fixture native retention and affected 411/423 tests pass. Full C2 passes
Debug 2069/160/31, Release 2125/160/31, GUI/RID/x64/MSIX archive, dependency replay
and measurement reverification. Corrected-source crash handler/FailFast/TerminateProcess
receipts cover six owned processes; OS cleanup is separate from partial managed
crash cleanup. Prior native owner/toast/DPI evidence remains preserved.
PERF-016 stays **IN_PROGRESS**: unchanged shell/startup membership and wallpaper
success need an appropriate isolated environment; tiling GDI no-growth remains
unmet (C5 Debug 122→131→126, Release 134→133→133). All 9,483 workload weak references
per configuration clear after maintenance; this does not prove native heap/GPU
or physical presentation. Ledger unchanged; PerformanceClaim=false,
StageAccepted=false. Earlier next actions are superseded where they conflict.

Earlier checkpoint (2026-09-12): `FWM-PERF016-NATIVE-20260912-R1` —
[native owners, construction rollback and teardown](docs/performance/NATIVE_OWNER_LIFETIME.md).
SettingsWindow/OverlayHost native close, failure/order/stale callbacks and repeated
retention pass. Five native constructor reds justify a two-file OverlayHost
rollback correction; candidate Debug/Release 166/166 and full C2
2067/160/31 Debug, 2123/160/31 Release pass, including x64/MSIX archive,
dependency replay and prior measurement reverification. The inherited nested
overlay correction and PERF-002 250 ms reconciliation remain intact.
Real auxiliary windows/hooks, Win32Workspace and theme watcher pass repeated
native epochs; partial production App.Terminate and four owned hard crashes are
verified. Hard-crash evidence proves OS reclamation, not managed cleanup.
Real HelpPage/WebView2 passes native closure and retention after ordinary WPF
view-cache maintenance; passive samples retain the last shown window/view model,
as documented by the preserved dump/root analysis. No immediate collection claim.
PERF-016 remains IN_PROGRESS for unchanged full startup graph retention/teardown
and its application crash handler. MainWindow writes global WindowArranging;
the next full-graph run requires a disposable isolated user/session/VM or an
authorized controlled seam. The current session forbids changing that setting.
Ledger unchanged; PerformanceClaim=false, StageAccepted=false. No WPR/DPI repeat.
The prior continuation paragraphs below retain historical results; their older
next actions are superseded by this checkpoint and the criterion mapping.

Earlier completed continuation (2026-09-12): [PERF-016 native toast lifetime](docs/performance/TOAST_NATIVE_LIFETIME.md).
Five new isolated tests exercise actual WPF/HWND construction, closure,
failure cleanup and Application shutdown. Debug passes 5 native + 162 related
tests; Release passes all 167. Each configuration verifies 100 post-warmup
hidden-window lifetimes with zero retained weak windows and unchanged USER/GDI
counts. The retained verifier checks 32 observation rows and four negative controls.
R1's two fixture errors are preserved; corrected R2 passes with production unchanged.
PERF-016 remains IN_PROGRESS for other native owners, whole-app retention and
hard-crash teardown. No ledger append, performance claim or whole-stage acceptance.
Next runnable action: native SettingsWindow/OverlayHost closure and retention.
The PERF-010/021 instrumentation dependency below remains; no new ETL was created.

Latest PERF-010 continuation (2026-09-12): [completed actual multi-DPI validation and display restoration](artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-DPI120-RESTORATION-R1/REPORT.md).
B18 and B17 each pass Debug/Release at actual 120 DPI on the unchanged
common fixture: four sequential processes, 48 transitions, owned input,
settings, interruption/recovery, restoration and cleanup. Independent
verification combines the pinned 96-DPI receipts with 1,616 native
120-DPI rows and 2,024 overlay pairs covering 1/10/50-window phases.
Multi-DPI criterion PASS: the user returned scale to 100%; actual HWND DPI=96 and the original 3440x1440 / work area 3440x1392 are verified. The four successful behavior processes were not rerun.
Full app-attributed GPU execution time, current-candidate GPU A/B and
existing non-client/full-frame hardware identity remain E2E_PENDING;
the EXECUTION-R2 alternative-source findings and exact dependencies remain.
Production, executable fixture, M8 CPU evidence and ledger are unchanged.
PERF-010 remains IN_PROGRESS; StageAccepted=false, no new stage ACCEPT
or performance REJECT. No WPR recording or new ETL was created.
Earlier next-action/environment paragraphs below are historical where superseded.

Updated: 2026-09-12. This is the live checkpoint. Read it with
[`PERFORMANCE_TODO.md`](PERFORMANCE_TODO.md), then open the compact
[`IMPLEMENTATION_RESULTS.md`](docs/performance/IMPLEMENTATION_RESULTS.md) and
[`scripts/performance/README.md`](scripts/performance/README.md). Consult the
[history index](docs/performance/HISTORY.md) only for a selected ID or artifact.

## Current state

- Root: `E:/developing/fancywm`.
- HEAD: `4fb943be3ee3768bb6de42b16b0a22bdbafa2e0b`.
- Audit baseline: `FWM-PERF-20260905-4fb943b-tree48c574b-u3`.
- Status: **25 IMPLEMENTED / 12 IN_PROGRESS / 0 whole-ID BLOCKED**.
- The working tree is intentionally heavily modified and contains inherited
  untracked files. Preserve it; do not reset, checkout or clean.
- `winman-windows` gitlink/HEAD remains
  `adb9f55b84b567db9f6e0ee8df4c33d11a12d91f`.

## Latest accepted stage

**PERF-022 — provider-owned display snapshot, C1/C2.** The production stage is
ACCEPT and closes the remaining locally testable work for this ID.

- `Win32DisplayManager.Displays` now sorts only when the owned display set or
  caller culture changes. It still returns a fresh concrete `List<Win32Display>`
  per read. Display mutation, snapshot publication and primary-display update
  share the existing display-list lock; events remain outside it.
- The final common fixture freezes list type/isolation, stable equal-key and
  current-culture ordering, concurrent publication and 1/10/50-display
  allocations. The old binary is red 6/3; candidate Debug/Release is 9/9.
- Five alternating A/B pairs in ten sequential Release x64 processes produced
  210 rows. Allocations fall 184/648/1,768 -> 64/136/456 B per getter at
  1/10/50 displays; all 15 allocation and timing comparisons improve and all 75
  semantic comparisons match. Timing is managed headless evidence only.
- The earlier operation-boundary M1 remains ACCEPT with production unchanged:
  `CanResize`/`Resize` property callbacks can mutate the position collection in
  the same call, so broader operation snapshots are rejected without an epoch.
- Targeted is 9/9 D/R; affected is 226/226 Debug and 244/244 Release. C2 passes
  full Debug 2033/160/31 and Release 2089/160/31, both restores, GUI, RID-aware
  x64/MSIX, dependency replay, package-content checks and fresh reverification.
- C2 summary:
  `artifacts/performance/FWM-DISPLAY-PROVIDER-SNAPSHOT-20260909-C2/validation/validation-summary.json`,
  SHA256 `62F89806816B7B97BF2D15AF8EA6CE3CB2A0659C7D106F8A13A505B624F8A942`.
- Ledger: 41,325 rows / 41,782,945 bytes, SHA256
  `84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
  This entire file is the mandatory exact historical prefix for the next append.

## Latest completed measurement stage

**PERF-032 — `KeybindingsPage` no-HWND materialization and tooltip candidate.**
The fixture materializes the real nested `ItemsControl` hierarchy with 1, 10 and
50 bindings in each of eight groups. Replacing each description tooltip
`TextBlock` with a bound string removes 8/80/400 eager tooltip UI elements, but
five alternating A/B pairs in ten sequential Release x64 processes show only
6/15 layout-allocation wins and 6/15 total-allocation wins; timing is mixed at
9/6. The candidate is REJECT and the production XAML is restored byte-exact.

The restored fixture passes targeted 2/2 and affected 65/65 in x64 Debug and
Release. No ledger append or C2 applies. The post-rejection
[`validation-summary.json`](artifacts/performance/FWM-KEYBINDINGS-PAGE-TOOLTIP-20260909-R1/validation/validation-summary.json)
has SHA256 `9D10CE5AA6105B1A3F6FF9914231A2B4FE2AF235A2A5DC75D0EB273A04069B0E`.

Totals are **25 IMPLEMENTED / 12 IN_PROGRESS / 0 whole-ID BLOCKED**. Native
display COM/process CPU remains E2E_PENDING; the managed provider and operation
snapshot questions now have accepted evidence.

## Native measurement dependency

An earlier 2026-09-10 continuation had an elevated interactive token with
`SeSystemProfilePrivilege`. Its preflight
`FWM-ANIMATION-NATIVE-BASELINE-20260910-N2` is AVAILABLE: CPU start/stop exits
zero and retains `preflight/cpu-smoke.etl` (224,395,264 bytes, SHA256
`23678890493EDFDE6F8E057CBB731E847AFC8A05FF02A62181A75A1B048BB716`).
WPR is inactive after the probe. The native 1/10/50-HWND baseline is now
captured at `FWM-ANIMATION-NATIVE-BASELINE-20260910-N3`, with the precision
reverification at `FWM-ANIMATION-NATIVE-BASELINE-20260910-V1`.

Five sequential Release x64 processes produce 120 measured transitions and
600 verified ETW boundary markers, with zero lost events/buffers. The real
production assemblies drive owned native windows. Median task completion at
1/10/50 targets is 233.5/236.8/237.8 ms; median final native-message timing is
233.6/241.0/663.6 ms. The 50-target workload exposes queued native updates on
one HWND owner thread while frame intervals remain about 6.94 ms.

Native fixture Debug/Release builds and smoke runs pass; the affected animation
filter passes 78/78 in both configurations, and the existing performance
harness still builds. Raw ETL, CPU/thread exports, profiles, call sidecars,
source/binary manifests and receipts are retained. See the
[native baseline report](docs/performance/ANIMATION_NATIVE_BASELINE.md).
Production batching and physical-presentation claims remain gated. Totals and
the historical optimization ledger are unchanged.

PERF-010 preflight `FWM-ANIMATION-NATIVE-BASELINE-20260908-N1` found WPR/WPA
and local profiles. `wpr.exe -start CPU -filemode` failed with `0xC5585011`
because that session could not enable system-performance profiling policy.
That historical run had medium integrity, no elevation and no
`SeSystemProfilePrivilege`; it produced no ETL. N1 remains immutable. The N2
result above supersedes this environment blocker without changing N1 evidence.

## Remaining evidence gaps

The owned native fixture covers animation cadence, position-call fan-out,
geometry latency and controlled owner topology with hardware witnesses. V14
covers full-app behavior with owned targets at 96 DPI. Full-app presentation,
representative cross-application topology, multi-DPI behavior, discovery/COM,
hook recovery, focus/UIA timeout and whole-process CPU/GPU remain E2E_PENDING
or NOT_MEASURED.
PERF-030 still needs production scheduling and native handle-type attribution,
UIA timeout, late focus and CPU/GPU evidence.
PERF-002 retains the mandatory 250 ms reconciliation because provider contracts
do not supply complete invalidation.
PERF-008 and PERF-009 broader reuse requires versioned equality/callback and
complete mutation-epoch contracts. PERF-028 and PERF-032 deferred visual
creation require first-presentation, pixel and interactive focus evidence.

## PERF-010 current checkpoint — full-app behavior PASS

Owner topology / presentation passed S1: 15 processes, 360 transitions and
7,320 hardware witnesses; receipt SHA256
`371024EFE4324821E25CE1D848F6E834E7F9D57C0FC9CD80E177B800C841C34C`.
Earlier occluded/profile-failure captures remain historical evidence.

V12 exposed missing generic layout invalidation on restoration without focus.
The correction passes the common regression (old 4/8, corrected 8/8), full
Debug 2043/2043 and Release 2099/2099, Layouts 160/160 and ThemeEngine 31/31
in each configuration, and both delivery builds. Its C2 integrity receipt is
`FWM-RESTORE-NO-FOCUS-20260910-C2/continuation-review.json`, SHA256
`5DDA06E110AC7325DF3ED443B5509597C9E2D49177DF29CE608E482F6E32F5DF`.
V13 retains the failed input visibility guard caused by MaxPayne; no input was
sent in that failed attempt.

Fresh **FWM-ANIMATION-FULLAPP-20260910-V14** reuses the exact B13 Debug/Release
binaries and passes both sequential processes plus the independent verifier.
Each configuration verifies 12 transitions at 1/10/50 targets, owned mouse and
direct-hotkey focus movement, 12 concurrent settings requests, native
minimize/close recovery, restoration of 10 windows and restoration of 50 windows
when shutting down during pending native work. Actual DPI is 96. Application
termination, target-process exit and owned-window cleanup pass.

Behavior receipt:
`artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-V14/verification/behavior.json`,
SHA256 `FE40437B7F95CF86F0794DA81DB0D54660204BD87611BDA92C008C9473A9AB0D`.
No executable source changed in this continuation. The sole production
difference from topology remains the restore correction. See
[the full-app report](docs/performance/ANIMATION_FULLAPP_VALIDATION.md).
Totals and the exact historical optimization ledger are unchanged. Final
continuation checks are retained in V14's `verification/continuation-review.json`.

## Latest measurement continuation — environment prerequisite

The prepared `Capture-AnimationFullApp.py` gates frozen B13 measurement mode on
S1/V14 and captures the existing profiles plus full-app markers. Native desktop
checks now reject incompatible monitor/work-area/DPI or inaccessible input
desktops before launch and around each process. The measurement verifier checks
provenance, geometry/ownership/messages, process CPU and available ETW markers.
Its fail-closed checks pass 23 replay/injected-error cases; no complete new
measurement has passed. Final tooling and review are frozen in
`FWM-ANIMATION-FULLAPP-20260910-D1`.

M1 confirms that this current medium-integrity session lacks tracing privilege:
WPR returns `0xC5585011`, creates no ETL and remains inactive. M2's explicit
CPU-only fallback times out at 50 windows on the transient `NoMonitor` display,
with 192 DPI; application/target cleanup passes. M3's native preflight rejects
RDP/`WinDisc`, 2880x1704 work area, 192 DPI and input-desktop access error 5,
before starting either WPR or the app. WMI's physical-GPU resolution does not
describe this remote desktop. All failed attempts remain immutable.
See [the measurement report](docs/performance/ANIMATION_FULLAPP_MEASUREMENT.md).

## Active continuation — 2026-09-11

Resumed PERF-010 at the recorded M4 action on unchanged `main` /
`4fb943be3ee3768bb6de42b16b0a22bdbafa2e0b`. The current token is elevated;
native preflight observes the local 3440x1440 / 3440x1392 work area / 96-DPI
desktop, with the executing and active input desktops both `Default`.
M4 retains a fresh source snapshot and passes V14 reverification. The user
requested an immediate stop to use the PC for gaming. The capture process was
stopped during preflight ETL inventory hashing, before WPR or either application
process started. WPR is inactive and no owned test process remains. The stop
receipt is `FWM-ANIMATION-FULLAPP-20260910-M4/user-stop.json`. No new measurement
was accepted from M4. The user has now requested continuation after gaming;
fresh `FWM-ANIMATION-FULLAPP-20260911-M5` is running after matching elevated
local-desktop controls and inactive WPR were confirmed again. All five native
processes now pass: 120 measured transitions, application termination, target
process exit and owned-window destruction. WPR stop and inactive-status checks
pass, and the 9,645,850,624-byte ETL has zero lost events/buffers. The independent
capture verifier REJECTS cross-process geometry: inherited Flex allocations
differ at 10/50 targets in seven process/count groups. Raw M5 is retained and
cannot be accepted as a controlled measurement. The exact diagnostic is
`FWM-ANIMATION-FULLAPP-20260911-D3/verification/m5-repeatability.json`.

The fixture now settles native geometry, calls the existing
`SplitPanelNode.DistributeChildrenEvenly` under the production backend lock,
and settles the resulting real layout before warmups. No production source or
tree/window identity is changed. B14 builds in Debug/Release, but V15 stops
before input because an `mstsc.exe` window covers the owned hit-test point.
Its native recovery and shutdown pass; no input was sent. The user has been
asked to uncover the desktop for the interactive test.

The independent no-input P1 gate then finds two process/count permutations:
equal panel sizes alone do not fix asynchronous discovery order. B15 adds
target-index ordering through existing `TilingNode.Swap`, preserving every node
and registration before distributing Flex. Both host/target configurations
build. P2 passes all five sequential Release processes with no input or ETW:
60 transitions including warmups, 30 measured-mode transitions, exact target
index/rectangle equality and complete native cleanup. The independent audit
also passes, with zero differing groups. B13/V14, B14/V15, M5 and P1 remain
immutable. B15 required new behavior validation, now completed as V16 below.

`Analyze-AnimationFullApp.py` now prepares offline attribution of CSwitch
occupancy to the app, Dispatcher and separate target process, plus scoped
PresentMon GPU counters. Eight scheduling/phase/failure checks pass in
`FWM-ANIMATION-FULLAPP-20260911-D2/development/scheduling-tests.json`.
Its real-capture success path is now exercised by M6 below. Production, B13 binaries,
submodule revisions and the historical optimization ledger are unchanged.

`Analyze-AnimationFullAppPresentation.py` and
`Verify-AnimationFullAppPresentation.py` adapt the existing DWM/Dxg method to
the separate target process and native/extended/client geometry. Their 26
geometry/identity/coverage checks pass against V14 observations with injected
faults; generated DWM payloads in these checks are tests, not measurement
evidence. The real-trace continuation below exposes a binding/geometry boundary. The existing measurement checks
remain 23/23 and scheduling checks 8/8.

## Latest verified continuation — V16 / M6, 2026-09-12

Continuation resumed on 2026-09-11 at unchanged `main` / `4fb943b`. Fresh
native preflight confirms elevation, local 3440x1440 / 3440x1392 / 96 DPI,
accessible `Default` input desktop and inactive WPR. No `mstsc` or owned
full-app process was present. V16 uses unchanged frozen B15;
Debug and Release both pass. Independent behavior verification and exact
target-index/native-rectangle repeatability pass for all 24 transitions. Fresh
M6 completes with B15 and V16: five processes, 120 measured transitions,
11,118,051,328 ETL bytes and zero lost events/buffers. Independent capture
verification passes all 750 markers, native geometry/cleanup and exact
cross-process repeatability (including 30 warmups). WPR is inactive.

CPU/thread attribution completes: 7,329,150 switches, ten process identities
and 460 threads, with no pairing/CPU mismatch. An independent signed-timestamp
sum matches xperf's lifetime totals for every process/thread within the export
precision bound. At 1/10/50 windows, median process CPU is 62.5/468.75/2328.125 ms;
Dispatcher occupancy is 32.789/293.105/1201.903 ms. These scopes include settings,
layout, overlays and harness observations. Shared DWM GPU counters are available;
whole-app and target GPU attribution remain NOT_MEASURED.

The raw presentation path exposed two assumptions in the prepared tools:
resizing rebinds the same HWND visual, and its draw uses client coordinates.
The corrected binding replay requires a continuous owner/visual chain anchored
after the owned alive observation and before warmups. It rejects unknown,
changed-HWND, stale and late-only bindings. Thirteen real-trace/injected cases
and all 26 existing geometry cases pass. Reanalysis retains 46,911 bound draws
and client translation/clip witnesses for all 2,440 targets. These diagnostics
do not identify the separately drawn DWM non-client frame. The independent
verifier records E2E_PENDING and exits nonzero; full-frame/hardware criteria
remain unchanged. Original rejected derivation and R1 pre-edit source remain.

Capture receipt: `M6/verification/capture/capture-verification.json`, SHA256
`0A0B5EF44875941566DE171FEB4C4452AC242EFCBEDE5C100F27C7E2B2B0F8F9`.
CPU receipt: `M6/analysis/cpu/derivation.json`, SHA256
`A11F841D748708DF6D6BD84E7FB2E760874DA973BEEF84EDC8AF767335B06A6B`.
Here M6 means `artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6`.
See [the measurement report](docs/performance/ANIMATION_FULLAPP_MEASUREMENT.md)
for commands, limits and the remaining presentation evidence.

## Latest diagnostic continuation — D6, 2026-09-12

The visual audit reads 782 installed schemas and confirms that the retained
parent/resource references do not identify the separate border visuals.
Dwm event 335 requires the missing `FrameVisualizationExtra` keyword; its
possible traversal meaning needs a diagnostic capture. The prepared profile
passes WPR parsing. P1 rejects before tracing/app launch because the current
desktop is RDP / 192 DPI / 2880x1704 work area. The capture success path is unrun.

Offline M6 analysis now covers all 120 measured app-process sample intervals.
JIT takes 29.353% / 12.039% / 24.556% of summed sample weights at 1/10/50 windows;
the separate two-interval export matches parsed rows and lifetime bounds pass.
Forty 50-window Dispatcher intervals contain 47,567 exclusive stack hits, but
51.134% remain unresolved. Module and function totals match. These diagnostics
do not select a production batching candidate. Full-frame hardware proof stays
E2E_PENDING. See [D6 evidence and limits](docs/performance/ANIMATION_FULLAPP_MEASUREMENT.md#d6-visual-identity-and-scoped-cpu-diagnostics--2026-09-12).

## Historical next action (superseded by current scope above)

Follow [GUI_RESOURCE_ATTRIBUTION.md](docs/performance/GUI_RESOURCE_ATTRIBUTION.md).
Native USER attribution and controlled compact-target USER/GDI no-growth now
pass with verified F11 receipts. Preserve the fallback viewport, stable-window
and startup-adapter boundaries; F5 and failed layout runs remain unchanged.
The next full-startup criterion needs an isolated shell/user/session/VM that
permits the actual WindowArranging write, genuine membership and wallpaper
success. Current restrictions forbid changing that global setting. Native
heap/GPU/physical presentation remain outside the weak/GUI-counter proof.
PERF-010/021 still require the contracts in EXECUTION-R2/restoration; CPU
admission does not provide GPU execution or hardware-presentation identity.
Do not repeat completed toast/DPI experiments. Keep IN_PROGRESS and the ledger.
The following D7-D9 notes retain their history.

D7 resolves the Dispatcher attribution using existing CLR metadata and cached
native symbols. All 47,929 scoped raw CPU samples match the decoded export;
45,056 complete frame chains independently match raw StackWalk fragments.
Construction of `TilingWindow` appears in 10,855 verified samples across all
five processes. The source path ties padding updates to full overlay invalidation
and subsequent WPF materialization. The symbol-enabled xperf probe has differing
totals and remains rejected. See [D7 evidence and limits](docs/performance/ANIMATION_FULLAPP_STACK_ATTRIBUTION.md).

The subsequent padding-only overlay reuse candidate is now implemented in three
production files. Shared B1/C1 tests go from 3/4 to 4/4; five alternating M2 pairs
verify 360 rows and 15/15 allocation and elapsed wins. At 50 windows, allocations
fall from 70.493 to 1.300 MB/update and materialized window/tab/SVG recreation falls
to zero. This is no-HWND managed WPF evidence. Reentrant managed ownership,
failure recovery, height/scaling fallback and disposal pass. The discovered WPF
nested-notification visual defect is corrected by the subsequent stage below.

C2 passes full Debug 2047/160/31 and Release 2103/160/31, GUI, RID-aware x64/MSIX,
dependency replay, package-content checks and independent M2 reverification.
The package is archived without installation, launch or publication.

The nested-notification B1/C1 stage goes 4/7 -> 7/7 on shared tests and passes
87/87 affected Release tests. Its 24 new cases verify deferred cleanup after
all collection listeners, both WPF surfaces, disposal, failure preservation and
retry. The baseline produces up to 99 controls for 50 windows; the corrected
candidate retains exactly 50. Five M1 pairs preserve zero padding recreation
and unchanged median allocation at 1/50 windows. Timing is mixed (5/15 wins),
with 50-window median 9.43 -> 9.79 ms; no additional speedup is claimed.
See [the correction and evidence](docs/performance/OVERLAY_NESTED_NOTIFICATION.md).

Nested C2 passes full Debug 2050/160/31 and Release 2106/160/31, both GUI/x64/MSIX
builds, dependency replay, embedded package checks and independent M1 replay.
It retains the exact measured candidate source and archives the build-only MSIX.

P4 restores the user's disconnected session to its unoccupied local console;
fresh 3440x1392 / 96-DPI controls pass. B16/V17 passes native behavior. M7 exposes
a fixture race after HWND destruction and is rejected. The shared ownership
guard correction passes ten native cases, then fresh baseline B18/V18 and
candidate B17/V19 pass all builds and both native configurations. Shipping
candidate source remains identical to nested C1/C2.

M8 independently verifies five alternating pairs / ten processes / 240 measured
transitions, exact geometry and cleanup. CPU wins 14/15 comparisons with one
tie; median process CPU at 50 windows is 1,500 -> 210.9375 ms. Eleven closed-HWND
drain waits are observed outside all transition intervals. D8's extra-event
identity trace has zero loss and 45 markers; two decoders count event 333
22,601 times and event 335 zero times. The missing nonclient visual edge remains
unproven. [Native CPU results and current limits](docs/performance/OVERLAY_FULLAPP_CPU.md).
Attributed GPU, full-frame/hardware presentation and whole-ID acceptance remain
pending; the ledger is unchanged. Totals stay 25 IMPLEMENTED / 12 IN_PROGRESS.

D9 verifies M6 GPU ownership offline: 89,585 app-owned events through 5 device /
20 context / 10 hardware-queue lifetimes. Two payload decoders agree; 77,967 queue
events and 518,683 fields match independent xperf output. It excludes 9,484
misleading header-PID matches within measured intervals. DMA/fence pairs are
verified telemetry, with no GPU-busy-time or candidate speedup claim.
[GPU ownership evidence and remaining execution-time gate](docs/performance/ANIMATION_FULLAPP_GPU.md).
