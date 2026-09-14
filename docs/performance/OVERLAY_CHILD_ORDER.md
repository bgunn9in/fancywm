# PERF-010/021: incremental tab order updates — 2026-09-14

One bounded optimization after the user-confirmed feature closeout in `a40cf6d`.
The existing overlay/model reuse remains in place. No second candidate or broad
performance audit was needed.

## Redundant work and change

`TilingOverlayRenderer.UpdateViewModel(TilingPanelViewModel, ...)` previously
called `ChildNodes.Clear()` and added every available child again whenever the
child order or membership changed. `TabBar` binds its WPF ItemsControl to this
collection. A permutation therefore discarded and recreated all of that panel's
tabs even though the renderer already owned the same window models.

The changed path moves existing collection entries, inserts newly participating
children and removes trailing obsolete entries. Unchanged lists retain their
existing check; surviving entries keep their models and materialized tabs.
The synchronizer reads the current tree and model dictionary within each call;
there is no new cache or invalidation key between calls.

Child notifications participate in the renderer's existing deferred invalidation
boundary. Reentrant invalidation cancels the pass, lets notification listeners
finish, then releases the owners. Listener exceptions retain their original
identity and the next public update uses the existing full recovery path.
Layout, command and drop rules, focus, MinSize, invariant/preflight/rollback and
250 ms reconciliation code were not changed.

## Comparable counter result

`OverlayReorderMaterializationCounterScenario` runs the production layout engine,
renderer, TilingOverlay, TabBar and TilingNodeTab templates. Each of six cases has
four warmup updates and 12 measured updates: upper satellite reorder, another
satellite reorder (across upper/wide slots in mixed mode), master side swap and
promotion, repeated three times. Each case starts with four windows.

Baseline: `a40cf6d` production plus the new counter test. Candidate: the same
workload with incremental child updates. Release x64 test process, .NET 10,
isolated STA WPF Application and identical theme resources; tiered compilation
disabled by the existing test harness. There are no HWNDs, physical gestures or
native icon I/O. Measurements count collection events and newly materialized tab
objects by reference, after each completed WPF layout.

| Mode / initial master | Updates | Reset before → after | Add before → after | Remove before → after | Move before → after | New tabs before → after |
|---|---:|---:|---:|---:|---:|---:|
| Horizontal / left | 12 | 15 → 0 | 39 → 6 | 0 → 6 | 0 → 17 | 39 → 6 |
| Horizontal / right | 12 | 15 → 0 | 39 → 6 | 0 → 6 | 0 → 16 | 39 → 6 |
| Vertical / left | 12 | 15 → 0 | 39 → 6 | 0 → 6 | 0 → 17 | 39 → 6 |
| Vertical / right | 12 | 15 → 0 | 39 → 6 | 0 → 6 | 0 → 16 | 39 → 6 |
| Mixed / left | 12 | 18 → 0 | 36 → 12 | 0 → 12 | 0 → 11 | 36 → 12 |
| Mixed / right | 12 | 18 → 0 | 36 → 12 | 0 → 12 | 0 → 10 | 36 → 12 |
| Total | 72 | 96 → 0 | 228 → 48 | 0 → 48 | 0 → 87 | 228 → 48 |

Replace events are zero in both runs. Total collection notifications decrease
from 324 to 183; newly materialized tabs decrease by 180 (78.9%). Tabs that move
to a different parent still require creation in that parent's ItemsControl.
This establishes fewer notifications and fewer tab constructions in this
workload, **not** whole-application latency, CPU, GPU or memory improvements.

Reproduce the counter with:

```powershell
dotnet test FancyWM.Tests/FancyWM.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~OverlayReorderMaterializationCounterScenario --logger 'trx;LogFileName=counter.trx'
```

The test forwards its `PERFCOUNTER overlay-reorder-*` lines to the parent TRX.
Local raw receipts are ignored under
`artifacts/performance/FWM-OVERLAY-REORDER-20260914/` (`before.trx`,
`after-local.trx`, `after-child.trx` and final regression reports).

## Correctness checks

The counter also verifies materialized tab order, geometry, focused/preview
windows, unchanged model identity and subscription count after every operation.
`OverlayChildOrderTest` covers unchanged refreshes, reorder, replacement,
removal/re-addition and exception/invalidation recovery at each collection
mutation boundary (seven cases).

Final regression results are recorded in PERFORMANCE_STATUS.md. Existing tests
cover both new features, including both master sides, mixed slot exchange,
keyboard and mouse entry points, preview, 2↔3↔4 satellites, MinSize, focus,
invariants, rollback and renderer lifetime/nested notification behavior.

No new interactive check was launched. The earlier manual feature confirmation
retains its stated scope; it does not verify unspecified displays, DPI or apps.
PERF-010/021 as whole research items retain their historical IN_PROGRESS status;
the local tab-update optimization is complete. Broader GPU/memory/provider work
remains deferred.
