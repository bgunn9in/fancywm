# FancyWM performance harness — live runbook

Latest verified continuation (2026-09-13): `FWM-PERF016-MEMCOUNTERS-20260913-R1` —
[native GPU and process memory accounting](../../docs/performance/DXGI_PROCESS_MEMORY.md).
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
[actual D3D mapped points and PSS boundaries](../../docs/performance/D3D_MAPPED_PSS.md).
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
[native D3D ownership observations](../../docs/performance/D3D_NATIVE_OWNERS.md).
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
[CLR/native caller attribution and heap boundary evidence](../../docs/performance/CLR_NATIVE_HEAP_ATTRIBUTION.md).
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
[complete baseline heap replay](../../docs/performance/NATIVE_HEAP_COHORTS.md).
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

Offline cohort tools: `Start-HeapCohortReview.py` verifies the prior seal;
`Verify-HeapCohorts.py --mode control` calibrates the baseline-seeded model on
retained E3 data; `--mode graph` replays retained F4 without launching the app.
`Verify-HeapCohortWitnesses.py` independently checks exported identities and
native birth/free/realloc witnesses. `Finalize-HeapCohortEvidence.py` binds
inputs to the predecessor manifest, checks exclusive-output rejection and
seals the new evidence. Exact executed commands and frozen sources are under
T1/T2/D1/D2 `command.json` and `source/`; existing IDs must never be reused.

Latest verified continuation (2026-09-13): `FWM-PERF016-HEAP-20260913-R1` —
[native heap and graph HWND generations](../../docs/performance/NATIVE_HEAP_LIFETIME.md).
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

Native-heap diagnostic tools: `Build-NativeHeap.py` / `Verify-NativeHeap.py`
calibrate the owned observer; `Run-NativeHeapAdmission.py` admits an own-PID
session; `Build-HeapDecoder.py`, `Decode-HeapGraph.py`, the pinned
`heap-stack-reader` and `Verify-HeapGraphTrace.py` independently verify raw
allocation and full stack payloads. `Verify-NativeHeapGraph.py` keeps heap
observations separate from unchanged GUI/weak/shutdown gates.
`Summarize-NativeHeapEvidence.py` derives bounded module/epoch observations;
`Finalize-NativeHeapEvidence.py` pins receipts, verifies cleanup and seals all
successes and failures with a second complete rehash. Existing outputs reject
overwrite. These tools do not authorize repeating a completed experiment.

F4's recorded native invocation was:

```powershell
python scripts/performance/Run-FullGraphLifetime.py --id FWM-NATIVE-HEAP-20260913-F4 --owned-membership --warmup 50 --modes graceful --compact-targets --gui-quiescence 45 --gui-stable-window 30 --heap-observer artifacts/performance/FWM-NATIVE-HEAP-20260913-T2/FancyWM.NativeHeap.dll --heap-trace
```

That immutable ID already exists. Any justified future experiment requires a
fresh actual-date ID, current source/environment admission and exact commands;
the retained receipt is not a reason to repeat this capture or the old DPI runs.

Latest verified continuation (2026-09-13): [GUI counter identity and native attribution](../../docs/performance/GUI_RESOURCE_ATTRIBUTION.md).
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
[full startup graph lifetime and exact remaining criteria](../../docs/performance/FULLGRAPH_LIFETIME.md).
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

Current runners: `Run-FullGraphLifetime.py --id <fresh-actual-date-id>` executes
Startup/AppMain on fresh non-input desktops. `--owned-membership --warmup 50`
adds the disclosed target-only membership adapter and matching epochs;
`--shutdown-probe` isolates the post-App.Run logger failure. Only the copied
build workspace receives adapters. Source/binaries/commands/raw logs are archived.
`Verify-MicaShutdownCandidate.py`, `Verify-WorkerRetentionCandidate.py` and
`Verify-FullGraphScope.py --scope crash|retention` enforce exact scopes and
exclusive receipts. Tiling GUI-retention remains unmet; preserve failed verifiers
and resource observations. `Validate-NativeOwnersDelivery.py --test-additions 2`
runs the full production gate; later harness-only changes need no repeated C2.
`Finalize-FullGraphLifetime.py` seals all new runs, including failures, and
independently rehashes the previous checkpoint.

Latest verified continuation (2026-09-13): `FWM-PERF016-PSS-20260913-R1` —
[PSS native memory and coverage boundaries](../../docs/performance/PSS_NATIVE_MEMORY.md).
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

Earlier native continuation: [PERF-016 owners/teardown](../../docs/performance/NATIVE_OWNER_LIFETIME.md),
checkpoint `FWM-PERF016-NATIVE-20260912-R1`.
`Run-NativeOwnerLifetime.py` freezes source/binaries, runs isolated HWND tests
and archives TRX/logs; `Verify-NativeOwnerLifetime.py --baseline <B3>` validates
the five constructor reds and the two-file corrected source. The production
correction already passed `Validate-NativeOwnersDelivery.py` full C2. Unchanged
delivery gates need not be rerun for later test/harness-only validation.

`Run-NativeTeardown.py --id <fresh-dated-id>` runs production auxiliary windows,
actual input hooks, partial App.Terminate, FailFast and self-TerminateProcess on
owned non-input desktops. `--modes providers` exercises actual Win32Workspace and
ThemeEngineManager. `--modes browser --current-desktop-browser` admits only
offscreen owned browser windows, no hooks/input, an isolated profile and unchanged
foreground; it records passive WPF view-cache retention separately from the
post-maintenance epochs. See `Verify-NativeTeardown.py`,
`Verify-NativeProviders.py`, and `Verify-NativeHelpBrowser.py` for strict receipts
and negative controls. Every output path must be absent before invocation.

`Finalize-NativeOwners.py --id <fresh-dated-id>` replays receipts, validates C2
archives and historical file preservation, checks Git/submodules/production and
ledger, and seals successful plus failed evidence. Replays are not new tests.
The full MainWindow startup cannot run in this session: it writes global
WindowArranging. An isolated user/session/VM is required for the remaining full
graph criteria. MSIX is archive-only; no WPR or DPI rerun is involved.

Earlier completed native lifetime continuation: [PERF-016 ToastWindow](../../docs/performance/TOAST_NATIVE_LIFETIME.md).
`FWM-TOAST-NATIVE-20260912-R2` retains the source snapshot, five isolated native
tests, Debug 5+162 / Release 167 results and the 32-row verification receipt.
The test filter is `FullyQualifiedName~NativeToastLifetimeTest`; the public
test methods launch their owned child processes automatically. No child flag
should be set for a normal run. The fixture owns and closes its transient toast
windows and handles only explicitly injected dispatcher exceptions.
Run that artifact's `verify-native.py --output <fresh-receipt.json>` to replay
the retained TRX evidence without launching windows. The output is exclusive-create.
This is a native lifetime check,
with no production change, package gate, ledger append or performance comparison.

This is the compact live runbook. The complete 4,543-line command/result history
is preserved byte-for-byte at
`.codex/session-backups/20260908-before-performance-live-doc-compaction/scripts/performance/README.md`
(263,089 bytes, SHA256
`2A671B98FB7BEE51299FB7365523888325A21774FBE9424F6272FBD871AE4209`).
Open it only for the chosen historical scenario. Current status and acceptance
gaps live in `PERFORMANCE_STATUS.md` and `PERFORMANCE_TODO.md`.

## Rules

- Preserve the intentionally dirty tree. Never use reset, checkout or clean.
- Use a fresh immutable snapshot and experiment ID for every new run.
- Freeze the final test/runner fixture in both variants; the baseline and
  candidate should differ only in the declared production change.
- Retain command logs, TRX, raw counters, source/binary manifests, environment
  metadata and process timing/order.
- Run at least five alternating A/B pairs for a performance claim. Keep warmups,
  iterations, process mode and inputs identical.
- Report allocations/counts separately from elapsed time and native CPU/GPU/
  latency. Mixed timing does not establish a speedup.
- Run strict evidence verification before appending accepted measurement rows.
  Append once and prove the exact old ledger prefix plus the new suffix.
- Build MSIX artifacts only. Do not install, launch or publish them.

## Standard workflow

Inspect the repository first:

```powershell
git status --short
git diff --check
git submodule status --recursive
```

Freeze exact inputs before a production edit:

```powershell
pwsh -NoProfile -File scripts/performance/Save-ImplementationSnapshot.ps1 `
  -SnapshotId <fresh-R0-or-R1-id>
```

Use the scenario-specific archive/runner/verifier recorded in the compact result
or historical runbook. Verify that old production fails the intended regression
with the final common fixture, then build and measure the candidate. Parser or
synthetic harness failures are development evidence, not baseline failures.

For a production stage large enough to require delivery validation:

```powershell
pwsh -NoProfile -File scripts/performance/Validate-Implementation.ps1 `
  -SnapshotId <fresh-C2-id>

pwsh -NoProfile -File scripts/performance/Test-DependencyPatches.ps1 `
  -SnapshotId <fresh-dependency-verification-id>
```

Archive the exact x64 MSIX output path recorded by the successful
`Release-full.log`. Record package size/hash, embedded `FancyWM.dll` path/hash,
architecture and explicit false values for installed/launched/published. Finish
with an immutable summary that rechecks frozen files, TRX, build logs, strict
reports, ledger, dependency evidence and package contents.

## Current FocusWorkerRetention harness — M3 ACCEPT

```text
Snapshot:   FWM-FOCUS-WORKER-RETENTION-20260909-M3
Experiment: EXP-FOCUS-030-WORKER-RETENTION-M3
Summary:    M3/validation/validation-summary.json
Manifest SHA256: 4E6114BE807F6A3E23382281B51768FF6C431272CE3CD23FF0C793578BE5C4A4
Measurements SHA256: 487C949B8091D18033E386097A3D8675DBB57C0F1B39C3F0F8E52C029F201C9B
Strict SHA256: C620EDABAF81327A01ABF5F3D5B14F9FFA2BC01B8EA5F5AF9CEE79A5A91286A1
Summary SHA256: FE3EDDE864B31656B4E25B29976EBE4B03F3F267391796A2684B95D8E170E2E8
```

`Prepare-FocusWorkerRetentionSnapshot.ps1` builds the frozen observation-only
source and confirms `FocusHelper.cs` is byte-identical to accepted concurrent
C2. `Measure-FocusWorkerRetention.ps1` runs five sequential isolated Release
x64 processes after equal-size warmups. Each process measures an active worker,
128 completed lifetimes, and two equal 64-worker overlap epochs with 16
admissions per worker. `Verify-FocusWorkerRetention.ps1` requires exact managed
ownership/call counts, zero settled process-thread deltas, dead weak worker
references and bounded aggregate handle deltas. There is no A/B speed claim.

M3 records 380 rows: all active process-thread deltas are +1 and every settled
thread and handle delta is 0. Targeted 3/3 and affected 250/250 leaves pass D/R.
The ledger append preserves the exact 40,597,252-byte prefix, SHA256
`4EB8974ADA760ADB195853E1C2CABBE4C9F687BF13681D88BD226E3F890F837A`,
and adds 262,220 bytes, SHA256
`F0B2CCBD71BB264FBEB7FB5B8C2E10AAA113975D063C10170BA04258E679924B`.
The ledger now has 40,200 rows / 40,859,472 bytes, SHA256
`8933BDA6C4281DDC2CC905B560736CFCC488D8E2B891AFC07705DE593B8B56D3`.

M1 and M2 are immutable failed measurements. Both stopped on a +3 first-overlap
aggregate handle delta against the original 0..1 criterion. M3 adds an equal
repeat epoch; it does not treat those attempts as product failures or append
their partial output. Aggregate `HandleCount` is not handle-type attribution.

## Previous FocusConcurrentAdmission harness — C1R2/C2 ACCEPT

```text
Baseline:   FWM-FOCUS-CONCURRENT-ADMISSION-20260909-R0R2
Candidate:  FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C1R2
Experiment: EXP-FOCUS-030-CONCURRENT-ADMISSION-C1R2
Delivery:   FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2
Summary:    C2/validation/validation-summary.json
C1 strict SHA256: 3791ABB363C0B2F5650868A678B67799F8C1A13CCDF0FB73826BE135893B5333
Measurements SHA256: EC1AD194DBA6FFC2DF2ADA18D0A1EF1977FC6C19F26E50C966630699A8867C09
Summary SHA256: 7DD7DE5BB0EB990C640F83F3F8D2B7F80437E41E212F2FE77B8148BD1C71DC44
MSIX SHA256: 8B36E25CF3C9CCB7B46D73E19F9EDB96225480902274303DEB83229F4EA80F7F
```

`Prepare-FocusConcurrentSnapshot.ps1` freezes source and the common final
fixture. `Measure-FocusConcurrentAdmission.ps1` runs five alternating A/B pairs
in ten sequential isolated Release x64 processes, using the candidate-built
test binary and swapping only `FancyWM.dll`. `Verify-FocusConcurrentAdmission.ps1`
requires all 400 semantic comparisons and all 15 worker-allocation improvements;
timing is descriptive. `Append-FocusConcurrentMeasurements.ps1` appends once
with exact prefix/suffix proof, and `Verify-FocusConcurrentDelivery.ps1` verifies
the full gate, dependency replay, package and a fresh C1 recheck.

The accepted ledger has 39,820 rows / 40,597,252 bytes, SHA256
`4EB8974ADA760ADB195853E1C2CABBE4C9F687BF13681D88BD226E3F890F837A`.
Its exact prior prefix is 39,811,448 bytes, SHA256
`9D40BD0AE06DE654DBE0C9E0CE036E55B0463A892621DCA23A873CA3D3D1B0B1`;
the 785,804-byte suffix SHA256 is
`75B50FFA4BEBE14408C15964764A1DE42B0641B9F3F03CF24FE713429B5DBD5B`.

R0/C1 and C1R1 are preserved protocol/harness failures. They are not accepted
baselines and were not appended. Do not rewrite or reuse their IDs.

## Previous FocusWorkerAdmission harness — C1/C2R1 ACCEPT

```text
Baseline:   FWM-FOCUS-WORKER-ADMISSION-20260909-R0
Candidate:  FWM-FOCUS-WORKER-ADMISSION-20260909-C1
Experiment: EXP-FOCUS-030-WORKER-ADMISSION-C1
Delivery:   FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1
Summary:    C2R1/validation/validation-summary.json
C1 strict SHA256: 4EAC61B5DCE99BA848563328E1DFEB118D8AE653D867B92F8958F1380DD4054B
Measurements SHA256: 36B375D70B17E88743269FA60EACF891349D4974E1D03892139C265A6D0BDEED
Summary SHA256: 04CF7A5FE96F7A16636886CE4C79FD3F65384A964B31A5FF84856E34AEC9DCD4
MSIX SHA256: A845C3246D6150E69432EC0BF2D897C2940FC68F9993AB165F65295D063B4F75
```

`FocusHelper.RequestSequence.Enqueue` now uses `m_workerStart ??= RunWorker`
inside the existing locked `m_worker == null` branch. The lazy bound
`ThreadStart` is reused across completed worker lifetimes; the first allocation
is unchanged. The final common `FocusHelperTest.WorkerAdmission.cs` fixture
runs 32 discarded warmups and 500 admissions for direct success, Alt retry and
attachment failure, starting and joining each real background worker with fake
native adapters. Old source passes five semantic/lifetime leaves and fails three
allocation leaves; candidate targeted tests pass 8/8 D/R and affected tests pass
239 leaves / 271 raw results in each configuration.

Five alternating, sequential isolated Release pairs with
`DOTNET_TieredCompilation=0` use the same candidate-built fixture and swap only
`FancyWM.dll`. All 15 allocation pairs improve 368 -> 304 B/call on the caller;
all 240 semantic comparisons agree across 570 rows. Timing is mixed, 10 wins /
5 losses / 0 ties, and includes OS-thread start/join. First-worker cost, total
cross-thread allocation, production contention/scheduling and native
focus/input/COM/WPF/DPI/CPU/GPU/latency remain E2E_PENDING / NOT_MEASURED.

The completed sequence used `Save-ImplementationSnapshot.ps1`,
`Prepare-FocusWorkerSnapshot.ps1`, `Measure-FocusWorkerAdmission.ps1`,
`Verify-FocusWorkerAdmission.ps1` and `Append-FocusWorkerMeasurements.ps1`.
The append preserves the exact 39,228,823-byte prefix (SHA256 `407C85168F01D049B885E58255E10CCF98BE91A5DFA0C1F798080CAA77D2AE94`)
and adds 582,625 bytes (SHA256 `0ACFEC5424001D0236BF686FE9AE62A3DE38CC93CABF160F68EC70478E3414F0`).
The ledger now has 38,730 rows / 39,811,448 bytes, SHA256
`9D40BD0AE06DE654DBE0C9E0CE036E55B0463A892621DCA23A873CA3D3D1B0B1`.

C2R1 passes full Debug 2,004/160/31 and Release 2,060/160/31 leaves, restore,
GUI, RID-aware x64/MSIX, dependency replay, exact test composition and fresh
measurement reverification. The [summary](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1/validation/validation-summary.json)
and [73,044,705-byte MSIX](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1/release/FancyWM.Package_0.0.0.0_x64.msix)
are immutable; the package was not installed, launched or published. Its embedded
AMD64 PE32+ `FancyWM.GUI/FancyWM.dll` is 1,466,368 bytes, SHA256
`03D558BEB0152FB34377B9C46DE949DC04A18D9789D697F47B379DA2BABE0E70`.

Recorded final verification command, after the gate, dependency replay and
package archive; do not rerun accepted IDs without a discovered reason:

```powershell
pwsh -NoProfile -File scripts/performance/Verify-FocusWorkerDelivery.ps1 `
  -SnapshotId FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1 `
  -BaselineSnapshotId FWM-FOCUS-WORKER-ADMISSION-20260909-R0 `
  -CandidateSnapshotId FWM-FOCUS-WORKER-ADMISSION-20260909-C1 `
  -ExperimentId EXP-FOCUS-030-WORKER-ADMISSION-C1 `
  -DependencySnapshotId FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1-DEPENDENCY-VERIFY
```

Initial `-C2` remains preserved with one Release failure in the existing
`CombinedWorkerAndLayoutFailuresRemainDistinctAndWaitForBothOwners (False)`
exception-identity assertion. The [diagnostic preflight](../../artifacts/performance/FWM-FOCUS-WORKER-ADMISSION-20260909-C2R1-PREFLIGHT/preflight-summary.json)
passed five isolated processes / ten leaves without source/binary changes;
C2R1 passed the complete gate with the same production and test inputs. Failure
cause remains unestablished. Exact evidence hashes are in the compact
[implementation results](../../docs/performance/IMPLEMENTATION_RESULTS.md).
Post-acceptance live-document updates do not change frozen C2R1 inputs.

## Accepted FocusRejectedAdmission harness

`FWM-FOCUS-REJECTED-ADMISSION-20260909-R0`, `-C1`,
`EXP-FOCUS-030-REJECTED-ADMISSION-C1` and `-C2R1` remain accepted. The common
fixture uses 1,000 warmups and 100,000 calls for superseded, expired and stopped
admission. Five alternating Release pairs record 270 rows: all 15 allocation
pairs improve 144 -> 0 B/call and all 90 semantic comparisons agree. Timing is
lower in 15/15 pairs and descriptive; targeted tests pass 4/4 D/R.

The recorded tools are `Measure-MicaRefresh.ps1 -Scenario FocusRejectedAdmission`,
`Verify-FocusRejectedAdmission.ps1`, `Append-FocusRejectedMeasurements.ps1` and
`Verify-FocusRejectedDelivery.ps1`. C1 strict SHA256 is
`9EE65CFAC6960B40559BD0EED19A448DA849E421A9FD171FAB1AB50A1CA100CA`;
[C2R1 summary](../../artifacts/performance/FWM-FOCUS-REJECTED-ADMISSION-20260909-C2R1/validation/validation-summary.json)
SHA256 is `4C86FD85DA66B14FBC4D48A6E81D66BD0D69D402D9670BDC556D5E69FD2A7A6B`.
Do not rerun or append those accepted IDs again without a discovered reason.

## Accepted ContainsWindow harness

Do not rerun accepted IDs without a discovered reason.

```text
Baseline:   FWM-CONTAINS-WINDOW-SCAN-20260909-R0R2
Candidate:  FWM-CONTAINS-WINDOW-SCAN-20260909-C1R2
Experiment: EXP-MASTER-SATELLITE-008-CONTAINS-WINDOW-C1R2
Delivery:   FWM-CONTAINS-WINDOW-SCAN-20260909-C2R1
Summary:    C2R1/validation/validation-summary.json
C1 strict SHA256: 40F447DF01D7EE376043599ECAB578C54FA9B25DCEE2756216FF2CA30B646DCE
Summary SHA256: 3355330A5A4AE463BD61554CC5DFC6458C6CA55D257EA7ECA899CD190281DC79
MSIX SHA256: 82EEDBEA22D58E1E50482FB445F855BBF2E8848BDA1E946345E5CA431E596158
```

Use `Measure-MicaRefresh.ps1 -Scenario ContainsWindow` with the exact IDs, then
`Verify-ContainsWindowScan.ps1`. The common fixture runs 1,000 warmups and
100,000 measured membership calls for master/first/last/absent lookups with
1/4/9 satellites. Ten sequential isolated Release processes run in
`B1,C1,C2,B2,B3,C3,C4,B4,B5,C5` order with `DOTNET_TieredCompilation=0`.

C1R2 strict verification accepts 840 rows, all 60 allocation pairs and 240
semantic comparisons. Master allocation changes 24 -> 0 B/call; cases that
enumerate satellites change 128 -> 40 B/call. Timing is mixed at 54/60 paired
wins and is not an acceptance gate. Append only through
`Append-ContainsWindowMeasurements.ps1`; retain the byte-exact prefix proof.
After full delivery validation, package archive and dependency replay, use
`Verify-ContainsWindowDelivery.ps1` for the final immutable summary. The prior
PERF-004 callback summary remains accepted at SHA256
`FB146FB42DBC62067C3D3D7B8A54778B743C2E351646770DF196D54FB0F68175`.

## Selecting a historical harness

Search the archived runbook by performance ID, method or experiment rather than
reading it end-to-end, for example:

```powershell
rg -n "PERF-010|AnimationThread|TransitionTargetGroup" `
  .codex/session-backups/20260908-before-performance-live-doc-compaction/scripts/performance/README.md
```

Before reusing a historical runner, inspect its current parameters and parser,
source/binary provenance, fixture hash, counter schema and limitations. Always
use fresh IDs and never append a historical CSV again.

## PERF-009 clone-reuse boundary M1

`FWM-GENERIC-PREVIEW-CLONE-BOUNDARY-20260909-M1` is the accepted managed safety
boundary. `GenericPreviewReuseBoundaryTest` changes a custom node's virtual
`Clone` callback without changing the live tree, proving that general cross-call
clone reuse cannot be keyed only by a tree epoch. Five isolated Release
processes record 105 count rows; all 1,500 controlled failures/callbacks and
30,500 identity/registration checks agree. Production is unchanged. Use the
snapshot's `Verify-GenericPreviewCloneBoundary.ps1` and append receipt as the
immutable protocol; do not rerun or append the experiment.

## PERF-018 forced-GC pause harness

The accepted IDs are:

```text
Mechanical baseline: FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-R0R2
Candidate:           FWM-DISPLAY-REMOVAL-GC-PAUSE-20260909-C1R3
Experiment:          EXP-DISPLAY-018-GC-PAUSE-C1R1
```

`Prepare-DisplayRemovalGcPause.ps1` derives an isolated baseline from the frozen
candidate source and restores only the historical duplicate/unknown collection
admission. It builds both source trees and proves the old behavior red. The
measurement always runs one shared candidate `FancyWM.Tests.dll`; only
`FancyWM.dll` differs.

`Measure-DisplayRemovalGcPause.ps1` runs five alternating pairs in ten
sequential Release x64 testhost processes. The fixture performs three equal
owner lifetimes for 1, 10 and 50 duplicates with 64 KiB churn/event. Each
admitted callback executes the exact two-pass compacting blocking collection
and finalizer wait from `GCHelper`. `Verify-DisplayRemovalGcPause.ps1` checks all
600 rows, source/binary provenance, exact lifecycle/churn semantics and process
ordering. Append only through `Append-DisplayRemovalGcPauseMeasurements.ps1`.

The accepted result is 960 -> 45 requests and 1,920 -> 90 full-GC passes; all 15
pause comparisons and 14/15 allocation comparisons improve. Pre-final managed
heap is higher in 15/15 candidate comparisons because redundant collections no
longer clear transient churn. Do not report a retained-memory benefit. The C1,
C1R1/R0 and C1R2/R0R1 preflights are preserved failed protocol attempts and
were not appended.

## PERF-010 native dependency and next run

N1 `FWM-ANIMATION-NATIVE-BASELINE-20260908-N1` records WPR/profile availability
and the failed `wpr.exe -start CPU -filemode` command: `0xC5585011`, current token
not elevated, system-performance profiling policy could not be enabled. No raw
ETL was created. The summary and raw command evidence are under `N1/preflight/`;
the summary SHA256 is
`B04F981CDC37B0B57EF8F46CC1A5C617FBEDBBE7800575F6EDEBE2621CAF79C7`.

On 2026-09-10 N2 succeeds with an elevated interactive tracing token. N3 now
captures five sequential Release x64 processes and 120 measured transitions
with owned native HWNDs. V1 retains fractional native-tail milliseconds in a
separately frozen verification of the same capture. All 600 ETW boundaries
match, zero events/buffers are lost and every target reaches its final native
rectangle. Median task/final-native-message times for 50 targets on one owner
thread are 237.8/663.6 ms. This is an instrumented baseline; physical
presentation and full-app effects remain separate requirements.

For a new baseline, use fresh IDs and an elevated interactive session:

```powershell
pwsh -NoProfile -File scripts/performance/Measure-AnimationNative.ps1 `
  -SnapshotId <fresh-native-id>
pwsh -NoProfile -File scripts/performance/Verify-AnimationNative.ps1 `
  -SnapshotId <fresh-native-id>
```

The runner freezes sources, builds Release x64, archives complete binaries,
records CPU/GPU/DesktopComposition plus QPC markers in a named WPR session,
then retains ETL, exported profiles, native-call sidecars, process/environment
receipts, CPU/thread exports and hashes. The process creates non-activating
test windows and closes them automatically. The verifier checks exact source,
binary, profile and raw-data hashes, process isolation, fixed geometry/DPI,
native final rectangles, marker payloads, trace loss and cleanup.

A corrected analysis can preserve an existing capture and report by freezing
the revised verifier separately, then supplying both IDs:

```powershell
pwsh -NoProfile -File scripts/performance/Save-ImplementationSnapshot.ps1 `
  -SnapshotId <fresh-verifier-id>
pwsh -NoProfile -File scripts/performance/Verify-AnimationNative.ps1 `
  -SnapshotId <existing-native-id> -VerifierSnapshotId <fresh-verifier-id>
```

The original N3 report rounded its derived native-tail column to whole
milliseconds through PowerShell overload selection. V1 uses floating-point
`Math.Max`; no source fixture, raw sample or ETL was changed. Development
PILOT1 exposed a separate EventSource manifest-width mismatch, corrected before
N3. See [the complete native report](../../docs/performance/ANIMATION_NATIVE_BASELINE.md).

PERF-008 mutation-boundary M1 is immutable at
`FWM-INVARIANT-MUTATION-BOUNDARY-20260909-M1`. Its five sequential Release
processes record 105 count rows and prove that external `IWindow.Equals` can
change between workspace and engine validations without a tree/state/revision
mutation. Strict verification accepts the safety boundary and rejects broader
result reuse until window identity/equality has an immutable or versioned
contract. Production is unchanged; no A/B speed claim or full C2 applies. The
summary SHA256 is
`C210282521D77257F89D583DE673D1C7F71428BEF78C1AA76FF525E393EC0EBA`.

## PERF-022 operation boundary and provider snapshot

`FWM-RESIZE-OPERATION-SNAPSHOT-BOUNDARY-20260909-M1` is the accepted safety
boundary for `CanResize`/`Resize`. The shared fixture proves exact repeated
property-read order and same-operation mutation; the hypothetical consolidation
is red 4/4, so production is unchanged and no speed claim is made.

The provider A/B uses baseline
`FWM-DISPLAY-PROVIDER-SNAPSHOT-20260909-R0R1`, candidate
`FWM-DISPLAY-PROVIDER-SNAPSHOT-20260909-C1` and experiment
`EXP-DISPLAY-022-PROVIDER-SNAPSHOT-C1`. `Prepare-DisplayProviderSnapshot.ps1`
freezes the common list/culture/concurrency/allocation fixture.
`Measure-DisplayProviderSnapshot.ps1` runs five alternating pairs in ten
sequential Release x64 processes and swaps only `WinMan.Windows.dll`.
`Verify-DisplayProviderSnapshot.ps1` requires 210 rows, old red 6/3, candidate
green 9/9, exact semantic counters and all 15 allocation improvements. Append
only through `Append-DisplayProviderSnapshotMeasurements.ps1`.

The accepted allocation result per getter at 1/10/50 displays is
184/648/1,768 -> 64/136/456 B. C2
`FWM-DISPLAY-PROVIDER-SNAPSHOT-20260909-C2` verifies full Debug/Release builds
and tests, clean dependency replay, the x64 MSIX content and a fresh C1 recheck.
The current ledger has 41,325 rows / 41,782,945 bytes, SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.

## PERF-032 keybindings page tooltip rejection

`Prepare-KeybindingsPageTooltip.ps1` froze baseline
`FWM-KEYBINDINGS-PAGE-TOOLTIP-20260909-R0` and candidate `C1` with the same real
compiled XAML and owned no-HWND WPF fixture. `Measure-KeybindingsPageTooltip.ps1`
ran five alternating pairs in ten sequential Release x64 processes at 1, 10 and
50 bindings per each of eight groups, producing 480 rows. The candidate removed
8/80/400 eager tooltip UI elements, but layout and total allocation comparisons
were each 6 wins / 9 losses and timing was mixed 9/6.

Strict verification rejected the candidate; do not run the append script. The
ledger remains unchanged. Production XAML was restored byte-exact and R1
targeted 2/2 plus affected 65/65 pass in x64 Debug and Release. R1 summary SHA256
is `9D10CE5AA6105B1A3F6FF9914231A2B4FE2AF235A2A5DC75D0EB273A04069B0E`.

PERF-010 N3/V1 establishes the native baseline. S1 closes owner topology /
presentation and V14 passes full-app behavior in Debug/Release plus independent
verification. Full-app CPU/GPU/presentation measurement is next. Any batching
candidate still requires an isolated A/B comparison and delivery gates.


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
The owner-topology continuation is documented in
[ANIMATION_OWNER_TOPOLOGY.md](../../docs/performance/ANIMATION_OWNER_TOPOLOGY.md).
`Measure-AnimationNative.ps1 -SnapshotId <fresh-ID> -OwnerLimit 1|5|50` pins
System32 WPR and replays the exact N3 exported CPU/GPU/DesktopComposition
profile; the current built-in export is retained separately because its
StackCaching setting varied. Before every tracing attempt, retain the complete
`rg --files --hidden --no-ignore` inventory and sorted ETL sizes/SHA256; inspect
WPR status and never interfere with an existing recording. Run cases sequentially.

`Verify-AnimationTopology.py` validates capture provenance and controls;
`Export-AnimationTopology.py` and `Analyze-AnimationTopology.py` retain offline
CPU/DWM derivations; `Verify-AnimationPresentation.py` checks raw DWM/Dxg
hardware witnesses. None individually accepts the stage. The final stage
verifier requires all three common-fixture cases; S1 passes that gate.
N3/D6 hardware telemetry is not emitted photons or panel-response measurement.

The native fixture now rejects a failing compositor-clock NTSTATUS, records the
original run error and preserves the bounded sidecar prefix. D15 checks the
overflow path. D13/PILOT6 retain their historical 0xC01E0006 failures; later
M7 captures and S1 supersede the presentation-path blocker. Do not overwrite
failed attempts or reinterpret them as successful evidence.

The later S1 owner-topology gate is ACCEPT. B13/V13 resume the existing
full-app behavior stage and expose/correct a missing generic restore
invalidation. Use `Build-AnimationFullAppValidation.py` for a fresh frozen
Debug/Release build, `Run-AnimationFullAppValidation.py` for sequential owned
processes, and `Verify-AnimationFullAppValidation.py` for independent checks.
The runner records production differences from the accepted topology sources;
the restore correctness change is the only such difference in B13/V13/V14.
V13 retains an input visibility failure while native restore and shutdown
pass. V14 uses B13 unchanged and passes both complete Debug/Release processes
and the independent behavior verifier; its receipt SHA256 is
`FE40437B7F95CF86F0794DA81DB0D54660204BD87611BDA92C008C9473A9AB0D`.

```powershell
python scripts/performance/Run-AnimationFullAppValidation.py --snapshot-id FWM-ANIMATION-FULLAPP-20260910-V14 --build-snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-B13 --topology-receipt artifacts/performance/FWM-ANIMATION-OWNER-TOPOLOGY-20260910-S1/verification/stage-verification.json
python scripts/performance/Verify-AnimationFullAppValidation.py --snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-V14 --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-V14/verification/behavior.json
```

These commands are the retained invocation, not reusable output IDs. A future
run needs a fresh ID. The separate `Capture-AnimationFullApp.py` runner and
`Verify-AnimationFullAppMeasurement.py` now prepare/check the existing S1/V14
measurement workload. `FullAppDesktop.py` checks native work areas and owned
HWND DPI before launch and around each process; physical-GPU WMI metadata alone
does not establish those controls. `Test-AnimationFullAppMeasurement.py` passes
23 replay/injected-error cases. The complete ETW success path remains pending.

M1 retains WPR 0xC5585011 with its historical medium token; M2's explicit
`--process-cpu-only` fallback fails at 50 windows on a transient `NoMonitor`
display; M3 rejects RDP/192 DPI/inaccessible input desktop before launch.
M4 was stopped before tracing at the user's request. M5 subsequently captures
five processes and saves ETW without lost events, but the independent verifier
rejects unequal inherited Flex allocations. B15 corrects fixture preparation
with existing node swaps and even Flex allocations before warmups. Its
Debug/Release builds and five-process no-input P2 repeatability gate pass.
V15's interactive guard rejects an overlying RDP client before sending input.
Fresh V16 now passes complete B15 Debug/Release behavior and exact repeatability.
M6 passes independent ETW capture verification: five processes, 120 measured
transitions, all 750 markers, exact geometry and zero lost events/buffers.
Offline CPU/thread derivation and independent xperf cross-check pass; full-frame
hardware presentation remains E2E_PENDING. See
the retained commands and current next action in
[the measurement report](../../docs/performance/ANIMATION_FULLAPP_MEASUREMENT.md).

`Run-AnimationFullAppRepeatability.py` checks a frozen build in five sequential
Release processes without ETW or injected input. The separate
`Audit-AnimationFullAppRepeatability.py` compares every warmup/measured final
rectangle and target index from immutable process evidence. These are fixture
tests and do not constitute accepted performance measurement. Historical
M5/P1/P2 receipts show failure, partial correction and full repeatability.

The offline tools `Analyze-AnimationFullApp.py`,
`Analyze-AnimationFullAppPresentation.py` and
`Verify-AnimationFullAppPresentation.py` prepare scheduling, scoped GPU-counter
and hardware-presentation analysis after a verified capture. M6's CPU success
path is exercised and every one of 460 lifetime thread totals matches xperf.
Raw presentation exposed same-visual surface rebinding and client-coordinate
draws. `Test-AnimationFullAppBindings.py` passes 13 real-trace/negative cases;
the existing 26 geometry cases still pass. The corrected replay finds 2,440
client translation/clip diagnostics but retains the full-frame criterion.
The independent verifier writes E2E_PENDING and exits nonzero for missing
full-frame correlation. It does not treat client coverage as hardware evidence.

The CPU-only option does not relax native desktop or provenance checks and
cannot close ETW/GPU/presentation gaps. Do not retry existing IDs or accept
partial failed-run counters. The next presentation work must establish the
separate DWM non-client visual identity; see [the measurement report](../../docs/performance/ANIMATION_FULLAPP_MEASUREMENT.md).
See [the full-app continuation](../../docs/performance/ANIMATION_FULLAPP_VALIDATION.md).

Frozen D6 (`artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-D6`) retains
the TDH schema/visual-identity audit, the all-120-interval xperf module exporter,
the forty-interval Dispatcher stack exporter and their checked derivations.
These are diagnostic tools with fresh output directories, not accepted A/B
measurements. The visual pilot's P1 rejects RDP/192 DPI before tracing; the
prepared runner now reserves fresh P2 and requires the original local controls.
Its profile adds DWM `FrameVisualizationExtra`, whose hierarchy meaning still
needs verification using raw event IDs. CPU follow-up must resolve unknown
stack addresses before selecting batching. See [D6 commands, receipts and
limits](../../docs/performance/ANIMATION_FULLAPP_MEASUREMENT.md#d6-visual-identity-and-scoped-cpu-diagnostics--2026-09-12).

D7 (`FWM-ANIMATION-FULLAPP-20260912-D7`) adds pinned TraceEvent 3.2.6 tools for
offline CLR/native decoding and separate raw sample/StackWalk readers. Its final
`verify-stack-fragments.py` accepts only exact raw frame chains; missing,
mismatched and unverified chains remain excluded. Use its final receipt rather
than the broader intermediate decoded reports or differing xperf symbol totals.
The shared source and optimization ledger are unchanged. See [the attribution
report and next experiment](../../docs/performance/ANIMATION_FULLAPP_STACK_ATTRIBUTION.md).

## Padding-only overlay reuse — B1/C1/M2

`Prepare-OverlayPaddingReuse.ps1` freezes source/binaries and the common four-test
fixture. B1 uses `-ExpectedFailed 1` for the model-identity assertion; C1 passes
all four. `Measure-OverlayPaddingReuse.ps1` runs five sequential alternating
Release x64 pairs with six warmups/twelve measured updates at 1/10/50 windows.
`Verify-OverlayPaddingReuse.py` independently matches raw TRX and the exact
360-row counter multiset and rehashes both frozen source/binary manifests.

```powershell
pwsh -NoProfile -File scripts/performance/Measure-OverlayPaddingReuse.ps1 `
  -BaselineId FWM-OVERLAY-PADDING-REUSE-20260912-B1 `
  -CandidateId FWM-OVERLAY-PADDING-REUSE-20260912-C1 `
  -ExperimentId <fresh-id>
python scripts/performance/Verify-OverlayPaddingReuse.py `
  artifacts/performance/<fresh-id> --output artifacts/performance/<fresh-id>/independent-verification.json
```

M1 is retained as a runner-cardinality failure (39 expected versus 36 emitted).
M2 verifies 15/15 allocation and elapsed wins with zero candidate window/tab/SVG
recreation. Geometry and managed ownership gates pass. Native performance and
nested-notification WPF visuals remain separate gates; do not append the ledger
from this result. [Scope, failures and receipts](../../docs/performance/OVERLAY_PADDING_REUSE.md).

C2 passes full Debug 2047/160/31 and Release 2103/160/31 plus GUI/x64/MSIX and
dependency replay. `Complete-OverlayPaddingReuseDelivery.ps1` verifies the full
TRX leaves, unchanged measured source, package path/hash/embedded AMD64 DLL,
historical ledger and an independent M2 replay. It writes a fresh immutable
`LOCAL_DELIVERY_PASS_NATIVE_PENDING` receipt and archives the build-only MSIX.

## Nested overlay notifications — correctness follow-up

`Prepare-OverlayNestedNotification.ps1` freezes the final common seven-test
fixture. Baseline B1 uses `-ExpectedFailed 3`; C1 passes 7/7, including 24 new
cases with real compiled hit-testable/non-hit-testable WPF surfaces. Existing
affected Release tests pass 87/87. All IDs in this section have prefix
`FWM-OVERLAY-NESTED-NOTIFICATION-20260912-`.

The padding runner/verifier now support explicit `-BaselineReusesPadding` mode.
It requires a one-file renderer delta, common test source and zero recreation/
subscription additions on both sides. Default mode preserves the original
three-file padding comparison and baseline recreation requirements.

```powershell
pwsh -NoProfile -File scripts/performance/Measure-OverlayPaddingReuse.ps1 `
  -BaselineId FWM-OVERLAY-NESTED-NOTIFICATION-20260912-B1 `
  -CandidateId FWM-OVERLAY-NESTED-NOTIFICATION-20260912-C1 `
  -ExperimentId <fresh-id> -BaselineReusesPadding
python scripts/performance/Verify-OverlayPaddingReuse.py `
  artifacts/performance/<fresh-id> --output artifacts/performance/<fresh-id>/independent-verification.json
```

Nested M1 verifies 360 rows in five pairs with no allocation losses. Timing is
mixed, including a 50-window median increase; do not claim another speedup.
`Complete-OverlayPaddingReuseDelivery.ps1 -NestedNotifications` selects this
stage's candidate, measurement and full-test expectations. No ledger append.
[Cause, cases and evidence](../../docs/performance/OVERLAY_NESTED_NOTIFICATION.md).

Nested C2 passes full Debug 2050/160/31 and Release 2106/160/31 plus both GUI/
x64/MSIX builds, dependency replay, package contents and independent M1 replay.
The completion script writes `NestedNotificationRegression=NO_HWND_VERIFIED`
while preserving native E2E_PENDING and an unchanged optimization ledger.

Full-app B16/P3 preparation is retained. After local admission, V17 passes.
The first native comparison M7 rejects a closed-HWND fixture race; the common
`Program.NativeOwnership.cs` guard now waits for PID-zero nodes without accepting
them or weakening known foreign-owner rejection. Ten native cases pass. Fresh
baseline B18/V18 and candidate B17/V19 pass all native behavior with the common
eleven-file fixture and unchanged shipping source.

`Build-AnimationFullAppValidation.py --baseline-production <baseline-build>`
creates a declared baseline inside a fresh snapshot. It keeps the original
candidate manifest and replaces exactly the three overlay production files.
The baseline behavior runner requires `--live-candidate-build <candidate-build>`
and verifies both source identities and the candidate manifest. Keep workspace
source unchanged between the two builds. The behavior runner still requires
an external fresh desktop check and retains its owned-input guards.

`Measure-OverlayFullAppCpu.py` compares verified baseline/candidate behavior
receipts in five alternating pairs using the existing measurement mode,
two warmups and eight measured transitions per count. It checks native desktop
controls before/after each process, explicitly disables tiering for both,
archives complete binaries, and leaves WPR stopped. The separate
`Verify-OverlayFullAppCpu.py` recomputes raw counters, all geometry, order,
cleanup, paired medians and ownership-drain wait boundaries. M8 verifies 240
measured transitions and 14 CPU wins plus one tie. D8 separately confirms
event 335 absent with the extra keyword enabled and zero trace loss.
[Replay commands, numbers and limits](../../docs/performance/OVERLAY_FULLAPP_CPU.md).
