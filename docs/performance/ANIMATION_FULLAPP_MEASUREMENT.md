# PERF-010 full-app measurement continuation — 2026-09-12

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

The full-app measurement stage remains **IN_PROGRESS**. Fresh B15/V16 behavior
and repeatability pass in Debug/Release. M6 now passes independent ETW capture
verification: five sequential Release processes, 120 measured transitions,
2,440 final target geometries and 750 matching markers, with zero lost events
or buffers. CPU/thread derivation and independent xperf cross-check pass.
Full visible-frame and hardware presentation remain **E2E_PENDING**: raw events
expose client-coordinate draws and separately composed non-client visuals.
M6 used unchanged B15 production. The later overlay candidate now passes native
behavior and a separate five-pair process-CPU comparison: M8 verifies 240 measured
transitions and 14 CPU wins plus one tie. D8's extra-event identity diagnostic
still leaves the nonclient visual edge unproven. [Current candidate results](OVERLAY_FULLAPP_CPU.md).
No optimization-ledger append applies.

D9 independently verifies GPU object ownership and packet joins for this M6
baseline. It resolves 89,585 app-owned events and cross-checks 77,967 queue events
against xperf. GPU busy time remains unmeasured; packet spans and shared DWM
counters are not substituted for it. [GPU diagnostic](ANIMATION_FULLAPP_GPU.md).

## Implemented tools

- `Capture-AnimationFullApp.py` re-verifies the selected behavior receipt,
  checks S1 and frozen source/binary correspondence, then prepares five
  sequential Release processes. Each runs two warmups and eight measured
  transitions at each 1/10/50-target count using the existing `measurement` mode.
  The tracing path replays the exact archived CPU/GPU/DesktopComposition XML
  and the new `AnimationFullApp.wprp` marker provider. It retains the complete
  file inventory and old ETL sizes/hashes before tracing. Only its own WPR
  instance and child processes are stopped, with failures and cleanup retained.
- `FullAppDesktop.py` queries the executing/input desktop, remote-session flag,
  native monitor rectangles/work areas and actual HWND DPI. Its temporary hidden
  DPI-probe windows are destroyed. Controls are checked before tracing and
  before/after every process. WMI GPU resolution is retained as metadata but is
  insufficient to establish the active desktop's geometry or DPI.
- `Verify-AnimationFullAppMeasurement.py` checks source/binary and raw-evidence
  hashes, process sequencing, exact native geometry, HWND owner identity,
  position messages, quantized process CPU, cleanup and (when present) all 750
  ETW boundary markers with zero lost events/buffers. It keeps layout-idle,
  native-message, native-rectangle and DwmFlush boundaries distinct.
- `--process-cpu-only` is an explicit fallback which never starts ETW. Even a
  successful fallback remains incomplete for scheduling/GPU/presentation. The
  native display controls apply equally to this fallback.

## Retained attempts

All IDs below have prefix `FWM-ANIMATION-FULLAPP-20260910-`.

| Attempt | Observed result | Interpretation |
|---|---|---|
| M1 | V14 reverification and source/profile checks pass. WPR start returns `0xC5585011`, stating that it cannot enable system-performance profiling policy. No app process or ETL is created; WPR remains inactive. | Current token is medium integrity, with no `SeSystemProfilePrivilege`; previous elevated-session N2 availability does not describe this session. |
| M2 | Explicit process-CPU-only path starts one B13 Release process. It reaches 20 transitions at 1/10 targets, then times out waiting for 50 managed windows. The app logs `Win32Display { NoMonitor }` and floating fallback. Native target DPI is 192, with roughly 1024x768 fallback geometry. Application termination and owned-target cleanup pass. | Retained failed/incomparable measurement; partial counters are not accepted or appended. This does not establish a production regression. |
| M3 | The new native preflight rejects before tracing or application launch. It observes RDP, device `WinDisc`, 2880x1800 monitor, 2880x1704 work area, 192 DPI, and input-desktop access error 5. The owned DPI probe is destroyed. | Native controls differ from accepted local 3440x1440 / work area 3440x1392 / 96 DPI. The desktop also changed between M2 and M3. |

WMI reports the physical GPU as 3440x1440 at 144 Hz in M1/M2 despite the remote
desktop's different native geometry. The final runner rejects this mismatch
using native observations, rather than starting another long/incomparable run.

Capture receipt SHA256 values:

| Attempt | SHA256 of `capture-run.json` |
|---|---|
| M1 | `97D26A1FDC28320245D2FE6E5792885418F9CB6074D9A90BD471222492C4D59C` |
| M2 | `DCD9885A267F2B8FA570F610C004D19054677ADCFAF957DFE094FCE4DBAEB988` |
| M3 | `FD925B76881A645B83B430429D476FB112C3C648FB5762F74A9DD5CB0C6AD3B6` |

## Validation and limits

`Test-AnimationFullAppMeasurement.py` passes 23 cases: an in-memory projection
of real V14 Debug/Release transition/message records, seven injected failures
per configuration, the actual retained M3 desktop rejection and six invalid
desktop/probe cases. These are test fixtures, not new measurement rows; V14
files remain byte-identical. Initial development checks exposed two differences
between behavior and measurement fixtures: validation inserts scenario IDs,
and a warmup can set an already-current padding. The final measurement CLI
still requires scenarios 1..30 and native move/resize evidence for every
measured transition; tests do not relabel validation as a real capture.

Final scripts, test receipt, failed-capture rejection checks and integrity review
are frozen under `FWM-ANIMATION-FULLAPP-20260910-D1`. Root and winman-windows
whitespace checks pass. No C# source, test, dependency revision or package input
changed, so the previous Debug/Release build/test evidence is retained.

At the historical D1 checkpoint, the complete measurement verifier had not yet
passed a successful five-process measurement capture. Its ETW success path and
the runner's successful WPR stop/export path were unexercised. CPU/thread
derivation and hardware-presentation adaptation for full-app HWNDs are still
pending. Native target frames include non-client/invisible margins; future
DWM correlation must use the recorded native/extended/client rectangles and
the target process identity, not assume the original borderless native fixture.

## Resumed capture and fixture correction — 2026-09-11

M4 (`FWM-ANIMATION-FULLAPP-20260910-M4`) was stopped at the user's request
during preflight, before tracing or application launch. Its source snapshot,
inventory and `user-stop.json` remain intact.

After the user resumed work, **FWM-ANIMATION-FULLAPP-20260911-M5** replayed B13
on the matching elevated local desktop. All five processes completed 120
measured transitions. Applications and target processes exited; owned windows
were destroyed. WPR stop/export succeeded, retaining a 9,645,850,624-byte ETL
covering 165.535 seconds, with zero lost buffers and events.

The independent capture verifier rejects M5 at the unchanged cross-process
geometry assertion. Callback timing during simultaneous window discovery leaves
different inherited Flex allocations. Seven process/count groups differ from
run 1; every observed final native rectangle still matches its own computed
layout. M5 is retained diagnostic evidence, not an accepted comparable workload.
No process CPU, GPU or speedup result is accepted from it.

Fixture preparation now uses production `TilingNode.Swap` to order the existing
window nodes by target index and `SplitPanelNode.DistributeChildrenEvenly` to
reset inherited allocations under the existing backend lock. It preserves the
real tree, node identities and registration, then invokes the existing layout
invalidation and waits for final native geometry before any warmup or timing
boundary. No production implementation changed.

| Evidence | Result |
|---|---|
| B14 | Host/targets build in Debug and Release; initial preparation resets allocations only. |
| V15 | Native restoration, concurrent settings and shutdown pass, but the interactive guard rejects a covering `mstsc.exe` window before input; this is not a complete behavior PASS. |
| P1 | Five native processes pass individually; two process/count groups reveal remaining target-index permutations. No ETW or input. |
| B15 | Adds target ordering through existing node swaps; all four host/target Debug/Release builds pass. |
| P2 | Five sequential Release processes pass: 60 transitions including warmups, 30 measured-mode transitions, exact geometry in every group, cleanup and independent repeatability audit. This is a preparation test, not performance measurement acceptance. |

These IDs have prefix `FWM-ANIMATION-FULLAPP-20260911-`. Exact old/new comparison
receipts are in D3's `verification/m5-repeatability.json`,
`p1-repeatability.json` and `p2-repeatability.json`. The stricter original
measurement assertion was not relaxed. B13/V14 remain immutable historical
behavior evidence; B15 must obtain its own complete behavior receipt.

New offline tools separate app/Dispatcher/target CSwitch occupancy, scoped
PresentMon counters and DWM/Dxg presentation verification. They retain the
distinction between native, visible extended-frame and client rectangles.
Eight scheduling checks, 26 geometry/identity/coverage checks and the existing
23 measurement checks pass. Their raw-trace success path is still pending;
generated payloads used by the tests are not presentation evidence.

The active input desktop is accessible and local, but the foreground RDP client
window covers the test point. The user has been asked to minimize it. No foreign
window was moved or closed. WPR is inactive and no owned test process remains.
Final sources and integrity review are retained in D4. Whole PERF-010 remains
IN_PROGRESS; totals are 25 IMPLEMENTED / 12 IN_PROGRESS, with no ledger append.

## V16 / M6 retained capture commands

V16 and M6 below have now completed and must not be reused as output IDs.
The elevated local desktop matched 3440x1440 / 3440x1392 / 96 DPI; the owned
interactive targets were unobscured. Both V16 configurations and the independent
behavior/repeatability checks pass. M6 retains an 11,118,051,328-byte ETL over
162.6549059 seconds. WPR stop and inactive status, application termination,
target-process exit and owned-window cleanup all pass. The strict capture
verifier and a separate repeatability audit confirm exact geometry across all
150 transitions, including 30 warmups. No prior failure was relaxed or replaced.

Verified process CPU medians are 62.5 / 468.75 / 2328.125 ms for 1/10/50 targets.
These are quantized process totals over settings submission through the observed
DwmFlush boundary and include layout, overlays and harness observations.
They establish no speedup or function-level CPU attribution. The completed
offline analysis and remaining presentation action are recorded below.

Retained commands:

```powershell
python -B scripts/performance/Run-AnimationFullAppValidation.py --snapshot-id FWM-ANIMATION-FULLAPP-20260911-V16 --build-snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-B15 --topology-receipt artifacts/performance/FWM-ANIMATION-OWNER-TOPOLOGY-20260910-S1/verification/stage-verification.json
python -B scripts/performance/Verify-AnimationFullAppValidation.py --snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-V16 --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-V16/verification/behavior.json
python -B scripts/performance/Audit-AnimationFullAppRepeatability.py --snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-V16 --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-V16/verification/repeatability.json
python -B scripts/performance/Capture-AnimationFullApp.py --snapshot-id FWM-ANIMATION-FULLAPP-20260911-M6 --build-snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-B15 --behavior-receipt artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-V16/verification/behavior.json --topology-receipt artifacts/performance/FWM-ANIMATION-OWNER-TOPOLOGY-20260910-S1/verification/stage-verification.json
python -B scripts/performance/Verify-AnimationFullAppMeasurement.py --snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6 --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/verification/capture
```

## M6 CPU/thread attribution — verified

`Export-AnimationTopology.py` retains the selected events plus the SHA256 of
the complete 50,475,996,061-byte xperf output stream. The original ETL is
11,118,051,328 bytes, SHA256
`4C233BF44DD9605190602F43E7F5445EC6CA06DD059345284D703B8C50D907FA`.
Both xperf and the pinned PresentMon 2.5.1 exporter exit zero.

`Analyze-AnimationFullApp.py` pairs 7,329,150 CSwitch events without missing,
duplicate or mismatched-CPU intervals for the ten app/target process identities
and 460 threads. The separate `M6/analysis/check-scheduling.py` computes signed
timestamp sums and compares complete thread/process lifetimes with xperf's
independent `cswitch -process -thread` totals. All 460 threads and ten process
totals match within the explicit microsecond export precision bound. This
cross-check includes startup, warmups and cleanup; transition rows separately
clip every interval to the five recorded QPC boundaries.

| Median per measured transition, ms | 1 window | 10 windows | 50 windows |
|---|---:|---:|---:|
| Quantized app process CPU | 62.500 | 468.750 | 2328.125 |
| Scheduled app occupancy | 65.890 | 467.490 | 2373.675 |
| Dispatcher occupancy | 32.789 | 293.105 | 1201.903 |
| Separate target-process occupancy | 20.005 | 50.335 | 206.583 |
| Native verification after observed layout idle | 0.044 | 0.101 | 0.254 |

Scheduled occupancy includes interrupt time. It is distinct from sampled CPU
and from the quantized process counter. The measurement includes settings,
layout, overlays and harness observations, so these rows do not isolate an
animation function. Lifetime sampled-module weights include JIT/startup and
cannot establish a transition-level bottleneck.

PresentMon provides shared-DWM GPU-active counters for all 120 transition
intervals: median sums 4.990 / 8.537 / 19.640 ms at 1/10/50 windows. No app or
target presents occur in those scoped exports; their attributed GPU counters
remain NOT_MEASURED. Shared compositor activity is not FancyWM GPU usage.

Receipts (M6 is `FWM-ANIMATION-FULLAPP-20260911-M6`):

| Receipt | SHA256 |
|---|---|
| `verification/capture/capture-verification.json` | `0A0B5EF44875941566DE171FEB4C4452AC242EFCBEDE5C100F27C7E2B2B0F8F9` |
| `analysis/cpu/derivation.json` | `A11F841D748708DF6D6BD84E7FB2E760874DA973BEEF84EDC8AF767335B06A6B` |
| `verification/scheduling-crosscheck.json` | `A121BE569813C2B8643FD877D475757471BD2137F421691B2B2A1B6F5AC1B908` |

## Raw presentation boundary and binding correction

The original `analysis/presentation` returns zero full-frame correlations;
the independent verifier rejects before hardware analysis. Both that output
and `verification/presentation/failure.json` remain retained.

The real trace exposes repeated `BIND_GDISPRITEBITMAP_FIRST_TOKEN` events for
the same HWND/visual during resize. The old predicate required the latest
binding to precede the first warmup, discarding valid later draws. The corrected
analysis and independent verifier preserve continuous binding histories, require
an anchor after the owned alive observation and before warmups, and reject
unknown sprites, changed HWNDs, stale associations, late-only anchors, ambiguous
lifetimes and incorrect attributed owners. A fresh pre-edit snapshot is retained
as `FWM-ANIMATION-FULLAPP-20260912-R1`; its `development/*-tests-final.json`
records 13 real-trace/negative binding cases and all 26 existing geometry cases.

Reanalysis in `analysis/presentation-r1` retains 46,911 binding-qualified draws.
All 2,440 final targets have exact recorded client-coordinate translations and
clip coverage. Those are diagnostic observations, not a full-frame proof.
For run 1, scenario 3, the native origin is (6,12), client origin is (13,19),
and the bound draw translates to (13,19). Its clip covers the client but excludes
parts of the recorded visible frame. DWM-owned border/header sprite draws are
separate. Their ownership cannot be inferred merely from nearby coordinates.

The full-frame geometry criterion is unchanged. The independent verifier now
retains an explicit `E2E_PENDING` receipt and exits nonzero for incomplete
coverage. It does not enter DXG hardware verification or report client coverage
as hardware display. The [pending receipt](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/verification/presentation-r1/presentation-verification.json)
has SHA256 `50614E25D659E8364293411F6137588157CA2201F7FFB23C35457A7806C91E19`.

Retained offline commands (all output paths are already used):

```powershell
python -B scripts/performance/Export-AnimationTopology.py --etl artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/native/fullapp.etl --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/exports --presentmon artifacts/performance/FWM-ANIMATION-OWNER-TOPOLOGY-20260910-D1/inputs/presentmon/PresentMon-2.5.1-x64.exe
python -B scripts/performance/Analyze-AnimationFullApp.py --capture-receipt artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/verification/capture/capture-verification.json --exports artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/exports --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/cpu
python -B artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/check-scheduling.py
python -B scripts/performance/Analyze-AnimationFullAppPresentation.py --cpu-derivation artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/cpu --exports artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/exports --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/presentation-r1
python -B scripts/performance/Verify-AnimationFullAppPresentation.py --snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6 --derivation artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/presentation-r1 --dxg-events artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/analysis/exports/dxg-events.csv --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-M6/verification/presentation-r1
```

## D6 visual identity and scoped CPU diagnostics — 2026-09-12

Fresh `FWM-ANIMATION-FULLAPP-20260912-D6` preserves the pre-edit source and all
diagnostic scripts, failed probes, raw exports and derivations. M6 is unchanged.

The installed TDH manifest query reads 782 event schemas from Dwm-Core,
DirectComposition and Win32k, retaining the raw schema buffers. The M6 example
parent `0x000001ce5499a0c0` appears in seven resource mappings, twelve client
bindings and 87 dirty-rectangle events across the trace; none is a drawing
context state or render-content event for that parent. These pointer references
do not establish lifetimes or a parent-to-border edge. Win32k's 307,288
`DCompCommandType` events contain type/status only, and its 246,165
`DCompCommandsInBatch` events contain counts only. Re-exporting those records
cannot recover command operands.

Dwm event 335 shares the render-content task name with event 333, has no
declared payload, and requires `FrameVisualizationExtra` (0x40000), absent from
M6's 0x1ffff mask. Its possible traversal-boundary meaning remains a hypothesis.
The prepared `VisualIdentity.wprp` passes `wpr -profiledetails` and adds that
keyword, Win32k resource mapping and DirectComposition basic events. Its dumper
retains raw event IDs to distinguish identically named events. It is a
diagnostic profile, with no CPU/GPU performance acceptance.

`pilot-P1` is rejected before tracing or launching the app: the current desktop
is RDP, 2880x1800 / work area 2880x1704 / 192 DPI, although its Default input
desktop is accessible. It does not match the local 96-DPI controls. No new ETL
is produced. The prepared runner now targets fresh `pilot-P2` and correctly
requires enabled ETW in native verification; its capture success path is unrun.

Offline xperf profile actions now cover **all 120 measured app intervals**,
using `StartQpc` through `DwmFlushedQpc`. Integer-microsecond range arguments
round inward by less than one microsecond per boundary. A separate two-interval
probe matches every parsed process/module row, including duplicate counts;
raw row order varies. Every accumulated module weight is bounded by its
independent lifetime export total. The original fractional-range rejection and
the subsequent byte-order comparison failure remain retained.

| App-process JIT sample share | 1 window | 10 windows | 50 windows |
|---|---:|---:|---:|
| Share of summed sample weights | 29.353% | 12.039% | 24.556% |
| Median per-transition share | 10.725% | 11.041% | 24.107% |

These are instruction-pointer sample weights, not occupancy or isolated
animation CPU. Two fixture warmups do not eliminate JIT from the measured
workload. The forty 50-window Dispatcher intervals separately contain 47,567
exclusive sampled-stack hits. Module and function exclusive totals match for
every interval: 51.134% unresolved, 24.155% CoreCLR, 17.075% kernel and 28 hits
(0.059%) attributed to clrjit. Unresolved addresses prevent a function-level
bottleneck or absence-of-JIT claim. Stack hit counts and weighted profile
samples must not be numerically equated. The initial empty export used an
incorrect event-name filter; retained `dispatcher-stacks-r1` uses `Sample.*Prof`.

Receipts relative to D6:

| Receipt | SHA256 |
|---|---|
| `visual-audit/audit.json` | `D34FD4934F018EDB604DF5F5D0A319A3DD9C3A417C3D06D950BB78BCF3DCEA56` |
| `sampled-analysis-r1/derivation.json` | `219CABA3F4437F3C521D992453F440DB7AA6025303CD7F5E7158DCEBFFA0B8A8` |
| `dispatcher-analysis-r1/derivation.json` | `3D19B88420E0DD7D45533975D169076F5B4B9E4559229BABA922704AA2998E0A` |

## Next exact action

The subsequent [D7 stack attribution](ANIMATION_FULLAPP_STACK_ATTRIBUTION.md)
uses existing CLR metadata and matching native symbols. All 47,929 scoped raw
samples match; 45,056 full frame chains independently match raw StackWalk events.
The final subset includes 10,855 `TilingWindow` construction samples across all
five processes. The initial symbol-enabled xperf probe has inconsistent totals
and remains rejected. Use D7's explicit verified subset for function attribution.

The padding-only overlay reuse candidate now passes the shared 1/10/50-node
fixture: baseline 3/4 -> candidate 4/4. Five alternating pairs verify 360 rows
and 15/15 managed allocation/elapsed wins. The three-file candidate retains
full invalidation for active/failed updates, settings retries and height/scaling
changes. Native full-app behavior and CPU comparison are the next gate;
the baseline nested-WPF-notification defect is now corrected and verified in
both no-HWND surfaces through the [subsequent correctness stage](OVERLAY_NESTED_NOTIFICATION.md).
Native behavior/presentation remains pending. [Padding scope and receipts](OVERLAY_PADDING_REUSE.md).

P4 obtains matching local controls and B16/V17 passes. After M7's rejected
closed-HWND fixture race, the common ownership guard correction passes ten
native cases; B18/V18 baseline and B17/V19 candidate pass all native behavior.
M8 verifies five alternating pairs with exact geometry/cleanup; median CPU at
50 windows is 1,500 -> 210.9375 ms. The source candidate remains nested C1/C2.
[Native comparison, rejected attempt and receipts](OVERLAY_FULLAPP_CPU.md).

For presentation, D8 runs a fresh B17 candidate pilot with the unchanged D6
extra-event profile. It verifies three measured/six warmup transitions, 45
markers, zero loss and cleanup. xperf and a separate TraceEvent reader agree:
22,601 event-333 records and zero event-335 records despite keyword 0x40000.
The possible traversal boundary remains unobserved. Further work must establish
the parent-to-nonclient visual edge independently; do not infer hierarchy from
adjacent render events. Full-frame/hardware presentation remains E2E_PENDING.
Original B15/M6 and unused D6 pilot-P2, submodule revisions, optimization ledger
and whole-ID counts remain unchanged.
