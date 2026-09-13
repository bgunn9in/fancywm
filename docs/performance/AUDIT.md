# FancyWM performance audit — live summary

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

Earlier checkpoint (2026-09-12):
[`FWM-PERF016-NATIVE-20260912-R1`](NATIVE_OWNER_LIFETIME.md).
Native tests exposed construction failure HWND/subscription retention in
OverlayHost; the two-file rollback correction has common-fixture red/green and
full Debug/Release/C2 delivery evidence. Other native owners and owned partial
graph teardown/crash observations are scoped in the criterion mapping. Browser
passive retention of the last settings window is attributed by an owned dump to
WPF's inactive-view cache; zero weak references is only claimed after normal
binding activity. Whole PERF-016 remains IN_PROGRESS. Full startup writes a global
window-arranging setting and needs a disposable isolated session/VM under the
current constraints. No ledger append, performance claim or whole-stage ACCEPT.

This file is the compact audit entry point. The original 2026-09-05 audit is
preserved byte-for-byte at
`.codex/session-backups/20260908-before-performance-live-doc-compaction/docs/performance/AUDIT.md`
(33,654 bytes, SHA256
`6F5CD1A83393C7945CC5A87CF7CEFE710C24D1B55F83FAACB6E24EC7EAA1E8DB`).
Its baseline findings are historical; current status comes from
`PERFORMANCE_STATUS.md` and `PERFORMANCE_TODO.md`.

## Scope and baseline

The audit covers the main FancyWM application, layouts, theme engine, WinMan
dependencies, tests, CI/package paths and performance harnesses. Historical
baseline commit is `4fb943be3ee3768bb6de42b16b0a22bdbafa2e0b`, tree
`48c574b1abd3adb858942bd8c6a0f4d9fb3e39b7`, snapshot
`FWM-PERF-20260905-4fb943b-tree48c574b-u3`.

Managed tests and microbenchmarks establish only their stated code paths. Native
window/COM/WPF/DPI behavior, input cadence, compositor presentation, process
CPU/GPU, energy and idle wakeups require native or end-to-end evidence.

## Current high-value boundaries

| ID | Area | Established boundary | Remaining proof |
|---|---|---|---|
| PERF-002 | Discovery/refresh | Owned snapshots reduce managed allocation while preserving reconciliation | Complete provider invalidation; COM/latency/WPR |
| PERF-004 | Layout scheduling | Cached instance callback removes 64 B/completed pass; order, eligibility, retry and shutdown tests pass | Native Dispatcher cadence, presentation latency and process CPU |
| PERF-008 | M+S lifecycle/workspace | Several bounded scans/materializations are accepted | Mutation epoch, fresh `MinSize`, identity/registration and rollback before broad reuse |
| PERF-009 | Generic move/resize | Recursion and public stack guards are accepted | Broader clone/transaction reuse; native drag cadence/DPI |
| PERF-010 | Animation | S1, M8 CPU and D9 ownership retained; B18/B17 Debug/Release actual 96/120-DPI behavior and original display restoration PASS | Complete GPU execution method and five-pair A/B; existing non-client/full-frame hardware identity |
| PERF-030 | Focus/UIA | Rejected/admitted/concurrent allocation, contention and managed retention stages are measured | Native timeout, late focus, handle types, production scheduling and CPU/GPU |
| PERF-032 | Settings keybindings | No-HWND 8/80/400-binding profile rejects a tooltip representation change without stable allocation benefit | Interactive virtualization, focus, first presentation and native UI |

Other open IDs and their exact acceptance gaps are listed once in
`PERFORMANCE_TODO.md`.

## Architectural constraints

- `AnimationThread` blocks correctly while idle. Active work is the target:
  per-window jobs, per-frame updates and native position reads/writes.
- Event-driven discovery cannot replace the 250 ms reconciliation sweep until
  every relevant provider transition has a demonstrated invalidation path and
  missed/out-of-order event recovery.
- Layout/M+S optimizations must preserve object identity, registration, callback
  ordering, `MinSize` freshness, lock ordering, preflight and rollback.
- WPF visual-tree, DWM/GPU, native hook and COM claims require traces or real
  integration evidence; managed counts are useful proxies but not substitutes.
- Mixed timing data is reported as mixed. Allocations/counts and real
  CPU/GPU/latency are stated separately.

## Required experiment discipline

1. Freeze the exact old source with the final common fixture and runner.
2. Add a regression that fails against old behavior and passes the candidate.
3. Keep inputs, process mode, warmup, iterations, binaries and fixture equal.
4. For a performance claim, run at least five alternating A/B pairs in isolated,
   non-overlapping processes and retain raw logs/TRX plus binary provenance.
5. Treat elapsed time as descriptive unless the chosen gate and data support the
   claim; never infer native CPU/GPU or presentation latency from managed time.
6. Append accepted rows once, after strict verification, while proving the exact
   historical ledger prefix and appended suffix.
7. Scale the final Debug/Release, GUI, RID-aware x64/MSIX and dependency replay
   gate to the change. Build packages only; do not install or publish them.

## Next native measurement

PERF-010 N1 (`FWM-ANIMATION-NATIVE-BASELINE-20260908-N1`) found WPR/WPA and
local profiles, but `wpr.exe -start CPU -filemode` failed with `0xC5585011`
while enabling system-performance profiling policy with a non-elevated token.
That historical run created no raw ETL. On 2026-09-10, fresh N2
(`FWM-ANIMATION-NATIVE-BASELINE-20260910-N2`) succeeds with an elevated
interactive token and retains a CPU smoke ETL. The native stage can proceed.

N3/V1 captures the production animation methods with five sequential Release
x64 processes and 120 measured 1/10/50-HWND transitions. All 600 ETW boundary
markers match and no events or buffers are lost. With 50 windows sharing one
owner thread, median task completion is 237.8 ms and the final native message
arrives at 663.6 ms. This baseline retains the actual profiles, commands,
environment, ETL, CPU/thread exports and source/binary hashes.

The next stage varies HWND owner topology and examines Dwm-Core correlation.
Native geometry application is distinct from physical presentation. Full-app
behavior and an isolated A/B comparison remain necessary before accepting an
animation batching change. See [the native report](ANIMATION_NATIVE_BASELINE.md).


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
The 2026-09-10 owner continuation retains N3/V1 unchanged. D2 supplies a
reproducible CSwitch derivation; D6 independently links all 2,440 N3 final
target geometries to OS/driver hardware flip completion (no photon claim).
These are components of the existing owner topology / presentation stage,
which remains IN_PROGRESS. Current CPU tracing works. D13/PILOT6 demonstrate
STATUS_GRAPHICS_PRESENT_OCCLUDED from the compositor clock, including after a
scoped display power request. Comparable final topology captures and subsequent
full-app behavior/CPU/GPU/presentation require that presentation path to recover.
See [the criterion-to-evidence mapping](ANIMATION_OWNER_TOPOLOGY.md).

Later interrupted-session evidence supersedes the preceding blocker: owner
topology / presentation passed S1 with 7,320 hardware witnesses. Full-app V12
then exposed a generic restore invalidation defect. The continuation's
correctness fix passes 8/8 regressions (old 4/8), full Debug 2043/2043 and
Release 2099/2099, Layouts/ThemeEngine and both delivery builds. V13 native
restore/shutdown pass, but a foreground game obstructs the input target.
Fresh V14 reuses B13 unchanged and passes both complete Debug/Release processes
and independent behavior verification at 96 DPI. The next stage is full-app
ETW, attributed CPU/GPU and hardware-presentation measurement. Whole PERF-010
remains IN_PROGRESS. No optimization ledger append or speedup claim applies.
[Full-app evidence and next action](ANIMATION_FULLAPP_VALIDATION.md).

Current-session measurement attempts M1/M2/M3 demonstrate a different token and
desktop: kernel tracing fails with 0xC5585011; native RDP/WinDisc geometry and
192 DPI differ from the validated 96-DPI local display. New native preflight
guards reject this environment before further app/trace launches. The prepared
capture/verification tooling passes 23 check cases, while its successful full
ETW path remains pending. [Measurement continuation](ANIMATION_FULLAPP_MEASUREMENT.md).

The later B15/V16/M6 continuation resolves those historical behavior and capture
gates. D6 scopes module samples to all 120 measured transitions and stack hits
to forty 50-window Dispatcher intervals; 51.134% of those stack hits still lack
function attribution. Its 782-schema visual audit identifies an unrecorded
DWM extra event for a diagnostic probe, whose meaning remains unverified.
Current RDP/192-DPI controls reject the probe before app/trace launch.
Production batching and full-frame hardware proof remain pending; see the
[current diagnostics](ANIMATION_FULLAPP_MEASUREMENT.md#d6-visual-identity-and-scoped-cpu-diagnostics--2026-09-12).

D7 supersedes the unresolved Dispatcher attribution with managed/native names
and a strict raw frame check: 45,056 verified chains out of 47,929 selected CPU
samples. Repeated overlay construction is present across all five processes.
The next bounded experiment is padding-only overlay reuse, preserving obsolete
pass cancellation, geometry and recovery. No production optimization or hardware
presentation claim is accepted. [D7 evidence](ANIMATION_FULLAPP_STACK_ATTRIBUTION.md).

Padding-only overlay reuse now has a three-file candidate, common B1/C1 tests
(3/4 -> 4/4) and independently verified five-pair M2 managed WPF measurements.
All 15 allocation/elapsed comparisons improve, with zero recreated candidate
window/tab/SVG elements. Active/failed passes and height/scaling/retry retain
complete invalidation. Native full-app behavior/CPU and a retained pre-existing
WPF nested-notification visual defect remain open; the ledger is unchanged.
[Candidate evidence](OVERLAY_PADDING_REUSE.md).

The nested-notification correctness stage now resolves that no-HWND visual
defect by deferring full invalidation until current collection listeners finish.
Shared B1/C1 tests go 4/7 -> 7/7 across 24 new cases, affected Release is 87/87,
and both overlay surfaces/cleanup/retry pass. Five M1 pairs preserve zero padding
recreation; allocation has no losses, elapsed time is mixed with a 3.86% median
increase at 50 windows. Native full-app behavior/performance remains pending.
[Correction scope and tradeoff](OVERLAY_NESTED_NOTIFICATION.md).

B16/P3 prepares the unchanged corrected candidate and common B15 full-app
fixture. Four builds and source/binary provenance checks pass; old V16 is
independently reverified. P3 rejects the current RDP / 192-DPI desktop before
app launch, input or trace start. This is neither candidate behavior evidence
nor a native speedup result. Require fresh matching local controls before V17
and CPU comparison; keep D6's border-identity gate and the ledger unchanged.
[Preparation receipts](ANIMATION_FULLAPP_VALIDATION.md#corrected-candidate-b16--preparation-p3--2026-09-12).

The subsequent local continuation passes candidate V17. M7's closed-HWND
fixture failure remains rejected; its shared correction waits for PID-zero
nodes without accepting them or relaxing known foreign-owner rejection. Ten
native cases and fresh baseline B18/V18/candidate B17/V19 pass. M8 verifies
five alternating pairs, 240 measured transitions and 14 CPU wins plus one tie;
11 ownership-drain waits lie outside all transition intervals. Shipping
production is unchanged. D8's enabled extra keyword yields no event 335 in
either independent decoder, with zero trace loss. This closes the attempted
pilot, not the missing visual-identity or full-frame proof. No ledger append
or whole-ID status change applies. [Native comparison and limits](OVERLAY_FULLAPP_CPU.md).

D9 independently verifies M6 GPU ownership through device/context/hardware-queue
lifetimes, including real pointer reuse. Two payload decoders agree on 89,585
app-owned events; 77,967 queue records and 518,683 fields match retained xperf.
The audit excludes 9,484 misleading header-PID matches within measured intervals,
checks DMA/fence pairs, and passes eight rejection/reuse checks. GPU execution
time and candidate GPU comparison remain pending. [D9 evidence](ANIMATION_FULLAPP_GPU.md).
