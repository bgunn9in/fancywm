# PERF-010/021: preview-window set conversion — 2026-09-14

Bounded follow-up authorized after `43dcfcc`. The previous tab-order optimization
and user-confirmed feature closure are unchanged.

## Finding and local change

Even when `CreateWindowDropPlan` returns the same accepted plan, the service
calls `plan.PreviewWindows.ToHashSet()` for each preview refresh. The plan owns
an immutable read-only list of one or two windows. Its conversion allocates an
enumerator in addition to the new HashSet and its storage.

`MasterSatelliteDropPlan.CreatePreviewWindowSet()` now allocates the set with
the known capacity and adds the current list entries by index. The service uses
that method. This only removes the enumerator; the set is still rebuilt on
every call, with default IWindow equality, the original insertion order and
fresh hashing/deduplication. No cache, native window admission rule or new
invalidation contract was added. Reusing the immutable plan is not sufficient
evidence that arbitrary IWindow equality/hash implementations are unchanged.

## Before/after counter

`PreviewWindowSetAllocationCounterScenario` uses the existing managed window
fixture (no Moq allocations in the measured section) and accepted production
drop plans. Eighteen cases cover Horizontal/Vertical/mixed layouts, left/right
master and satellite reorder, promotion and master-side movement. Repeated
planning is checked to return the same plan before the conversion measurement.

The baseline delegate executes the exact old expression
`plan.PreviewWindows.ToHashSet()`; the candidate calls the production method.
Both consume the same list and allocate fresh sets. Each case has 32 warmup
calls per variant and three alternating pairs of 1,000 conversions per variant.
`GC.GetAllocatedBytesForCurrentThread()` brackets only conversion and result
count consumption. Planning, assertions and output are outside the measurement.
The test checks equal member order and an unchanged live layout.

Release .NET 10 x64 with `DOTNET_TieredCompilation=0`:

| Scope | Before | After | Reduction |
|---|---:|---:|---:|
| One conversion, one or two members | 208 B | 176 B | 32 B |
| Each case, 3,000 conversions | 624,000 B | 528,000 B | 96,000 B |
| All 18 cases, 54,000 conversions | 11,232,000 B | 9,504,000 B | 1,728,000 B |

Final Debug regression also reduces allocation in every case: aggregate
11,232,000 → 9,504,048 B. Its candidate total is 48 B above Release; exact
aggregate counts are retained rather than presenting both runs as identical.

This is a small reduction in managed allocation for set conversion. It does
not remove repeated set creation or demonstrate faster dragging, a reduction
in total application memory, CPU time or GPU work. No timing or UI claim is made.

Reproduce with:

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet test FancyWM.Tests/FancyWM.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~PreviewWindowSet --logger 'trx;LogFileName=counter.trx'
```

The two new tests also exercise an unchanged plan whose windows' hash/equality
changes: distinct → equivalent → distinct, independent published sets,
unchanged plan contents, and empty/singleton lists. Existing regression covers
both features, mouse-up revalidation, MinSize, focus, preview invalidation,
canonical invariants and rollback. Their production paths and the 250 ms window
reconciliation were not modified.

Final regression counts are in PERFORMANCE_STATUS.md. Raw logs, TRX and the
machine-readable summary are ignored under
`artifacts/performance/FWM-PREVIEW-WINDOW-SET-20260914/`.
No application UI, native overlay or interactive user check was launched for
this measurement. Whole PERF-010/021 research acceptance remains unchanged.
