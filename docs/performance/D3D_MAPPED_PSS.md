# PERF-016 D3D mapped points and PSS boundaries — 2026-09-13

Checkpoint: `FWM-PERF016-D3DMAP-20260913-R1`. PERF-016 remains **IN_PROGRESS**.
This continuation closes scoped hardware vertex/index method coverage and
identifies actual full-graph D3D9 mapped points missing from PSS. It also
reproduces shared-section commit propagation into a clone with zero clone CPU
time. Whole-process memory and USER/GDI no-growth remain false. There is no
production change, performance claim, stage acceptance or ledger append.

All short run IDs below have prefix `FWM-D3D-MAP-20260913-`. Earlier
[D3D evidence](D3D_NATIVE_OWNERS.md) and [PSS evidence](PSS_NATIVE_MEMORY.md)
remain immutable. Their historical next actions are superseded by this report.

## Native source, calibration and affected full graphs

The independent Methods.cpp producer creates its own hidden HWND on a private
desktop, real D3D9Ex HAL devices with both software and hardware vertex
processing, and WPF-shaped dynamic vertex/index and static vertex buffers.
It calls real Lock/Unlock and records canonical IUnknown, actual method
addresses and returned pointers. Per configuration it creates 900 resource
generations and performs 2,700 map pairs over repeated 50-cycle epochs.
It writes only its own 128-byte buffers; no input or display change occurs.

P1/P2 with the prior observer record only 1,350 of 2,700 producer pairs:
hardware-processing vertex/index buffers use different native method
implementations. B1 reproduces this coverage gap. The prior finite producer
calibration remains valid within its original scope. T2 adds the four actual
hardware buffer methods to the observer. P3/P4 use the identical T1 producer
binaries and observe all 2,700 pairs per configuration (V1, six negative
controls). This is a harness correction with a common native fixture, not a
production candidate or performance A/B experiment.

T3 adds in-lock PSS observations. The callback records the actual successful
map result, its native call/thread/owner generation and VirtualQuery point
metadata; while that same resource remains locked it captures a PSS clone,
queries its VA map twice, probes one byte only in the clone and releases the
clone before returning from the map observer. A separately loaded copy of the
calibrated PSS DLL has an independently verified module identity. Snapshot
state is serialized and drained before observer detachment. The observer
never reads or writes live mapped buffer bytes. It does not claim ownership
of the entire containing VA region, heap block or physical GPU allocation.

P6/P7 repeat the common producer with T3: V2 verifies all 2,700 map pairs per
configuration. P8/P9 exercise texture/plain-surface/buffer ownership, worker
release, cancellation and 150 nested surface locks per configuration; V3
verifies 1,050 generations, 600 external map pairs and 1,350 native call
boundaries per process with nine negative controls. Across P6–P9 there are
48 in-lock PSS clones. V4 verifies the original point observations with nine
negative controls; V6 rechecks those same raw files with eleven controls after
the region-extent correction described below. V6 is not a new native run.

F1 executes the affected Debug and Release full startup/service graphs with
the same T3 observer, controlled settings/profile/BAML and owned-membership
adapters. The ordinary owned target layout is AutoSplitCount=5; earlier compact
PSS workloads are not an A/B baseline. Each process completes 50 warmup and
100 post-warmup lifetimes on the live STA dispatcher, retains zero of 9,483
workload weak owners after WPF maintenance, closes all four graph HWND
generations and completes graceful worker/provider/application teardown.
The two real graph processes and two own target processes exit zero.

| F1 configuration | Registered/released private owners | Map pairs | Native call entry/exit pairs | Active owners after each live epoch / shutdown |
|---|---:|---:|---:|---|
| Debug | 83073 / 83073 | 117282 | 234564 | 45 / 0 |
| Release | 82545 / 82545 | 116316 | 232632 | 45 / 0 |

D4 verifies these 165,618 registered private-owner generations and 233,598 map
pairs with eleven negative controls, including missing native call evidence,
surviving HWND and retained managed owner. Thirteen warm owners survive the
post-warmup epochs alongside 32 replacement owners; all registered owners
release before exit. Graph journals observe hardware vertex-buffer and plain
surface maps. No graph index-buffer Lock is observed; native controls cover
the tested hardware index methods. Other resource/method implementations are
not assumed covered. D3D9 private-data IUnknown release is still distinct from
COM object destruction and physical backing-allocation release.

D7 verifies seven in-lock snapshots per configuration: four actual hardware
vertex-buffer points have committed MEM_PRIVATE source mappings with protect
0x404, but are MEM_FREE in the clone and clone-only reads fail with 299. Three
plain-surface points per configuration are committed/readable in the clone.
All fourteen owner generations, native Lock intervals, PSS capture/release
order and clone absence before callback return are verified. The epoch
boundaries remain on a live dispatcher. These are eight actual graph omission
witnesses and six present points, not complete graphics memory accounting.

Microsoft documents [resource locking](https://learn.microsoft.com/en-us/windows/win32/direct3d9/locking-resources)
and [buffer access](https://learn.microsoft.com/en-us/windows/win32/direct3d9/accessing-the-contents-of-a-vertex-buffer).
The native receipts establish which points are returned on this runtime;
neither those API contracts nor a containing VirtualQuery region supply a
physical allocation lifetime or full resource byte extent.

## Preserved failures and calibrated snapshot limits

P5 fails provenance preflight because the older PSS manifest has SHA256 fields
without byte counts. No native PID is created. The runner correction retains
SHA256 validation and all later attempts use fresh IDs; P5 remains archived.

D1's unchanged strict PSS verifier rejects F1 Release epoch 0: three
MEM_MAPPED intervals change from reserved to committed between clone VA
queries, totalling 57,344 bytes / fourteen 4-KiB pages. D2's dependent verifier
attempt has no PASS receipt. The clone executes no measured CPU time. These
observations are not assigned to a specific JIT, WPF or GPU allocation owner.

The new Shared.cpp fixture (T4, P10/P11) creates its own reserved shared
read/write and execute/read/write mappings, captures a clone, then commits
pages through its own original-process views. No executable bytes are run.
V5 verifies four clones, eight shared views and 131,072 propagated commit
bytes while clone CPU time remains zero and 65,536 private clone bytes remain
identical in each case. The fixture writes source private memory, but does not
save an independent source-after byte witness; that claim remains false.
All views/handles and private allocations are released, and each clone is
absent while its source process is still alive. Five negative controls reject
wrong size/state, retained/running clone and changed private clone bytes.

The [PSS capture flags contract](https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/ne-processsnapshot-pss_capture_flags)
includes cloneable private and shareable mapped/image pages.
[CreateFileMapping](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createfilemappinga)
documents SEC_RESERVE and later commit through VirtualAlloc. The native fixture
calibrates the observed propagation; there is no assertion that every shared
mapping is a frozen private copy.

New D3 uses a separate verifier with the V5 calibration. It permits only the
observed same-section MEM_MAPPED reserved-to-committed transitions, retaining
all other metadata/private-VA checks. WholeCloneVaStable remains false; all
ten ordinary captures have stable private VA between their two observations.
D6 reproduces the old strict failure without changing its frozen verifier,
checks the fourteen pages using an independent page oracle and rejects five
private-region/decommit/section/type/protection mutations. D6 starts no process.
The older finite all-VA equality receipts retain their original scope.

D5's initial mapped verifier fails on two plain-surface points whose containing
region grows (581632 to 1056768 bytes in Debug; 61440 to 421888 in Release).
The same address, allocation base, state, type and protection persist. The
point contract now requires containment before and after, unchanged allocation
identity/attributes and a nonshrinking region; it does not equate region size
with resource size. V6 adds changed-allocation and out-of-region negative
controls. D7 passes the unchanged native F1 files using this calibrated point
verifier. D5 and its original code remain archived; no arbitrary exception is
suppressed and the full-VA failure is not converted to PASS.

| Configuration | Clone-private committed bytes: warmup / epoch 1 / epoch 2 | Shutdown | Private no-growth | USER/GDI criterion |
|---|---|---:|---|---|
| Debug | 256495616 / 265924608 / 267399168 | 188674048 | False | False |
| Release | 232525824 / 258531328 / 255889408 | 178057216 | False | False |

Diagnostic generation/call metadata is deliberately retained by the observer
and included in these totals. The seed devices initialize D3D before graph
startup; PSS adds observer activity inside Lock. No production leak attribution,
timing improvement, native-heap leak freedom, physical GPU release or physical
presentation claim follows from this instrumented run.

## Remaining criterion mapping

| Criterion | Existing and new verified evidence | Missing evidence / condition for PASS |
|---|---|---|
| Native settings/overlay/toast and auxiliary owners | Earlier constructor/close/failure receipts preserved; F1 repeats affected live settings/graph closure | Completed scoped checks stay closed; whole native ownership is not inferred from two window types |
| Full graph managed retention and graceful teardown | F1/D3, 100 post-warmup lifetimes/configuration, zero of 9,483 weak owners; four graph HWND generations | Unmodified startup/fixed-singleton ownership remains outside the adapters |
| D3D9 registered private owners and map points | V1–V3 method calibration; D4 owner generations; D7 actual in-lock PSS omissions | Complete resource implementations, COM destruction and backing allocation identity/lifetime remain unproved |
| D3D11 destruction | Earlier native notifier controls preserved | Full-graph and physical allocation release are not supplied by D3D9 private-data callbacks |
| Complete native/graphics memory | Eight omitted and six present actual graph points; 48 native point controls | Source covering omitted/unmapped resources and physical allocation generations; VA regions cannot substitute |
| Atomic native heap / logical ownership | Earlier CLR stacks, heap controls and replay boundaries; new shared-commit calibration | Atomic allocator decoder, pre-attach history and logical-owner contract; shared VA state cannot be assumed frozen |
| Whole-process and USER/GDI no-growth | F1/D3 records both criteria false in Debug and Release | Attributed equivalent workload and measured no-growth; observer totals do not justify production optimization |
| Minimal WPF / adapted full graph / hard crash | Distinct earlier scopes preserved; current-production C3 six-crash receipt rechecked | OS reclamation remains separate from managed cleanup; unchanged successful crash runs are not repeated |
| Unmodified shell/startup/membership/wallpaper | Existing isolated profile/BAML/write/membership adapters | Suitable disposable shell/user/VM evidence; current foreign Explorer and input remain untouched |
| PERF-010/021 | EXECUTION-R2 and actual-DPI restoration preserved | Attributed GPU execution and non-client/full-frame presentation identity, then candidate GPU A/B |

The available hardware-method, in-lock map/PSS, shared-section native controls
and affected full-graph observations have been executed. The next useful
memory step requires a verifiable resource/backing-allocation lifetime source
or an atomic allocator/logical-owner decoder, calibrated before another graph
claim. The current sources do not provide that contract. Repeating these
captures or CPU admission, elevation or display changes would not supply it.
PERF-016 stays IN_PROGRESS; no whole-ID BLOCKED or artificial ACCEPT is created.

## Runs, changes and integrity

New work comprises ten isolated native control processes, two graph processes,
two owned target processes, 62 in-lock PSS clones (48 controls plus 14 graph),
ten ordinary epoch clones and four shared-control clones: 90 owned process
identities. Four native tool builds invoke the compiler 30 times across
Debug/Release; four graph/target builds succeed. All primary native executions
are sequential, with exact command, UTC times, environment, PID, binary/source
provenance and raw logs recorded. P5 is one failed preflight with no process.
New TRX, hard-crash, dedicated DPI and ETL runs: zero. Offline receipt replay
and negative controls are not counted as fresh application tests.

Only native observer/fixture, runner/verifier and documentation files change.
Production remains the 2,155-file inherited state including 72 winman-windows
files and five previously justified corrections; no new production delta or
test-project source delta occurs here. PERF-002 reconciliation remains 250 ms.
No new production candidate, A/B claim or unchanged C2 delivery rerun is needed.
Existing failed runs and dirty/untracked files remain intact.

Entry/final audits verify HEAD/tree/submodules, root/index diff checks,
production comparison, document byte/line/SHA256 values and ledger integrity.
The prior D3D seal is rehashed (18,621 files / 2,548,846,773 bytes); old toast,
DPI-restoration and current-production C3 receipts are rechecked, not rerun.
Ledger remains 41,782,945 bytes / 41,325 data rows, SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
The entire old ledger is the identical prefix and suffix length is zero.
Finalization verifies absence of the recorded own process identities, checks
receipt overwrite refusal and double-hashes fresh evidence. All 32 ETL paths
and sizes remain unchanged; WPR/session queries are read-only. No new tracing
or full ETL hash pass is claimed.

Principal commands (use new IDs for new work; existing receipt paths refuse
overwrite):

```powershell
python scripts/performance/Build-D3DLifetime.py --id FWM-D3D-MAP-20260913-T3
python scripts/performance/Run-FullGraphLifetime.py --id FWM-D3D-MAP-20260913-F1 --owned-membership --warmup 50 --modes graceful --pss-tool artifacts/performance/FWM-PSS-HEAP-20260913-T5 --d3d9-tool artifacts/performance/FWM-D3D-MAP-20260913-T3 --d3d9-mapped
python scripts/performance/Verify-PssShared.py --id FWM-D3D-MAP-20260913-V5
python scripts/performance/Verify-PssSharedGraph.py --snapshot artifacts/performance/FWM-D3D-MAP-20260913-F1 --id FWM-D3D-MAP-20260913-D3
python scripts/performance/Verify-D3D9Graph.py --graph artifacts/performance/FWM-D3D-MAP-20260913-F1 --pss-proof artifacts/performance/FWM-D3D-MAP-20260913-D3/verification.json --calibration artifacts/performance/FWM-D3D-MAP-20260913-V3/verification.json --id FWM-D3D-MAP-20260913-D4
python scripts/performance/Verify-MappedPss.py --id FWM-D3D-MAP-20260913-V6
python scripts/performance/Verify-MappedPssGraph.py --graph artifacts/performance/FWM-D3D-MAP-20260913-F1 --graph-proof artifacts/performance/FWM-D3D-MAP-20260913-D4/verification.json --point-calibration artifacts/performance/FWM-D3D-MAP-20260913-V6/verification.json --id FWM-D3D-MAP-20260913-D7
```

Receipt SHA256:

- R0: `EF66C5A11C00946887A908503EC489E3CDD8BD70C52117C8812CBFAC4D39AD09`.
- B1: `71237DE0522A27349FF3A4B0160221378AFAD7C77BBFF710833D7735AA427675`.
- V1: `7D316E3695198AADC8560C33FDEB90EAADAC3E890923F6BF5789E14CF2F216F0`.
- V2: `5C5C2DCA81A1110CBA632141C7AF8C0FFB9868AB95905F925259AF7F9591448E`.
- V3: `C02D61900F1995B2F6266CE18B72620BECD0D28F036714EA19DC557A50F8F489`.
- V4: `20A039D7263D6C674A2EB7E44559D668C8B509689D39760930777FADEDE37B5B`.
- V5: `09847EF4D105C34C696346D0554D75FB34C12398D227464A6BFC80C01380B3BD`.
- V6: `4843312A0849060DC320D656F08DE3C1A58C083F13D8D261505048768D05F988`.
- D3: `79A4A4A229563069DAF49DF2748DA89E8404EEDF541F3EC8E97CD18E79F6C5E3`.
- D4: `0ECAC56745C3C9D3F12EFE33BD4B72C5E22CC7C6D4B374895F43A6B453DE09D8`.
- D6: `53A85E444E0560E9192721EE813EBD043C74FEF9B76B6EF1BAFCE180CEFE1133`.
- D7: `3297EBFF849AF058B4986A1D004B08F863312D774C3A8E57F83876D42453A000`.
