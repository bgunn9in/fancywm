# PERF-010 owner topology / presentation protocol

This is the existing next stage from `ANIMATION_NATIVE_BASELINE.md:139` and
`PERFORMANCE_STATUS.md`. This owner stage is ACCEPT at S1; the early failed
attempts below remain historical evidence. Full-app behavior subsequently
passes V14. Whole PERF-010 remains IN_PROGRESS for full-app measurements.
There is no production batching or A/B optimization claim.

## Required evidence mapping

| Existing live-plan criterion | Existing evidence | Missing proof / completion condition |
|---|---|---|
| Examine HWND-owner scheduling through final queued updates | N3 ETL, markers, calls, V1 geometry report; previous console-only CSwitch analysis | Reproducible derivation with raw ETL/tool/source provenance, matching switch pairs and CPU IDs, clipping scheduled intervals to each measured transition and native tail |
| Vary HWND owner-thread count at 1/10/50 targets | N3/V1 has one owner per scenario | Fresh sequential isolated processes using the same final fixture; actual HWND-to-native-thread mapping, fixed geometry/250 ms/display/DPI/144 Hz/profiles, call and task fan-out, cadence, separate completion/native-message/native-rectangle times, CPU, cleanup and strict raw-evidence verification |
| Correlate Dwm-Core before a physical-presentation claim | N3 captures Dwm-Core and DxgKrnl; DwmFlush is only an API boundary | Demonstrated linkage from each target geometry update through compositor processing to displayed output. Provider presence, nearest timestamps, WM_WINDOWPOSCHANGED and DwmFlush alone do not pass. Unavailable attribution remains E2E_PENDING / NOT_MEASURED |
| Full-app native / interactive behavior after topology | Prior managed semantics, native owned-window baseline | Actual FancyWM Dispatcher/WPF, DPI, interactive input, concurrent callbacks, cancellation, failures and shutdown evidence with owned process/window cleanup |
| Full-app CPU/GPU/presentation measurements after topology | N3 fixture process CPU, sampled CPU and context switches | Attributed whole-app CPU, available GPU/compositor counters and correlated presentation, retained environment/commands/raw traces/provenance; unavailable counters explicitly NOT_MEASURED |
| A production batching candidate only when supported by native evidence | Accepted managed stages and existing regression fixtures | Conditional: one justified delta, common final fixture, old red, targeted/affected D/R, five alternating A/B pairs in ten isolated processes, strict verification, then one append and full C2 gate. No candidate is required merely to observe topology |

## Topology cases and controls

Use three owner limits: 1, 5 and 50. For each 1/10/50-target scenario, actual
owners are `min(targets, ownerLimit)`, assigned by `targetIndex % owners`.
The first case preserves the N3 shared-owner queue; five owners tests bounded
sharing (2 or 10 targets/owner); the final case gives one owner per target.
These are controlled same-process thread topologies, not proof of representative
cross-process application behavior. The one-target case is the common control.

Each case uses five sequential Release x64 processes, two warmups and eight
measured transitions per target count. A process runs exactly one owner limit.
This is an observational comparison, not an invented baseline/candidate pair.
Grid coordinates, colors, window styles, 250 ms nominal duration, refresh-rate
selection, CPU/GPU/DesktopComposition profiles and marker provider remain the
N3 controls. Record actual native owner IDs for every HWND and join every owner
thread. Freeze source before each fresh immutable capture; retain failed runs.

CPU intervals derived from CSwitch are scheduled wall-clock occupancy, include
interrupt time unless separately attributed, and are distinct from sampled CPU
and quantized Process.TotalProcessorTime. Native geometry is distinct from
physical presentation and from complete FancyWM behavior.

## N3 offline derivation and independent presentation proof

`FWM-ANIMATION-OWNER-TOPOLOGY-20260910-D2/analysis` reproduces the original
N3 context-switch analysis: 6,667,348 CSwitch events, 15 owner threads and
266,552 matched intervals, with no unmatched switches or CPU mismatches.
At 50 targets the nearest-rank p50 ETW task-to-geometry tail is 426.287 ms,
including 322.862 ms scheduled owner occupancy. Conventional even-sample
medians are 426.334 and 322.975 ms; these are different percentile definitions,
not additional measurements. D2 references retained raw exports in D1 and
the original N3 ETL. Its derivation SHA256 is
`2C0E26E55D8E61EAEA2A0D0539BAF5F737B8353B416131FD388CE111A6F75404`.

The independently frozen D6 verifier checks all 2,440 measured final target
positions against raw Dwm-Core and DxgKrnl events. The chain is HWND -> GDI
sprite -> bound visual -> exact final translation, full-size render content
and covering clip -> compositor frame -> same-thread desktop flip -> unique
submit sequence -> MMIO3 per-plane present ID -> hardware flip completion.
The matched VSync event has the same hardware frame number and agrees with
PresentMon within xperf's microsecond export precision. MMIO can execute in
the Idle context; its thread identity is recorded, not assumed to equal the
submitting DWM thread. The scalar `FlipPresentId` is not the per-plane ID.
The pinned [PresentMon v2.5.1 consumer source](https://github.com/GameTechDev/PresentMon/blob/v2.5.1/PresentData/PresentMonTraceConsumer.cpp)
documents these submit-sequence and per-plane identifiers; tool, source and
download provenance are archived in D1.

D6 verifies 2,440/2,440 targets. Nearest-rank p50 time from submission until
all final target positions reach hardware flip completion is
247.0684 / 249.9234 / 675.4605 ms at 1/10/50 targets. Hardware completion,
VSync interrupt, native geometry and task completion remain separate values.
This is OS/driver display telemetry, not emitted photons or panel response.
The D6 presentation receipt SHA256 is
`14C262D6480FD7E8D93E6E1626F9F3CF4E2EBB4E9CF523AAB4E533103FFFC0B6`;
the 2,440-row witness table SHA256 is
`C5360C17D7D9DEF5BE7B5B801CB33D80BFA818065F6C4EB9A45A5CCE544AF81D`.
D3/D4/D5 retain earlier failed or incomplete derivations. No N3 capture or
historical receipt was repeated or overwritten. This proof is a component
of the existing topology/presentation stage, not a new accepted stage.

## Capture comparability diagnostic

`FWM-ANIMATION-OWNER-TOPOLOGY-20260910-M1-OWNERS50` is not comparable:
prepending the Toolkit directory to locate xperf also selected Toolkit WPR.
Its exported system collector adds stack caching, unlike N3 and the
owner-limit-1/5 cases. The strict verifier refused the profile mismatch.
The original capture, failed verifier, diagnostic and offline export are
retained. This is not a performance rejection. Pinning System32 WPR initially
restored the expected export, but M5 later reproduced the extra StackCaching
element with that same System32 executable. The cause of the export variation
is not established; selection of the executable alone is insufficient control.
The runner now retains the current built-in export for diagnosis and replays
the exact immutable N3 CPU/GPU/DesktopComposition export as an explicit custom
profile. The verifier checks both its complete XML and the actual start command.
No system policy or installed profile is changed.

## Final fixture visibility correction

Subsequent physical validation exposed a separate native visibility defect.
M1-OWNERS1 is fully correlated by D8 (2,440/2,440); its previous one-target
failure selected a partial damage-clipped draw when a covering draw existed
in the same frame. M1-OWNERS5 has 93 final positions without matching draw
evidence. M2-OWNERS50 uses the correct system WPR profile, but no final target
position has a correlated compositor draw. These incomplete presentation
results and their otherwise valid geometry data remain unchanged.

D9 observes native visibility, cloak state, hit-test HWNDs and screen-center
pixels from the unchanged archived fixture. The first HWND of each owner
loop lacks WS_EX_TOPMOST despite the configured managed property. D10 shows
that HWND_TOP does not correct this. D11's explicit HWND_TOPMOST restores all
61 windows for owner limit five, but PILOT2/PILOT3 expose demotion between
different hidden WinForms owner groups. D12 checks owner limit fifty:
ordinary owner reordering leaves 15/61 targets obscured, while
SWP_NOOWNERZORDER preserves native topmost and correct screen pixels on 61/61.
The exact Windows/WinForms internals causing the initial loss are not claimed.

The final fixture explicitly establishes HWND_TOPMOST with NOOWNERZORDER
after owner loops start and before the existing 100 ms settling delay.
It records native styles before repair and verifies native visibility,
non-cloaked state and five hit-test points per target before and after each
transition, outside CPU/timing boundaries. It never repairs visibility during
a measured transition. Only owned HWNDs are reordered; foreign windows and
foreground ownership are untouched. Geometry, colors, 250 ms duration,
144 Hz cadence selection, display/DPI and WPR profile settings are retained.

PILOT4 passes all six sequential Debug/Release native smoke processes, 54
transitions, geometry, visibility and cleanup. Earlier failed pilots remain
immutable. This changes the final native fixture and establishes the need for
fresh common owner-limit-1/5/50 measurements. It does not repeat
N3 or overwrite earlier captures, and it is not a production optimization or
an accepted topology stage by itself.

M3 stopped at a PowerShell parser error before tracing; its frozen source and
failure are retained. M4 completed four processes, then a native hit test in
the fifth process found HWND 197294 over one target corner despite the target
retaining TOPMOST. A subsequent read-only observation identified the foreign
HWND as Explorer's XamlExplorerHostIslandWindow. The ETL was saved, all recorded
owned HWNDs were destroyed and WPR was inactive. This is incomplete visibility
evidence, not a performance rejection. M5 stopped before tracing because its
current built-in export differed from N3. M6 uses the exact N3 profile replay
and the same final native fixture; no case is accepted until its complete
geometry, scheduling, raw evidence and hardware-presentation proof pass.

## Current external prerequisite and closure status

M6 started the exact N3 profile and retained its ETL, but the sidecar exceeded
500,000 records. PILOT5's preserved prefix shows 249,590 Frame calls, all
returning 0xC01E0006, with median wait duration 0.0011 ms. This is
STATUS_GRAPHICS_PRESENT_OCCLUDED. The [Windows API documentation](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-dcompositionwaitforcompositorclock)
describes an immediate return with this status when the display is off; the
precise physical/driver cause here is not established. D13 retains the
reproducible sidecar derivation and a fresh direct probe: 20/20 calls return
that status both before and after a temporary ES_DISPLAY_REQUIRED request.
The request was restored, without persistent power or security policy changes.

The harness now preserves the valid sidecar prefix, avoids throwing from HWND
callbacks on overflow, and rejects a failing clock status at the observation
boundary. D15 verifies the old double-exception failure and the corrected
500,000-record prefix/controller rejection. PILOT6 builds native, full-app
host and owned targets in Debug/Release; its two native probes retain the
exact clock failure with four samples each and no overflow. Its failed-run
CleanupPassed remains false; process exit and destroyed HWNDs are checked
separately, not reinterpreted as a successful measurement.

CPU tracing is actually available. Completion requires an active presentation
path with real compositor ticks and unobscured owned targets. No further
measurements are authorized by an elapsed wait or by a successful build.
The same final fixture must still pass complete 1/5/50-owner captures, strict
geometry/scheduling/CPU checks and every required presentation witness.
Then execute the existing full-app/native/interactive validation and attributed
CPU/GPU/presentation measurements. Full-app projects have only been built;
input behavior, DPI, concurrent callbacks, cancellation, failure and shutdown
are not claimed validated. There is no production candidate, no optimization
append, no new accepted stage and no whole-ID BLOCKED status. PERF-010 remains
IN_PROGRESS. The final R2 review records hashes, ledger integrity, Git state,
source-path/byte comparisons, old receipt verification, cleanup and ETL inventory.

R1/R2 retain failed review attempts (text decoding, then applying an old full
live-document hash after new facts were appended). The completed R2 integrity
checks remain valid. R3 applies explicit original-prefix checks only to those
six live documents and full hashes to every immutable artifact.

## Recovered later checkpoint — 2026-09-10

The earlier prerequisite was subsequently resolved in the interrupted session.
M7-OWNERS1/5/50 passed the existing S1 owner topology / presentation gate:
15 processes, 360 transitions and 7,320 hardware witnesses. The immutable
S1 report and receipt are referenced in
[the full-app continuation](ANIMATION_FULLAPP_VALIDATION.md).
This supersedes the IN_PROGRESS owner-stage statement above; whole PERF-010
remains IN_PROGRESS.

Full-app V12 then exposed missing layout invalidation when a generic window
is restored without focus. The continuation reproduces and corrects that
defect, with old 4/8 and corrected 8/8, full Debug 2043/2043 and Release
2099/2099. V13 passes native restoration and pending shutdown but retains an
interactive visibility failure caused by a foreground game. Fresh V14 now
passes both complete Debug/Release processes and independent behavior
verification with unchanged B13 binaries: 24 transitions, owned input/focus,
concurrent settings, native restore and pending-shutdown recovery at 96 DPI.
Full-app ETW, attributed CPU/GPU and hardware presentation are next; no new
ETW or ledger append applies to V14. See the linked full-app report.
