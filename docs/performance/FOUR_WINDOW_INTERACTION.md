# Four-window interaction measurement — 2026-09-14

Completed at production source `0b625b7`, without production changes.
Accepted evidence: `artifacts/performance/FWM-FOUR-WINDOWS-20260914/run-7/`
and `run-7-verification.json` in its parent directory (local, ignored).
Runs 1–6 are incomplete fixture/admission attempts, never baselines.

## Scenario and fixture correction

The existing full-app host runs the real FancyWM App/MainWindow/DI, native
workspace, hooks and overlays. A separate WinForms process owns four captionless,
resizable colored windows, each with native minimum size 48×48. Each layout
phase creates four fresh HWNDs; there are 12 target HWNDs over the whole run.
Other processes are excluded through production settings; settings/logs use the
fresh run directory. No window, input provider or WindowArranging write is mocked.

The scenario uses automatic SendInput, not physical user gestures. Its sender
now runs on a worker while the WPF Dispatcher remains available to synchronous
mouse-hook queries. Previously it sent input on that Dispatcher; a residual
left-button state recurred even during the agreed hands-off interval. The owned
residual button was released after that failed host exited. Run 7, with worker
injection, completes every drag and finishes with no pressed input. This corrects
the fixture; it is not a measured application optimization or a proven native
hook-timeout diagnosis.

The state reader uses the current `State` entry property; drag start admits
`Starting`/`Moving` before cursor movement. Alt/button ownership spans all awaits,
and both releases are attempted in finally. Foreign-window, occupied-key/button,
foreground and unexpected cursor movement guards remain enabled. Timeouts were
not increased. A partial SendInput still uses the existing release helper.

Per cycle: satellite swap/return, adjacent-satellite promotion/return, master
right/left, and modifier-drag with preview/return. Mixed also exchanges the upper
and wide slots and returns them. Thus H/V have 8 operations and Mixed has 10.
Two warmup cycles precede three measured cycles in each phase: **52 warmup + 78
measured operations**, comprising 60 automatic direct commands and 18 drags.
Every inverse pair/cycle restores the exact original slot identities and roles.

## Conditions and results

Release x64, .NET 10.0.12, local Default desktop, one 3440×1440 display at 96 DPI,
work area 3440×1392, production 100 ms window animation enabled. Default runtime
tiering; no forced GC or ETW/GPU capture. One host process (PID 4436), layout order
Horizontal → Vertical → Mixed, total host lifetime about 35.9 s including setup,
warmup and cleanup. Results below sum operation intervals within each cycle;
focus/setup between operations is excluded. Values are median [min–max] over
the three measured cycles. MiB means 1,048,576 bytes.

| Layout | Operations/cycle | Process CPU, ms | Managed allocation, MiB | Result observation, ms | GC Gen0/1/2, all 3 cycles |
|---|---:|---:|---:|---:|---:|
| Horizontal | 8 | 1453 [1078–1531] | 4.963 [4.777–5.151] | 1803 [1769–1841] | 1/0/0 |
| Vertical | 8 | 625 [453–719] | 4.869 [4.853–4.876] | 1814 [1811–1824] | 1/0/0 |
| Mixed | 10 | 547 [438–688] | 8.746 [8.737–8.812] | 2054 [2040–2090] | 2/1/0 |

Across measured operations: **0 additions to window/panel model collections,
0 child-collection Reset notifications**; child changes total 36/36/84 for H/V/M.
This does not count construction of every WPF visual or all overlay refreshes.

CPU is TotalProcessorTime of the process containing FancyWM **and the fixture**,
with 15.625 ms observed quantization, not computer-wide CPU. Approximate allocation
is GC.GetTotalAllocatedBytes(false); collections are GC.CollectionCount deltas.
All include fixture tasks, counters, reflection/geometry polling and concurrent
application work. No attribution/subtraction of their individual costs was done.

Result observation ends when layout idle/native HWND geometry is observed. It
includes animation, polling and, for drag, explicit 30 ms + six 16 ms + 100 ms
waits and scheduling delays. It is neither command execution time nor frame
latency. Direct-hotkey notification observation is separately recorded: median
1.045 ms [0.771–10.311] for 60 commands, including input/queue delivery. Preview
checks inspect the visible native overlay and WPF element/opacity, not pixels or
physical presentation timing.

Mixed drag intervals allocate about 1.6 MiB each versus about 0.85 MiB in H/V,
but different tree/slot transitions and observer work prevent assigning the
difference to a particular method. CPU has substantial variation and sequential
phase order is a confounder; the table is not a ranking of layout efficiency.
**No specific redundant production work or justified next optimization is
established.** No application change or before/after speedup is claimed.

## Verification and reuse

Release host/targets build; all 130 scenario operations and independent checks
of the 78 measured rows pass. Exact identities, slot swaps, promotion, master
side, operation focus, accepted preview, drop cleanup and native geometry pass.
All 12 target HWNDs are destroyed, target/host processes exited and no keys or
buttons remained pressed. User JSON hashes, original visible-window rectangles
and monitor geometry match before/after. WindowArranging is true before, after
normal application shutdown and after the outer guard. The scenario also calls
cursor/previous-foreground restoration in finally; those two restoration return
values were not separately recorded. No full application regression or portable
rebuild is needed; [portable 2.19.1.9](../portable.md) remains current.

Build the existing host and targets with `dotnet build -c Release` using their
projects under `scripts/performance/fullapp` and `fullapp/targets`. After checking
that another FancyWM is not running and agreeing a short input-free interval:

```powershell
$hostExe = (Resolve-Path 'scripts/performance/fullapp/bin/Release/net10.0-windows10.0.18362.0/FancyWM.FullAppHarness.exe').Path
$targetExe = (Resolve-Path 'scripts/performance/fullapp/targets/bin/Release/net10.0-windows10.0.18362.0/FancyWM.Perf010Targets.exe').Path
$runOutput = Join-Path (Get-Location) ('artifacts/performance/four-windows-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
& $hostExe $runOutput $targetExe 3 four-windows
```

Use a fresh output directory. Do not use the mouse/keyboard while this automatic
scenario runs; an occupied-input stop is a failed run. Keep the existing native
desktop/window inventory and user-settings hash checks around any new run.
