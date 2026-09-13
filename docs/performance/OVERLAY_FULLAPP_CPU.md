# Overlay reuse: native behavior and CPU comparison — 2026-09-12

The corrected candidate passes native Debug/Release behavior and a five-pair
process-CPU comparison against the production baseline. At 50 owned windows,
median CPU per controlled padding transition falls from 1,500 to 210.9375 ms.
Across 1/10/50 windows there are 14 CPU wins, one tie and no losses. This measures
padding reuse and the nested-notification correction together; it does not
isolate the correction's cost or establish hardware presentation.

## Native admission and behavior

After the user disconnected RDP, the session remained disconnected on `WinDisc`
at 192 DPI with input-desktop access error 5. P4 verified that the current user's
session was disconnected and the console had no user, then transferred only
that session to the console. Fresh checks observe local 3440x1440, work area
3440x1392, 96 DPI and accessible `Default` execution/input desktops. No display
settings or other user sessions were changed. The initial case-sensitive WTS
station-name lookup failed before transfer and is retained alongside the
successful case-insensitive lookup.

B16/V17 first passes the unchanged B15 fixture in both configurations. The
subsequent common-fixture correction is validated again in baseline B18/V18
and candidate B17/V19. Each native validation has 12 transitions per configuration,
owned mouse/direct-hotkey focus, 12 concurrent settings requests, minimize/close
recovery, restoration of ten windows, and restoration of 50 saved positions
during pending shutdown. All target processes and owned windows are cleaned up.
Independent geometry audits find no differing rectangles across Debug/Release.

Each configuration retains four first-chance `InvalidOperationException` and
four `InvalidWindowReferenceException` observations during interruption cases.
Final errors are null. These repeated propagation observations are not counts
of distinct failed operations.

## Rejected M7 and the common fixture correction

M7's first baseline process completes the one-window transitions and then fails
the old ownership guard after `close-all`. It is retained and excluded from
the comparison. The guard treated PID zero from a destroyed HWND as a foreign
process before asynchronous removal reached the layout model.

The native regression reproduces that classification with a real hidden HWND,
destroys it and observes PID zero. Ten cases verify owned/unknown/foreign
classification, including an actual foreign shell HWND and mixed arrays in
both orders. The new `WindowOwnersReady` helper waits for unknown owners and
still rejects every known foreign owner. Unknown nodes cannot satisfy an idle
boundary; the existing exact node-count, queue, frozen-window and three-poll
conditions remain required. The fixture records these waits separately from
transition observations. No shipping production source changes in this stage.

Fresh B17 and B18 each build all four Debug/Release host/target archives. B17
production equals the measured nested C1 and delivered C2. B18 is constructed
inside a fresh snapshot from the same candidate source, replacing exactly the
three overlay production files with their verified B15 versions. The original
candidate manifest and transformation receipt are retained; the final build
manifest describes the actual baseline source. All other production/dependency
source and all eleven fixture files match between B17/B18. The live checkout
keeps the candidate throughout.

## Verified M8 comparison

`Measure-OverlayFullAppCpu.py` launches ten sequential Release x64 processes in
AB/BA/AB/BA/AB order. Each process uses two discarded warmups and eight measured
transitions at each target count. Both variants explicitly disable tiered
compilation. Complete frozen archives are used; separately compiled DLL/PDB
differences are listed in provenance, rather than claiming binary identity for
all unchanged-source dependencies. No WPR recording or injected input occurs.

`Verify-OverlayFullAppCpu.py` verifies 7,425 frozen source files, both archive
sets, exact raw CSV/summary correspondence, process order, desktop checks,
ownership, native messages, geometry and cleanup. There are 240 measured and
60 warmup transitions, including 4,880 measured final target rectangles. All
300 transition geometries match by target index. Eleven ownership-drain waits
occur across nine processes, all at zero-window cleanup and outside every
warmup/measured interval. This also observes the corrected fixture race during
the actual comparison. Eight synthetic aggregation checks cover known results,
zero-CPU ties and invalid/missing/duplicate/nonfinite data.

Values below are medians of the five process medians; each process median uses
eight measured transitions at the stated size.

| Windows | Baseline CPU ms | Candidate CPU ms | CPU reduction | Wins / ties / losses |
|---:|---:|---:|---:|---:|
| 1 | 23.4375 | 15.625 | 33.33% | 4 / 1 / 0 |
| 10 | 257.8125 | 54.6875 | 78.79% | 5 / 0 / 0 |
| 50 | 1,500 | 210.9375 | 85.94% | 5 / 0 / 0 |

The corresponding native-geometry observation boundaries are 142.626 → 142.928,
268.054 → 142.678 and 1,406.705 → 269.720 ms. They are not hardware display times.
`Process.TotalProcessorTime` is quantized and covers settings submission through
the `DwmFlush` observation, including application and harness work. The small
one-window CPU difference is close to that quantization scale. No system-wide
CPU utilization, attributed GPU, multi-DPI or general workload claim applies.

## D8 presentation diagnostic

After M8 completed, a separate short B17/V19 diagnostic used D6's unchanged
extra-event profile. It records three measured transitions, six warmups, 61
measured final geometries and 45 exact boundary markers, with zero lost events
or buffers and successful cleanup. The DWM keyword mask is `0x5ffff`, including
`FrameVisualizationExtra` (`0x40000`).

Both xperf statistics/decoded output and a separate pinned TraceEvent 3.2.6
reader count 22,601 event-333 records and zero event-335 records. The expected
additional event does not appear in this trace despite the enabled keyword.
This does not establish a traversal boundary or the missing parent-to-nonclient
visual edge. Do not infer that edge from neighboring render events. The result
is limited to this trace, not all Windows builds/workloads. D6's old B15 pilot
P2 remains unused; D8 used a new candidate pilot P1.

Full-frame/hardware presentation and attributed app GPU remain pending. The
next presentation step needs independently evidenced visual ownership, not
another acceptance claim from the same absent-event hypothesis. M6 and its
independent border/full-frame checks remain unchanged.

D9 subsequently verifies GPU context/queue ownership and packet joins in the
existing M6 baseline, including 77,967 independent xperf queue matches. This
resolves ownership without establishing GPU busy time or a candidate GPU
comparison. [GPU evidence and remaining work](ANIMATION_FULLAPP_GPU.md).

## Receipts and replay

| Receipt | SHA256 |
|---|---|
| [Baseline V18 behavior](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-V18/verification/behavior.json) | `21866918E3ECFF1B5DD1586BA4EF8F010AA2114B6FA7EAF372FE172AC733D84D` |
| [Candidate V19 behavior](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-V19/verification/behavior.json) | `48503F6F33E61F68BC5A29586DC36229AB5A6100D55279E8E66BCC1A05DC71E5` |
| [M8 independent CPU comparison](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-M8/verification/comparison.json) | `18F5CC08527FD00AF9A357F8F9D7F2E93524599D2932E05F975F84EE0D55D463` |
| M8 raw transitions | `7D9D4C105A306C361A3EEF727EC4A4436A796EB1711DF4908EAAADAEBF2C856C` |
| [D8 independent identity audit](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-D8/identity-audit.json) | `1EBA239A4574F64A4C315F1DB0908085CE2D2197B73EAA6BBE31797B98735B8E` |

From the repository root, replay the retained comparison without native launch:

```powershell
python -B scripts/performance/Verify-OverlayFullAppCpu.py --snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-M8 --output <fresh-verification-file>
```

A new native comparison requires fresh local-desktop admission and a fresh ID:

```powershell
python -B scripts/performance/Measure-OverlayFullAppCpu.py --snapshot-id <fresh-id> --baseline-behavior artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-V18/verification/behavior.json --candidate-behavior artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-V19/verification/behavior.json
```

The existing nested C2 full Debug/Release tests and package validation still
cover unchanged shipping source; no package is installed, launched or published.
No optimization-ledger append or whole-ID status change applies. Counts remain
25 IMPLEMENTED / 12 IN_PROGRESS.
