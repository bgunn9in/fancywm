# Performance optimization plan

## Current scope — 2026-09-14 preview conversion follow-up complete

The user authorized the proposed follow-up after `43dcfcc`. One small PERF-010/021
change removes the enumerator from conversion of an accepted plan's indexed
PreviewWindows list to a fresh set. Inter-call set reuse was not introduced:
current IWindow hashing/equality still determines each newly published set.

Same-process baseline/candidate counters across 18 cases (three layouts, both
master sides, reorder/promotion/side movement) show **208 → 176 B per conversion**
in Release; 54,000 conversions allocate **11,232,000 → 9,504,000 B**. Debug also
improves in every case, with **9,504,048 B** in the candidate aggregate.
This modest local allocation saving does not establish faster application/UI
behavior. [Method and limits](docs/performance/PREVIEW_WINDOW_SET.md).

Final affected regression: **758 Debug + 800 Release**, all passed, including
both features and overlay/preview recovery. No layout rule, MinSize/preflight/
rollback, focus or 250 ms reconciliation change; no interactive UI launch.
This follow-up is complete; earlier feature closure and tab-order optimization
remain valid. Broader PERF-010/021 research remains deferred as recorded below.

## Current scope — 2026-09-14 bounded overlay optimization complete

The user manually confirmed both new features work normally. Their stage is
closed in `NEW_FEATURE_TODO.md` and `docs/new-features-verification.md`, with
implementation/tests/docs committed locally as `a40cf6d`. No repeat UI pass is
required without a concrete reason; unspecified monitors, DPI and applications
are not covered by that confirmation.

One local PERF-010/021 improvement is complete: update panel child order with
Move/Insert/Remove instead of clearing and repopulating the tab collection.
The existing overlay reuse is preserved. In the same 72-update WPF workload
across Horizontal, Vertical and mixed layouts and both master sides, new tabs
decreased **228 → 48**, Reset notifications **96 → 0**, all collection
notifications **324 → 183**. This is a construction/notification count result,
not a measured whole-application speedup. See [method and results](docs/performance/OVERLAY_CHILD_ORDER.md).

Final affected regression: Debug **756**, Release **798** passed cases; no
failures/skips. Both features, MinSize, recovery, commands, mouse/preview and
overlay lifetimes are included. No interactive UI was launched. No new cache,
layout/command rule or 250 ms reconciliation change was introduced.
The authorized one-optimization pass is finished; no second candidate was
investigated. Broad PERF-010/021 acceptance and deferred research retain their
historical status. This section supersedes older next actions below.

## Current scope — 2026-09-13 practical closeout

The user confirmed **«Работает»** and reported no new problem. The practical
stage is **complete**. Next action: use the working version and fix concrete
newly reported bugs. No further UI run, permission wait, audit or optimization
is required to close this stage.

The checked portable is `FancyWM-Portable-win-x64-20260913-214722.zip`
(509 files, self-contained Release x64); see [launch instructions and limits](docs/portable.md).
The successful Debug/Release tests, GUI/x64/MSIX builds and scoped automated UI
results below retain their recorded results. They were not repeated for this
documentation/Git closeout, and the application was not relaunched.

The general confirmation does not separately verify all DPI/display scenarios,
the exact Horizontal/right-master MoveRight case, Firefox placement, physical
focus/drag or native desktop transfer/overflow. These remain optional checks,
not blockers. Cross-monitor and mixed-DPI checks need a second display.

This section supersedes all historical next actions below and in linked research
reports, following the user's reduced scope. Preserve implemented changes and
verified results; unverified criteria retain their status and are not accepted.

| Work | Current decision |
|---|---|
| PERF-010/021 | Keep overlay reuse, nested-notification correction and verified CPU results. DEFER attributed GPU execution/A/B and hardware presentation research; retain their existing evidence requirements. |
| PERF-016 | Check concrete cleanup failures during ordinary scenarios. Deeper heap/GPU attribution is DEFERRED unless reproducible resource accumulation or another observable defect justifies it. Individual memory samples do not establish a leak. Existing native lifetime, teardown and crash evidence remains valid within its recorded scope. |
| PERF-002/008/009 | Keep safe optimizations and the 250 ms reconciliation. DEFER dirty-only discovery and broader reuse requiring new event, equality or mutation contracts. |
| PERF-032/035 | DEFER tooltip/cache/virtualization variants without demonstrated practical benefit. Preserve rejected candidates and prior measurements. |
| Remaining UI | Optional scenario-specific checks only; general user confirmation does not turn unverified DPI, display, MoveRight, Firefox or physical gesture cases into passes. No scheduled UI run or permission wait remains. |

Closeout verified: `FWM-PRACTICAL-CLOSEOUT-20260913-R1/R2`.
[Final regression/build receipts](artifacts/performance/FWM-PRACTICAL-CLOSEOUT-20260913-R2/final-validation/result.json):
six fresh TRX files, Debug 2,069 + 160 + 31 and Release 2,125 + 160 + 31
passed leaves, no failures/skips; restore, GUI and x64/MSIX builds pass.
Both archived packages pass ZIP integrity, x64 apphost/manifest and embedded
production-DLL checks; neither was installed or launched. Existing dependency
and compiler warnings remain. R1's archive-path error for Layouts.Tests was
corrected in R2; successful commands were reused with their receipts, not rerun.

[Short automated UI pass](artifacts/performance/FWM-PRACTICAL-CLOSEOUT-20260913-R1/ui/verification.json):
Release App/MainWindow/DI, nine transitions at 1/10/50 owned windows, settings
updates, minimize/restore/close and shutdown during native movement pass.
The private desktop uses intercepted WindowArranging writes and controlled
virtual-desktop membership; this is not unmodified Startup.Main or manual UI.
Each full regression also passes 12 native owner and five native toast tests;
focus (88 cases) and drag (three cases) have managed regression coverage only.
Physical focus/drag gestures and visual inspection remain unperformed.

No new production/test change or reproducible production defect was found.
All 2,155 baseline production paths retain only the five inherited corrections;
PERF-002 stays at 250 ms. The 41,782,945-byte ledger is unchanged; no new speedup
or whole-ID acceptance is claimed. Next action: use the working version and
fix concrete new bugs; deferred research and evidence gaps do not create
mandatory work.

### Historical UI preparation before user confirmation

`FWM-USER-UI-20260913-R2` prepared a current-source portable (508 files,
publish and `--help`/`--version` passed). The previous GUI win-x64 output was
stale and was not launched. At that checkpoint ordinary startup/UI awaited
explicit permission for MainWindow's temporary global WindowArranging write.
Settings/logs and window placement were saved read-only; originals were
unchanged. The prepared [short scenario](artifacts/performance/FWM-USER-UI-20260913-R2/UI-SCENARIO.md)
is historical evidence, not the current next action.

## Historical checkpoints and research criteria

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
[native owners and teardown](docs/performance/NATIVE_OWNER_LIFETIME.md).
Native settings/overlay, auxiliary owners, actual hooks/workspace/theme and
HelpPage/WebView2 have verified scoped lifetime receipts. Five constructor reds
justify the two-file rollback correction; targeted/affected 166/166 D/R and full
C2 delivery pass. Owned abrupt exit proves OS cleanup only. Shown settings/browser
retention is explicitly after WPF view-cache maintenance. Full unchanged startup
retention and app crash handler require an isolated session/VM because MainWindow
writes global WindowArranging. PERF-016 stays IN_PROGRESS, ledger unchanged,
PerformanceClaim=false, StageAccepted=false. Earlier next actions below are history.

Earlier completed continuation (2026-09-12): [PERF-016 native toast lifetime](docs/performance/TOAST_NATIVE_LIFETIME.md).
Actual HWND closure/construction failure and WPF Application shutdown pass in
five isolated tests per configuration. Debug 5+162 and Release 167 tests pass;
100 post-warmup hidden-window lifetimes per configuration retain no weak windows
or additional USER/GDI objects. Production and ledger unchanged; whole PERF-016
stays IN_PROGRESS. Next runnable action: native SettingsWindow/OverlayHost lifetimes.

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

Compact live table for all 37 stable IDs. Current checkpoint: [PERFORMANCE_STATUS.md](PERFORMANCE_STATUS.md); audit constraints: [AUDIT.md](docs/performance/AUDIT.md); evidence summary: [IMPLEMENTATION_RESULTS.md](docs/performance/IMPLEMENTATION_RESULTS.md); harness: [README.md](scripts/performance/README.md); byte-exact archives: [HISTORY.md](docs/performance/HISTORY.md).

Audit baseline: `FWM-PERF-20260905-4fb943b-tree48c574b-u3`, commit `4fb943be3ee3768bb6de42b16b0a22bdbafa2e0b`, tree `48c574b1abd3adb858942bd8c6a0f4d9fb3e39b7`.

**25 IMPLEMENTED / 12 IN_PROGRESS / 0 whole-ID BLOCKED, REJECTED or NOT_STARTED.** Evidence applies only to the stated stage; native gaps are `E2E_PENDING`.

| ID | P | Implementation | Evidence | Local validation | Remaining / E2E_PENDING |
|---|---:|---|---|---|---|
| PERF-001 | 1 | IMPLEMENTED | MEASURED callback; scheduling CODE_CONFIRMED | Timer races, A/B, patch/build | Native wakeup/visibility/shutdown; WPR |
| PERF-002 | 1 | IN_PROGRESS | MEASURED getter and owned discovery/Refresh snapshots | Discovery gates retained; Refresh 5/9 targeted and 194/212 affected D/R; 480-row A/B ACCEPT; C2 full gate 1970/2026+160+31 D/R and dependency replay ACCEPT | Dirty-only blocked by missing provider events; retain 250 ms sweep; COM/latency/WPR |
| PERF-003 | 1 | IMPLEMENTED | CODE_CONFIRMED | Red 3 -> green 8/8 | Native multi-monitor discovery |
| PERF-004 | 1 | IMPLEMENTED | MEASURED completion counts and cached callback allocation | Callback C1/C2R3 ACCEPT: old identity red; targeted 3/3 D/R; affected 264/282 D/R; all 15 pairs save 64 B/pass; 90 semantics equal; timing mixed 13/15 | Native Dispatcher/cadence/presentation latency; process CPU |
| PERF-005 | 1 | IMPLEMENTED | MEASURED | Constraints/clone differential/A/B | Whole-process effect |
| PERF-006 | 1 | IMPLEMENTED | MEASURED fake-adapter path | Success/failure snapshots/full gates | Native allocation/timing |
| PERF-007 | 1 | IMPLEMENTED | MEASURED managed preview | Invalidation/mouse-up replan/A/B | Native gesture/DPI; process CPU |
| PERF-008 | 1 | IN_PROGRESS | MEASURED invariant and membership-scan stages | Mutation-boundary M1 ACCEPT / broader reuse REJECT: external `IWindow.Equals` changes across validations without tree/state/revision mutation; 1,500/1,500 controlled outcomes, zero MinSize/revision delta, 7,500 identities/registrations; 2/2 targeted and 122/146 affected D/R. `ContainsWindow` C1R2/C2R1 retained | Broader reuse requires an explicit immutable/versioned window equality contract plus full epochs; native CPU/latency |
| PERF-009 | 1 | IN_PROGRESS | MEASURED traversal and reuse-boundary stages | Clone-boundary M1 ACCEPT / general cross-call reuse REJECT: unversioned virtual `Clone` callbacks remain observable without tree mutation; 1,500 exact failures/callbacks, zero MinSize, 30,500 identities/registrations; 2/2 targeted and 100/102 affected D/R. Recursion/stack C2 retained | Exact-built-in reuse still requires a complete mutation epoch and fresh constraint/preflight reset; native cadence/DPI |
| PERF-010 | 1 | IN_PROGRESS | S1 retained; native B18/B17 Debug/Release at actual 96/120 DPI; M8 process CPU | Actual 96/120-DPI behavior and original display restoration PASS; 48 transitions and 2,024 verified overlay pairs. GPU method gaps preserved in EXECUTION-R2 | Complete attributed GPU method then B18/B17 five-pair A/B; existing non-client/full-frame hardware chain |
| PERF-011 | 1 | IMPLEMENTED | CODE_CONFIRMED | Idempotence/stale ticks/100 lifecycles | Native overlay z-order |
| PERF-012 | 1 | IMPLEMENTED | MEASURED ownership | Native icon subprocess/failure/double-dispose | Visual/DPI integration |
| PERF-013 | 1 | IMPLEMENTED | MEASURED operations | Races/retry/stale capture/100 cycles | Native wallpaper/display |
| PERF-014 | 1 | IMPLEMENTED | MEASURED operations | Concurrent fields/last save/retry/lifetime | Native startup/shutdown; process CPU |
| PERF-015 | 2 | IMPLEMENTED | MEASURED converter | Parser/selector/resource digests | Whole-app theme cost |
| PERF-016 | 2 | IN_PROGRESS | Scoped native owners/startup graph with adapters; CLR/native caller identity | Prior native candidates/C2/crashes retained; new F1 weak/teardown and calibrated code-generation/stack witnesses | Atomic heap boundary fails native small-allocation control; logical owners/pre-attach history, unmodified shell and broader GUI/GPU/presentation contracts remain missing |
| PERF-017 | 2 | IN_PROGRESS | MEASURED managed hook calls | Reinstall failure/signed result/startup/dispose | Native idle/latency/recovery before timer redesign |
| PERF-018 | 2 | IMPLEMENTED | MEASURED requests, managed allocations/memory and forced-GC pause | Equal-cycle C1R1: exact mechanical baseline, 5 A/B pairs/10 sequential x64 processes/600 rows; requests 960->45, full-GC passes 1,920->90, pause 15/15 wins, allocation 14/15; targeted 2/2 and affected 26/26 D/R | Real display/native heap/process CPU/GPU/DWM remains E2E_PENDING; pre-final heap is higher without redundant collections, so no retained-memory claim |
| PERF-019 | 2 | IMPLEMENTED | MEASURED diagnostic reads | 27 guards/enabled fields/errors/A/B | Logging volume/process I/O |
| PERF-020 | 2 | IMPLEMENTED | MEASURED allocations/callbacks | Deadline/version/clock/lifetime/A/B | Native wakeups; process CPU |
| PERF-021 | 2 | IN_PROGRESS | MEASURED managed padding reuse/nested cleanup and native process CPU | Nested C2 full D/R and delivery PASS. B18/V18 baseline and B17/V19 candidate pass native D/R with common corrected fixture. Actual 96/120-DPI common-fixture behavior and display restoration pass. M8 verifies five pairs / 240 transitions, 14 CPU wins + 1 tie; 50-window CPU 1500 -> 210.9375 ms | Attributed GPU and full-frame/hardware presentation; preserve M7 rejection and D8 event-335 absence |
| PERF-022 | 2 | IMPLEMENTED | MEASURED lookup/display/count/provider snapshot; operation boundary | Provider C1/C2: old 6/3, candidate 9/9 D/R, affected 226/244 D/R, 210-row A/B, allocations 184/648/1,768 -> 64/136/456 B/getter, full 2033/2089+160+31 D/R and dependency replay. Operation M1 rejects unsafe broader property snapshots | Native display COM/process CPU remains E2E_PENDING |
| PERF-023 | 1 | IMPLEMENTED | MEASURED counts; UI CODE_CONFIRMED | Bounded workers/cache/generation/dispose | Real package/application/DPI |
| PERF-024 | 1 | IMPLEMENTED | CODE_CONFIRMED; counts MEASURED | Latest-wins STA/retry/error/lifetime | Native watcher/ModernWpf/startup/shutdown |
| PERF-025 | 1 | IMPLEMENTED | CODE_CONFIRMED | Notifications/overlay/settings | Visual fade/DPI |
| PERF-026 | 2 | IN_PROGRESS | CODE_CONFIRMED ownership | Hidden-empty surface/100 cycles | First-toast pixels/latency; DWM/GPU |
| PERF-027 | 2 | IMPLEMENTED | CODE_CONFIRMED | Browser ownership/reentry/failure/late-close | Real WebView2 |
| PERF-028 | 2 | IN_PROGRESS | MEASURED SVG/materialization | Pixels/STA/ownership/SVG/ActionBar A/B | Broader lazy visuals; native render |
| PERF-029 | 2 | IMPLEMENTED | MEASURED allocations/operations | Ordered diff/stale/cancel/focus/A/B | Native visuals |
| PERF-030 | 1 | IN_PROGRESS | MEASURED rejected admission, caller/worker allocations, concurrent contention and managed retention; stop CODE_CONFIRMED | Retention M3 ACCEPT: production unchanged; five isolated processes/380 rows cover 640 completed + 640 overlapped workers and 10,240 admissions; every settled thread/handle delta 0, `m_worker` null, 0 live `Thread`; targeted 3/3 and affected 250/250 D/R. Concurrent C1R2/C2 and earlier stages retained | Production scheduling; native handle types, UIA timeout/late focus/CPU/GPU |
| PERF-031 | 2 | IMPLEMENTED | MEASURED; p99 trade-off | Matching/lifetime/A/B | Native hook latency/recovery |
| PERF-032 | 3 | IN_PROGRESS | MEASURED small-display saving and no-HWND page materialization | Tooltip M1 REJECT: 8/80/400 eager UI elements -> 0, but layout and total allocations each improve only 6/15 pairs; timing mixed 9/6; production restored. Restored targeted 2/2 and affected 65/65 x64 D/R | Interactive page profile, virtualization/focus/first presentation; native UI |
| PERF-033 | 3 | IMPLEMENTED | MEASURED equality calls | Order/custom comparer hash semantics | Ordinary integration |
| PERF-034 | 3 | IMPLEMENTED | CODE_CONFIRMED | Inactive/capture-lost/active offsets | Native DPI/capture |
| PERF-035 | 2 | IN_PROGRESS | Cost MEASURED; benefit NOT_MEASURED | Loopback/size/decode identity/owners | Native heap/render before cache decision |
| PERF-036 | 2 | IMPLEMENTED | CODE_CONFIRMED | Lock snapshot/generation/Dispatcher/cache | Native theme integration |
| PERF-037 | 1 | IMPLEMENTED | CODE_CONFIRMED | Subscription/bounded async/STA/100 cycles | Live startup/accent/exclusions; process CPU |

## Historical bounded stage (superseded by current scope)

PERF-022 provider snapshot C1/C2 is accepted. `Win32DisplayManager.Displays`
caches the stable current-culture ordering under the existing display lock while
returning a fresh concrete list per read. Five alternating pairs improve all 15
allocation comparisons at 1/10/50 displays; full delivery, package-content and
dependency replay pass. Operation-boundary M1 separately proves that broader
`CanResize`/`Resize` property snapshots are unsafe without a mutation epoch.

PERF-032 no-HWND materialization is complete. Its tooltip representation
candidate is rejected because removing eager objects did not produce a stable
allocation benefit; the original XAML is restored and the ledger is unchanged.
PERF-010 owner topology / presentation is ACCEPT at S1 (15 processes / 360
transitions / 7,320 hardware witnesses). V12's generic restore correction passes
old 4/8 -> corrected 8/8, full Debug 2043/2043 and Release 2099/2099 plus delivery
checks. V13's obstructed-input failure remains retained; fresh V14 passes both
complete native/interactive configurations and the independent verifier using
the unchanged B13 binaries. See [the full-app report](docs/performance/ANIMATION_FULLAPP_VALIDATION.md).

Full-app M5 now captures five processes / 120 measured transitions in a local
elevated 96-DPI session, with zero lost ETW events/buffers and successful cleanup.
The independent verifier rejects unequal initial Flex allocations across
processes. Fixture B15 fixes allocations and target ordering using existing
layout operations before warmups; both configurations build and P2 passes
five sequential processes / 60 transitions plus independent exact-geometry
comparison. Production and the ledger are unchanged. V15's input guard catches
an overlying `mstsc.exe` window before sending input. Fresh B15/V16 now passes
both configurations and repeatability; M6 passes the strict capture gate and
CPU/thread derivation with an independent 460-thread xperf cross-check. The
raw presentation path requires continuous surface rebindings and distinguishes
client-coordinate draws from the separately composed border. The correction
passes 13 real-trace/negative and 26 geometry cases; full-frame hardware proof
remains E2E_PENDING, with no acceptance relaxation. See
[measurement status](docs/performance/ANIMATION_FULLAPP_MEASUREMENT.md).
D6 now audits 782 provider schemas and prepares the extra-event identity
profile; its native P1 rejects RDP/192 DPI before tracing or app launch.
Offline analysis covers all 120 measured CPU sample intervals and forty
50-window Dispatcher stack intervals. D7 now matches all 47,929 scoped raw
samples and independently verifies 45,056 full frame chains. Repeated overlay
construction supports the padding-only reuse candidate now tested in B1/C1.
Shared tests go 3/4 -> 4/4 and five M2 pairs verify 360 counters, 15/15 allocation
and elapsed wins, and zero candidate window/tab/SVG recreation. The separate
nested-notification correction now verifies both WPF surfaces in 24 cases,
with shared 4/7 -> 7/7 and zero recreation retained across five M1 pairs.
Timing is mixed; [the correction report](docs/performance/OVERLAY_NESTED_NOTIFICATION.md)
retains the 50-window elapsed tradeoff. P4 now obtains matching local 96-DPI
admission. V17 passes; M7 exposes a closed-HWND fixture race and remains rejected.
The common guard correction passes ten native cases, then B18/V18 baseline and
B17/V19 candidate pass all native behavior. M8 verifies five alternating pairs,
240 measured transitions, equal geometry/cleanup and 14 CPU wins plus one tie.
At 50 windows, median process CPU is 1500 -> 210.9375 ms.
[Native results and receipts](docs/performance/OVERLAY_FULLAPP_CPU.md). See
[candidate evidence](docs/performance/OVERLAY_PADDING_REUSE.md) and
[D7 attribution](docs/performance/ANIMATION_FULLAPP_STACK_ATTRIBUTION.md).
D8's fresh candidate identity pilot enables the extra DWM keyword, but two
decoders find zero event-335 records, with zero loss. Seek independent evidence
for the missing parent-to-nonclient visual edge; do not infer it from render
event order. Full-frame proof, attributed app GPU and whole-ID acceptance remain pending.
D9 resolves M6 process ownership through live GPU objects, with 89,585 events,
77,967 independent xperf queue matches and explicit rejection of misleading
header PIDs. Next validate GPU execution-time semantics for legacy/HWS contexts
before fresh candidate GPU comparisons; verified queue/fence spans are not GPU
busy time. [D9 evidence](docs/performance/ANIMATION_FULLAPP_GPU.md).
V14 is behavior evidence at
96 DPI, with no measurement capture, multi-DPI or speedup claim. Preserve
PERF-002's 250 ms reconciliation. Counts remain 25 IMPLEMENTED / 12 IN_PROGRESS /
0 whole-ID BLOCKED, and the optimization ledger is unchanged.
