# Performance implementation results — live summary

Latest verified continuation (2026-09-13): `FWM-PERF016-MEMCOUNTERS-20260913-R1` —
[native GPU and process memory accounting](DXGI_PROCESS_MEMORY.md).
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
[actual D3D mapped points and PSS boundaries](D3D_MAPPED_PSS.md).
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
[native D3D ownership observations](D3D_NATIVE_OWNERS.md).
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

Latest verified continuation (2026-09-13): `FWM-PERF016-CLR-20260913-R1` —
[CLR/native caller attribution and heap boundary evidence](CLR_NATIVE_HEAP_ATTRIBUTION.md).
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
[complete baseline heap replay](NATIVE_HEAP_COHORTS.md).
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
[native heap and graph HWND generations](NATIVE_HEAP_LIFETIME.md).
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

Latest verified continuation (2026-09-13): [GUI counter identity and native attribution](GUI_RESOURCE_ATTRIBUTION.md).
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
[full startup graph lifetime and exact remaining criteria](FULLGRAPH_LIFETIME.md).
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

Earlier native continuation: [PERF-016 owners and teardown](NATIVE_OWNER_LIFETIME.md),
checkpoint `FWM-PERF016-NATIVE-20260912-R1` (2026-09-12). Common native constructor
tests go from five reds with 1/1/2/2/2 surviving HWNDs to five greens. The retained
two-file OverlayHost rollback correction passes 166/166 targeted/affected leaves
in both configurations and full C2 Debug 2067/160/31, Release 2123/160/31, both
GUI/RID/x64/MSIX gates, dependency replay and prior measurement reverification.
Native auxiliary/hook partial-App teardown contributes 1818 observations,
workspace/theme 690, and real HelpPage/browser 236, each strictly verified with
negative controls. Browser's last shown settings window has observed WPF cache
retention before ordinary binding maintenance; afterward all 600 weak references
are dead on the live dispatcher. Four owned crashes verify OS reclamation only.
Full unchanged startup retention and application crash-handler evidence remain
pending an isolated session/VM because MainWindow changes global WindowArranging.
Production delta is correctness-only; ledger unchanged, StageAccepted=false,
PerformanceClaim=false, PERF-016 IN_PROGRESS. Earlier scoped results remain valid.

Updated: 2026-09-12. This file contains only current decision evidence. The full
8,374-line history is preserved byte-for-byte at
`.codex/session-backups/20260908-before-performance-live-doc-compaction/docs/performance/IMPLEMENTATION_RESULTS.md`
(548,082 bytes, SHA256
`ADAC0C7BD867866D5F2B18117CF5541EBC7FB3D9CD6E13FC545C861886C1C5E6`).
Use [`PERFORMANCE_TODO.md`](../../PERFORMANCE_TODO.md) as the sole live per-ID
status table. Open the archive only for the selected ID, method or artifact.

## Current state

- HEAD `4fb943be3ee3768bb6de42b16b0a22bdbafa2e0b`; historical audit snapshot
  `FWM-PERF-20260905-4fb943b-tree48c574b-u3`.
- **25 IMPLEMENTED / 12 IN_PROGRESS / 0 whole-ID BLOCKED**.
- Latest accepted production stage: PERF-022 provider-owned sorted display
  snapshot C1/C2. Latest completed measurement is the rejected PERF-032 tooltip
  representation stage.
- PERF-010 native owner topology / presentation is ACCEPT at S1. B15/V16
  full-app behavior and repeatability pass. M6 ETW capture, process CPU and
  thread attribution are verified; all 460 thread totals match xperf. Full-frame
  hardware display and app-attributed GPU remain pending.
- Functional `TODO.md` and `IMPLEMENTATION_STATUS.md` remain byte-identical to
  the session baseline. Inherited dirty and untracked work remains preserved.

## PERF-032 keybindings page materialization M1

`FWM-KEYBINDINGS-PAGE-TOOLTIP-20260909-R0` and `C1` freeze the actual compiled
`KeybindingsPage.xaml`, real `SettingsViewModel`/`KeyPressBox` controls and an
owned no-HWND WPF application. The fixture uses the page's eight real groups
with 1, 10 and 50 bindings per group, giving 8, 80 and 400 binding rows.

Changing each description tooltip from a nested bound `TextBlock` to the bound
string removes exactly 8/80/400 eagerly allocated tooltip UI elements. Five
alternating A/B pairs in ten sequential Release x64 processes produce 480 rows,
but layout and total allocations each improve in only 6/15 comparisons and lose
in 9/15; combined managed timing is mixed at 9 wins and 6 losses. The candidate
is REJECT. No rows were appended and no production C2 applies.

`FWM-KEYBINDINGS-PAGE-TOOLTIP-20260909-R1` confirms the production XAML was
restored byte-exact at SHA256
`238A43BD8402EE009534B585D05EA79D76A03FDA8939450A7C97F859B24D3CDF`.
Its normalized live fixture verifies the baseline tooltip type and binding
ownership and passes targeted 2/2 plus affected 65/65 in x64 Debug and Release.
Interactive virtualization remains gated on scroll-owner, focus, first-frame
and pixel evidence.

## PERF-008 mutation boundary M1

`FWM-INVARIANT-MUTATION-BOUNDARY-20260909-M1` freezes a safety fixture without
changing production. A mock `IWindow.Equals` changes after the workspace input
invariant and before `MasterSatelliteLayoutEngine.ExecuteMutation` validates the
current state. Tree structure, runtime state and `Revision` remain unchanged, so
no tree/state mutation epoch can distinguish the two validation results.

Five sequential isolated Release x64 processes cover 1,500 attempts in 105
rows. Every second boundary raises the exact controlled exception before
detached preflight: there are zero `MinSize` reads and zero revision changes,
while all 7,500 expected node identities and registrations remain intact.
Targeted tests pass 2/2 D/R; affected invariant/workspace tests pass 122/122
Debug and 146/146 Release. This is count-only safety evidence, with no A/B speed
claim and no production C2.

The broader invariant-result reuse hypothesis is REJECT. It needs an explicit
immutable or versioned `IWindow` identity/equality contract as well as complete
tree and runtime-state epochs. The accepted reuse of an engine result at its
immediate return boundary is unchanged.

## PERF-030 managed worker retention M3

`FWM-FOCUS-WORKER-RETENTION-20260909-M3` is an observation-only snapshot;
`FancyWM/Utilities/FocusHelper.cs` is byte-identical to accepted concurrent C2.
The fixture measures one active worker, 128 completed worker lifetimes and two
equal 64-worker overlap epochs with 16 admissions per worker after an equal-size
warmup. It keeps process thread counts, aggregate `Process.HandleCount`, weak
`Thread` references, the private worker slot and exact fake-native ownership
counters separate.

Five sequential isolated Release x64 processes produce 380 rows and cover 640
completed workers, 640 overlapped workers and 10,240 overlapped admissions.
Every active worker adds exactly one process thread; every settled process-thread
delta is 0. All completed, first-overlap and repeat-overlap handle deltas are 0;
`m_worker` is null and all weak worker references are dead. Targeted tests pass
3/3 D/R and affected FocusHelper/MainWindowLifetime/ModifierWindowMover tests
pass 250/250 leaves (284 raw results) D/R. No A/B speed claim or full production
C2 applies because production and build inputs did not change.

M1 and M2 remain immutable failed measurements. Each stopped when its first
overlap epoch observed +3 aggregate handles against the original 0..1 bound.
M3 added a second equal overlap epoch to distinguish bounded initialization from
worker-proportional retention; it did not loosen the repeated-epoch criterion.
Aggregate testhost counts do not attribute native handle types.

## Earlier PERF-030 concurrent admission and worker allocation C1R2/C2

The final fixture forces 32 callers to overlap at `RequestSequence.Enqueue`,
then pipelines 256 requests through one blocked worker. It records lock/enqueue
time, accepted and replaced requests, worker starts/exits, callbacks, ownership,
completion order, admitting-caller allocation, worker-thread allocation and
total managed allocation separately. Exact semantic cases cover direct success,
Alt retry and attachment failure.

`RunWorker` now passes the sequence and request token directly to `RunFallback`;
a static helper performs every existing current-request check. This removes the
per-request display class and delegate while preserving check and native-call
order, pending replacement, callback identity/order, ownership, expiry,
shutdown, failure/rollback behavior and lock ordering.

Final snapshots `FWM-FOCUS-CONCURRENT-ADMISSION-20260909-R0R2` and `-C1R2`
differ only in `FancyWM/Utilities/FocusHelper.cs`. Old source gives 5 PASS / 3
allocation FAIL; candidate passes 8/8 targeted D/R and 247/247 affected leaves
(281 raw results) D/R. Five alternating A/B pairs in ten sequential isolated
Release x64 processes produce 1,090 rows. All 15 worker-allocation pairs improve
24,624 -> 144 B per 255 repeated transitions, saving 24,480 B or 96 B per
repeated transition. All 400 semantic comparisons agree. The separately
measured first lazy delegate remains 368 B on the first admitting call and 304 B
on a warm call. Managed enqueue timing is mixed 9 wins / 11 losses / 0 ties and
does not establish a speedup.

`FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2` passes the full gate: Debug
2,012/160/31 and Release 2,068/160/31 leaves, restore, GUI, RID-aware x64/MSIX,
dependency replay, exact test composition, package-content verification and
fresh measurement reverification. The built-only MSIX is 73,044,690 bytes,
SHA256 `8B36E25CF3C9CCB7B46D73E19F9EDB96225480902274303DEB83229F4EA80F7F`;
its embedded `FancyWM.GUI/FancyWM.dll` is AMD64, 1,465,856 bytes, SHA256
`A8AF1F3D6BAC392E26C93DA488F2B38A873949290B800A0A31ECFF80849AC6A4`.
The package was not installed, launched or published.

The initial R0/C1 protocol attempt incorrectly compared separately built test
binaries across paths; C1R1 used the final binary protocol but had a stale
aggregate constant. Both immutable attempts remain preserved as harness
failures. Neither was treated as a performance result or appended to the ledger.

## Earlier PERF-030 worker-admission allocation C1/C2R1

`FocusHelper.RequestSequence.Enqueue` lazily caches its bound `ThreadStart`
with `m_workerStart ??= RunWorker` inside the existing locked `m_worker == null`
branch. The first delegate allocation is unchanged; subsequent completed worker
lifetimes reuse it. Admission, callback order, worker lifetime, failure/rollback,
expiry, shutdown and lock ordering remain unchanged; collectibility is tested.

`FWM-FOCUS-WORKER-ADMISSION-20260909-R0` and `-C1` share the final fixture and
differ only in `FancyWM/Utilities/FocusHelper.cs`; measured binaries differ only
in `FancyWM.dll`. Old source gives 5 semantic/lifetime PASS / 3 allocation FAIL.
Candidate targeted tests pass 8/8 in Debug and Release; affected tests pass
239 leaves / 271 raw results in each configuration.

`EXP-FOCUS-030-WORKER-ADMISSION-C1` records five alternating, sequential isolated
Release pairs: 32 discarded warmups, then 500 completed admissions per case
(direct success, Alt retry, attachment failure), with real workers joined by the
fixture and fake native adapters. All 15 allocation pairs improve **368 -> 304
B/call**, saving 64 B on the caller, and all 240 semantic comparisons agree
across 570 measurement rows. Timing is **mixed: 10 wins / 5 losses / 0 ties**.
Median ns/call are 102,629.4 -> 101,408.4; 102,231.2 -> 101,635.2; and
101,672.0 -> 101,989.4 respectively. Timing includes thread start/join and does
not establish a speedup. First-worker cost, total cross-thread allocation,
production contention/scheduling and native CPU/GPU/latency remain unmeasured.

## C2 validation

`FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1` freezes the accepted source. Its
full gate passes 2,004/160/31 Debug and 2,060/160/31 Release leaves with zero
failures or skips. Restore, GUI and RID-aware x64/MSIX builds, exact composition
against the preceding accepted rejected-admission C2R1, dependency replay at the
unchanged `adb9f55b84b567db9f6e0ee8df4c33d11a12d91f` gitlink/HEAD, ledger append,
fresh C1 reverification and package-content verification all pass. The summary
is ACCEPT. The archived MSIX is 73,044,705 bytes; its `FancyWM.GUI/FancyWM.dll`
is 1,466,368 bytes, AMD64 PE32+, SHA256
`03D558BEB0152FB34377B9C46DE949DC04A18D9789D697F47B379DA2BABE0E70`.
The package was not installed, launched or published.

Initial `-C2` remains preserved: Release had one failure in
`CombinedWorkerAndLayoutFailuresRemainDistinctAndWaitForBothOwners (False)`
at the existing exception-identity assertion. Five isolated preflight processes
passed all 10 leaves without source/binary changes; the cause was not established.
Fresh C2R1 passed the full gate with the same production and test inputs.

## PERF-010 native baseline N2/N3/V1

`FWM-ANIMATION-NATIVE-BASELINE-20260908-N1` found WPR/WPA and local profiles.
The CPU profile start failed with `0xC5585011` while enabling
system-performance profiling policy under a non-elevated token. No ETL was
created and no recording remains active. An elevated interactive tracing token
with the required system-profiling permission is the external dependency.
The historical read-only recheck also found medium integrity and no
`SeSystemProfilePrivilege`. On 2026-09-10 fresh N2
(`FWM-ANIMATION-NATIVE-BASELINE-20260910-N2`) instead succeeds with an elevated
interactive token: CPU start/stop exits zero and a smoke ETL is retained.
The environment blocker is resolved; N1 remains immutable.

N3 (`FWM-ANIMATION-NATIVE-BASELINE-20260910-N3`) retains five sequential
Release x64 processes, 120 measured 1/10/50-HWND transitions and 600 matched
ETW markers with zero lost events/buffers. Current production assemblies drive
real Win32Window adapters and owned native windows. Task-completion medians
are 233.5/236.8/237.8 ms; final native-message medians are 233.6/241.0/663.6 ms.
This exposes queued geometry updates in the 50-target, one-owner-thread
workload while frame intervals remain around 6.94 ms.

V1 (`FWM-ANIMATION-NATIVE-BASELINE-20260910-V1`) re-verifies the same immutable
capture with a separately frozen verifier that retains fractional native-tail
milliseconds. The initial report rounded only that derived column; raw QPC
data and all original evidence remain unchanged. Native fixture Debug/Release
builds and smoke checks pass; the affected animation filter is 78/78 D/R and
the existing performance harness builds. The ledger is unchanged. See
[ANIMATION_NATIVE_BASELINE.md](ANIMATION_NATIVE_BASELINE.md) for methodology,
limits and the next topology/presentation stage. Physical presentation and
full-app effects remain E2E_PENDING / NOT_MEASURED.

## PERF-009 generic preview clone-reuse boundary M1

`FWM-GENERIC-PREVIEW-CLONE-BOUNDARY-20260909-M1` freezes the final common
fixture around `TilingWorkspace.MockMoveNode`. A custom `SplitPanelNode` changes
its virtual `Clone` callback between repeated previews without changing the live
tree. Both calls must invoke `Clone`; the second exact controlled exception is
observable behavior. General cross-call clone reuse would skip it, so that
hypothesis is REJECTED. Production is unchanged.

Five sequential isolated Release processes record 105 rows: 1,500 attempts all
deliver the exact exception and 1,500 virtual clone callbacks, with zero
`MinSize` reads, zero live-tree changes and 30,500 node-identity/registration
checks. Targeted tests pass 2/2 D/R; the GenericPreview/Resize affected filters
pass 100/100 Debug and 102/102 Release. This boundary has no A/B speed claim and
does not require a production C2. Exact-built-in-only caching remains possible
only with a complete tree mutation epoch, fresh constraints, clone reset,
preflight, registration and rollback proof.

## PERF-018 equal-cycle forced-GC pause C1R1

`FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-C1R3` and mechanical baseline
`FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-R0R2` share the final fixture. Their
source manifests differ only at `FancyWM/MultiDisplayTilingService.cs`; the
baseline restores only the historical duplicate/unknown-removal collection
admission. Every A/B execution uses the same candidate `FancyWM.Tests.dll` and
the run trees differ only in `FancyWM.dll`. The old behavior fails
`UnknownAndDuplicateDisplayRemovalsDoNotRequestGarbageCollection` exactly as
expected.

`EXP-DISPLAY-018-GC-PAUSE-C1R1` runs five alternating pairs in ten sequential
isolated Release x64 processes. For 1, 10 and 50 duplicate events it performs
three identical owner lifetimes, event calls, 64 KiB allocation churn per event,
routing checks and cleanup. Each admitted callback executes the exact
`GCHelper` sequence: compacting blocking full GC, pending-finalizer wait, then a
second compacting blocking full GC.

Across 600 rows, baseline/candidate admitted requests are 960 -> 45 and actual
forced full-GC passes are 1,920 -> 90. Candidate GC-pause ticks improve in all
15 pairs and cumulative managed allocation improves in 14/15. Pre-final managed
heap is higher in all 15 candidate comparisons because transient churn remains
uncollected until the explicit final settling collection; this is the expected
trade-off and no retained-memory benefit is claimed. Median pause ticks at
1/10/50 duplicates are 208,426 -> 113,609; 1,053,286 -> 97,617; and
4,835,376 -> 98,085. Targeted tests pass 2/2 D/R and the affected multi-display
suite passes 26/26 D/R. This measurement adds no production delta, so the
earlier accepted production full gate remains authoritative and no new C2 is
required.

The mixed-newline C1 matcher preflight, C1R1/R0 independent-test-binary check,
and C1R2/R0R1 verifier-index attempt remain preserved. C1R2 produced a complete
measurement, but strict verification rejected its pair-index calculation; it
was never appended. C1R1 is the sole accepted experiment and ledger suffix.

## Key evidence

| Evidence | SHA256 |
|---|---|
| [PERF-032 rejected C1 measurements](../../artifacts/performance/FWM-KEYBINDINGS-PAGE-TOOLTIP-20260909-C1/EXP-SETTINGS-032-KEYBINDING-TOOLTIP-C1/measurements.csv) | `C15E82E4DE12B84FDA9FC17D9E40FCD7DDBE2DB20F31263D045F8F4E89491C31` |
| [PERF-032 rejection summary](../../artifacts/performance/FWM-KEYBINDINGS-PAGE-TOOLTIP-20260909-C1/validation/rejection-summary.json) | `14C1CE82EC24E001C4C67ABFC48925A6E4633328AB94A534B62ED2878C094AF5` |
| [PERF-032 restored validation](../../artifacts/performance/FWM-KEYBINDINGS-PAGE-TOOLTIP-20260909-R1/validation/validation-summary.json) | `9D10CE5AA6105B1A3F6FF9914231A2B4FE2AF235A2A5DC75D0EB273A04069B0E` |
| [PERF-032 restored manifest](../../artifacts/performance/FWM-KEYBINDINGS-PAGE-TOOLTIP-20260909-R1/manifest.csv) | `EB16990F411F3903BF293F85D4E0917F663172119DEA5A3D98605AF292286ED0` |
| [PERF-008 boundary manifest](../../artifacts/performance/FWM-INVARIANT-MUTATION-BOUNDARY-20260909-M1/manifest.csv) | `624FC1CCEC7994D195132E0818039617EEF2F7806B2C9AE29D60CADBEC2A072C` |
| [PERF-008 boundary measurements](../../artifacts/performance/FWM-INVARIANT-MUTATION-BOUNDARY-20260909-M1/EXP-INVARIANT-008-MUTATION-BOUNDARY-M1/measurements.csv) | `549FEC048B87DD0E3B0571ABE074C0CC5438523A5D71A79E12DC3966A3993E0D` |
| [PERF-008 boundary strict](../../artifacts/performance/FWM-INVARIANT-MUTATION-BOUNDARY-20260909-M1/validation/mutation-boundary-strict-verification.json) | `85D95F35F3C0B575BF4812CCAE6B09AF855EE5AC8B52F33744FE14DB8AE097AC` |
| [PERF-008 boundary append](../../artifacts/performance/FWM-INVARIANT-MUTATION-BOUNDARY-20260909-M1/EXP-INVARIANT-008-MUTATION-BOUNDARY-M1/ledger-append.json) | `0D21F4E0863178092D404E3A8B09E3774B70004A3332AF642EA8F58876ED6B47` |
| [PERF-008 boundary summary](../../artifacts/performance/FWM-INVARIANT-MUTATION-BOUNDARY-20260909-M1/validation/validation-summary.json) | `C210282521D77257F89D583DE673D1C7F71428BEF78C1AA76FF525E393EC0EBA` |
| [PERF-009 clone-boundary measurements](../../artifacts/performance/FWM-GENERIC-PREVIEW-CLONE-BOUNDARY-20260909-M1/EXP-GENERIC-PREVIEW-009-CLONE-BOUNDARY-M1/measurements.csv) | `AFB6B329A4706D90C83B24AC14A78FEBBD7B783B6B55CE9E00328B5FC5AE6E37` |
| [PERF-009 clone-boundary strict](../../artifacts/performance/FWM-GENERIC-PREVIEW-CLONE-BOUNDARY-20260909-M1/validation/clone-boundary-strict-verification.json) | `0BD55683FD04369AB16DB4BBB0F91C1AECBA0988AF3400A28090F3029109C332` |
| [PERF-009 clone-boundary append](../../artifacts/performance/FWM-GENERIC-PREVIEW-CLONE-BOUNDARY-20260909-M1/EXP-GENERIC-PREVIEW-009-CLONE-BOUNDARY-M1/ledger-append.json) | `D8DE0CB6EE8E01655FF70AFB8A8C0315455C2F3CB63FA179FC0255A2222F925C` |
| [PERF-009 clone-boundary summary](../../artifacts/performance/FWM-GENERIC-PREVIEW-CLONE-BOUNDARY-20260909-M1/validation/validation-summary.json) | `3259896B868627852F98B0986D2FB95670D3EAF33E423DBE3EE29F8228389D6A` |
| [PERF-018 C1R1 manifest](../../artifacts/performance/FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-C1R3/manifest.csv) | `39C2463389667BEBA6D05060029C1BFFCAA27511A0EB34DD12EEC89F3F1E92C1` |
| [PERF-018 R0R2 manifest](../../artifacts/performance/FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-R0R2/manifest.csv) | `35FBBEFB8D541B43231CF18BF9A4D2B980EAFAAE30E19577C4417937F8AD40BE` |
| [PERF-018 measurements](../../artifacts/performance/FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-C1R3/EXP-DISPLAY-018-GC-PAUSE-C1R1/measurements.csv) | `F633554301779BC082DE8813B4FE40F677E5D74967E591C5113AD773D22E3EB2` |
| [PERF-018 strict](../../artifacts/performance/FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-C1R3/validation/display-removal-gc-pause-strict-verification.json) | `80D87E0CCAD0AFA02B15A5C48D0A905591FE4E201CD81306D9CF52E80CE20147` |
| [PERF-018 append](../../artifacts/performance/FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-C1R3/EXP-DISPLAY-018-GC-PAUSE-C1R1/ledger-append.json) | `773BB65F4D2EC7ED4969C5C01E78D79F574EA8D64468514471B53BCFFCF5704B` |
| [PERF-018 summary](../../artifacts/performance/FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-C1R3/validation/validation-summary.json) | `8AF5E9D0D014D7D68EE3951FB58562380B40AAB89FFB8B6F4D35CDA5834BB125` |
| `FWM-ANIMATION-NATIVE-BASELINE-20260908-N1/preflight/native-preflight-summary.json` | `B04F981CDC37B0B57EF8F46CC1A5C617FBEDBBE7800575F6EDEBE2621CAF79C7` |
| [Retention M3 manifest](../../artifacts/performance/FWM-FOCUS-WORKER-RETENTION-20260909-M3/manifest.csv) | `4E6114BE807F6A3E23382281B51768FF6C431272CE3CD23FF0C793578BE5C4A4` |
| [Retention M3 measurements](../../artifacts/performance/FWM-FOCUS-WORKER-RETENTION-20260909-M3/EXP-FOCUS-030-WORKER-RETENTION-M3/measurements.csv) | `487C949B8091D18033E386097A3D8675DBB57C0F1B39C3F0F8E52C029F201C9B` |
| [Retention M3 strict](../../artifacts/performance/FWM-FOCUS-WORKER-RETENTION-20260909-M3/validation/focus-worker-retention-strict-verification.json) | `C620EDABAF81327A01ABF5F3D5B14F9FFA2BC01B8EA5F5AF9CEE79A5A91286A1` |
| [Retention M3 append](../../artifacts/performance/FWM-FOCUS-WORKER-RETENTION-20260909-M3/EXP-FOCUS-030-WORKER-RETENTION-M3/ledger-append.json) | `BB8C27D9021040780069B68E44DC3455120E2F65A43677D5DC90A9DD6115944C` |
| [Retention M3 summary](../../artifacts/performance/FWM-FOCUS-WORKER-RETENTION-20260909-M3/validation/validation-summary.json) | `FE3EDDE864B31656B4E25B29976EBE4B03F3F267391796A2684B95D8E170E2E8` |
| [Concurrent R0R2 manifest](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-R0R2/manifest.csv) | `841071D728835D4005D9DC4E42E11DB64C43DC6A22C767ACA10DFDBEF5385265` |
| [Concurrent C1R2 manifest](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C1R2/manifest.csv) | `3A8BC3E8D55A3D5956137D6154A2FB1D59CCD32B9401230505E5720B409E891A` |
| [Concurrent C1R2 measurements](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C1R2/EXP-FOCUS-030-CONCURRENT-ADMISSION-C1R2/measurements.csv) | `EC1AD194DBA6FFC2DF2ADA18D0A1EF1977FC6C19F26E50C966630699A8867C09` |
| [Concurrent C1R2 strict](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C1R2/validation/focus-concurrent-strict-verification.json) | `3791ABB363C0B2F5650868A678B67799F8C1A13CCDF0FB73826BE135893B5333` |
| [Concurrent append receipt](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C1R2/EXP-FOCUS-030-CONCURRENT-ADMISSION-C1R2/ledger-append.json) | `A7449FBDDDB6FD17D3E1A9E2D338EB20DF5149AFE62642F03DE306ED23C1499A` |
| [Concurrent C2 summary](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2/validation/validation-summary.json) | `7DD7DE5BB0EB990C640F83F3F8D2B7F80437E41E212F2FE77B8148BD1C71DC44` |
| [Concurrent C2 reverification](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2/validation/focus-concurrent-measurement-reverification.json) | `B5DC3A80464D78F2EC932E455794BE7A916098BD698A825B04DB2731DC9ABA87` |
| [Concurrent C2 provenance](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2/release/provenance.json) | `F3C87281EAB5D5EBC1CCE67D7D5859A29B451B33AAA13A446BB5587F8F3C8655` |
| [Concurrent C2 MSIX](../../artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2/release/FancyWM.Package_0.0.0.0_x64.msix) | `8B36E25CF3C9CCB7B46D73E19F9EDB96225480902274303DEB83229F4EA80F7F` |
| [R0 manifest](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-R0/manifest.csv) | `99E6F3CCFAC53D57F36594B3ABD12CFC84B09A626F0810031CDEA2B00AAAB265` |
| [C1 manifest](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C1/manifest.csv) | `A232E2EE308707697213B1AC759430908A9CCD1030BA7DC342B1E986CED32C81` |
| [C1 measurements](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C1/EXP-FOCUS-030-WORKER-ADMISSION-C1/measurements.csv) | `36B375D70B17E88743269FA60EACF891349D4974E1D03892139C265A6D0BDEED` |
| [C1 strict verification](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C1/validation/focus-worker-strict-verification.json) | `4EAC61B5DCE99BA848563328E1DFEB118D8AE653D867B92F8958F1380DD4054B` |
| [C1 append receipt](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C1/EXP-FOCUS-030-WORKER-ADMISSION-C1/ledger-append.json) | `AC36AC9F843624C28E5780C018F70FA5EBE8A1BCEC663AF7CD794C58BFFF2B1D` |
| [C2R1 manifest](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1/manifest.csv) | `B405B63E9A34A754636A24AB25B8D9B5034B5544FE3A106D5F51193819BDB2DB` |
| [C2R1 summary](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1/validation/validation-summary.json) | `04CF7A5FE96F7A16636886CE4C79FD3F65384A964B31A5FF84856E34AEC9DCD4` |
| [C2R1 reverification](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1/validation/focus-worker-measurement-reverification.json) | `A1419E74C064C1227ACA129FE4D0E1F1B5CECC4C50009F1E95C5744BD679A606` |
| [C2R1 package provenance](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1/release/provenance.json) | `858ABF26E52D58393BD976A7E75F829786A66E936DCE3E79115C5A488DC35204` |
| [C2R1 MSIX](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1/release/FancyWM.Package_0.0.0.0_x64.msix) | `A845C3246D6150E69432EC0BF2D897C2940FC68F9993AB165F65295D063B4F75` |
| [Failed C2 receipt](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2/validation/run-receipt.json) | `CE8FCAFFE4F9F94171CBA5B64A63A4B2327A9FA9CBDEE3DD3CB5850F130C26C1` |
| [Failed C2 Release TRX](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2/validation/Release-FancyWM.Tests.trx) | `A0C10DE086A21258E69CBCDA82357BCBCC81B626F5ABBF4021A9D641BA15612A` |
| [C2R1 diagnostic preflight](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1-PREFLIGHT/preflight-summary.json) | `A0CB12BE5837DFBB9D77CEE9B8871B0D68208613B632C2948AB5184F88C90AB6` |

`R0`, `C1` and `C2R1` are relative to the matching
`artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-*` roots.

## Ledger integrity

`docs/performance/OPTIMIZATION_MEASUREMENTS.csv` contains 41,325 rows and
41,782,945 bytes, SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
Its exact PERF-022 provider prefix is 41,628,950 bytes, SHA256
`EBB7BCCDB977FDA9D8DEBD5B7D353FD3025D0A45218733D839F8818ABA14C038`;
the single accepted 153,995-byte suffix is SHA256
`3845229F3A109C5ED9F377CC01F00F74F674429F179A2A5E4CA8F4B9C74AE4D6`.

## Other recent accepted bounded stages

- PERF-030 rejected admission remains C1/C2R1 ACCEPT: all 15 allocation pairs
  improve 144 -> 0 B/call; all 90 semantics agree; targeted 4/4 D/R and affected
  482/500 D/R pass. Its [delivery summary](../../artifacts/performance/FWM-FOCUS-REJECTED-ADMISSION-20260909-C2R1/validation/validation-summary.json)
  SHA256 is `4C86FD85DA66B14FBC4D48A6E81D66BD0D69D402D9670BDC556D5E69FD2A7A6B`;
  strict C1 SHA256 is `9EE65CFAC6960B40559BD0EED19A448DA849E421A9FD171FAB1AB50A1CA100CA`.
- PERF-008 `ContainsWindow`: all 60 allocation pairs improve (master 24 -> 0
  B/call; satellite scans 128 -> 40), all 240 semantics agree and timing is
  mixed 54/6. C2R1 summary SHA256
  `3355330A5A4AE463BD61554CC5DFC6458C6CA55D257EA7ECA899CD190281DC79`
  remains accepted; broader reuse constraints are unchanged.
- PERF-004 cached callback: all 15 allocation pairs save 64 B/pass, all 90
  semantics agree and timing is mixed 13/2. C2R3 full gate and summary SHA256
  `FB146FB42DBC62067C3D3D7B8A54778B743C2E351646770DF196D54FB0F68175`
  remain accepted.
- PERF-002 public Refresh owned-window snapshot: all 20 allocation pairs save
  32 B/pass and all 180 semantics agree; timing remains mixed. C2 summary
  `artifacts/performance/FWM-REFRESH-WINDOW-SNAPSHOT-20260908-C2/validation/validation-summary.json`
  has SHA256 `6195C331AE525D184076CD2F9765FD014562DF288805E78890751A98BEA9F248`.
  Whole PERF-002 remains IN_PROGRESS and retains 250 ms reconciliation.
- PERF-009 recursion and public stack guards: C2 ACCEPT; direct public-call
  allocation progressed 456 -> 360 -> 216 B/call. Stack C2 passes
  1,965/160/31 Debug and 2,017/160/31 Release; summary
  `artifacts/performance/FWM-PUBLIC-MOVE-NODE-STACK-20260908-C2/validation/validation-summary.json`
  has SHA256 `256B1EA57BEFEEEB1A52A92DE57E3FA95544B69F234A6593C80872DB96BA1258`.
  GenericPreview timing remains mixed. Clone-boundary M1 additionally rejects
  general cross-call clone reuse across an unversioned virtual callback; whole
  PERF-009 remains IN_PROGRESS only for exact-built-in/native follow-up.
- PERF-010 transition completion aggregation: managed C1/C2 ACCEPT; N1 records
  the external profiling-permission blocker and no ETL.
- PERF-016 earlier Toast construction, TilingWindow and overlay ownership stages:
  stated C2 gates ACCEPT. [Native toast R2](TOAST_NATIVE_LIFETIME.md) now verifies
  actual HWND/failure/shutdown paths in five isolated tests per configuration;
  Debug 5+162 and Release 167 pass. Each configuration's 100 retention cycles
  leave zero weak windows and no additional USER/GDI objects. Production and
  ledger unchanged; other native owners and whole-app teardown remain pending.
- PERF-022 operation-boundary M1 freezes repeated `Position`/`WorkArea` reads and
  same-call collection mutation. Its hypothetical two-site consolidation fails
  all four fixture leaves, so production remains unchanged; summary SHA256 is
  `79159AEE0E55082396E550ACFAED3365DA7A5A4740092F473AA604DB9F679589`.
- PERF-022 provider snapshot C1/C2 caches the stable current-culture sort under
  the display-list lock while preserving a fresh concrete list on every getter.
  Old is 6 pass / 3 allocation fail; candidate is 9/9 D/R. Five alternating
  pairs improve allocations 184/648/1,768 -> 64/136/456 B/getter and all 15
  managed timings, with 75 semantic comparisons equal. C2 full tests pass
  2033/160/31 Debug and 2089/160/31 Release. The
  [C1 measurements](../../artifacts/performance/FWM-DISPLAY-PROVIDER-SNAPSHOT-20260909-C1/EXP-DISPLAY-022-PROVIDER-SNAPSHOT-C1/measurements.csv)
  SHA256 is `D3762C7B957D2F89F2691D4667A9AB04BEB2C43DDED47E9DD48CA53EB1AB2936`;
  [strict](../../artifacts/performance/FWM-DISPLAY-PROVIDER-SNAPSHOT-20260909-C1/validation/display-provider-strict-verification.json)
  is `1FA5BFB9BD0CB09AB1D8F4B5DEFA9F6340388ABB018B90DFD13095B046BE7DA5`;
  [C2 summary](../../artifacts/performance/FWM-DISPLAY-PROVIDER-SNAPSHOT-20260909-C2/validation/validation-summary.json)
  is `62F89806816B7B97BF2D15AF8EA6CE3CB2A0659C7D106F8A13A505B624F8A942`.
- PERF-032 tooltip representation M1 is rejected. It eliminates deterministic
  eager objects without a reproducible allocation benefit, so the production
  XAML is restored, the measurement ledger is unchanged and no C2 applies.

Exact historical commands, rejected hypotheses, intermediate snapshots and every
older stage hash remain in the byte-exact archive and immutable artifact trees.
Do not copy historical `next action` text into the live plan.

## Remaining boundary and next stage

PERF-002 retains the 250 ms reconciliation because providers lack complete
invalidation. Native window/COM/WPF/DPI, polling latency, idle wakeups and
whole-process CPU/GPU/energy remain E2E_PENDING or NOT_MEASURED.

N3/V1 resolves the PERF-010 native baseline; S1 closes owner topology /
presentation and V14 closes full-app behavior verification. Next is full-app
ETW, attributed CPU/GPU and hardware-presentation measurement using a separate
runner for the existing frozen host's `measurement` mode. Any production
batching candidate still requires isolated A/B and delivery gates.

## PERF-010 owner topology and full-app behavior


Latest verified continuation (2026-09-13): `FWM-PERF016-PSS-20260913-R1` —
[PSS native memory and coverage boundaries](PSS_NATIVE_MEMORY.md).
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

Latest PERF-010 continuation (2026-09-12): [completed actual multi-DPI validation and display restoration](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-DPI120-RESTORATION-R1/REPORT.md).
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
D2 preserves the N3 scheduling derivation; D6 verifies 2,440/2,440 final target
positions through the DWM/Dxg hardware flip chain. Fresh owner-limit captures
retain separate geometry, process CPU and compositor diagnostics. M1-OWNERS50
has a profile mismatch; M1-OWNERS5/M2-OWNERS50 lack required draw correlation;
M3 fails before tracing; M4 has an external occlusion; M5 fails profile controls
before tracing; M6 retains an ETL after sidecar overflow. Those failed attempts
do not establish a production speedup. All original failed evidence remains.

Harness changes provide native owner mapping, visibility checks, exact N3
profile replay, immutable source/binary capture, scheduling and presentation
verification, and preserved error-sidecar prefixes. PILOT4 passed six native
smoke processes/54 transitions before the final error-path guards. PILOT6
builds native/full-app/target projects in both configurations and confirms
0xC01E0006 cleanly at the native clock boundary. D15 checks the old overflow
failure and the corrected 500,000-record prefix without HWND callback throws.
D13 independently reproduced STATUS_GRAPHICS_PRESENT_OCCLUDED before and after
a scoped display-power request. Later M7-OWNERS1/5/50 resolve this prerequisite
and pass S1: 15 sequential processes, 360 transitions and 7,320 hardware
witnesses. See [the owner protocol and receipts](ANIMATION_OWNER_TOPOLOGY.md).

V12 full-app behavior exposed missing generic layout invalidation on restoring
a window without focus. The correctness correction passes old 4/8 -> corrected
8/8, full Debug 2043/2043 and Release 2099/2099, Layouts/ThemeEngine and both
delivery builds. V13 retains the obstructed-input failure without sending input.

V14 reuses B13 unchanged and passes Debug/Release plus the independent behavior
verifier. Each process verifies 12 transitions at 1/10/50 windows, owned
mouse/direct-hotkey focus, 12 concurrent settings requests, native minimize/
close recovery, restoration of 10 windows and pending-shutdown restoration of
50 windows. Actual DPI is 96; application termination and owned-target cleanup
pass. Receipt SHA256:
`FE40437B7F95CF86F0794DA81DB0D54660204BD87611BDA92C008C9473A9AB0D`.
No executable source changed during V14; the earlier restore correction is the
sole production difference from topology. PERF-010 remains IN_PROGRESS, with
no ledger append or full-app CPU/GPU/presentation/speedup claim.
See [the full-app report and next action](ANIMATION_FULLAPP_VALIDATION.md).

The subsequent full-app measurement continuation adds a capture runner,
full-app marker profile and native desktop/measurement verifiers. Twenty-three
replay/injected-failure checks pass. M1 retains actual WPR error 0xC5585011 under
the current medium token; M2 retains an incomplete CPU-only run with 192 DPI
and `NoMonitor` fallback; M3 rejects incompatible RDP/native-desktop controls
before application launch or tracing. No measurement or production change is
accepted. The next capture needs the validated local display/DPI and an elevated
interactive token. [Exact evidence and limitations](ANIMATION_FULLAPP_MEASUREMENT.md).

The 2026-09-11/12 continuation resolves the environment and repeatability gates:
B15/V16 passes Debug/Release behavior, and M6 verifies five processes / 120
measured transitions / 750 ETW markers with no lost events. CPU/thread analysis
passes, including an independent xperf check for ten processes and 460 threads.
Median Dispatcher occupancy at 1/10/50 windows is 32.789/293.105/1201.903 ms.
Shared compositor GPU counters are available; app/target GPU attribution is not.

The first real presentation analysis exposes surface rebinding and client-space
draws. The corrected analysis/independent verifier require continuous owned
binding anchors and pass 13 real-trace/negative plus 26 existing geometry cases.
All 2,440 target client translations/clip diagnostics match, but separate
DWM-owned non-client visuals lack a proven identity chain. Full visible-frame
hardware proof remains E2E_PENDING; the verifier preserves the strict criterion
and records a nonzero-exit pending receipt. No production, binary, dependency,
ledger or whole-ID status change applies. See the current
[measurement report](ANIMATION_FULLAPP_MEASUREMENT.md).

D6 adds measured-interval module samples for all 120 transitions and 47,567
Dispatcher stack hits across the forty 50-window intervals. JIT remains present
in measured app intervals, while 51.134% of Dispatcher stack hits are unresolved;
function attribution is the next CPU step. The visual identity audit retains
782 installed schemas and a validated extra-event WPR profile. Its pilot rejects
RDP/192 DPI before tracing or app launch. No executable, ledger or acceptance
change follows. [D6 diagnostics and next actions](ANIMATION_FULLAPP_MEASUREMENT.md#d6-visual-identity-and-scoped-cpu-diagnostics--2026-09-12).

D7 resolves managed methods from M6 and native methods from matching cached
PDBs. All 47,929 scoped samples match a raw ETW reader; 45,056 full frame chains
also match raw StackWalk fragments. The verified subset contains 10,855
`TilingWindow` construction samples across five processes. Padding changes reach
full overlay invalidation, motivating a bounded reuse candidate with geometry,
reentrancy and lifetime tests. At D7 the candidate was not yet applied. The rejected
xperf symbol probe, excluded stacks and all intermediate reports remain intact.
[D7 function attribution](ANIMATION_FULLAPP_STACK_ATTRIBUTION.md).

The following padding-reuse B1/C1/M2 stage implements the three-file candidate.
Common tests go 3/4 -> 4/4; five alternating Release x64 pairs independently
verify 360 counters. At 1/10/50 windows, median allocations per update fall from
2.507/15.089/70.493 to 0.065/0.259/1.300 MB, with 15/15 allocation and elapsed
wins and zero candidate window/tab/SVG recreation. Geometry, subscriptions,
managed reentrant cancellation, failure retry, height/scaling and disposal pass.
Native app behavior/CPU/GPU/presentation remain pending, including a separately
retained baseline WPF nested-notification visual defect. M1's incorrect runner
counter cardinality is rejected; no partial rows or ledger append apply.
[Candidate scope and receipts](OVERLAY_PADDING_REUSE.md).

C2 verifies unchanged C1 executable/test source, full Debug 2047/160/31 and
Release 2103/160/31, both GUI/x64/MSIX builds, pinned dependency replay and package
contents. M2 reverification passes. The 73,110,248-byte Release package is archived
only; no installation, launch or publication. Local delivery passes while native
acceptance and whole-ID counts remain unchanged.

The nested-notification follow-up reproduces the collection guard exception:
an interrupted removal can leave 99 WPF controls for 50 windows even after an
idle Dispatcher barrier. The renderer now cancels immediately and completes
invalidation after current add/remove listeners return. B1/C1 shared tests go
4/7 -> 7/7 (24 new cases); affected Release is 87/87. Both WPF overlay surfaces,
listener order, disposal, primary/supplemental errors and retry pass. Five M1
pairs retain zero padding recreation and no allocation losses; timing is mixed
at 5/15 wins and 50-window median rises 9.43 -> 9.79 ms. This is a correctness
fix without a new speedup claim or ledger append.
[Correction and evidence](OVERLAY_NESTED_NOTIFICATION.md).

Nested C2 passes full Debug 2050/160/31 and Release 2106/160/31, both restores,
GUI/RID-aware x64/MSIX, clean pinned dependency replay and independent M1
reverification. The 73,110,551-byte archived package contains the exact built
AMD64 DLL and is not installed/launched/published. Measured candidate source,
historical ledger and 25 IMPLEMENTED / 12 IN_PROGRESS counts are preserved.

Full-app B16 now passes four Debug/Release host/target builds with production
identical to corrected C1/C2 and ten common fixture files identical to B15.
P3 verifies 7,405 source and 244 binary files and independently reproduces the
old V16 receipt. Its native desktop check rejects RDP / 192 DPI / 2880x1704
before candidate launch. V17 behavior and native CPU comparison await fresh
local 3440x1392 / 96-DPI admission. No production or ledger change applies.
[B16/P3 evidence and continuation](ANIMATION_FULLAPP_VALIDATION.md#corrected-candidate-b16--preparation-p3--2026-09-12).

P4 subsequently restores the disconnected user's session to an unoccupied
local console, and matching 96-DPI admission passes. V17 verifies the candidate.
M7's baseline run rejects a stale closed-HWND ownership classification; ten
native guard cases validate the common fixture correction. Fresh B18/V18
baseline and B17/V19 candidate pass all Debug/Release native behavior and exact
geometry audits without shipping-source changes. M8 verifies five alternating
pairs / 240 measured transitions: 14 CPU wins, one tie, with 50-window median
CPU 1,500 -> 210.9375 ms. D8 records the extra-event profile with zero loss;
xperf and TraceEvent agree on 22,601 event-333 records and no event 335. The
nonclient visual edge, full-frame/hardware presentation and attributed GPU
remain pending. [Current native results and receipts](OVERLAY_FULLAPP_CPU.md).

D9 resolves GPU event ownership in the old M6 baseline: 5 devices, 20 contexts,
10 hardware queues and 89,585 app-owned events. Independent xperf matches 77,967
queue events / 518,683 fields; the audit rejects misleading header-PID matches
and verifies packet identities/lifetimes. This is offline attribution evidence,
not a GPU-time or candidate speedup result. Shipping source, M8 and the ledger
remain unchanged. [GPU diagnostic and pending gates](ANIMATION_FULLAPP_GPU.md).
