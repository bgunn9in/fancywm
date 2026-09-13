# PERF-016 CLR/native caller attribution — 2026-09-13

Checkpoint `FWM-PERF016-CLR-20260913-R1` continues the
[retained cohort checkpoint](NATIVE_HEAP_COHORTS.md). PERF-016 remains
**IN_PROGRESS**. Production and all five inherited justified deltas are
unchanged. This is diagnostic native evidence with no performance claim,
stage acceptance, ledger append or MSIX execution.

## New source and calibration

The new external collector uses pinned TraceEvent 3.2.6, a fresh own session,
`NoRestartOnCreate`, PID-filtered CLR Loader/JIT/rundown providers and native
ControlTrace stop counters. Foreign events are rejected before storage;
both concurrent excluded control processes execute their real workload,
and the collector receives zero foreign events. Completed rundown precedes
all attributed allocations. Method/module load sequences, QPC boundaries,
address ranges and emitted ReJIT IDs distinguish code generations; a metadata
ID or address alone is not a generation identity. The independent Python
decoder checks raw method/module payloads against the SDK fields.

[CLR method events](https://learn.microsoft.com/en-us/dotnet/framework/performance/method-etw-events)
and the [pinned parser source](https://github.com/microsoft/perfview/blob/v3.2.6/src/TraceEvent/Parsers/ClrTraceEventParser.cs)
define the captured fields. Caller attribution does not supply object identity,
inlined logical owners, native DLL load lifetimes or ownership transfer.

T1/T2 preserve both builds; T2 adds incremental failure journals and safe
cleanup of remaining native buffers. P1 Debug and P2 Release each run an
included and excluded own process. Their 2 epochs cover 48 allocate/realloc/free
chains per process. **96 traced chains** across Debug/Release verify warmed
rundown code, newly JIT-compiled code and collectible plugin code. Four traced
plugin unloads occur while the corresponding native buffers remain live;
later realloc/free witnesses prove that code unload is not native owner cleanup.
The excluded processes execute another 96 chains as PID-filter controls.
V1 rejects 9 semantic negative controls per configuration; N1 rejects 9 raw
PID/QPC/version/address/module/tail/count corruptions. V0's first verifier
failure is retained: a pointer can have temporary allocations of other sizes
inside the first reflective JIT-call bracket. Exact pointer/size/thread/QPC
matching resolves that ambiguity without changing the native runs.

## Full startup graph and the failed atomic-snapshot assumption

F1 executes fresh Debug/Release Startup/AppMain/DI graphs on separate owned
non-input desktops. Each has 50 warmup and 100 post-warmup lifetimes and
0/9,483 workload weak owners after public WPF cache maintenance on its live
dispatcher. Every acquired graph HWND closes; settings HWNDs, target windows,
native hooks, workers, Mica/provider and graceful Application/dispatcher order
pass the retained strict checks and negative controls. Startup singleton owners
remain intentionally referenced during epochs. Existing profile/BAML/setting/
membership adapters remain; this is not unmodified Explorer startup.

CLR capture starts before production startup; heap capture starts at epoch 0.
F1 uses the fixture defaults of zero extra GUI quiescence and zero stable-window
admission, while older F4 used 45 seconds/30 seconds. Lifetime counts remain
comparable; heap/GUI totals are not a performance comparison across these
fixtures. Actual target HWND DPI is recorded in raw protocols; no display
setting or input was changed and no PERF-010 DPI experiment was repeated.
F1's default-heap no-growth gate is **false**. The scoped USER/GDI gates are
Debug=True, Release=True; they do not replace earlier GUI receipts.

D1 fails the unchanged strict baseline-seeded replay: three 32-byte native
blocks are allocated on another thread within Debug's HeapWalk interval and
are absent from its snapshot. X1 retains their raw address/size/TID/QPC and
later free witnesses. No atomic heap/replay PASS is issued for F1.

L1 tests that boundary in a separate own native process. All **12 allocations
of 32 bytes complete while the main thread still holds the successful
HeapLock**; all 12 allocations of 64 KiB wait for unlock. Raw ETW allocation
events match independent call/lock intervals and exact pointers; 5 negative
controls reject missing/wrong identities and false completion boundaries.
This is observed behavior of this environment and these allocator paths.
It does not identify a general allocator implementation or a production defect.
[HeapLock documentation](https://learn.microsoft.com/en-us/windows/win32/api/heapapi/nf-heapapi-heaplock)
describes serialized access, but that description is insufficient to promote
these observations to a universally atomic whole-process snapshot.

The older F4 exact snapshot equalities remain verified observations of those
four boundaries. They are not broadened into a general atomicity contract.
Their immutable receipts and calibrated replay code remain byte-exact.
No repeated capture is selected merely to obtain a matching snapshot.

## Verified event-stream callers

D2 independently verifies **201,730 CLR records**, including
raw method/module decoding, and **8,358,470 full native stacks /
507,816,584 PCs** from 26,647,667 native records.
Native ProcessTrace and the separate TraceEvent stack reader agree byte-for-byte;
both readers and CLR session stop report zero lost events/buffers.
The unchanged realloc model reconstructs recorded allocation-event generations.
It does not seed F1 with a purported atomic baseline. Snapshot disagreements
are exported explicitly, with `CompleteDefaultHeapSnapshotsMatch=false`.

D3 reopens all 14,834 selected native stacks,
61,824 managed caller witnesses and 33,839 phase rows, checks
the original native allocation addresses/sizes and raw stack tails, then checks
code-version/module-generation/QPC boundaries separately. Ten additional
negative controls reject stale methods, wrong PCs/versions/modules and tail
corruption. Rows below mean live **in the event stream at each cut**, not a
complete atomic native heap inventory or managed logical ownership.

| Configuration / cut | Traced stream-live blocks | With managed caller | With FancyWM/WinMan frame |
|---|---:|---:|---:|
| Debug 0 | 244 | 41 | 41 |
| Debug 1 | 4143 | 434 | 399 |
| Debug 2 | 4023 | 433 | 403 |
| Debug shutdown | 4851 | 817 | 666 |
| Release 0 | 41 | 41 | 41 |
| Release 1 | 5856 | 474 | 428 |
| Release 2 | 6786 | 489 | 419 |
| Release shutdown | 7895 | 929 | 717 |

Exact per-event stacks and names are in D2's `stack-callers.jsonl`; phase rows
and snapshot discrepancies are separate immutable exports. Unresolved native
frames and pre-attach allocations remain unknown. No native-heap, direct
VirtualAlloc, private-heap, GPU-leak or physical-presentation claim is made.

## Current criterion mapping

| Criterion | Verified evidence | Missing proof / PASS condition |
|---|---|---|
| Native toast/settings/overlay/auxiliary closure | Prior immutable native receipts; F1 affected full-graph settings/graph lifetimes | Preserve constructor/failure/cache-maintenance bounds; no standalone toast repeat |
| Full graph retention and graceful teardown | F1 Debug/Release 100 post-warmup lifetimes, zero workload weak owners, native resources closed | Existing startup/membership adapters and intentionally live singleton ownership remain |
| CLR caller identity across native allocations | New P1/P2/V1/N1 and F1/D2/D3 with real code unload/address reuse and zero trace loss | Scoped PASS for emitted code generations and native stack callers; not logical owners |
| Atomic complete native heap lifetime / no-growth | F1 snapshots and D1/X1 mismatch; L1 demonstrates concurrent small allocation during HeapLock | Need a calibrated snapshot/serialization and ownership source valid for these paths; changing QPC cuts or retrying until equality is not PASS |
| Native logical owner and pre-attach lifetime | Raw event-stream allocations/reallocs/frees now carry observed managed callers | Caller names do not expose native ownership transfer or pre-attach history; no such identity contract is present in these providers |
| Minimal WPF, full graph and hard crash | Earlier separate receipts; current-production C3's six owned crashes retained | OS reclamation remains separate from managed cleanup; no production/crash-handler delta justifies a repeat |
| Unmodified startup / Explorer membership / wallpaper | Prior environment findings; F1 repeats explicit isolated adapters | Requires an appropriate disposable shell/user/session/VM; current host restrictions remain |
| Changing-display GUI no-growth | Earlier scoped F11 PASS; F1 and earlier mixed/false observations retained | New controlled environment and applicable native lifetime evidence; no inferred counter PASS |
| PERF-010/021 GPU/presentation | EXECUTION-R2 and actual DPI restoration receipt retained | New attributed GPU execution and presentation identity contracts, then candidate GPU A/B |

Available CLR/native payloads and selected caller witnesses have been decoded
and checked. The failed atomicity condition is reproduced by a bounded native
control. These providers do not expose a stronger snapshot or logical-owner
contract; repeating the same captures, elevation or another CPU trace cannot
supply it. No production optimization/candidate is justified by these counts.

## Integrity and retained failures

R0 rehashes the prior cohort seal: 2,633 files / 223,547,560 bytes, preserving
verification `2201A71A5EACD8337FBD27C20B77ADB0BA91A84FE9F04D09B13BF13A054534AE`.
This is old receipt verification, not new tests. T1, V0 and D1 remain archived.
New native work comprises four managed/native control processes, two full-graph
processes with two own target processes, four CLR collector processes and one
native lock-boundary process. Builds and offline decoders are recorded
separately. There are zero new TRX, hard-crash or dedicated DPI runs. CPU
admission succeeds independently and is not a performance measurement.

E2 rehashes all **32 ETLs**, including ignored files. The 26 old
paths/sizes/SHA256 values are unchanged; six new ETLs are one CPU admission,
two CLR/native controls, two full-graph heap traces and one lock control.
All nine own heap/CLR sessions are inactive. Final review rechecks Git/root/
index/recursive submodules, pinned HEAD/tree, all 2,155 production files
(72 winman-windows), control document sizes/lines/SHA256 and old receipts.
PERF-002 remains 250 ms. Ledger remains 41,782,945 bytes / 41,325 data rows,
SHA256 `84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`;
its full old bytes are the prefix, with an empty suffix.

Receipt hashes (artifact prefix `FWM-CLR-HEAP-20260913-`):

- `V1/verification.json`: `0EAFC47E5AA8376871327CEF14A67D09664CE05822A79E71878BDE2868A6DFD9`.
- `N1/verification.json`: `AA3B63CCA42723BCB61917BE9AB2D9901213B066AB48DC76FBC4FEF54B08A03F`.
- `L1/verification.json`: `D9A432D705551485CF2EC4908EF9B01C90F265D0D859705E9CC04005F050B83A`.
- `D2/verification.json`: `DCC940E387CC9B026D42646525B86F5153789F829CDD34C09FE82926CD49AF3F`.
- `D3/verification.json`: `6E1E427FE7610DD9CD2F59B9D97018091FA3FFC01EAE710D01D27C46101BF55E`.
- `E2/verification.json`: `36711BFE4169BD84608F07EF98DDDD201295C50AF4A21C84D7E5765089E73192`.
