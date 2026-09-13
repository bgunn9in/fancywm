# Overlay invalidation during collection notifications — 2026-09-12

The retained padding-reuse visual defect is reproduced and corrected in the
real renderer and both compiled WPF overlay surfaces, without creating HWNDs.
The final common fixture goes from baseline B1 4/7 to candidate C1 7/7, and the
existing affected Release suite passes 87/87. This is a correctness change;
native full-app behavior and CPU/GPU/presentation remain E2E_PENDING.

## Cause and behavior

The earlier 12-controls-for-10-models result was not merely delayed WPF cleanup.
`InvalidateViewCore` tried to clear an `ObservableCollection` inside its current
add/remove notification. With multiple listeners, the collection rejects that
mutation before clearing its items. This matches the runtime's
[`CheckReentrancy` implementation](https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.ObjectModel/src/System/Collections/ObjectModel/ObservableCollection.cs).
The real baseline exception stack reaches that guard. Cleanup still disposed
models and cleared the renderer dictionary/snapshot; settings application then
logged the error. A subsequent update added new models beside the old collection
entries. An idle Dispatcher barrier did not repair the mismatch.

The shared regression removes or adds windows at 1/10/50 nodes. Before correction,
recovery after an interrupted removal produces 1/19/99 window controls; interrupted
addition produces 2/11/51. Even the one-window removal logs a settings error.
After correction, both variants settle at exactly 1/10/50 controls with zero
settings errors and empty collections at the canceled-pass boundary.

Only `FancyWM/TilingOverlayRenderer.cs` changes in production:

- Owned collection add/remove operations track their synchronous notification
  scope. An invalidation request immediately advances the cancellation epoch and
  rejects nested update admission.
- Complete cleanup waits until all current listeners return, then executes
  synchronously before the suspended mutation resumes. Repeated requests coalesce;
  no Dispatcher job, timer or background task is introduced.
- Disposal uses the same deferred cleanup boundary. A callback failure retains
  its original exception/stack; a later cleanup failure is attached separately.
  Failed deferred reset is retried before another snapshot is admitted.

The previously measured padding-only reuse path remains in place. Cancellation
from model-property callbacks retains its prior behavior; this change does not
claim to prevent every scalar assignment to an already released model.

## Shared tests and provenance

The new three-test fixture contains 24 cases:

| Cases | Coverage |
|---:|---|
| 6 | Padding publication during window add/remove at 1/10/50 nodes; collections, models, settings errors, immediate and post-Dispatcher visuals, geometry |
| 16 | Window/panel add/remove × invalidate/dispose × listener installed before/after WPF; tail-listener ordering, repeated requests, nested admission rejection, both surfaces and retry |
| 2 | Original add/remove notification error plus deferred reset error; exact primary identity/stack, supplemental error, cleanup, retry and stable subsequent ownership |

The original four padding tests also pass. The fixture uses the real service
publication/layout/renderer paths with fake windows, generic WPF application,
fixed resources and native icon discovery disabled. It verifies item-container
model identity in both `TilingOverlay` and `NonHitTestableTilingOverlay`, as well
as window/panel collections and subscription release. No test-only collection
clear or view replacement repairs the result under test.

Snapshot IDs use prefix `FWM-OVERLAY-NESTED-NOTIFICATION-20260912-`:

- R0: pre-edit source and the development reproduction, all three new tests red.
- B1: final common fixture and unchanged baseline production; four old tests pass,
  three new tests fail the intended visual/reentrancy/exception assertions.
- C1: identical test source, one declared production-file change, 7/7 targeted
  and 87/87 affected Release tests.
- M1: five alternating pairs of the already optimized baseline and the corrected
  candidate, ten sequential Release x64 processes, 360 counters.
- C2: delivery validation of unchanged C1 production and test source.

Unlike the earlier padding-stage M1, this separate nested-notification M1 is a
valid completed comparison; experiment IDs and directories are distinct.

## Performance regression comparison

Both sides already reuse padding models. The runner's explicit
`-BaselineReusesPadding` mode requires zero model/window/tab/SVG creation and
zero subscription additions in both variants. It independently enforces the
one-file production delta and identical common test sources. Warmups (six),
measured updates (twelve), sizes (1/10/50), disabled tiered compilation and
AB/BA/AB/BA/AB process order match.

| Windows | Before B/update | After B/update | Before ms/update | After ms/update |
|---:|---:|---:|---:|---:|
| 1 | 65,268.67 | 65,268.67 | 0.721 | 0.723 |
| 10 | 259,114 | 258,250 | 1.879 | 1.875 |
| 50 | 1,300,218.67 | 1,300,218.67 | 9.430 | 9.794 |

Values are medians across five processes. Allocations improve in 5/15 comparisons
and tie in 10/15, with no allocation loss; every materialization/subscription
counter remains zero. Elapsed time improves in 5/15 and worsens in 10/15. All
five 50-window comparisons are slower, with a 3.86% median increase. This
tradeoff is retained explicitly; there is no additional speedup claim. The
earlier padding-reuse allocation saving remains supported. These measurements
cover the controlled managed/WPF path, not native CPU, retained heap or latency.

The [independent receipt](../../artifacts/performance/FWM-OVERLAY-NESTED-NOTIFICATION-20260912-M1/independent-verification.json)
matches the exact raw TRX/counter multiset and verifies 5,732 frozen source/binary
files. SHA256:
`0B3071BFC32CB6562BE1015D4A92A2D2C22072E35B6C35AE9240CD08B2A3F56B`.
Counter SHA256:
`F8A987F5835573632462BE1D9F131C0CFC91853A016DA813A101B8E2784A66C1`.

## Delivery validation

C2 passes full Debug 2,050 application / 160 layouts / 31 theme tests and
Release 2,106 / 160 / 31, both restores, GUI and RID-aware x64/MSIX builds,
clean pinned dependency replay and independent M1 reverification. Production
and test source match measured C1 exactly.

The archived Release MSIX is 73,110,551 bytes, SHA256
`E49379419BA62398AAE3319A615FEE4A93C3634531F3DB9192B2DC26EC089880`.
Its embedded `FancyWM.GUI/FancyWM.dll` matches the built RID DLL and has AMD64
architecture. Installation, launch and publication are false.
[C2 delivery receipt](../../artifacts/performance/FWM-OVERLAY-NESTED-NOTIFICATION-20260912-C2/validation/validation-summary.json)
SHA256: `75D501338CC01FFD8E659BC9C068321D9701F13E87BACF9DCEF1B95E42ABA7BB`.

No optimization-ledger append or whole-ID status change applies. The corrected
production now passes native behavior in B16/V17 and, after a shared closed-HWND
fixture correction, B17/V19. Baseline B18/V18 also passes. M8 independently
verifies five alternating native pairs: 14 CPU wins, one tie and median
50-window process CPU 1,500 -> 210.9375 ms. This comparison includes padding
reuse and the correction together; the correction-only managed timing tradeoff
above still applies. [Native behavior, CPU and D8 identity limits](OVERLAY_FULLAPP_CPU.md).
Preserve M6/D6's independent border-identity/full-frame presentation gate.
