# Full-app Dispatcher stack attribution — D7

The next optimization experiment is **overlay reuse when window padding changes**.
M6's measured 50-window transitions repeatedly destroy and recreate WPF overlay
elements. This is now supported by managed method names, matching native PDBs
and independently checked raw stack addresses. No production candidate is
applied or accepted by this diagnostic stage.

Artifacts: `artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-D7`.
The source, fixture and B15/M6 binaries are unchanged. The historical optimization
ledger remains byte-exact. Full-frame hardware presentation remains E2E_PENDING.

## Exact scope and coverage

The forty measured 50-window intervals come from D6's retained boundaries:
five app processes, eight transitions each, captured Dispatcher PID/TID, and
`StartQpc` through `DwmFlushedQpc`. The inherited xperf range rounds inward by
less than one microsecond at either endpoint. These intervals include settings,
layout, overlays and fixture observations; they do not isolate animation work.

The raw reader independently scans all **415,949** CPU sample events, matching
M6's xperf event statistics with zero lost events. It selects **47,929** samples.
TraceEvent's decoded export matches their complete multiset of PID, TID, QPC,
instruction pointer and non-process flag exactly.

A second raw reader scans all **11,320,391** StackWalk events, again matching
xperf statistics without loss, and retains 54,179 relevant fragments. The
independent frame checker permits only a complete raw stack or an exact raw
kernel fragment followed by an exact raw user fragment at the same PID/TID/QPC.

| Raw-sample outcome | Count |
|---|---:|
| Complete stack matches one raw event | 38,765 |
| Complete stack matches a raw kernel/user pair | 6,291 |
| Decoded frame chain not independently verified | 2,804 |
| Decoded leaf differs from the sampled IP | 10 |
| No decoded stack | 59 |
| Total selected raw samples | 47,929 |

Only the **45,056** exact frame chains contribute to the final attribution
below. Of these, 43,683 (96.953%) have a resolved leaf method; 1,373 do not.
The 2,873 samples outside that subset remain excluded, not imputed.

## Observed work and source path

Each category counts a sample once if the named method or method family occurs
anywhere in its verified stack. Categories overlap and must not be added.
These are sample counts, not CPU milliseconds, allocations or invocation counts.

| Verified inclusive category | Samples |
|---|---:|
| `TilingWindow` construction | 10,855 |
| Runtime GC heap functions | 7,770 |
| `SvgIcon` construction | 6,201 |
| Virtual-desktop calls | 6,184 |
| `TilingService.DetectChanges` | 5,617 |
| Kernel stack walking/unwinding | 3,227 |
| Overlay invalidation | 2,572 |

`TilingWindow` construction occurs in all five processes: 2,214 / 2,089 / 2,187 /
2,230 / 2,135 verified samples. It covers at least 22.648% of all selected raw
samples, including the excluded denominator. This supports an experiment; it
does not predict the amount of CPU an implementation would save. GC and kernel
profiling activity also occur in the captured workload.

The frozen baseline source explains why a padding change reaches this work:

1. The fixture alternates `WindowPadding` between 4 and 12 while keeping panel
   height fixed, through the real settings publication path.
2. [`OnSettingsChanged`](../../FancyWM/TilingService.cs) detects geometry changes
   and calls `SetPanelHeight` to propagate both panel padding and spacing. That
   propagation is necessary even when height is unchanged.
3. [`PropagatePanelHeightChange` / `UpdateGuiNodeOptions`](../../FancyWM/TilingService.Private.cs)
   update the geometry, call `m_gui.InvalidateView()`, then invalidate layout.
4. [`InvalidateViewCore`](../../FancyWM/TilingOverlayRenderer.cs) clears overlay
   collections, disposes view models and clears the previous snapshot. Subsequent
   layout materializes new WPF elements. Exact retained witness stacks separately
   show the synchronous invalidation chain and later constructor calls.

The selected bounded candidate should preserve overlay model/control identity on a
padding-only update while still recomputing panel/window bounds and resources.
It must retain invalidation epochs that cancel obsolete reentrant update passes,
failure recovery, disposal and native DPI/scaling behavior. Simply deleting the
full invalidation call is not an established safe implementation.

Start with a shared 1/10/50-node regression/materialization fixture that freezes
geometry, model/control identity, subscriptions and callback behavior. Cover
reentrant padding changes, update failure and retry, focus/preview state and
disposal during notifications. Then compare allocations/materialization with
an isolated candidate. Native behavior and CPU comparison require a fresh
validated local capture. Keep animation batching and virtual-desktop caching
as separate questions.

The subsequent [padding reuse experiment](OVERLAY_PADDING_REUSE.md) implements
this candidate with a common B1/C1 fixture and five alternating M2 pairs. It
verifies fewer allocations and zero window/tab/SVG recreation in completed
no-HWND passes. The [nested-notification correction](OVERLAY_NESTED_NOTIFICATION.md)
subsequently verifies both WPF surfaces under reentrant cleanup. Native CPU and
behavior remain separate gates; original M6/D7 attribution evidence is unchanged.

## Decoding and retained limitations

M6 contains CLR method load/unload events and rundown metadata. TraceEvent
3.2.6, pinned in each diagnostic tool's lock file, resolves managed methods from
these records. Native lookup uses the matching PDB identifiers and the D7 local
symbol cache: 11,007 of 11,245 requested addresses resolve, with no ambiguous
module/address mappings. The mapping fills only previously unnamed frames.
The [TraceEvent guide](https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md)
describes this managed stack model; Microsoft's [symbol-loading documentation](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/loading-symbols)
describes symbol paths and WPR's associated PDB directory.

The initial symbol-enabled xperf probe is retained and rejected: run 1 scenarios
23/30 produce 1,216/1,019 stack hits, versus D6's 1,227/1,021, and leave managed
addresses unresolved. The reason for that decoder difference is not established.
D6's stack tables remain diagnostic history; use raw-event-matched D7 coverage
for this attribution. D6's separate weighted profile results and M6's independently
checked scheduling totals are not replaced by these stack counts.

TraceEvent conversion logs contain stack-stitching warnings. The raw frame checker
therefore excludes chains it cannot reconstruct exactly. A successfully decoded
leaf alone is insufficient. The intermediate managed/resolved reports retain
broader subsets and must not substitute for the final frame-verification receipt.

The generated 11,905,318,309-byte `fullapp.etlx` is an offline derivative of the
unchanged M6 ETL. SHA256:
`C92918D6C7D3BB338C5F0DB8DE3BBC5475913256BDE159010C955AB846FFC11F`.
No new recording, user input, app launch or MSIX installation occurs. NuGet
dependencies are restored only for the isolated diagnostic tools.

## Receipts and replay

Final [frame verification](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-D7/stack-fragment-verification/verification.json):
SHA256 `3836F1A6452D1C153D70317E27B4A3846380378E15696990936917E4A4A446C3`.

Raw sample receipt SHA256:
`76FD4093B070CB8E568FF2B2513E40E18FC9B664AEEA34D073D1CF3E039BFD0B`.
Raw StackWalk receipt SHA256:
`7F9D6EF7A2AE181C479D29C2EE0769B75A48CC940204DAE174E43666B10F9EBE`.

D7 retains `export-symbol-stacks.py`, four small pinned C# readers/resolvers,
all command/input/output manifests, raw exports, symbol cache and the Python
sample/frame verifiers. Existing output directories are immutable. Reuse the
ETLX and retained decoded exports for further analysis; a new execution needs
a fresh output path. Keep the original ETL, failed xperf probe and intermediate
reports intact.

The subsequent corrected candidate now passes native behavior and a five-pair
process-CPU comparison. M8 verifies 240 measured transitions and 14 CPU wins
plus one tie; the 50-window median is 1,500 -> 210.9375 ms. This newer direct
comparison includes both padding reuse and the nested-notification correction;
it does not convert D7's sampled stack counts into CPU durations. D8's extra
identity event remains absent in a zero-loss diagnostic trace.
[Native results and preserved presentation limits](OVERLAY_FULLAPP_CPU.md).
