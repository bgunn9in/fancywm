# PERF-016 native heap and graph HWND generations — 2026-09-13

Continuation checkpoint: `FWM-PERF016-HEAP-20260913-R1`. PERF-016 remains
**IN_PROGRESS**. This work changes diagnostic fixtures/readers/verifiers only.
Production, its five inherited justified deltas, PERF-002 reconciliation at
250 ms, delivery gates and ledger are unchanged. `ProductionChanged=false`,
`PerformanceClaim=false`, `StageAccepted=false`, `LedgerAppended=false`.

All run IDs below use the prefix `FWM-NATIVE-HEAP-20260913-`. Their actual
commands, process order, source/build/binary provenance, native snapshots, ETLs,
exports, failed attempts and exclusive receipts are retained under
`artifacts/performance`. Offline decoder/reverification runs are not native
application tests. No new TRX, crash, Toast-only or DPI experiment was run.

## Native graph closure and live retention

F1 exposed an assumption in the old full-graph verifier: the initial two overlay
windows can close before App.Terminate when the native display provider changes.
The private desktop naturally alternated between the production NoMonitor
1024x768 fallback and a 2880x1800 monitor at scaling 2. No agent display setting
change was made. Initial-window closure is therefore not a sufficient model of
all graph windows. F1's Debug/Release executions and rejected verification are
preserved, without a full-graph PASS receipt.

`Program.GraphWindows.cs` now witnesses every actual graph HWND generation,
including replacements, using weak metadata and native PID checks. The strict
verifier requires acquisition/closure identity and temporal order, exact prefix
history at each epoch, live Application.Windows/native membership, and zero
surviving observed HWNDs at Application.Exit and after dispatcher shutdown.
MainWindow must close after termination is requested. Existing production
constructors, full Startup/AppMain/DI graph and all previously disclosed
profile/BAML/WindowArranging/owned-membership adapters remain in use.
The fixed startup-owner references in the original harness are intentionally
alive until shutdown; this does not claim weak collection of retired graph
singleton generations. Workload weak-owner retention is checked separately.

F2 preserves a fixture compilation failure (unassigned PID). The corrected
fixture completes F3 and F4, Debug then Release, in four new isolated full-graph
processes. Each executes 50 warmup plus **100 post-warmup lifetimes**, clears
all **9,483 workload weak references** after the established public WPF view
maintenance on the live dispatcher, and completes graceful Main/hooks/animation,
workspace/Mica/provider, Startup, dispatcher and own-target teardown.
F3 observes **8/4**, F4 **4/8** acquired/closed graph generations (Debug/Release).
These observations cover natural replacement; they do not force or reproduce
an external display transition.

F3 `verification-v2.json`:
`7A2C2758870DFC859DAE3F8AF126CFBCBD1E41D9A7BE2AA9A3DAD1B88867ED39`.
F4 `verification.json`:
`04E8F5298A210D5EB1C05C8A1EC0B8882B77E1BC70EA93302105371BFBEB7385`.
Each rejects nine controls, including missing workload/heap scenarios,
surviving HWNDs, retained workload owner, missing generation closure,
surviving replacement HWND, wrong active generation, wrong PID and stopped
live dispatcher. F3 v1 is preserved; v2 is stricter receipt verification of the
same native runs, not another test.

The unchanged GUI no-growth gate is **false in F3 Debug and both F4 processes**;
F3 Release passes. F3 USER is 44/45/45 and 43/43/43; GDI is 32/26/24 and
11/11/11. F4 USER is 42/43/42 and 43/43/44; GDI is 11/11/11 and 11/11/26.
These changing-display/instrumented observations do not extend or invalidate
the earlier F11 compact/fallback scoped PASS. They supply no new general GUI
no-growth PASS, and no failed criterion is relaxed.

## Locked default-heap observations

The native observer records metadata using GetProcessHeap, HeapLock, HeapWalk
until ERROR_NO_MORE_ITEMS, then HeapUnlock. A fixed VirtualAlloc output buffer
and CREATE_NEW output file are prepared before locking. No CRT, managed callback,
logging, allocation or file write occurs while the heap is locked. No memory
block contents are read. Startup, live epochs 0/1/2 and shutdown have native
PID/TID/QPC/lock/unlock/completion witnesses. This is intrusive diagnostic
instrumentation, without a timing/performance claim.

T1/T2 each calibrate 24 known allocations, reallocations and frees on an owned
private heap and the process default heap (48 exact pairs), with 13 rejected
negative controls. T2 adds only the controlled pipe rendezvous needed for
trace admission. The same observer implementation is used for F3/F4.
Other libraries' private heap handles are never walked: Microsoft explicitly
warns that concurrent destruction can invalidate handles returned by
[GetProcessHeaps](https://learn.microsoft.com/en-us/windows/win32/api/heapapi/nf-heapapi-getprocessheaps).
See the [HeapWalk contract](https://learn.microsoft.com/en-us/windows/win32/api/heapapi/nf-heapapi-heapwalk)
and [PROCESS_HEAP_ENTRY fields](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-process_heap_entry).

| Run/configuration | Busy default-heap blocks, epochs 0/1/2 | Busy bytes, epochs 0/1/2 | Count-and-bytes no-growth |
|---|---|---|---|
| F3 Debug | 90,847 / 91,726 / 92,197 | 19,642,642 / 18,073,731 / 17,586,789 | false |
| F3 Release | 99,124 / 101,482 / 101,557 | 18,263,054 / 18,423,649 / 18,542,099 | false |
| F4 Debug | 90,360 / 91,717 / 91,735 | 21,420,662 / 17,202,581 / 17,130,283 | false |
| F4 Release | 99,431 / 101,839 / 102,453 | 18,267,687 / 18,583,926 / 19,081,443 | false |

Pointer/size equality between snapshots alone does not establish allocation
generations. No native-heap leak freedom, whole-process memory baseline or
performance acceptance follows from these counts.

## Own-PID heap trace admission and independent verification

Current identity/token, WPR and complete ignored-file ETL inventory were read
before tracing. A separately named CPU admission succeeds in E1 and stops;
it is not a CPU/GPU measurement. The heap collector uses only its unique owned
session and own PID, without registry/IFEO edits or a global kernel session.
The installed xperf requires `-Pids`; its saved help corrects the older
[heap capture example](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/enabling-heap-data-capture).
E1's localized absence-check failure and E2's unsupported `-Pid` failure are
preserved. E1's pipe child exits on EOF; recovery proves its absence and the
owned session's absence by HRESULT 0x80300002. E2's native control completes,
but failed trace admission is not represented as trace PASS.

E3 proves all 48 native alloc/realloc/free control pairs, QPC-bracketed against
locked snapshots, with exact own PID and 166 allocation stacks. Native
ProcessTrace/raw, tracerpt XML and xperf CSV agree; event/buffer loss is zero.
Four negative controls reject missing allocation/free/stack and foreign PID.
Receipt: `8E4E0F517DF4226C46BB3452ED400C2BEDD1B616D013D5D22C7F94BE36AF196F`.

F4 attaches after warmup before epoch 0 and stops after the final shutdown
snapshot. Debug/Release ETLs are 983,564,288 / 942,669,824 bytes, with zero lost
events/buffers. Every traced allocation/reallocation has one exact PID/TID/QPC
stack association. The allocation model verifies native pointer generations,
free/reallocation ordering and heap destruction, and matches every known live
default-heap allocation to its address and size in the corresponding locked
snapshot. Pre-attach allocations stay unknown.

Two decoder assumptions failed and were corrected using the existing native
evidence, with all failed scripts/logs preserved. Native HeapReAlloc can emit
inner allocation/free events before its outer summary; E3's 48 moved native
control pairs validate that exact sequence and three rejection controls.
Tracerpt's MOF schema exports only 32 PCs for longer stacks, while xperf's stack
timestamp is unreliable without base process metadata. Neither limitation is
hidden. The offline pinned Microsoft
[TraceEvent 3.2.6 reader](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/3.2.6)
independently preserves full QPC/PID/TID/PC payloads. T5 validates all 166 control
stacks and a native 59-frame stack, rejecting five corruptions including a
changed tail after frame 32. Source, NuGet dependencies, binaries and commands
are archived. These decoders start no trace session or native application.

F4 `trace-verification.json` is **SCOPED_POST_ATTACH_NATIVE_HEAP_ATTRIBUTION_PASS**,
SHA256 `DECF9134FED51292D6187BE400438C7257EAB2ED803F6C4DF62035007B8B5C20`.
It verifies **6,790,750 allocation/reallocation/free rows**, **3,434,673 full
stacks / 163,322,214 PCs**, with native and TraceEvent full-payload equality;
XML/CSV independently verify allocation fields, and XML verifies its actual
full-raw or fixed-32 stack boundary. Six further negative controls reject a
surviving unobserved block, wrong size and missing survivor stack in each
configuration. No managed allocation owner or continuous module lifetime is
inferred from an epoch module/RVA annotation.

| Configuration | Known traced busy blocks, epochs 0/1/2/shutdown | Unknown busy blocks at epoch 2 |
|---|---|---|
| Debug | 52 / 3,503 / 3,541 / 4,326 | 88,194 |
| Release | 42 / 5,110 / 5,930 / 6,417 | 96,523 |

`observed-scope-summary.json` derives exact counts and descriptive first
non-allocator module buckets from the verified stacks. At epoch 2 those buckets
include coreclr (1,254,651 Debug / 1,446,518 Release bytes), RPCRT4 and WPF graphics.
Release's WPF bucket is 464,472 bytes, then 2,184 at the post-dispatcher
snapshot. Runtime/observer allocations and unresolved frames remain in the
observations; a module bucket is not a logical owner or a leak diagnosis.
Post-dispatcher process-alive observations are separate from live-dispatcher
retention. Direct VirtualAlloc, unwalked private heaps and GPU memory are outside
the proof. The current evidence cannot establish complete native-heap leak
freedom or justify a production delta.

## Current criterion mapping

| Criterion | Existing/new verified evidence | Missing proof and PASS condition |
|---|---|---|
| Toast lifetime | Original R2 receipts remain byte-exact | Scoped PASS; no standalone repeat |
| Native SettingsWindow/OverlayHost, auxiliary owners/browser/hooks/workspace | Prior native-owner and full-graph checkpoints, retained failure/order/dispatcher/weak evidence | Scoped PASS under their documented constructor/cache-maintenance boundaries |
| Dynamic full-graph HWND closure | F3/F4 account for all acquired and closed generations, Application.Windows and native absence at exit/shutdown | Scoped PASS; no claim that intentionally retained singleton references collect before shutdown |
| Live workload retention and graceful startup-graph teardown | Four newly verified F3/F4 processes, 100 post-warmup lifetimes and 0/9,483 weak owners each | Scoped PASS with explicit existing isolation adapters |
| Minimal WPF shutdown | Preserved native-teardown receipts | Separate from full Startup/AppMain/DI and abrupt exit |
| Hard crash / production error handler | Preserved current-production C3 six-process receipt, partial managed cleanup and OS reclamation separated | No new crash needed for unchanged production; OS cleanup never implies managed completion |
| USER/GDI no-growth | Prior F11 compact/fallback scoped PASS remains; new F3/F4 mixed/failing observations are retained | Broader changing-display condition remains unproved; do not replace its false gates with attribution PASS |
| Native heap observations and post-attach allocation attribution | T1/T2 controls, F3/F4 locked snapshots, E3 admission, T5 independent full stacks, F4 strict replay | Scoped attribution PASS; no-growth is false, pre-attach/other memory and logical owners are incomplete; full leak freedom needs complete lifetime/owner evidence |
| Unmodified startup, genuine Explorer membership and wallpaper success | Existing adapter boundaries; current read-only host/VM admission inventory | Requires an isolated shell/user/session/VM allowing actual global WindowArranging behavior and genuine shell services; no ready disposable environment was found |
| GPU execution and physical presentation | Preserved PERF-010/021 EXECUTION-R2 and actual DPI restoration | Requires new attributed GPU execution/identity contracts, then candidate GPU A/B; CPU/heap admission supplies neither |

Read-only environment discovery found no ready disposable VM/Sandbox command
or running HCS container; optional-feature queries failed with Class not
registered and establish no feature-state fact. No environment was provisioned,
rebooted or configured, and no security/display policy was changed. The shared
vmcompute service was observed Running after read-only HCS discovery; it was
not explicitly started/stopped or treated as an owned service.

The available native heap source was calibrated, captured and fully replayed
through its established boundary. Another identical capture supplies no startup
history or logical-owner identity. The next unchanged-startup criterion needs
the isolated environment above. Broader heap/graphics claims need additional
ownership/lifetime evidence; no arbitrary production optimization or new
candidate stage is introduced. PERF-010/021 waits for its recorded OS/driver
contracts, not another elevation or same-source capture.

## Integrity, actual new work and preserved receipts

There are six new complete graph executions (F1/F3/F4 Debug/Release), twelve
successful graph/target builds, and one failed fixture build (F2). F1's two
executions are observations with rejected verification, not criterion PASS.
T1/T2 and E1/E2/E3 add five owned native control processes; E1 exits before
control completion. All offline replays, native/C# decoder builds and receipt
negative controls are listed separately in the seal. No new TRX or production
delivery rebuild is claimed or required for diagnostic-only changes.

The prior GUI checkpoint's **77,230 files / 4,710,391,610 bytes** were independently
rehashed. Its verification remains
`9D1B924AC4273CF248EFD3DDD88F6329C133F7656EAAC014B0DA4DEDD6847F57`.
The original Toast R2 and DPI restoration hashes are verified again by the
entry/final checkpoint review. Receipt overwrite attempts are rejected without
modifying the protected files. Old receipt reading is not counted as new testing.

E4 verifies all **26 ETLs**, including ignored paths: the previous 22 files /
111,521,300,480 bytes remain byte-exact. Only four new owned ETLs exist: E1 CPU
admission, E3 control heap, F4 Debug and Release heap traces. Sorted inventory
SHA256: `5F5343647332CF0936609F2DC40F63E19F1E07D74D3CB8CEBF29318307D1CEFA`.
All named owned sessions are stopped; foreign sessions/processes are untouched.

Final checkpoint verification binds the frozen source, every new raw/failed
artifact and receipt, and performs an independent second manifest rehash.
Git HEAD/tree and recursive submodules retain the requested identities.
The 2,155 production files (72 winman-windows) match the entry state with only
the same five inherited justified differences from nested-notification C2.
The ledger is **41,782,945 bytes / 41,325 data rows**, full prefix SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`;
suffix length is zero. No snapshots, failed runs or previous receipts are
removed or overwritten; no commit, publication or MSIX launch occurs.
