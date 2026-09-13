# Padding-only overlay reuse — 2026-09-12

PERF-010 / PERF-021 candidate C1 retains settled overlay models and WPF
elements when only `WindowPadding` changes. Five alternating pairs show fewer
managed allocations and lower elapsed time in every comparison. Native behavior,
application CPU/GPU and presentation remain E2E_PENDING; this is not whole-ID
acceptance and no optimization-ledger rows have been appended.

The subsequent [nested-notification correction](OVERLAY_NESTED_NOTIFICATION.md)
now resolves this stage's retained collection/visual mismatch in both no-HWND
WPF surfaces. It preserves padding reuse and records its own common fixture,
performance regression comparison and delivery gate; the original evidence
below remains unchanged.

## Change and common fixture

The change is confined to `TilingService.cs`, `TilingService.Private.cs` and
`TilingOverlayRenderer.cs`. Panel padding/spacing and layout still propagate.
The renderer retains a completed model snapshot for a padding-only update and
advances its invalidation epoch. Active update depth or pending failure recovery
forces the original complete invalidation. Height changes, settings retries and
display scaling retain the complete path. The interface default also retains
complete invalidation for other renderer implementations.

The common fixture calls the real service settings publication, layout and
renderer methods, then materializes the compiled `TilingOverlay` XAML in an
isolated generic WPF `Application`. It uses 1/10/50 fake window nodes, fixed
96-DPI model resources, padding 4/12 and panel height 22. One registered fake
window creates the service tree; other nodes are attached while layout scheduling
is held. Discovery, native positioning, icon discovery and HWND creation are
excluded. Source files for the tests are byte-identical in B1 and C1.

Four tests cover:

- Model and complete materialized visual-tree identity through padding changes
  and duplicate publication, with panel/window geometry, child order, focus and
  preview values, and unchanged event subscription additions.
- Real WPF window/tab/SVG materialization and managed allocation/elapsed counters.
- Managed cancellation during add/remove/persist callbacks and successful retry.
- Update failure, settings-propagation failure/retry, height/scaling invalidation,
  terminal disposal and balanced window/cursor subscription release.

B1 passes 3/4 and fails the intended model-identity assertion. C1 passes 4/4.
The unmodified baseline source is retained in B1; C1 differs in exactly the three
declared production files. The independent verifier checks 5,721 frozen source
and binary files, all ten raw TRX results and the exact 360-row counter multiset.

## Measurements

M2 runs ten sequential Release x64 test processes, with order AB / BA / AB / BA /
AB, tiered compilation disabled in both variants, six equal warmups and twelve
measured changes per size. Setup and observation/assertion work are outside the
timed interval. The interval includes the real settings/layout/renderer calls,
fake-adapter overhead and WPF layout. These are per-update medians across five
processes; MB uses decimal bytes.

| Windows | Baseline MB/update | Candidate MB/update | Baseline ms/update | Candidate ms/update |
|---:|---:|---:|---:|---:|
| 1 | 2.507 | 0.065 | 25.370 | 0.727 |
| 10 | 15.089 | 0.259 | 165.318 | 1.867 |
| 50 | 70.493 | 1.300 | 806.236 | 9.622 |

Allocation and elapsed comparisons each improve in 15/15 pairs. Every baseline
update creates N+1 models, N window controls, N tabs, 5N+1 SVG controls and 6N
event subscriptions. Candidate counts are zero at all three sizes. Both variants
settle at N+1 models and N window controls, pass geometry checks and release their
owned subscriptions. This does not measure native heap, retained memory, DWM,
hardware presentation or whole-process CPU.

## Retained failures and limits

R0 preserves fixture-development logs. Dev1 is a compile error; dev2 includes a
fixture timeout and overly broad disposed-panel assertions. Dev3 exposes a
separate pre-existing WPF nested-notification problem: interrupting an observable
collection update with a synchronous clear can leave 12 materialized window
controls for 10 settled models. Dev4 also demonstrates that a canceled persisted
model may finish assigning fields after its window ownership has been released.
Neither is reported as a new candidate failure or silently treated as passing
visual evidence. The final reentrancy test explicitly exercises managed ownership
without attaching WPF item controls. The materialization test separately covers
completed, non-reentrant passes. Native/reentrant visual behavior needs follow-up.

M1 is rejected runner-development evidence: it expected 39 counters while the
fixture emits 36. The first baseline test itself passed. M2 corrects only the
runner cardinality, uses the same B1/C1 binaries and starts all five pairs afresh.
No partial M1 rows are accepted.

## Receipts and next gate

All IDs below are under `artifacts/performance/`:

- `FWM-OVERLAY-PADDING-REUSE-20260912-R0`: pre-edit source and retained development failures.
- `FWM-OVERLAY-PADDING-REUSE-20260912-B1`: frozen baseline/common fixture, 3/4.
- `FWM-OVERLAY-PADDING-REUSE-20260912-C1`: frozen candidate/common fixture, 4/4.
- `FWM-OVERLAY-PADDING-REUSE-20260912-M1`: rejected runner cardinality.
- `FWM-OVERLAY-PADDING-REUSE-20260912-M2`: ten processes, 360 counters, independent PASS.
- `FWM-OVERLAY-PADDING-REUSE-20260912-C2`: delivery validation of the unchanged candidate.

[Independent M2 receipt](../../artifacts/performance/FWM-OVERLAY-PADDING-REUSE-20260912-M2/independent-verification.json)
SHA256: `74478D49D8A4B8335732B938781747DE1E322C8236224A68EB1A09AE28E5A7C5`.
Raw counter SHA256:
`9E2A3C42648A6426009DD0F9DC7D050C839DEDBA716A1F6CB6DF5E29093DFE41`.

C2 passes the complete Debug suite (2,047 application / 160 layouts / 31 theme)
and Release suite (2,103 / 160 / 31), both restores, GUI and RID-aware x64/package
builds, clean pinned dependency replay and an independent M2 reverification.
Its executable/test sources match measured C1 exactly. The archived Release
MSIX is 73,110,248 bytes, SHA256
`FFF790D8351E7977DE9DBAE5D87F228AE81448D7EDFEB4272E9EEF848E0A99D1`.
The embedded `FancyWM.GUI/FancyWM.dll` matches the RID build and has AMD64 PE
architecture. The package was built only; installed/launched/published are false.
[C2 delivery receipt](../../artifacts/performance/FWM-OVERLAY-PADDING-REUSE-20260912-C2/validation/validation-summary.json)
SHA256: `615461149CDDD6DBE620E8B15EA2106734DD6759523FAA96C613FBDFFA9809E4`.

Next native gate: prepare the unchanged full-app fixture for this candidate,
repeat behavior validation and then collect isolated baseline/candidate traces
with matching local desktop/DPI controls. Preserve the separate DWM border
identity and full-visible-frame requirements from M6/D6. Keep animation batching
and virtual-desktop caching out of this candidate. The retained WPF collection
reentrancy case is now corrected in the subsequent stage linked above; native
full-app validation must use that corrected candidate.
