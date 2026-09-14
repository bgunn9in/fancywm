# PERF-002: bounded discovery snapshot check — 2026-09-14

Source: `e69c96cbddf9803fe090af7e653277c1a364a38c`. One candidate was checked:
whether regular DiscoverWindows obtains another provider desktop snapshot for
each eligible window. No other optimization candidate was pursued.

The current implementation already takes one lazy defensive desktop copy per
successful pass and passes it to ownership resolution. It snapshots the tracked
window set into one array before callbacks. The existing tests count desktop
provider getter calls through Moq:

| Scenario | Getter calls per pass | Reason |
|---|---:|---|
| Two eligible, already registered windows | 1 | Both use the same successful snapshot |
| No eligible window (minimized) | 0 | Snapshot acquisition is lazy |
| Two eligible windows, initial snapshot throws | 3 | One failed shared attempt, then a fresh retry for each window |

All three cases pass with algorithmic layout both enabled and disabled.
The error-path retries are intentional freshness/recovery behavior, not redundant
copies of successfully obtained data. Ownership/native property probes remain
fresh for each window. Removing the defensive copies is also unjustified:
existing callbacks can add tracked windows or mutate the provider's returned
array while a pass is running.

Decision: **no implementation change; the selected candidate is already
optimized**. There is no candidate variant or before/after gain to claim. No
cross-pass cache, callback/equality change, event system or new layer was added.
The 250 ms reconciliation remains unchanged, including correction of manual
desktop moves without a corresponding window event.

Ten targeted Release cases passed, zero failures/skips:

- DiscoverySharesOneSnapshotAndRetriesItsFailurePerWindow (2)
- DiscoverySkipsDesktopSnapshotWhenNoWindowIsEligible (2)
- DiscoveryUsesStableWindowSnapshotWhenStateReadMutatesTrackedWindows (2)
- DiscoverySnapshotIsolatedFromProviderArrayMutationDuringOwnershipProbe (2)
- NextDiscoveryReconcilesManualDesktopMoveWithoutAWindowEvent (2)

The preceding 758 Debug / 800 Release results retain their original scope and
were not repeated. The dependency patch checker reports all pinned/applied blobs
match. A fresh self-contained Release win-x64 portable was built from the same
production commit; see [the current package and launch instructions](../portable.md).
GUI then console projects were locally packaged into one fresh output directory,
retaining both deps/runtimeconfig files. Only the packaged FancyWM.bat was adjusted
to invoke the supplied FancyWM.exe; repository source and the patch mechanism
are untouched.

Raw test/build/CLI/ZIP receipts remain ignored under
`artifacts/performance/FWM-DISCOVERY-PORTABLE-20260914/`. No interactive UI,
GPU/ETW/D3D study, memory investigation, installation or external publication.
