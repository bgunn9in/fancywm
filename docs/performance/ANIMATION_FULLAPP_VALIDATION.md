# PERF-010 full-app validation continuation

Latest PERF-010 continuation (2026-09-12): [completed actual multi-DPI validation and display restoration](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-DPI120-RESTORATION-R1/REPORT.md).
B18 and B17 each pass Debug/Release at actual 120 DPI on the unchanged
common fixture: four sequential processes, 48 transitions, owned input,
settings, interruption/recovery, restoration and cleanup. Independent
verification combines the pinned 96-DPI receipts with 1,616 native
120-DPI rows and 2,024 overlay pairs covering 1/10/50-window phases.
Multi-DPI criterion PASS: the user returned scale to 100%; actual HWND DPI=96 and the original 3440x1440 / work area 3440x1392 are verified. The four successful behavior processes were not rerun.
Full app-attributed GPU execution time, current-candidate GPU A/B and
existing non-client/full-frame hardware identity remain E2E_PENDING;
the EXECUTION-R2 alternative-source findings and exact dependencies remain.
Production, executable fixture, M8 CPU evidence and ledger are unchanged.
PERF-010 remains IN_PROGRESS; StageAccepted=false, no new stage ACCEPT
or performance REJECT. No WPR recording or new ETL was created.
Earlier next-action/environment paragraphs below are historical where superseded.

Current result: **V14 Debug/Release and independent behavior verification PASS**.
The restore correction and full-app behavior stage are complete. Whole
PERF-010 remains IN_PROGRESS for full-app CPU/GPU/presentation measurements.

Recovered on 2026-09-10 after the interrupted connection. The owner topology /
presentation stage already passed S1; its immutable report is
`artifacts/performance/FWM-ANIMATION-OWNER-TOPOLOGY-20260910-S1/REPORT.md`.
The receipt SHA256 is
`371024EFE4324821E25CE1D848F6E834E7F9D57C0FC9CD80E177B800C841C34C`.
That stage covers 15 processes, 360 transitions and 7,320 hardware witnesses.
Its native fixture and historical evidence are unchanged.

## Recovered defect and correction

Full-app V12 passed Debug but failed Release after the tenth target was
restored from minimization. The failure was
`TimeoutException: final native geometry for 10 owned windows` at the restore
check in `Program.Run`. All ten nodes existed, but the remaining window still
occupied the expanded nine-window rectangle. No subsequent relocation appeared
in the retained log after the restore event.

`TilingService.RegisterAndRestoreLocation` changed generic tree membership
before calling `DetectChanges`. That method consequently found an already
registered window and did not request layout. A later focus callback could
mask the missing invalidation. The defect also exists in the current HEAD's
restore implementation; it is not a demonstrated regression of an accepted
performance candidate.

The correction tracks successful generic registration/reattachment and calls
`InvalidateLayout` after releasing the backend lock. Algorithmic placement
retains its existing completion path. This is a correctness change, with no
batching, speedup claim or optimization-ledger append.

The new `RestoreWithoutFocusPublishesNativeGeometry` regression uses the real
service with fake native adapters, preserves focus on the surviving window and
checks published overlay membership plus both final positions. Eight cases
cover minimized/maximized state, saved/unsaved location and generic/algorithmic
layouts. The unchanged implementation fails all four generic cases; all four
algorithmic controls pass. With the correction all eight pass.

## Retained evidence

| Artifact | Result |
|---|---|
| `FWM-RESTORE-NO-FOCUS-20260910-R0/tests/restore-before.trx` | Old source: 4 failed / 4 passed; failure is missing layout publication |
| `FWM-RESTORE-NO-FOCUS-20260910-R0-SOURCE` | Frozen old production source and common regression |
| `FWM-RESTORE-NO-FOCUS-20260910-C1/tests/restore-after.trx` | Corrected Release: 8/8 |
| `FWM-RESTORE-NO-FOCUS-20260910-C1/tests/full-Debug.trx` | Complete application suite: 2043/2043, including all eight regressions |
| `FWM-RESTORE-NO-FOCUS-20260910-C1/tests/full-Release.trx` | Complete application suite: 2099/2099 |
| `FWM-ANIMATION-FULLAPP-20260910-B13` | Frozen full-app host and owned-target Debug/Release builds, all four successful |
| `FWM-ANIMATION-FULLAPP-20260910-V13` | Debug reached native restore and pending-shutdown verification, but interactive input failed its unobscured-target guard; Release was not started |
| `FWM-ANIMATION-FULLAPP-20260910-V14` | Exact B13 binaries: Debug and Release exit zero; independent behavior verifier PASS |
| `FWM-RESTORE-NO-FOCUS-20260910-C2` | Debug/Release solution restore, GUI and x64/MSIX builds pass; Layouts 160/160 and ThemeEngine 31/31 pass in both configurations |

V13's interactive final-state evidence records the obstructing HWND `6357490`
and PID `44432`. A subsequent read-only process query identified `MaxPayne.exe`.
The guard rejected the target before sending any input. The existing harness
continued its independent settings, minimize/restore/close and pending-shutdown
checks and retained the original interactive failure. Application shutdown,
target process exit and cleanup succeeded. This run is a retained failure,
not an accepted full-app stage.

## Reproduction

The reusable builder replaces hardcoded development-pilot commands:

```powershell
python scripts/performance/Build-AnimationFullAppValidation.py --snapshot-id <fresh-build-id>
python scripts/performance/Run-AnimationFullAppValidation.py --snapshot-id <fresh-run-id> --build-snapshot artifacts/performance/<fresh-build-id> --topology-receipt artifacts/performance/FWM-ANIMATION-OWNER-TOPOLOGY-20260910-S1/verification/stage-verification.json
python scripts/performance/Verify-AnimationFullAppValidation.py --snapshot artifacts/performance/<fresh-run-id> --output artifacts/performance/<fresh-run-id>/verification/behavior.json
```

The runner now verifies production-source correspondence and records the exact
production difference from the accepted topology captures. B13/V13 contain
only the restore correction in `FancyWM/TilingService.Private.cs`; their
receipts truthfully report `ProductionDelta=true`. The host explicitly records
`NativeRestoreVerified`. The independent verifier checks hashes, process
sequence, geometry, exact focus destination, concurrent settings, native
interruptions and pending-shutdown restoration. A failing run cannot pass it.

## V14 verified behavior — 2026-09-10

V14 reused the unchanged B13 source and all four frozen binary archives. The
owned input target was unobscured in both configurations; the existing input
guards and every assertion remained unchanged. Debug and Release ran
sequentially, exited zero and passed `Verify-AnimationFullAppValidation.py`.

| Verified behavior | Debug | Release |
|---|---:|---:|
| Transitions across 1/10/50 targets, including warmups | 12 | 12 |
| Owned mouse / keyboard inputs; direct-hotkey notifications | 3 / 6; 1 | 3 / 6; 1 |
| Concurrent settings requests with final versions applied | 12 | 12 |
| Native minimize and close during movement | PASS | PASS |
| Final geometry after restoring the minimized window | 10/10 | 10/10 |
| Saved native positions restored during pending shutdown | 50/50 | 50/50 |
| Application termination, target exit and window cleanup | PASS | PASS |
| Actual native DPI | 96 | 96 |

The observer retains first-chance native exceptions from the minimize/close
failure paths: four `InvalidOperationException` observations in each process
and four/five `InvalidWindowReferenceException` observations in Debug/Release.
These include repeated propagation observations; they are not counts of
distinct failed operations. Both final application errors are null and the
native recovery checks pass. Individual canceled animation Task status is not
instrumented.

Exact commands executed from the repository root:

```powershell
python scripts/performance/Run-AnimationFullAppValidation.py --snapshot-id FWM-ANIMATION-FULLAPP-20260910-V14 --build-snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-B13 --topology-receipt artifacts/performance/FWM-ANIMATION-OWNER-TOPOLOGY-20260910-S1/verification/stage-verification.json
python scripts/performance/Verify-AnimationFullAppValidation.py --snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-V14 --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-V14/verification/behavior.json
```

The [behavior receipt](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-V14/verification/behavior.json)
has SHA256 `FE40437B7F95CF86F0794DA81DB0D54660204BD87611BDA92C008C9473A9AB0D`.
Its run/evidence manifest hashes are respectively
`48E5EA031244419D805D214037B7D168C1C6074C4412C43F681CD50F125B1684` and
`2F4DDD907412C99F88E0D784F6B3293F7E74B28C3FF3565B9A62AAFFDC6C4677`.
The runner and independent verifier confirm source/binary provenance, geometry,
focus destination, settings versions, interruptions and shutdown/cleanup. The
previous V13 failure remains immutable and cannot pass the verifier.

No executable source changed during V14; the previously tested restore fix
remains the only production difference from the topology captures. Existing
full tests/builds were retained rather than repeated for documentation changes.
No new ledger rows, ETL capture, package install, commit or push applies.
The [V14 continuation review](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260910-V14/verification/continuation-review.json)
records final source/document comparisons, retained evidence, Git checks and
ledger integrity.

## Next measurement action

V16 now verifies the corrected B15 fixture; M6 is the fresh ETW continuation.
See [the current measurement report](ANIMATION_FULLAPP_MEASUREMENT.md) for
capture verification and CPU/GPU/hardware-presentation evidence. Behavior
validation alone establishes no CPU/GPU, multi-DPI or speedup claim.

Restore-fix integrity review (retained from the previous continuation):
`artifacts/performance/FWM-RESTORE-NO-FOCUS-20260910-C2/continuation-review.json`,
SHA256 `5DDA06E110AC7325DF3ED443B5509597C9E2D49177DF29CE608E482F6E32F5DF`.
It checks retained old/new tests, build receipts, unchanged common regression,
V12/V13 evidence, the sole production difference and unchanged ledger SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
Root and winman-windows whitespace checks pass. No submodule HEAD was changed;
no package was installed or launched, and no commit or push was performed.

## B15 / V16 verified behavior — 2026-09-11

Fresh V16 uses the four unchanged B15 Debug/Release host/target binary archives.
The native preflight observes the elevated local 3440x1440 display, 3440x1392
work area, 96 DPI and accessible `Default` input desktop. No RDP client process
is present and the owned input checks pass.

Both sequential configurations exit zero and independently pass the same
behavior gate as V14: 12 transitions each across 1/10/50 targets, owned mouse
and direct-hotkey focus, concurrent settings, minimize/close recovery, 10 native
restorations and 50 position restorations during pending shutdown. Both target
processes exit and destroy their owned windows. Observed first-chance counts
are four `InvalidOperationException` and four `InvalidWindowReferenceException`
per configuration during the retained interruption scenarios; final errors
are null. The separate repeatability audit finds zero differing target-index /
native-rectangle mappings across all 24 transitions, including warmups.

- [Behavior receipt](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-V16/verification/behavior.json),
  SHA256 `EA0C38DA2F93F9720939D02B5D6E20BFF191206EC08F0D799136E036B13C0184`.
- [Repeatability receipt](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260911-V16/verification/repeatability.json),
  SHA256 `F9FED5663A06D60203F9A320732C317CBE6A6E90ABDD9C535093C6773E1765A1`.

No production or fixture source changed in this continuation. The historical
restore correction remains the sole production difference from topology S1.
B13/V14, failed V15 and preparation P2 remain immutable. V16 provides the
required behavior receipt for the new B15 ETW capture; it is not a performance
comparison or whole-ID completion.

The later D6 diagnostic attempt observes a different desktop: RDP, 192 DPI and
2880x1704 work area. It rejects before app launch or WPR start, preserving V16's
local 96-DPI behavior scope. Offline M6 sample/stack analysis and the prepared
extra-event identity profile are recorded in the
[measurement continuation](ANIMATION_FULLAPP_MEASUREMENT.md#d6-visual-identity-and-scoped-cpu-diagnostics--2026-09-12).

## Corrected candidate B16 / preparation P3 — 2026-09-12

Fresh `FWM-ANIMATION-FULLAPP-20260912-B16` builds all four x64 Debug/Release
host/target archives successfully. Its production source matches the measured
nested-notification C1 and delivered C2 exactly. The ten common fixture files
(nine full-app harness/target files plus `FancyWM/app.manifest`) match B15
byte for byte. The only production changes from B15 are
`TilingOverlayRenderer.cs`, `TilingService.cs` and `TilingService.Private.cs`:
padding reuse plus the subsequent nested-notification correction. The old
V16 result remains evidence for B15; it does not validate B16 behavior.

[Preparation P3](../../artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-P3/preparation.json)
verifies 7,405 frozen source files across B15/B16/P3, all 244 files in B16's four
binary archives and AMD64 apphost headers. Independent V16 reverification
reproduces the original behavior receipt byte for byte. Full application tests
and delivery remain covered by unchanged corrected C1/C2; this preparation
does not rerun them or alter production/fixture source.

At `2026-09-12T05:40:14Z`, the native desktop check reports RDP, a 2880x1800
monitor, a 2880x1704 work area and 192 DPI. Both executing and input desktops
are accessible `Default`; the hidden owned DPI probe is destroyed. Admission
rejects the remote session, DPI and work-area differences from the accepted
local 3440x1440 / 3440x1392 / 96-DPI controls. No candidate application is
launched, no input is sent and WPR remains stopped. This is an environment
rejection, not a failed B16 behavior test. V17 was unused at this checkpoint.

| Receipt | SHA256 |
|---|---|
| B16 source manifest | `1FFBB05057FBBF96999C55D3FA281631482A152FC6D18729C5BA9DD8E9CCEC6C` |
| B16 build validation | `2B4720C317AC60D4A1AE07B7E14779CD63D93CD4F4AA0DEE3C05104AFE5B487B` |
| P3 preparation | `FFFA6CD750337F20650B9B26FB917479E8F80735287F6C3D79569E96C73961E4` |
| P3 desktop controls | `5BA35107C3C0AF3692A0206DE5174A1DE9434F73B989F7E9C85D2C3A5DF1CB76` |

The P3 resume procedure was to observe the desktop again with `FullAppDesktop.snapshot()` and
`control_failures()` against the retained expected DPI/work area. A fresh
passing observation is required before using the following commands; P3's
rejection cannot authorize launch. The existing owned-input guards remain
required. Use fresh IDs if V17 has since been used.

```powershell
python scripts/performance/Run-AnimationFullAppValidation.py --snapshot-id FWM-ANIMATION-FULLAPP-20260912-V17 --build-snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-B16 --topology-receipt artifacts/performance/FWM-ANIMATION-OWNER-TOPOLOGY-20260910-S1/verification/stage-verification.json
python scripts/performance/Verify-AnimationFullAppValidation.py --snapshot artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-V17 --output artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-V17/verification/behavior.json
```

Then verify repeatable native geometry and compare CPU under matching controls.
The old B15/M6 baseline, D6's unused identity pilot P2 and the independent
full-frame/hardware-presentation gate remain intact. No native performance
claim, optimization-ledger append or whole-ID status change follows from B16/P3.

## Local V17–V19 behavior and M8 comparison — 2026-09-12

P4 transfers only the disconnected current user's session to the unoccupied
console. Local 3440x1440 / work area 3440x1392 / 96-DPI admission passes before
and after V17. The corrected B16 candidate passes both configurations, including
owned input, settings, interruptions, restoration and shutdown. Its independent
receipt SHA256 is `B63CCB663D42C62AED3576604EEC31F20BC517544901FCB9F5A53D18E3D0605F`.

The first paired measurement M7 exposes a fixture race after `close-all`:
destroyed HWNDs report PID zero before their asynchronous model removal. The
new common `WindowOwnersReady` guard waits for that removal, still rejects
known foreign owners and cannot accept unknown nodes at an idle boundary.
Ten native cases cover the corrected classification. M7 remains rejected.

Fresh B17 contains unchanged candidate production; B18 contains verified B15
production and the same eleven fixture files. Each has four successful builds.
`Build-AnimationFullAppValidation.py --baseline-production <snapshot>` retains
the candidate manifest, replaces exactly the three declared overlay production
files inside its fresh source snapshot and records the actual resulting
manifest. `Run-AnimationFullAppValidation.py --live-candidate-build <build>`
requires that declaration, baseline source identity, matching original candidate
manifest and byte-exact live candidate production before admitting a baseline.
It never replaces production files in the live checkout.

Baseline B18/V18 and candidate B17/V19 each independently pass Debug/Release:
24 transitions, exact geometry repeatability, focus, settings, interruption
recovery and pending-shutdown restoration. All native controls and cleanup
pass. Both configurations retain the expected four/four first-chance exception
observations, with null final errors. M8 then verifies five alternating CPU
pairs; D8 separately tests the missing visual-identity event.
[Current CPU results, diagnostic limits and receipts](OVERLAY_FULLAPP_CPU.md).
