# PERF-016 native GPU and process memory accounting — 2026-09-13

Checkpoint: `FWM-PERF016-MEMCOUNTERS-20260913-R1`. PERF-016 remains **IN_PROGRESS**.
New native sources measure DXGI process video-memory usage and OS private
commit without PSS clones or native heap/D3D interception. F3 passes scoped
DXGI usage no-growth at its recorded live epochs and reports zero usage after
shutdown. Private commit grows in both configurations; USER/GDI fails Release.
These results do not close whole-process leak freedom or physical allocation
lifetime. ProductionChanged=false, PerformanceClaim=false, StageAccepted=false,
LedgerAppended=false. Five inherited corrections and PERF-002 250 ms remain.

All short IDs below use `FWM-DXGI-MEMORY-20260913-`. Earlier
[mapped D3D/PSS](D3D_MAPPED_PSS.md), [D3D owners](D3D_NATIVE_OWNERS.md),
[PSS](PSS_NATIVE_MEMORY.md) and native-owner receipts remain immutable.

## Documented sources and native calibration

`IDXGIAdapter3::QueryVideoMemoryInfo` exposes current process usage and budget
for an adapter node and LOCAL/NON_LOCAL segment. The new DLL enumerates hardware
adapters, retains explicit DXGI factory/adapter interfaces during observation,
and queries node 0. Every row carries PID, LUID, ordinal, node, segment, QPC
bounds, HRESULT, Budget, CurrentUsage and reservation fields. It never sets
reservations, changes budgets, creates a tracing session or intercepts methods.
Software adapters are inventoried but excluded from this hardware-source scope.
See [QueryVideoMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo)
and [memory fields](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/ns-dxgi1_4-dxgi_query_video_memory_info).

T1/T2 build Debug and Release controls and observer DLLs with MSVC /W4 /WX,
/Od or /O2, /MT and debug symbols. The native control creates an own hidden HWND
on a private non-input desktop and an actual hardware D3D9Ex device. It matches
the D3D9 adapter to DXGI by the exact LUID, a documented
[cross-API mapping](https://learn.microsoft.com/en-us/windows/win32/api/d3d9/nf-d3d9-idirect3d9ex-getadapterluid).
The observed hardware adapter has LUID 68892, vendor 4098, device 30032; node 0
and both segment groups are queried. No display/driver/security setting changes,
input injection, desktop switch or Present call occurs in the native controls.

P1–P4 execute three 50-cycle epochs per process. Each cycle creates four real
1024x1024 A8R8G8B8 render-target surfaces, fills them, and waits for an own
D3D EVENT query. The control samples before allocation, with four owners, with
one owner remaining, and after final release plus another EVENT query. Queries
are fixed pairs, separated by 20 ms; no minimum-selection retry is used. The
allocation workload is bounded to 16 MiB of requested surface payload and is
admitted only with at least 256 MiB of process budget and adequate headroom.

| Native profile | Four live owners minus baseline | One live owner minus baseline | After final release/query minus baseline |
|---|---:|---:|---:|
| P1 Debug / P2 Release, T1 | 16777216 | 16777216 | 0 |
| P3 Debug / P4 Release, T2 | 16777216 | 4194304 | 0 |

These values repeat in all 150 cycles of each process. T2 adds only an EVENT
query after releasing three owners; the observer source is byte-identical to
T1. The first profile is valid boundary evidence: Release return alone does
not require this accounting value to drop immediately. T2 calibrates the
additional completion boundary. It does not supply a physical allocation ID
or a general driver reclamation guarantee. V1 verifies 2,400 surface lifetimes,
9,600 DXGI queries, source/binary identity, resource/sample order, module cleanup
and nine negative controls. Native CurrentReservation stays zero.

The second DLL calls `GetProcessMemoryInfo(GetCurrentProcess(), ...)` with an
80-byte x64 PROCESS_MEMORY_COUNTERS_EX. Each row retains current private commit,
pagefile/peak values, working set, page faults, PID, ABI size, QPC bounds and
the exact API result. Microsoft's [structure contract](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex)
defines PrivateUsage as process private commit; it is not a managed heap size
or resident working set. The [query API](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo)
uses the supplied process handle; this observer only supplies its own process.

T3/P5/P6 calibrate this source with 16 initial query warmups and three 50-cycle
epochs. A cycle creates/touches four own 8-MiB VirtualAlloc buffers, then frees
three and finally the remaining one. V2 verifies 1,200 allocation lifetimes,
2,432 queries and seven negative controls. The private-commit increments are
33,619,968 bytes with four buffers, 8,404,992 with one, and zero after all frees,
in every cycle. The charge exceeds the requested 32-MiB payload by 65,536 bytes.
That observed difference is not assigned to a guessed allocator/page-table
owner, and requested payload size is not silently treated as commit charge.
Both native DLLs write bounded records directly to files and retain no event
history or resource-owner references. Control resources and observer modules
are released while each source process is still alive.

## Full startup graph execution and verified limits

F1 adds DXGI to the actual Startup.Main/service graph on an own non-input
desktop. Both native processes exit zero, but strict D1 rejects missing
Application.Windows checkpoints: the older guard admitted only heap/PSS
observers. Acquired/closed HWND events exist; the missing checkpoints prevent
full lifetime acceptance. D0 reproduces this exact failure in both configurations
and preserves the frozen source, raw data and failed verifier. No F1 full graph
PASS is manufactured from its process exit or summary.json.

F2 changes the single checkpoint guard to admit DXGI observation. New affected
Debug/Release runs and D2 verify all acquired/closed graph generations,
Application.Windows checkpoint histories, 100 post-warmup lifetimes per process,
zero of 9,483 workload weak owners after public WPF maintenance on the live
dispatcher, and graceful application/worker/provider teardown. F2 Release
LOCAL usage grows by 262,144 bytes between warmup and later epochs. Its strict
DXGI no-growth and USER/GDI gates remain false. F2 Debug passes both scoped gates.

F3 adds the calibrated private-commit observer and performs two new affected
graphs with the same T2 DXGI and T3 memory binaries used in native controls.
It has no PSS, heap trace, CLR trace, GUI interception or D3D method interception.
This is a new source configuration, not a retry to select a favorable F2 result
and not a performance A/B candidate. D3/D4 preserve all source/binary provenance
and verify actual native query intervals against managed phase boundaries.
Each graph again has 50 warmup plus 100 post-warmup lifetimes, zero of 9,483
workload weak owners after WPF maintenance, all four graph HWND generations
closed, Application exit before dispatcher stop and completed native teardown.

The existing explicit adapters remain: isolated profile, exact BAML URI,
intercepted global WindowArranging writes, a 1500-ms provider-disposal boundary
probe and own-target desktop membership. Target AutoSplitCount remains 5;
foreign windows and Explorer state are untouched. No extra quiescence or
stability admission is selected. Passive last settings/page/view-model roots
remain documented before WPF maintenance; fixed startup owners intentionally
stay alive in the fixture. Unmodified startup and all singleton ownership are
not inferred from these graphs.

Every graph observes startup, epochs 0/1/2 and shutdown, with five fixed samples
per phase, separated by 100 ms. The no-growth gate requires stable values within
each live phase and later phase maxima no greater than the warmup maximum.
All samples remain in the receipt; there is no selected later baseline or
discarded warmup value. Startup/shutdown observations are separate from live
retention. LOCAL=0, NON_LOCAL=1 below. Values are maxima of those five samples.

| Graph / configuration | LUID / node / segment | CurrentUsage bytes: warmup / epoch 1 / epoch 2 | Shutdown | Scoped usage no-growth |
|---|---|---|---:|---|
| F2 Debug | 68892 / 0 / 0 | 68657152 / 68657152 / 68657152 | 0 | True |
| F2 Debug | 68892 / 0 / 1 | 23527424 / 23527424 / 23527424 | 0 | True |
| F2 Release | 68892 / 0 / 0 | 68395008 / 68657152 / 68657152 | 0 | False |
| F2 Release | 68892 / 0 / 1 | 23527424 / 23527424 / 23527424 | 0 | True |
| F3 Debug | 68892 / 0 / 0 | 68657152 / 68657152 / 68657152 | 0 | True |
| F3 Debug | 68892 / 0 / 1 | 23527424 / 23527424 / 23527424 | 0 | True |
| F3 Release | 68892 / 0 / 0 | 68657152 / 68657152 / 68657152 | 0 | True |
| F3 Release | 68892 / 0 / 1 | 23527424 / 23527424 / 23527424 | 0 | True |

| F3 configuration | PrivateUsage bytes: warmup / epoch 1 / epoch 2 | Shutdown | Private commit no-growth |
|---|---|---:|---|
| Debug | 314064896 / 318668800 / 316493824 | 235859968 | False |
| Release | 316612608 / 316829696 / 317386752 | 235417600 | False |

F3 USER/GDI: Debug: True (strict gate passed); Release: False (USER/GDI growth).

D3 verifies twelve negative controls, including missing lifetime/window/query,
surviving HWND, retained owner, wrong PID/LUID/segment/order and injected growth
or unstable samples. D4 verifies nine private-commit coverage/identity/order/
ABI/atomicity/no-growth controls. CPU and DXGI calls are sequential and explicitly
non-atomic. They are not added together: accounting domains can overlap and
these APIs do not supply per-allocation identity or a combined memory snapshot.
Working-set and peak values are recorded separately and are not substituted
for CurrentUsage or PrivateUsage. Both native observer modules are unloaded
after their explicit cleanup returns successfully, before process exit.

F3's observed DXGI usage reaching zero before process exit is a process-counter
result. It is not proof of physical GPU allocation release, COM destruction,
all-node/adapter coverage, native heap leak freedom or physical presentation.
Private commit still grows without the prior heavy observers; that observation
does not identify a leaking logical owner or justify a production optimization.
F2's counter/GUI failures and all earlier heap/PSS failures remain unchanged.

## Remaining criterion mapping

| Criterion | Verified evidence | Missing proof / condition for PASS |
|---|---|---|
| Native settings/overlay/toast and auxiliary owners | Earlier constructor/close/failure receipts; F2/F3 repeat affected real settings and graph HWND lifetimes | Preserve completed scoped checks and their explicit bounds; no standalone toast repeat |
| Full graph managed retention and graceful shutdown | F2/D2 and F3/D3/D4, 100 post-warmup lifetimes per configuration, zero 9,483 workload weak roots, all graph generation checkpoints/teardown | Unmodified startup and intentionally rooted singletons remain outside fixture scope |
| Process GPU memory accounting | V1 native calibration and F2/F3 observations by LUID/node/segment; F3 stable live usage and zero after shutdown | Finite node-0 process-counter PASS; F2 Release growth remains, broader resource/physical allocation lifetime unproved |
| Process private commit | V2 calibrated native buffers and F3/D4 queries without heap/PSS interception | No-growth false in both configurations; complete allocation/logical-owner attribution remains required before a production defect claim |
| Native heap / logical ownership | Earlier stacks, heap replay and atomicity controls preserved; new coarse accounting | Calibrated atomic allocator decoder, pre-attach history and logical-owner contract; counters do not enumerate blocks |
| D3D/physical backing allocations | Earlier private-data/notifier/map/PSS controls retained; new process usage | Complete resource implementations, backing allocation IDs/generations and release contract still absent |
| USER/GDI | F2/F3 Debug passes; Release fails; earlier F11 scoped PASS preserved | Broader fixed-environment native attribution/no-growth remains open; no favorable-run substitution |
| Minimal WPF, full graph, hard crash | Separate earlier scopes; current-production C3 six-crash receipt rechecked | OS reclamation remains distinct from managed cleanup; no new production/crash-handler delta warrants repeating completed crash runs |
| Unmodified shell/startup/membership/wallpaper | Explicit safe adapters and earlier environment findings | Suitable disposable isolated shell/user/VM evidence; current foreign session remains untouched |
| PERF-010/021 GPU execution/presentation | EXECUTION-R2 and actual-DPI restoration preserved | Attributed GPU execution and full-frame/non-client hardware presentation identity, then candidate GPU A/B; memory counters do not supply those contracts |

The newly available process-usage and private-commit sources have been calibrated
and exercised through the full workload. Next useful attribution requires a
verifiable native allocator/logical-owner or physical resource-generation source,
or an appropriate isolated shell environment. The current counters provide no
such identity/atomic-state contract. Repeating these samples, CPU admission,
elevation or display changes would not supply it. PERF-016 remains IN_PROGRESS;
there is no whole-ID BLOCKED, artificial ACCEPT or ledger append.

## Exact work, changes and integrity

New native work: six control processes, six graph processes and six owned
target processes, eighteen recorded own process identities in total. F1's two
graphs retain the strict fixture failure; F2/F3 have verified lifetime receipts.
Three native tool builds invoke the compiler twelve times across Debug/Release;
twelve graph/target builds succeed. Commands, process UTC order, private desktop
identity, environment, compiler/build logs, raw stdout/stderr/native JSONL and
source/binary manifests are archived. Offline replay/negative controls are
not counted as new native application executions.

Only harness/native observer/fixture/verifier/documentation files change.
There is no production or test-project delta, no new candidate/performance A/B,
and no reason to repeat unchanged C2 delivery gates. New TRX, hard-crash,
dedicated DPI, PSS clone and ETL runs: zero. Old toast, current-production C3
and DPI-restoration receipts are rechecked, not rerun. The prior D3DMAP seal is
rehashed at entry/finalization: 11,459 files / 2,298,658,682 bytes.

Finalization checks Git status, root/index and recursive-submodule diff checks,
pinned HEAD/tree/winman-windows identities, all 2,155 production files including
72 winman-windows files, the five inherited corrections, document byte/line/
SHA256 values and ledger integrity. Ledger remains 41,782,945 bytes / 41,325
data rows with SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
The full old ledger is the identical prefix; suffix length is zero. All eighteen
own process identities are checked after exit. New receipt paths reject
overwrite, and the fresh evidence manifest is hashed twice. All 32 ETL paths
and sizes remain unchanged; read-only WPR/session checks are saved. No tracing
session or repeated full ETL hash pass is claimed. Dirty/untracked files,
snapshots, raw evidence and failed runs remain preserved.

Principal commands (exact native process invocations are in created.json;
existing IDs refuse overwrite):

```powershell
python scripts/performance/Build-DxgiMemory.py --id FWM-DXGI-MEMORY-20260913-T2
python scripts/performance/Verify-DxgiMemoryControl.py --id FWM-DXGI-MEMORY-20260913-V1
python scripts/performance/Build-ProcessMemory.py --id FWM-DXGI-MEMORY-20260913-T3
python scripts/performance/Verify-ProcessMemoryControl.py --id FWM-DXGI-MEMORY-20260913-V2
python scripts/performance/Run-FullGraphLifetime.py --id FWM-DXGI-MEMORY-20260913-F3 --owned-membership --warmup 50 --modes graceful --dxgi-memory-tool artifacts/performance/FWM-DXGI-MEMORY-20260913-T2 --process-memory-tool artifacts/performance/FWM-DXGI-MEMORY-20260913-T3
python scripts/performance/Verify-DxgiMemoryGraph.py --graph artifacts/performance/FWM-DXGI-MEMORY-20260913-F3 --id FWM-DXGI-MEMORY-20260913-D3
python scripts/performance/Verify-ProcessMemoryGraph.py --graph artifacts/performance/FWM-DXGI-MEMORY-20260913-F3 --graph-proof artifacts/performance/FWM-DXGI-MEMORY-20260913-D3/verification.json --id FWM-DXGI-MEMORY-20260913-D4
```

Receipt SHA256:

- R0: `AE2741829BEA69F7321ABD266CBC55ACE0E901EA97624B7DAD99BC77D24865BB`.
- V1: `F18AC09ACD9326CC57198B75BCBB255889981111D53D696921BA47937F09F7ED`.
- V2: `973D2574CAE4871BE0341B80EBB2589E1B409E4FA952B9E35933321CBFBAEEAA`.
- D0: `94DE66CCEBF22E4D7F31BB98C5254FF2E3BAC490FB351907CB3A168B8B9FA5E8`.
- D2: `3317CBA53D4D5DA3E9F3AF74CAAA19A9DCE0BBAAA3CBA085C0843B5A965C39B2`.
- D3: `16D0CD685CFC7B941B15CA6D0CA628E57ADFEE20985FBD2CD5552094DFE1F031`.
- D4: `31E5B7A96CD6B108296CC11894C0CA193E9E2F255A2DEB1049D0ED920B505FF9`.
