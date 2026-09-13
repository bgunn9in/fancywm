# PSS native memory and coverage boundaries — 2026-09-13

Current checkpoint: `FWM-PERF016-PSS-20260913-R1`.
PERF-016 remains **IN_PROGRESS**. ProductionChanged=false,
PerformanceClaim=false, StageAccepted=false and LedgerAppended=false.
The five inherited production corrections and PERF-002's 250 ms reconciliation
remain unchanged. Earlier next actions are historical where superseded here.

PSS now supplies verified **clone VA observations**, with an independently
reproduced coverage limit: real mapped D3D11 buffers are absent from the clone.
This is not a complete process-private-memory or native-heap snapshot PASS.
F3's affected full startup graph lifetime/teardown passes; its clone-private VA
no-growth gate is false in both configurations. USER/GDI passes in Debug and
fails with growth in Release. These failures remain in the receipts.

## New native evidence

Artifact prefix below: `artifacts/performance/FWM-PSS-HEAP-20260913-`.

- `P3/P4`, verified by `V1`: two native producer processes, two epochs each,
  208 known blocks total (192 HeapAlloc, 16 VirtualAlloc). Three reads of every
  clone block preserve its original bytes while independent raw source-memory
  files prove the live buffers were overwritten. Native clone maps remain equal.
  All four clones disappear while the producer is still running.
- `F3`, verified by `D2`: Debug/Release production Startup.Main graphs on own
  non-input desktops, each with 50 warmup + 100 post-warmup lifetimes. All
  9,483 workload weak references clear after public WPF cache maintenance on
  the live dispatcher. Every acquired graph HWND generation closes. Settings,
  targets, hooks, workers, Mica/provider and Application-before-dispatcher
  shutdown pass. Each process captures startup, epochs 0/1/2 and shutdown;
  all ten clones are released before the source process exits.
- `D3`: independent interval and 4-KiB page-set calculations agree for every
  clone-private committed range and epoch delta. Address occupancy does not
  identify allocation generations, native logical owners or native heap blocks.
- `P5/P6`, verified by `D4`: 48 ordinary private allocations at 4 KiB, 64 KiB
  and 1 MiB, including read-only, read/write, write-combined and non-cached
  protections, are all readable with exact contents in their clones.
- `P7/P8`, verified by `D4`: two own hardware D3D11 devices, no swapchain,
  six mapped dynamic vertex buffers per configuration. All 12 mappings remain
  committed MEM_PRIVATE/PAGE_READWRITE|PAGE_WRITECOMBINE in the live source,
  with exact raw contents, but their addresses are MEM_FREE in the clone.
  ReadProcessMemory returns 299 (`ERROR_PARTIAL_COPY`). All snapshots are
  freed before unmapping/releasing the owned buffers and D3D interfaces.
  This establishes a real source-coverage limit, not a GPU leak or presentation
  result. No driver, HWS, security policy, display setting or user input changes.

The successful controls and graphs use the exact T5 observer DLL for the
matching Debug/Release configuration. T6/T7 add separate control executables;
their rebuilt observer copies are not substituted into the experiments.
Source/binary manifests, native compiler commands, environment logs, native
process order, raw bytes and stdout/stderr are retained. D2/D3 commands and
offline logs are also retained. There are 21 rejecting evidence mutations
across V1/D2/D4 and four final receipt-overwrite rejection checks.

## What the native snapshot does and does not cover

[PSS capture flags](https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/ne-processsnapshot-pss_capture_flags)
describe cloneable pages and a separately captured VA-space inventory.
The raw source-space map and clone map are therefore retained separately;
neither is silently substituted for the other. The clone is never explicitly
resumed. Its recorded CPU times are zero; this is not a general execution proof.
Shared mapped/image pages are not claimed to have private-page immutability.

The source/clone discrepancies below are independently accounted for in
`D4/graph-omissions.json`. Most missing bytes have protection 0x404. D3D11
controls reproduce this combination, while ordinary VirtualAlloc controls
prove that protection alone does not imply omission. No specific resource
owner is assigned to a graph interval merely from that protection or analogy.

| Configuration / epoch | Clone private committed bytes | Source-map private committed bytes |
|---|---:|---:|
| Debug 0 | 225,939,456 | 265,154,560 |
| Debug 1 | 240,893,952 | 280,109,056 |
| Debug 2 | 243,834,880 | 280,690,688 |
| Debug shutdown | 158,560,256 | 195,395,584 |
| Release 0 | 249,544,704 | 276,963,328 |
| Release 1 | 253,755,392 | 281,174,016 |
| Release 2 | 250,568,704 | 277,987,328 |
| Release shutdown | 165,543,936 | 192,942,080 |

Reserved VA is also recorded; reservation is not committed memory or a leak.
Clone-private totals include runtime, native allocator and other private
allocations without a logical-owner classification. Decreased totals at
shutdown do not prove that all resources were reclaimed. Fixed startup owners
are intentionally referenced by the fixture, and runtime/driver caches remain.

The explicit full-graph adapters are unchanged: isolated profile, exact BAML
URI, intercepted WindowArranging writes, a 1500-ms post-App.Run disposal probe,
and own-target virtual-desktop membership. Passive last SettingsWindow,
view-model and page-navigation references remain before WPF cache maintenance.
No extra 45-second GUI quiescence or 30-second stability admission is used.
Actual display/DPI values are observations only; this is no repeated PERF-010
DPI experiment or performance comparison with older fixture variants.

Toolhelp heap enumeration on every inspected own clone returns error 5
(`ERROR_ACCESS_DENIED`). An empty result is never called a heap PASS.
`S1/source-contract.json` records the checked debugger paths and available
DLL hashes. Neither the documented region fields nor the successful native
controls provide a native allocator decoder or logical ownership identity.
See [Heap32First](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-heap32first),
[region information](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/ns-memoryapi-win32_memory_region_information)
and [protection values](https://learn.microsoft.com/en-us/windows/win32/memory/memory-protection-constants).

## Preserved failures and fixture corrections

T1/T2 are preparatory builds with no native executions. T3 adds raw proof of
actual source-memory mutation. P1 (T3) fails the assumption that an extra
duplicated clone handle becomes signaled after PssFreeSnapshot. P2 (T4) fails
an immediate PID-absence assertion after closing that extra handle. Both full
runs remain intact. T5 records the held-handle observation, closes it, and
checks bounded clone-PID disappearance while its source remains alive. The
successful runs require actual disappearance, not source-process termination.
This is fixture correction; [PssFreeSnapshot](https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/nf-processsnapshot-pssfreesnapshot)
does not promise the rejected extra-handle wait assertion.

F1's missing C# imports cause a build failure before any graph process starts.
F2 completes two processes but fails strict graph-generation coverage because
the existing HWND journal was gated on the old heap observer. D1's frozen
verifier is retained, and D0/D1R preserve a fresh offline reproduction of that
failure. F3 enables the same journal for PSS, then repeats the affected native
Debug/Release graphs. F2 is not promoted to full graph PASS.

No production defect or optimization is inferred from these fixture/source
limits. There is no production delta, A/B candidate, new delivery gate or
ledger append. Failed fixtures, old snapshots and rejected evidence remain.

## Current criterion mapping

| Criterion | Existing/new evidence | Missing proof and PASS condition |
|---|---|---|
| Native toast/settings/overlay/auxiliary closure | Prior receipts retained; F3 affected settings and all graph HWND generations | Scoped constructor/failure/ownership boundaries remain; no standalone toast repeat |
| Full startup graph managed retention/graceful teardown | F3 Debug/Release, zero of 9,483 workload weak owners; native owners and provider teardown complete | Unmodified startup and fixed-singleton ownership are not inferred from adapted graphs |
| Stable ordinary private clone contents | V1 and D4 exact bytes, independent source mutation, live-source clone release | PASS for stated allocations; shared memory and omitted mappings excluded |
| Complete native/graphics memory snapshot | D3 source/clone differences; D4 real D3D mapped-buffer omissions | Need a source covering those mappings with verified resource/lifetime identity; PSS alone fails completeness |
| Native heap blocks/logical ownership | Prior CLR/native stack witnesses retained; own Toolhelp clone queries denied | Need a calibrated decoder and valid allocator-state/ownership contract, including pre-attach history |
| Whole-process memory no-growth | F3 clone-private totals and independent occupancy analysis | No-growth false; address sums cannot establish heap/GPU leak freedom or identify a production defect |
| USER/GDI | F3 Debug passes, Release growth; prior scoped F11 retained | Broader no-growth remains unproved; do not replace failed runs or select favorable baselines |
| Minimal WPF/full graph/hard crash | Earlier separate receipts and current-production C3 six-crash receipt retained | No new crash-handler/production change; OS reclamation remains distinct from managed cleanup |
| Unmodified shell/startup/membership/wallpaper | Prior environment evidence; explicit safe adapters preserved | Appropriate disposable isolated shell/user/VM still required |
| PERF-010/021 GPU execution/presentation | EXECUTION-R2 and actual DPI restoration retained | New attributed execution/presentation identity contracts, then candidate GPU A/B |

All newly available PSS payloads, clone/source discrepancies and directly
applicable native controls have been executed and verified. Another identical
capture, CPU admission, elevation or display change cannot supply the absent
mapping/ownership contract. The next evidence-dependent step is a calibrated
native allocator/resource ownership source covering the omitted mappings and
frozen allocation state; it must pass ordinary/D3D controls before a new graph
claim. PERF-016 remains IN_PROGRESS, with no whole-ID BLOCKED or ACCEPT stage.

## Integrity and commands

R0 rehashes the prior CLR seal: 10,218 files / 16,412,010,261 bytes. Finalization
rehashes it again and checks current-production C3, original toast and DPI
receipts. This is old evidence verification, not new native tests.
New work totals eight native control sources, six inspectors, four graph and
four target processes, plus 34 PSS clones. Two graph processes have strict
lifetime receipts; two retain the fixture coverage failure. Seven native tool
builds contain 48 compiler invocations; eight graph/target builds succeed and
one graph build fails. New TRX, hard-crash, dedicated DPI and ETL runs: zero.

No tracing session is created. All 32 existing ETL paths/sizes match the prior
fully hashed E2 inventory; a new full ETL hash pass is not claimed. Final
read-only WPR/session state and all 56 own process/clone identities are checked.
Git/root/index/submodules, HEAD/tree, 2,155 production files (72 winman-windows),
control document sizes/lines/SHA256 and the five inherited deltas are rechecked.
Ledger: 41,782,945 bytes, 41,325 data rows,
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
The entire old ledger is the byte-identical prefix; suffix length is zero.

Principal successful commands (exact invocations and environments accompany
the immutable artifacts):

```powershell
python scripts/performance/Run-PssHeapControl.py --id FWM-PSS-HEAP-20260913-P3 --tool artifacts/performance/FWM-PSS-HEAP-20260913-T5 --config Debug
python scripts/performance/Run-PssHeapControl.py --id FWM-PSS-HEAP-20260913-P4 --tool artifacts/performance/FWM-PSS-HEAP-20260913-T5 --config Release
python scripts/performance/Run-FullGraphLifetime.py --id FWM-PSS-HEAP-20260913-F3 --owned-membership --warmup 50 --modes graceful --compact-targets --pss-tool artifacts/performance/FWM-PSS-HEAP-20260913-T5
python scripts/performance/Run-PssProtection.py --id FWM-PSS-HEAP-20260913-P5 --config Debug
python scripts/performance/Run-PssProtection.py --id FWM-PSS-HEAP-20260913-P6 --config Release
python scripts/performance/Run-PssProtection.py --id FWM-PSS-HEAP-20260913-P7 --config Debug --d3d
python scripts/performance/Run-PssProtection.py --id FWM-PSS-HEAP-20260913-P8 --config Release --d3d
```

Receipt SHA256 values:

- V1: `25F6E3E9F4461ED2F1627A9C3AE599E83F8B5C597F918B6B8B82FDE462921291`.
- D2: `870F5E389543A014A4371254E547F3D05313633F734DEDE5CAD9724B9AC030E2`.
- D3: `3E1DF0687C0D5210B0E32B70C4B1A96DC922CABB72A06F7D91C4647E4CB10E74`.
- D4: `1F2A21DA355EFEACDA9B1773313B814D7BEED5322BEF1F918F405FBD5F29A79F`.
