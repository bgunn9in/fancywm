# PERF-016 native toast lifetime — 2026-09-12

Latest verified continuation (2026-09-13): [GUI counter identity and native attribution](GUI_RESOURCE_ATTRIBUTION.md).
F11 Debug/Release pass the unchanged strict USER/GDI no-growth gate after
45-second quiescence and first-stable-window admission: USER 44/43/44 and
44/44/44, GDI=11 throughout, zero of 9,483 workload weak owners per process.
Native raw events, 48 HIMC control pairs and live creator-thread IDs explain
the earlier USER variation. Twelve complete new full-graph processes, four
failed native fixtures and one failed fixture build are preserved. F5's changing
baseline remains rejected. The final compact targets use the production native
NoMonitor 1024x768 fallback while their actual HWND DPI is 192; this is no
physical-presentation or unchanged-shell claim. Shared PERF-010 target source
is restored byte-exact. Production, inherited five deltas, PERF-002 250 ms and
ledger remain unchanged. PERF-016 is IN_PROGRESS; no stage acceptance or
performance claim. Next evidence needs an isolated shell/startup environment;
PERF-010/021 retain their existing GPU/presentation contracts.
Earlier next actions are superseded; original receipts are preserved.


Latest continuation: [FULLGRAPH_LIFETIME.md](FULLGRAPH_LIFETIME.md), checkpoint
`FWM-PERF016-FULLGRAPH-20260912-R1`. Standalone toast R2 and its pinned receipts
remain unchanged; new full-graph evidence does not broaden its scope.

Continuation note (2026-09-12): the R2 evidence below remains unchanged and
complete at its stated scope. Its former next action has been executed in
[NATIVE_OWNER_LIFETIME.md](NATIVE_OWNER_LIFETIME.md), checkpoint
`FWM-PERF016-NATIVE-20260912-R1`. Do not rerun the standalone completed toast
experiment without a fixture/evidence change. This note does not expand R2's scope.

The native `ToastWindow` lifetime checks pass in Debug and Release. This closes
the HWND/base-window gap for the tested toast paths. PERF-016 remains
IN_PROGRESS: other native window owners, whole-app retention and hard-crash
teardown are outside this result. Production code and the performance ledger
are unchanged; no speedup or whole-stage acceptance is recorded.

## Scope and results

`FancyWM.Tests/Toasts/NativeToastLifetimeTest.cs` adds five isolated-process
tests. Each uses the production native toast platform, real WPF windows and a
live STA dispatcher. Display providers are controlled mocks; their work area
comes from the native primary-display work area. The fixture uses a minimal
WPF `Application`, not the complete FancyWM startup/service graph.

- Window close, window disposal, service disposal and worker-thread disposal
  each destroy the owned HWND, detach the `HwndSource`, remove the window from
  `Application.Windows` and complete both active/queued toast operations.
  Cancellation and captured stale display callbacks after close remain inert.
- Position, primary-display subscription and scaling subscription failures
  during construction preserve the injected exception, destroy the HWND already
  created by the native platform and release event ownership.
- Six close-error cases cover primary/scaling unsubscription and a `Closed`
  handler, through both `Close` and `Dispose`. Each exact exception is observed
  once at its real boundary and native cleanup completes. For errors dispatched
  through `WM_DESTROY`, the test handles only the exact injected exception via
  `Dispatcher.UnhandledException`; this does not claim automatic production
  recovery from an unhandled exception.
- `Application.Run`/`Shutdown` closes a visible toast and completes its
  uncancelled waiter before dispatcher termination.
- After ten warmup lifetimes, two 50-window epochs on the same live dispatcher
  retain zero weak window references after explicit GC. USER and GDI counts
  return exactly to their initial values in both configurations. These are
  hidden-window retention checks, not native-heap, process-memory or GPU results.

The tests observe real HWND ownership, visibility and destruction. They do not
establish first-toast pixels, physical presentation, a new DPI criterion,
real display-provider failure behavior or an additional performance comparison.
The existing PERF-026 visual/latency gate stays pending.

## Verification

Evidence: `artifacts/performance/FWM-TOAST-NATIVE-20260912-R2`.

| Run | Passed |
|---|---:|
| Debug native toast tests | 5/5 |
| Debug related lifetime tests | 162/162 |
| Release native and related lifetime tests | 167/167 |

Related tests cover the existing toast fixture, overlay-window closure,
settings-window closure, application lifetime and tiling-window lifetime.
Both configurations build through `dotnet test`; Debug related tests reuse
the already built binaries. This tests-only change does not repeat the
unchanged solution/package/dependency delivery gates.

The retained [verifier](../../artifacts/performance/FWM-TOAST-NATIVE-20260912-R2/verify-native.py)
checks all result leaves, the five required native scenarios and all 32 native
observation rows. Four altered-evidence cases are rejected: a live weak window,
missing close path, surviving HWND and duplicate epoch. Its
[receipt](../../artifacts/performance/FWM-TOAST-NATIVE-20260912-R2/verification.json)
records the source TRX hashes. Source/binary provenance and final Git/ledger
checks are in the accompanying `continuation-review.json`.

R0's first three native tests passed. R1 is retained as a failed fixture run
(3 passed, 2 failed): it expected native close exceptions at the managed call
site and tried to terminate an unowned dispatcher loop. R2 corrects those test
assumptions without changing production behavior or weakening the cleanup
assertions. R1 is not a product regression or performance rejection.

## Next action

Continue PERF-016 with native `SettingsWindow` and `OverlayHost` closure and
retention, using their actual HWND paths and the established failure/lifetime
assertions. Preserve the completed toast evidence. PERF-010/021 GPU execution
and full-frame identity still require the instrumentation evidence recorded in
the latest DPI-restoration/EXECUTION-R2 checkpoint; no unchanged trace was recaptured.
