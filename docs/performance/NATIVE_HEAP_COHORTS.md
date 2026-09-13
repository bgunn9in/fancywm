# PERF-016 complete baseline heap replay — 2026-09-13

Checkpoint `FWM-PERF016-COHORT-20260913-R1` continues the verified
[native heap checkpoint](NATIVE_HEAP_LIFETIME.md) using retained evidence only.
PERF-016 remains **IN_PROGRESS**. There are **zero new native application,
TRX, crash, DPI or ETL runs**, no production change, no performance claim,
no stage acceptance and no ledger append. Old native runs are not counted as
new tests. Every new artifact uses `FWM-HEAP-COHORT-20260913-` plus its suffix.

## New proof and its boundary

The previous replay verified traced allocations but left pre-attach blocks
unknown. The existing locked epoch-0 HeapWalk snapshot supplies their exact
initial addresses and sizes. The new replay seeds these blocks with explicitly
unknown allocation stacks/TIDs, then applies the existing native event stream.
It requires the **complete busy address/size set**, rather than a traced subset,
to equal each subsequent locked snapshot. Native generation retirements,
births, reallocation successors and block/byte conservation are checked.
No missing post-baseline free or reallocation origin is silently accepted.

This closes the complete-default-heap inventory/lifetime replay criterion
**from epoch 0 through the observed epoch 1/2/shutdown boundaries**. It does
not reconstruct pre-baseline allocation stacks or identify logical owners.
Native generation retirement is distinct from logical owner cleanup:
[HeapReAlloc may move a block and preserve its contents](https://learn.microsoft.com/en-us/windows/win32/api/heapapi/nf-heapapi-heaprealloc).
The observer's actual native control includes the inner allocation/free and
outer realloc events, so a freed old address may have a live successor.
Neither this result nor a post-dispatcher snapshot proves heap leak freedom.

## Calibration and independent witnesses

T1/T2 replay the unchanged E3 native control. Both exactly reconstruct the
owned private and default heap snapshots and all **48 moved realloc pairs**.
The held-to-resized control explicitly finds 24 retired generations with 24
live successors per heap; the final released control finds none. Thirteen
negative controls reject missing allocation/free/epoch/baseline block, wrong
PID/size and extra live blocks. The empty private baseline cannot supply a
missing-baseline-block control; that control is applied to the default heap.

T1 is preserved. T2 validates the identical model after an output-only producer
correction: compute the selected generation set once before exporting deaths.
This avoids rebuilding the same set for every death; it changes no replay
semantics and is not a production optimization or performance claim.
T2 receipt SHA256:
`6EA0791D41C69C11AC0C95C711D4E65A29AB572A327860DDC230E15F785704A9`.

D1 replays existing F4 Debug/Release native streams and all eight snapshots.
The calibration source hashes are required unchanged. It records complete
generation witnesses in a fixed 64-byte schema, with address, size, generation,
birth QPC/TID, current allocation-stack QPC, seeded flag and heap identity.
Seeded initial blocks explicitly have no original allocation-stack claim.
D1 receipt is `SCOPED_COMPLETE_DEFAULT_HEAP_BASELINE_REPLAY_PASS`, SHA256:
`BA884E71E59714729DB1530F4FB848983C53C3341E159ED031361D486A5EE0D4`.

D2 reproduces D1 with the same calibrated model and checks its exports against
the raw native data separately: full HeapWalk sets, individual allocation births,
retirement events, and realloc old/new address, size, thread and QPC identities.
It exports the birth/death metadata for every related realloc generation,
including transient generations absent from the four snapshots. All
**771,654 phase rows**, **160,560 allocation/stack witnesses**, **156,137
retirement witnesses**, and **73,811 realloc-successor witnesses** pass.
These are repeated observations of some same blocks across epochs, not that
many unique live owners. Twelve further negative controls reject missing
epochs/baseline blocks/retirements, wrong birth QPC, wrong successor, and a
retired generation falsely surviving in a later snapshot.
D2 receipt is `SCOPED_NATIVE_COHORT_WITNESSES_PASS`, SHA256:
`469819EEEFD514B5E65A8FE1503918FF1FE5855D2494BDAF645131B854AFA0A0`.

## Verified lifetime decomposition

All rows below refer to the original F4 native processes, not new executions.
"Original survivors" means the same native generation still present since
epoch 0; "new live" excludes those original generations.

| Configuration / boundary | Original survivors | Original generations retired | New live generations | Total busy blocks |
|---|---:|---:|---:|---:|
| Debug epoch 0 | 90,360 | 0 | 0 | 90,360 |
| Debug epoch 1 | 88,258 | 2,102 | 3,459 | 91,717 |
| Debug epoch 2 | 88,237 | 2,123 | 3,498 | 91,735 |
| Debug shutdown | 87,492 | 2,868 | 4,284 | 91,776 |
| Release epoch 0 | 99,431 | 0 | 0 | 99,431 |
| Release epoch 1 | 96,771 | 2,660 | 5,068 | 101,839 |
| Release epoch 2 | 96,564 | 2,867 | 5,889 | 102,453 |
| Release shutdown | 95,967 | 3,464 | 6,376 | 102,343 |

Between epochs 1 and 2, Debug has 824,957 native generation births and
824,939 retirements (net +18 blocks); Release has 816,657 / 816,043 (net +614).
This includes transient generations between snapshots. Counts are diagnostic
observations under the existing intrusive heap tracing/observer and graph
adapters, with no timing or performance comparison.

The baseline-to-epoch-2 net growth is +1,375 Debug / +3,022 Release blocks.
All of the reported retired original generations have exact native HeapFree
witnesses. One original generation per configuration later has an observed
realloc successor during shutdown; neither successor remains live at that
final snapshot. Reallocation history is preserved rather than equating every
HeapFree with logical ownership completion.

## Current criterion mapping and remaining evidence

| Criterion | Verified evidence | Remaining proof / PASS condition |
|---|---|---|
| Native toast/settings/overlay/auxiliary lifetime | Earlier immutable receipts, including genuine HWND/dispatcher/order/failure and scoped weak retention | Preserve existing scoped PASS and constructor/cache-maintenance limits; no standalone repeat |
| Full startup graph workload and graceful teardown | Earlier F3/F4 dynamic HWND closure, 100 post-warmup lifetimes and 0/9,483 workload weak owners per process | Explicit startup/profile/BAML/global-setting/membership adapters remain |
| Complete default-heap baseline replay | New T2/D1/D2 cover every observed busy address/size and native generation lifetime from epoch 0 through shutdown | Scoped PASS; no pre-baseline allocation-stack or logical-owner claim |
| Native heap no-growth / leak freedom | Existing false no-growth gates now have exact native generation decomposition | Still unproved; persistent baseline and new runtime/observer/WPF allocations need logical ownership and complete relevant lifetimes, not only pointer retirement |
| Minimal WPF shutdown versus full graph versus hard crash | Earlier separate receipts, including current-production C3's six crash processes | OS reclamation is separate from managed cleanup; no crash repeat for this offline-only change |
| Unmodified startup / genuine Explorer membership / wallpaper | Existing read-only environment findings and adapter disclosures | Needs a disposable isolated shell/user/session/VM permitting the actual startup global-setting behavior; none supplied by these traces |
| Changing-display GUI no-growth | Existing mixed/false F3/F4 observations; earlier F11 compact/fallback scoped PASS retained | New controlled environment/lifetime evidence required; heap replay supplies no GUI counter PASS |
| PERF-010/021 GPU/presentation | Earlier EXECUTION-R2 and actual DPI restoration retained | New attributed GPU execution and presentation identity contracts, then candidate GPU A/B; no same-source repeat |

All available busy-inventory and realloc lifetime evidence in the retained F4
stream has now been replayed and checked. The next unmodified-startup criterion
still requires the isolated environment above. Pre-baseline stacks and managed
logical ownership are not present in this trace; further offline replay cannot
manufacture them. A new heap observation would need a concrete additional
ownership/lifetime source, rather than another identical capture. No new
production candidate is justified by these diagnostic counts.

## Integrity and commands

R0 independently rehashes all **28,800 files / 42,723,438,206 bytes** in the prior
heap seal and compares Git/production/ledger/control documents byte-for-byte.
Prior verification SHA256 remains
`1D6E923883AE7FA3ADA2D4E569F6157EDFD008F4A1357659B76E9688C755930C`.
This is receipt integrity verification, not new native testing.

Each new analysis records its exact command, Python PID, UTC start/end, exit
code, raw log hash and frozen source. Native input files remain in their old
immutable IDs; the new seal verifies their size/SHA256 against that prior
manifest. Exclusive-output negative controls reject attempts to reuse T2/D1/D2.
No earlier receipt, failed run, source snapshot or ETL is overwritten.

Final review checks root/index diff, recursive submodules, the pinned HEAD/tree,
all 2,155 production files (72 winman-windows) and the same five inherited
justified deltas. PERF-002 reconciliation remains 250 ms. Ledger remains
**41,782,945 bytes / 41,325 data rows**, SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`;
the full old file is the prefix and the suffix is empty. No native processes,
recordings, MSIX launches, policy changes, commits or publications are created.
