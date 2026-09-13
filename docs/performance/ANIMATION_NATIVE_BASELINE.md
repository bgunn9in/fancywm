# PERF-010 native animation baseline

The 2026-09-10 continuation resumes the native measurement stage after the
profiling-permission dependency was resolved. Production animation code is
unchanged. The new fixture, runner and verifier live under
`scripts/performance/native/`, `Measure-AnimationNative.ps1` and
`Verify-AnimationNative.ps1`.

## Method

The fixture loads the current `FancyWM.dll`, `WinMan.Windows.dll` and `WinMan.dll`.
Reflection selects the existing `AnimationThread` and
`TransitionTargetGroup.PerformSmoothTransitionAsync`; it does not copy or
replace their implementations. The real compositor wait/boost delegates are
wrapped to record timestamps. `AnimationThread.Frame.UpdateFrame(Func<bool>)`
continues to run on the production animation worker.

Each sequential Release x64 process runs 1, 10 and 50 visible, non-activating,
owned WinForms HWNDs. All target HWNDs in a scenario share one UI owner thread.
The forms use the TOOLWINDOW style and close before the process exits. Their
geometry uses a fixed 10 by 5 grid derived from the primary work area; each
window moves by one third of a grid cell without resizing. The fixture chooses
the maximum provider refresh rate using the same expression as `MainWindow`.

Each count receives two warmup transitions followed by eight measured
transitions of nominal duration 250 ms. Directions alternate. A real
`Win32Workspace` and its `UnsafeCreateFromHandle` wrappers provide window state
and `SetPosition`; the fixture initializes their state through the real native
configuration path. It records additional `IWindow` calls through a forwarding
adapter. These explicitly owned wrappers are not the complete FancyWM service
or a complete provider discovery/lifecycle workload.

There are five sequential baseline processes and no candidate variant. This
is a diagnostic baseline, not an A/B performance improvement claim.

## Result

N3 captures 120 measured transitions, 40 per target count, on one
3440 by 1440 display at 96 DPI and 144 Hz. Strict verification matches all
600 ETW boundaries and reports zero lost events and buffers. Every owned HWND
reaches its final rectangle; the animation workers, HWNDs and named WPR session
close successfully.

| Targets | Median task completion (ms) | Median final native message (ms) | Median frame interval (ms) | Maximum position calls per frame |
|---:|---:|---:|---:|---:|
| 1 | 233.5 | 233.6 | 6.934 | 1 |
| 10 | 236.8 | 241.0 | 6.943 | 10 |
| 50 | 237.8 | 663.6 | 6.947 | 50 |

The native-message and task-completion columns are separate medians. Their
difference is not the median of individual transition differences. In the
50-target scenario, frame cadence remains near the display interval while
native geometry application continues well after task completion. This
establishes a backlog in this instrumented, single-owner workload; it does not
establish the latency of every FancyWM configuration or justify a particular
batching implementation.

The per-transition native tail after task completion has median/p95 values of
0.202/0.311 ms, 3.431/4.610 ms and 415.187/518.170 ms for 1/10/50 targets in V1.

There are 1,291 / 12,519 / 62,014 position calls and matching native messages
across the measured 1/10/50-target transitions. Median process CPU through the
flush boundary is 62.5 / 187.5 / 875 ms per transition. This accounting includes
all fixture/workspace threads, is quantized, and can exceed wall time when
threads run concurrently.

## Boundaries and instruments

The preallocated sidecar records QPC timestamps and native thread IDs for the
real compositor waits, clock boosts, `IWindow.Position` reads,
`IWindow.SetPosition` calls, and each target's `WM_WINDOWPOSCHANGED` handler.
It performs no file writes or per-call sample allocations during transitions.
Compositor-wait completion precedes `UpdateFrame`; its interval is a frame
boundary measurement rather than the duration of `UpdateFrame` itself.

`Win32Window.Position` reads its provider cache and checks native window
liveness. The read counter is therefore not a `GetWindowRect` counter.
`SetPosition` uses the existing asynchronous `SetWindowPos` path. The native
message counter distinguishes application of queued geometry from completion
of the managed call. The verifier checks every final native message rectangle
and the count of applied messages against completed position calls.

Four ETW markers identify submission, animation task completion, independent
verification of actual HWND rectangles, and return from `DwmFlush`. The marker
payload includes QPC so the exported ETL can be matched to the sidecar by
process, transition and phase. The final native-message timestamp is reported
separately from the polling-based rectangle verification boundary.

The process CPU counters include the fixture, WinMan workspace and message
owner. Their Windows accounting resolution is coarser than QPC; they are not
the full FancyWM application's CPU utilization. Raw sampled-CPU and context
switch data are also retained. WPR captures CPU, GPU, DesktopComposition and
the fixture marker provider in a named session, retaining both the custom
profile and exported built-in profiles.

`DwmFlush` is kept as an API boundary. Its documented scope covers outstanding
updates from the calling application, rather than the entire session's render
batch. It does not provide per-target physical-presentation measurements for
this fixture. See Microsoft's [DwmFlush documentation](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush).

## Evidence and validation

- `FWM-ANIMATION-NATIVE-BASELINE-20260910-N2`: elevated preflight, CPU smoke
  ETL, build development logs, Debug/Release native smoke runs, and animation
  regression TRX. The regression filter passes 78/78 in both configurations.
- `FWM-ANIMATION-NATIVE-BASELINE-20260910-PILOT1`: development-only full-profile
  pilot. It exposed an EventSource overload/manifest width mismatch in the
  fixture's markers. The final fixture uses three explicit Int64 fields. Pilot
  timing is not accepted as the final baseline.
- `FWM-ANIMATION-NATIVE-BASELINE-20260910-N3`: frozen source and complete binary
  archives, five baseline processes, raw call sidecars, ETL, profile exports,
  environment and process receipts, and strict verification.
- `FWM-ANIMATION-NATIVE-BASELINE-20260910-V1`: a separately frozen verifier and
  derived report for the same N3 capture. It preserves fractional milliseconds
  in the native-tail calculation by selecting the floating-point `Math.Max`
  overload explicitly. The initial N3 report rounded that column to whole
  milliseconds; the raw QPC data and original report remain unchanged.

The verifier requires exact source/binary/evidence hashes, non-overlapping
processes, fixed display/DPI/geometry, correct final native rectangles, all
ETW boundary payloads, zero lost events/buffers, and successful owned-window,
animation-worker and WPR cleanup. The historical optimization ledger remains
unchanged; this baseline does not append an accepted optimization.

The [verified V1 report](../../artifacts/performance/FWM-ANIMATION-NATIVE-BASELINE-20260910-V1/native-verification.json)
has SHA256 `BEFDD8281A9E3BD42B2D67A7FDB98264B551788EDF1C1D5E39EA8A9DB8C392C5`.
Its [120-row transition table](../../artifacts/performance/FWM-ANIMATION-NATIVE-BASELINE-20260910-V1/native-transition-measurements.csv)
has SHA256 `DB69A036313AA2A88DB4AAC30478EC1E3A1646B8E982E433897F0DC786C006E5`.
The [N3 ETL](../../artifacts/performance/FWM-ANIMATION-NATIVE-BASELINE-20260910-N3/native/animation.etl)
is 8,341,422,080 bytes, SHA256
`C820A3EF019FB7519B63886B6B84D1E9180827EDB9E90797AF245F9FA2554FED`.
The N3 native-evidence manifest has SHA256
`493AEC2EFBD3562311D6D3E1FE449F3B4EC3F8444739ABDB21851295D9128C87`.

Physical presentation, representative target-owner/process topology,
interactive FancyWM behavior, full-app CPU/GPU and an A/B candidate comparison
remain separate evidence requirements before a production batching change.

## Next bounded stage

Use the N3 markers and ETL to examine scheduling of the target HWND owner
through the final queued geometry updates. Extend this same fixture to vary
the number of HWND owner threads, then capture fresh 1/10/50-target runs with
the same geometry, duration and profiles. Retain final native rectangles and
correlate Dwm-Core events before making a physical-presentation claim. Any
production batching candidate must still preserve the existing concurrent
callback, cancellation, failure and shutdown tests and pass an isolated A/B
comparison.

The [owner-topology continuation](ANIMATION_OWNER_TOPOLOGY.md) now retains
reproducible N3 scheduling (D2) and independent 2,440-target OS/driver hardware
flip correlation (D6). N3/V1 source, raw capture and receipts remain unchanged.
The existing next stage is still IN_PROGRESS: the final common topology set
and subsequent full-app evidence are not complete. Current CPU tracing is
available; D13/PILOT6 establish an unavailable compositor clock
(STATUS_GRAPHICS_PRESENT_OCCLUDED), unaffected by a scoped display power request.
