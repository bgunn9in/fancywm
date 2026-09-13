# Full-app GPU ownership diagnostic — 2026-09-12

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

D9 verifies which DxgKrnl packet events belong to FancyWM in the existing M6
trace. It resolves ownership through live device, context and hardware-queue
identities, including pointer reuse. It does **not** establish GPU busy time,
a candidate GPU improvement, or full-frame presentation. PERF-010 remains
IN_PROGRESS; no optimization-ledger rows are appended.

M6 contains the old B15 application baseline. The corrected overlay candidate's
separate M8 process-CPU result is unchanged. This diagnostic runs offline without
launching the native application or starting a tracing session.

## Verified evidence

Artifacts have prefix `FWM-ANIMATION-FULLAPP-20260912-D9`.

| Check | Result |
|---|---:|
| Original ETL events read by each reader | 104,918,867 |
| Selected raw DxgKrnl events | 2,341,442 |
| Installed DxgKrnl schemas retained through TDH | 711 |
| App processes / measured transitions | 5 / 120 |
| Closed app device / context / hardware-queue lifetimes | 5 / 20 / 10 |
| Events attributed to app-owned objects | 89,585 |
| Queue events independently matched to xperf | 77,967 |
| Queue payload fields independently matched | 518,683 |
| Legacy DMA start/interrupt/stop triples | 690 |
| Hardware-queue fence event pairs | 4,739 |
| Matching full-app markers / lost events | 750 / 0 |
| Rejection and identity-reuse checks | 8 passed |

The DMA and fence pairs cover complete process lifetimes, including startup,
warmups and cleanup; they are not measured-transition totals. Both raw readers
agree on every retained event header, owner and payload. All DxgKrnl event
ID/version/opcode counts match the previously retained xperf statistics.
The audit rechecks all 145 original M6 native-evidence files and the ETL SHA256,
plus raw binary schemas and the archived xperf export hash.

## Why event-header PID is insufficient

Across this trace, 18,404 selected events have an app PID in their header but
do not resolve to an app-owned GPU object: 18,399 resolve to another owner and
five have an unresolved context. Those five lie outside all measured intervals.
Within the 120 measured intervals, 9,484 header matches resolve to other owners
and are excluded. A PID-only sum would mix these records into the app result.

Ownership follows the explicit `Device` payload process ID, `hDevice`, `Context`
creation and `HwQueue.ParentDxgHwQueue`. Creation and destruction bound every
app handle. Context-start header PIDs provide another check; capture-state
header PIDs are never taken as owners. The trace reuses one context address
between processes 8016 and 5860, and both decoders preserve separate lifetimes.

The hardware-queue mapping follows the relationships used by pinned PresentMon
2.5.1. That implementation explicitly uses queue-packet duration as a DMA-duration
approximation under hardware scheduling.
[PresentMon GpuTrace.cpp](https://github.com/GameTechDev/PresentMon/blob/v2.5.1/PresentData/GpuTrace.cpp).

## Packet checks and measured counts

Legacy DMA pairs use process, context, submit sequence and submission/completion
ID. All have nonzero sequences, render-buffer packet type, completion interrupts,
no page-fault flags, and subsequent non-preempted stops. All 690 starts have null
`pDmaBuffer`; the audit retains this observation without inventing a buffer
address or execution-start timestamp.

Hardware-queue pairs use process, live queue and progress-fence value. Every pair
joins one render `QueuePacket` by progress fence and one stop by context/submit
sequence. All satisfy the recorded ordering: queue submission, event 450, event
451, queue stop. Status is zero; no matched stop reports preemption or timeout.
Missing ends, overlapping identical fences, foreign owners/queues and reversed
timestamps are rejected. Fence reuse after explicit completion remains valid.

These are medians of **event counts** over 40 measured transitions per window
count, using `[StartQpc, DwmFlushedQpc)`. They are neither milliseconds nor GPU
utilization. Raw rows retain all 120 exact QPC intervals.

| Windows | DMA starts (175) | Queue starts (178) | Wait starts (244) | Signal starts (245) | Hardware-fence records (450) |
|---|---:|---:|---:|---:|---:|
| 1 | 2 | 4 | 2 | 3 | 2 |
| 10 | 5 | 19 | 98.5 | 6.5 | 14 |
| 50 | 3 | 51.5 | 343 | 4 | 48.5 |

Submission/completion telemetry does not by itself locate exclusive GPU
execution. Microsoft's GPUView documentation distinguishes waiting context
queue work from execution on a GPU hardware queue.
[Context CPU Queue](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/context-cpu-queue).
D9 consequently does not sum packet spans into GPU busy time. Existing shared-DWM
PresentMon counters remain separate from app GPU.

## Receipts and retained tools

| Artifact | SHA256 |
|---|---|
| [Ownership and packet audit](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-D9/audit-r1/verification.json) | `AC4A2893788AAF802EFE7F67B29395E5CE860D0255FFFE9597251CDAD1A32E54` |
| [Provenance and rejection checks](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-D9/closure.json) | `9C543EAC683BF04639195C801D15C3550FA0D48C1B4ACECB998BB6AE5ADA89ED` |
| Raw selected events, 245,557,932 bytes | `8880AAAEBF296CB6192F832D2ED3E98E2FE42FA4C4908502B1AE4AA48830C448` |
| Independent app-owned raw events | `B80ADFB91EBDC6A7586FB1508FC57968876333DF2ECA051B429A2096EEB04B77` |
| [Measured transition counts](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-D9/audit-r1/transition-counts.csv) | `8AE083A945B6CEA10656FF4A3B8E5124A80DB7A5E6A2A72FEFF49BEEA34CFB91` |

The isolated `gpu-reader` exports raw payloads; `inspect-gpu.py` decodes them
using retained TDH schemas. The isolated `owner-reader` independently re-reads
the ETL using fixed payload offsets and enforces object lifetimes. Both pin
TraceEvent 3.2.6 with locked restore. `audit-gpu.py` compares their results,
xperf fields and packet joins; `close-diagnostic.py` verifies provenance and
rejection cases. These diagnostic tools and build logs remain inside D9,
outside shipping projects.

The initial `owner-r1` rejected an incorrectly assumed 64-byte device payload
before producing any owned event. Its source and binary are retained; the
installed v2 layout is 60 bytes, and fresh `owner-r2` passes. No partial output
is promoted. Use fresh output directories for future runs; retained audit scripts
refer to their immutable D9 evidence paths.

## Remaining work

GPU process ownership is evidenced for M6. A GPU-time claim still needs validated
execution-time derivation for legacy and hardware-scheduled contexts, including
queue waits, overlapping engines and interrupts. A candidate GPU comparison then
requires fresh controlled captures of the common B18/B17 fixture. D9 cannot
supply that comparison from the old B15 trace.

The missing parent-to-nonclient DWM visual edge and full visible-frame/hardware
presentation remain separate pending gates. D8's absent event 335 is not retried
or reinterpreted here. Multi-DPI acceptance is unchanged.

The final desktop probe observes disconnected RDP/WinDisc at 192 DPI and destroys
its probe window; WPR is not recording. Offline analysis does not depend on native
admission. Shipping source, dependencies, packages, M8 CPU evidence and the ledger
are unchanged. Counts remain 25 IMPLEMENTED / 12 IN_PROGRESS.

## Execution-time semantics continuation from F4/D9

M6's retained queue/fence endpoints admit different execution schedules with
the same recorded observations. They do not establish GPU milliseconds. The
new immutable EXECUTION-R1 directory records that offline result, a successful
scoped CPU admission probe and four B17 native diagnostic captures. The final
profile enables 0x84000843, uses explicit scoped capture-state requests and
retains a 30-second unmeasured drain tail. No production or fixture source changed.

The tail recovers P4's hardware scheduling coverage. The independent verifier
matches all 87,760 event-113, 31,496 event-262, 28 event-432 and 544 event-433
payloads against xperf, and separately reconstructs 373 app states / 186 closed
RUNNING spans. Fourteen boundary/negative cases preserve wait exclusion, lifetime
ownership, overlap union and fail-closed handling. A missing entire state pair
can evade alternation, so ETW zero loss is insufficient for GPU-log completeness.

Six legacy node-1 DMA submissions occur inside P4's three measured intervals;
185/263 hardware timestamps are absent even with history keyword 0x2 enabled.
113 contains repeated bookkeeping values and is not summed as GPU time. Event
431 identifying the CMP clock is also absent. Raw 432 log handles explicitly
join 433.hOsHandle, while the public DDI names hKmdContext. The clock projections
are labeled provisional, retain rational QPC values, and publish no GPU milliseconds.

Result receipt: `execution-audit/result.json`, SHA256
`793E61F38C6FB8820A9A462EEF9DF3B8C88D2F198E5C077159846C98298BD380`.
Independent receipt: `execution-verification/verification.json`, SHA256
`BD268CEC9326AD3D10804F70EF1A11C55E48B4227A3B93324E3B21448CAC2320`.
Both live under `FWM-ANIMATION-FULLAPP-20260912-EXECUTION-R1` and retain
StageAccepted=false. They establish the exact missing instrumentation/semantic
evidence; they are neither a performance REJECT nor a GPU candidate comparison.
The next capture requires a changed evidence-producing hypothesis for those
gaps. Once the complete method passes, run five alternating pairs in ten
sequential B18/B17 processes. Retain M8 CPU and the separate correction-only
9.43 -> 9.79 ms 50-window timing tradeoff without remeasurement.
