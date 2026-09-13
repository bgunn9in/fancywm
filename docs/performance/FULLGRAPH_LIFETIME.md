# PERF-016 full startup graph lifetime — 2026-09-12

Counter-label correction (2026-09-13): the full-graph USER/GDI labels below
were reversed. See [the verified correction and native attribution](GUI_RESOURCE_ATTRIBUTION.md).
Old receipts and values remain intact; the open aggregate counter is USER.

Checkpoint: `FWM-PERF016-FULLGRAPH-20260912-R1`. PERF-016 remains **IN_PROGRESS**.
`PerformanceClaim=false`, `StageAccepted=false`, `LedgerAppended=false`.
Earlier [native owner evidence](NATIVE_OWNER_LIFETIME.md), failures, toast/DPI
receipts and the two-file OverlayHost construction correction remain preserved.

## Actual graph and controlled boundaries

The harness calls **Startup.Main → AppMain → App.Run**, production App/MainWindow
constructors and the complete configured DI graph: real Win32Workspace, COM
virtual-desktop/display providers, native input hooks, AnimationThread, tiling
and overlay renderers, ToastService, FauxMicaProvider and theme watcher. Each
process uses a fresh owned non-input desktop. PID/thread/HWND/hook identity is
checked; no desktop switching, synthesized input or foreign-window manipulation.

Immutable `source/` is copied into a build workspace. `adapters.json` records the
exact test-copy changes: own data/profile path, generated versioned BAML URI,
WindowArranging write interception with a real native getter, and a 1500 ms hold
at the existing post-App.Run/pre-provider-disposal boundary. The native setting
stays true; disable/restore intent is observed. No display/security/driver/HWS
setting changes. This is **not an unchanged startup executable**. No ready VM or
second isolated user session was found or provisioned.

R5 proves the private desktop's target HWND belongs to neither Explorer virtual
desktop: workspace sees the window, native membership is false and backend nodes
are absent. Plain full-graph R7 covers workspace/settings without tiling. Further
tiling runs add a disclosed membership adapter for the verified owned target PID
only; native current-desktop identity stays real and all other windows retain
the original provider. Actual WindowNodes, renderer view-models, native HWNDs,
queued layout/animation and closure are exercised. Genuine shell membership and
physical presentation remain unproven. The private desktop lacks an Explorer
wallpaper surface, so Mica's actual native capture-error path is covered; its
successful wallpaper-capture path is outside this result.

## New verified receipts

* `FWM-FULLGRAPH-RETENTION-20260912-R7/verification.json`: **8 sequential processes,
  636 observations, 8 negative controls**. Per configuration: 10 warmup plus two
  50-cycle epochs, 2,158 native target windows, 110 SettingsWindows, zero of
  **2,488 weak references** after public WPF view-cache maintenance. Fixed startup
  owners intentionally remain alive. Passive samples retain the last settings
  window/view-model/page navigator. Settled USER/GDI do not grow in this scope.
  SHA256 `E9F32209B40AF234DD9AE04386033A6EBB664AF5D9B77CCFBB78E07219952BBC`.
* `FWM-FULLGRAPH-MICA-20260912-B1` reproduces the native shutdown crash already
  observed spontaneously in R5: the Mica error callback accesses App.Current
  after Application exit. Common fixture C1 passes Debug/Release with the minimal
  logger-lifetime correction; **317/317** affected leaves and **6 negative
  controls** pass. Receipt SHA256
  `9B7684DB4F01311A040EF1047BED741BBC2C7DA6A408A609FB2FE77DFEFD9F08`.
* Worker B1 has a native Debug red after warmup: one WindowReference and one
  SettingsWorkspaceReference survive. Owned dumps/root logs in fullgraph tiling
  D1 and worker D1 identify completed animation/callback payloads in idle stack
  slots. Both native HWNDs are dead and subscriptions cleared. The separate
  original Release tiling R3 does not retain these Debug JIT slots.
* `FWM-WORKER-RETENTION-20260912-C2/verification.json`: common fixture passes
  **100 post-warmup cycles per configuration**, zero of **6,914 weak references**,
  graceful native teardown and **6 negative controls**. T2 passes **411 Debug /
  423 Release** leaves, including two new EventLoop tests for idle payload release
  and FIFO/reentrancy/original exception/accepted shutdown work. Receipt SHA256
  `4AA10633826EE18E41F82B9BF0339A9599C3576F858CD6787D0079F5270CD6A8`.
  This receipt explicitly does not claim absence of USER/GDI growth.
* Corrected-source worker C3 crash verification passes **6 processes and 3
  negative controls**: FailFast `0x80131623`, TerminateProcess `0xE000F016`, actual
  production error handler `0xE0434352`, each in Debug/Release. The owned native
  ErrorMessageBox chooses quit with restart/log submission disabled; the original
  exception is logged and rethrown. The handler closes three graph windows.
  OS reclamation of acquired HWNDs does **not** prove completion of every managed
  worker. Receipt SHA256
  `CED4EE9C8975DB7D5075E5EB076353651EDAB66F463707198928D9671AF51FB8`.

## Production changes and full delivery

There are exactly three additional production changes. FauxMicaProvider captures
the logger while App exists, preserving warnings and interval validation.
AnimationThread's blocking admission and EventLoop's nonblocking drain use bounded
non-inlined methods, so completed payload slots leave before the next idle wait.
Queue/FIFO, batching policy, clock design, **250 ms reconciliation**, cancellation,
failure and shutdown semantics are preserved and tested. Intermediate worker C1
cleared only the animation root; its callback retention and dump remain preserved.

The cumulative dependency patch keeps all previous bytes as a prefix and adds
the EventLoop hunk. Its manifest covers six sources; pinned gitlink/HEAD stays
unchanged. Original bytes for rollback are archived. No performance A/B experiment
or optimization stage is invented for these correctness changes.

`FWM-FULLGRAPH-DELIVERY-20260912-C2/validation/delivery-verification.json` verifies:

* Debug **2069/160/31**, Release **2125/160/31** leaf tests;
* restore, GUI, RID-aware x64/MSIX in both configurations;
* archive-only packages and content/provenance checks; the final checkpoint also
  compares embedded WinMan.Windows.dll with actual built dependency binaries;
* pinned dependency Check/Apply/Apply/Check, conflict preservation and Release
  build, plus applicable nested-overlay measurement reverification.

Receipt SHA256 `D276DC91E66B1C871FCD5A5B83A806DE585FFC7AE00BD22C656C862E28A9DC9C`.
Packages were never installed or launched. Later settling/EOF changes are harness
only. TRX counts use UnitTestResult leaves **without InnerResults**; old TRX and
receipt re-reads are not counted as new tests or measurements.

## GDI observations and exact remaining criteria

Worker C2's 10-cycle warmup produces Debug USER 108→110→110 / GDI 118→132→125,
Release USER 108→110→110 / GDI 121→131→124. Matching 50-cycle warmup in C3 gives
Debug USER 110→110→110 / GDI 125→134→130. Strict no-growth verification rejects
these results. C4 adds five GC/dispatcher settling passes but then exposes a
target shutdown/EOF fixture race; that failed run remains intact.

C5 removes the duplicate target shutdown request and completes both configurations.
Per configuration: **150 cycles, zero of 9,483 weak references**, identical fixed
graph HWND inventory and an empty workspace after each epoch. Its **798
observations and 6 negative controls** are verified by
`FWM-WORKER-RETENTION-20260912-C5/resource-observations-verification.json`, SHA256
`7AFBA4976B67435CA04349FDADDA364CACD504C868AE3043CDBF927B2E670F36`.
Debug USER is 110→110→110, GDI **122→131→126**; Release USER 110→110→110,
GDI **134→133→133**. The receipt says **CRITERION_UNMET**, with native weak
retention/graceful teardown verified separately. These counters do not attribute
the native objects causing the differences. Replaying counters or guessing a
production change would not supply allocation/free identity.

| Criterion | Verified evidence | Remaining evidence / PASS condition |
|---|---|---|
| Earlier native owners, toast/settings/overlay/hooks/workspace/theme/browser | Prior receipts fully rehashed; no standalone toast rerun | Preserve their constructor/provider/cache boundaries |
| Complete configured startup graph acquired | R7 and controlled tiling candidates, actual native identities | Unchanged startup requires an isolated shell environment permitting its global setting write |
| Live graph workload retention | R7; worker B1→C2; C5 zero workload weak owners on live dispatcher/workers | Fixed singleton startup owners intentionally stay rooted until shutdown |
| Graceful graph teardown | Main/animation/hooks complete before Application.Exit; AppMain returns after workspace/Mica/provider stop | PASS for explicit adapters and native error-path environment |
| Hard crash/application error handler | Current-source C3 six processes, native owned dialog, original exception, partial managed cleanup and OS reclamation | Do not infer full managed cleanup from abrupt OS exit |
| Tiling USER/GDI accumulation | Native series, fixed HWND inventories, preserved strict failures | Open: attributed native GDI allocation/free identity and a verified no-growth explanation/result |
| Real membership/wallpaper and unchanged startup | Private-desktop admission gap measured | Appropriate isolated shell/session/VM, then evidence without these adapters |
| Native heap/GPU/physical presentation | Neither weak references nor USER/GDI supplies this proof | Attributed allocation/GPU/presentation contracts; preserve PERF-010/021 EXECUTION-R2/restoration dependencies |

No WPR session, ETL, driver, HWS or DPI setting was touched. No successful DPI or
standalone toast experiment was repeated. Local managed dumps identify the two
payload roots; they do not attribute native GDI object allocation/free. This is
a criterion dependency, **not whole-ID BLOCKED**.

## Failures and integrity

All fullgraph R0–R7, tiling R1–R3/D1, Mica B1/C1/T1, worker B1/C1–C5/D1/T1/T2,
and delivery evidence remain. Fixture corrections include STA entry, matching
BAML URI, diagnostic interface name, reflection delegate type and target EOF.
Failed GDI verifiers and native production reds remain readable. Every run uses
a fresh actual-date ID. Verifiers refuse existing receipts and reject missing
scenarios, surviving HWNDs and retained owners.

The final checkpoint review records Git/root/index diff checks, recursive
submodules, HEAD/tree and control-document bytes/lines/SHA256. It compares all
**2,155 production files**, including **72 winman-windows files**: exactly five
differ from the inherited nested-notification baseline, comprising the previous
two OverlayHost files and these three corrections. The previous checkpoint's
**66,681 files / 3,824,727,450 bytes** and pinned receipts are independently rehashed.

The ledger remains **41,782,945 bytes / 41,325 data rows**, SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
The full old file is the byte-exact prefix; suffix length is zero, SHA256
`E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`.
No commit, push, PR, release or publication was created.
