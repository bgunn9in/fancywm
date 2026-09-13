# PERF-016 GUI resource attribution — 2026-09-13

PERF-016 remains **IN_PROGRESS**. This continuation changes test/diagnostic
instrumentation only. Production, its five previously justified source deltas,
250 ms reconciliation, delivery receipts and the optimization ledger are preserved.
`PerformanceClaim=false`, `StageAccepted=false`, `LedgerAppended=false`.

## Corrected counter identity

The prior full-graph harness reversed its JSON labels: `USER` called
GetGuiResources with flag 0 and `GDI` with flag 1. The actual Win32 contract is
**GDI=0, USER=1** ([Microsoft API documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguiresources)).
The standalone toast/native-owner/native-teardown fixtures use the correct flags.
Their evidence is unaffected. Old full-graph files and receipts remain byte-exact;
the corrected derivation covers 16 historical runs and supersedes their labels.
Old C5 therefore has Debug USER **122→131→126**, Release USER **134→133→133**,
and GDI **110→110→110** in both configurations. Its combined USER/GDI criterion
remains unmet; the prior attribution of the problem specifically to GDI was wrong.

`FWM-GUI-ATTRIBUTION-20260913-P3/verification.json` verifies the native counter
contract with 24 simultaneous bitmaps, 24 menus and 24 icons, all 72 corresponding
create/destroy pairs, six negative controls and the historical correction.
SHA256 `88AA28BF2D6C1FF9A40B5ED0C30131E5224716D39FA40EE670D3135BB867CF34`.
P4 repeats the calibration for the expanded observer, SHA256
`43CEB9457D21F40B083D3BB270F65EFA560A3795F139B7A9F32DE2F82ABC6F2D`.
The controls reject reversed flags, missing native calls/phases, surviving menus
or icons and a changed counter. Earlier brush-based controls P1/P2 remain failed:
all DeleteObject calls succeeded but the GDI count retained an extra ten objects.
The bitmap fixture records its actual observed return to the starting count.

## New live full-graph evidence

F1 and F2 each run Debug then Release in separate owned non-input desktops,
with unchanged production constructors/startup graph and the previously disclosed
profile, BAML, WindowArranging and owned-target membership adapters. Per process:
50 warmup plus two 50-cycle epochs, zero of **9,483 workload weak references**
after the established public WPF view maintenance, live dispatcher sampling,
and complete existing graceful shutdown/target cleanup assertions.

| Run | Debug USER | Release USER | GDI, both configurations |
|---|---|---|---|
| F1, 30 intercepted APIs | 134→121→125 | 118→125→129 | 110→110→110 |
| F2, 46 APIs plus own-PID window events | 125→122→131 | 128→132→134 | 110→110→110 |

F1 receipt SHA256:
`F1869D1F988842D18E5112A54F5E4EE451F9795D891A86840785D73B637AB2A2`.
F2 receipt SHA256:
`2E4A61E97C8C08A8A889855EF4084D22DD50C6390854E29EF661D9F724FC73A1`.
Both `gui-verification.json` files say `OBSERVATIONS_VERIFIED` and
`GuiCriterionPassed=false`; each checks 802 managed observation rows, native
calibration/counter agreement, source/binary provenance and four additional
negative controls for missing lifetimes, retained owners, surviving HWNDs and
a stopped dispatcher. This is four new full-graph processes, not old TRX replay.

F2 `counter-criteria.json`, SHA256
`D68FE2C2F1C7059C9D4437381A3D620117B510CC86B9D3AFD185A837C5102AB5`,
establishes **scoped GDI object-count no-growth PASS** across all four processes.
USER no-growth remains unmet. Neither result proves native heap/GPU leak freedom,
physical presentation, genuine Explorer membership or unchanged startup.

## Native observer boundaries

The observer is built from pinned Microsoft Detours commit
`adb07604aa56508448b95bf037c2a6d0d3b6831a` (MIT). It intercepts documented API calls
inside its own process, preserves arguments/results/LastError and records raw
native stack PCs, sequence, thread, handle and call depth. Module load ranges
provide module/RVA identities; unresolved JIT frames are explicitly unresolved.
Nested API aliases, borrowed/shared loads, implicit kernel cleanup and direct
syscalls are not silently treated as independent allocations or leaks.
No foreign process is injected, suspended or manipulated. F2's WinEvent hook
filters the exact current PID. Window witnesses are queried only after checking
that the HWND still belongs to that PID. Instrumentation timing is not a
performance measurement. [Detours contract](https://github.com/microsoft/Detours/wiki/Using-Detours).

At every F2 epoch, both configurations have exactly the same **35 native HWND
identities**, five API-observed timers and stable observed resource counts:
15 WinEvent hooks (including the observer), three input/message hooks, one menu,
one icon and one cursor. The native API witness set does not explain the changing
aggregate USER count. It is not a complete kernel USER-object inventory.
`F2/native-api-attribution.json` preserves those comparisons and stack/module RVAs.

## Native event admission and attribution

The installed Win32k manifest and TDH metadata expose USER handle events
452/453/454 (create/destroy/owner update), GDI events 455–458, and payload fields
HandleValue, HandleType, SessionId and OwnerProcessId. These are saved under
`FWM-GUI-ATTRIBUTION-20260913-S1`. Native calibration then established delivery.
E1's PID filter and E2's OwnerProcessId payload filter were accepted by the API
but did not restrict the installed kernel provider. Both strict receipts remain
`PROVIDER_FILTER_UNMET`; these file captures were not extended into measurements.

E3 validates a realtime consumer that persists only resources owned by its own
PID, plus the transition of an already known own handle. Other resource events
are discarded before either binary or JSON storage. It does not claim provider
filtering. Independent decoding of the binary raw stream matches JSON exactly;
all 72 calibration create/free pairs and five negative controls pass, with zero
event/buffer/write/decode losses and the owned session stopped. Receipt SHA256:
`23F92DFA326EF837AC91835EBED2137905EBF5327252BCE7F2D1045E25B63BDA`.

F3 and F4 add four actual Debug/Release processes, each with the same 50 warmup
and 100 post-warmup lifetimes. All 9,483 workload weak references clear in each
process. Native QPC brackets connect the samples to owned create/free events.
F3 USER: Debug 123→129→122, Release 130→130→118. F4: Debug 126→128→129,
Release 121→135→124. GDI remains 110. Their raw no-growth gates remain false.

The varying native type 17 is calibrated in I1 with 24 explicit
ImmCreateContext/ImmDestroyContext pairs and 24 default contexts retrieved from
real own worker HWNDs. USER rises by 24 while the explicit contexts are held,
then returns to baseline. Worker contexts survive successful destruction of
their windows; every context has a native destroy event on its creating thread
after ThreadLeaving and before join completes. USER returns to the starting
count while the main process remains alive. Independent raw/JSON comparison,
48 exact native context pairs and five negative controls pass. I1 receipt:
`DF65C2572150A22E1D7DD1979377A75C57A69195DF3865EDA6708338D3ECF1F7`.
This agrees with the documented [default per-thread input context](https://learn.microsoft.com/en-us/windows/win32/intl/input-context)
and [explicit destruction contract](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immdestroycontext).

F4 independently enumerates its own live native thread IDs. Every live type-17
context at all six epochs has a unique creating thread that is still alive.
F3/F4 strictly verify native changes by handle identity: two hooks and a timer
are replaced with corresponding create/free events; F4 Release additionally
destroys one HWND. No non-context native USER type increases over baseline.
F3 attribution receipt SHA256:
`A1D63B8152F63739E5AABB76EE4B31043F48AA1A3F028688A7DA5B15620C1CC2`;
F4: `8FE5BD56208838DB6810B1DB7EC0A7024BCD787632D05F7AB56D2F403AF438F0`.

The proof concerns changes in the owned inventory. Two startup menus have
observed owner transfers; the current-owner inventory is consistently two below
GetGuiResources at the live epochs. Their transfer does not establish the
counter's charging rules. The absolute shutdown snapshot also has a discrepancy
in F3 Release. Neither absolute accounting nor a native heap/GPU leak-freedom
claim is made. The failed verifier assumptions and raw runs remain preserved.

## Quiescence boundary

F5 adds 45 seconds of observation after the same public WPF maintenance at each
epoch. Dispatcher, startup services and workers continue to run, and no thread
pool setting changes. Debug USER=86 and GDI=110 at all epochs. Release has
USER 88→86→86, GDI=110, with zero weak workload owners in both configurations.
However, Release's baseline samples rise 87→88, so the original strict GUI
gate rejects the run (`USER/GDI growth` within baseline). The receipt remains
false; post-warmup counters alone do not supersede that failure. F5 native
attribution passes, SHA256
`C59A5FB8CE974B979C1301AFAB1E6E98B136FAEC7E12C6D2071FF74C34969846`.
The next fixture records every stability sample, admitting the first 20 equal
samples after the same 45-second quiescence with a maximum of 300 attempts.
The existing no-growth verifier is preserved.

F6 preserves a fixture compilation error (int/uint). F7–F10 preserve actual
owned-process failures waiting for all 50 targets to enter layout, with zero
counter/retention acceptance. Between F5 and these runs, native target DPI is
observed to change from 96 to 192. Native WinForms reports a 2880×1800 screen
and 2880×1704 work area, while the production Win32DisplayManager in the private
desktop exposes its **NoMonitor 1024×768, scaling 1, 60 Hz fallback**. Production
bytes are unchanged. No display change or restoration is performed by this work;
the earlier 96/120-DPI criterion and restoration receipt retain their history.

The diagnostic targets now have an explicit 8×8 native minimum via their own
[WM_GETMINMAXINFO callback](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-getminmaxinfo),
with the existing resize/minimize styles and actual HWNDs. The fresh isolated
settings profile uses AutoSplitCount=50 so all 50 native targets fit one panel
in the observed fallback viewport. WindowPadding still alternates 4/12 and
the owner/lifetime/weak-reference criteria are unchanged. This is a compact
diagnostic layout, not original layout geometry or physical presentation.
F11 records the actual native minimum-size replies, target DPI and production
display identity. Its first equal 20-sample plateau is admitted under the same
rule for every epoch, with all prior samples retained and no minimum selection.

The executable target source is now isolated under
`scripts/performance/fullgraph-targets`, byte-exact to F11's frozen executed
source. The shared PERF-010 target source is restored byte-exact to F5's source.
T14 verifies the relocated target's Debug/Release build replay. This relocation
is not another native test or a repetition of the completed DPI experiment.

F11 completes both configurations and passes the **unchanged strict USER/GDI
no-growth verifier**. Debug USER 44→43→44 and Release USER 44→44→44;
GDI is 11 at all epochs in both. Each process verifies 50 warmup plus 100
post-warmup lifetimes and zero of 9,483 workload weak references on the live
dispatcher. Graceful graph/provider/target completion also passes.
`gui-verification.json` SHA256:
`2E6D6781F5E076AAB69ED707D265A2477F82FE92AB860BE14BB00075027BA910`.
`realtime-verification.json` verifies the complete raw/native/context/thread,
compact-target and first-stable-plateau contracts, SHA256:
`5EE897F094BF628204D0FFB3FB087744A1475F5E1F0D5222B960EC20B62C3BBB`.
This scoped PASS does not replace F5's failure or establish unchanged desktop
geometry, shell membership, wallpaper success, heap/GPU leak freedom or physical
presentation. No production optimization, A/B claim or ledger append follows.

The current token, WPR status and all ignored/untracked ETL paths are recorded in
E0. Unlike the earlier environment, a separately named CPU admission in E1
succeeds and stops cleanly. This is admission, not CPU/GPU measurement or a
performance claim. E4 hashes all 22 ETLs: the previous 19 files, totaling
111,353,266,176 bytes, are byte-exact; the only additions are the three E1/E2
owned captures. F3 onward uses realtime raw files and creates no ETL.
After the later environment change, E5 repeats the complete 22-file hash
inventory and current identity/token/WPR observation. It again verifies the
same 19 originals and only three additions. E5 verification SHA256:
`89DBA966A34590EFC043FBDC465DAC978E4C2AF1CD865F86C0DDA9D5CBFEF9E1`.
Existing foreign ETW sessions remain untouched. No graphics/display/security
policy changes, MSIX installation, production optimization or ledger append is
part of this continuation. Unmodified startup and genuine shell/wallpaper success
still require an appropriate isolated shell/user environment.

## Criterion mapping

| PERF-016 criterion | Verified evidence | Remaining proof / PASS boundary |
|---|---|---|
| Toast native lifetime | Preserved R2: five isolated native tests, 100 post-warmup lifetimes per configuration | Scoped PASS; no standalone rerun |
| SettingsWindow/OverlayHost closure and retention | Preserved native-owner checkpoint, constructor failure reds/correction, actual HWNDs/owners/dispatcher, cleanup order, stale callbacks, weak epochs | Scoped PASS; preserve real/mock and public WPF cache-maintenance boundaries |
| Auxiliary native windows, hooks, workspace, theme, browser | Prior native-owner receipts and full graph checkpoint rehashed | Scoped PASS at their documented constructors/provider/browser boundaries |
| Minimal WPF Application shutdown | Prior native teardown evidence | Separate from full startup/DI graph and from abrupt exit |
| Full configured startup graph workload retention | F1–F5 and F11: 12 live native graph processes, each 100 post-warmup lifetimes and zero of 9,483 workload weak owners | Fixed singleton startup owners remain intentionally alive until shutdown; explicit isolation adapters remain |
| Graceful full graph shutdown | New runs verify Main/animation/hooks before Application.Exit and workspace/Mica/provider stop before Startup return; own targets exit | Scoped PASS with adapters; no process-exit substitution for live weak retention |
| Hard crash and production error handler | Previous current-production C3: six owned Debug/Release processes; original exception/native dialog/partial managed cleanup and OS reclamation | Preserved evidence, not new crash tests; managed completion is not inferred from OS reclamation |
| Correct USER/GDI identity | P3/P4 native calibration, 16 historical corrections, immutable old evidence | Scoped PASS; GDI=0 and USER=1 |
| Native USER variation attribution | E3 own-resource realtime source, I1 exact HIMC calibration, F3/F4 handle/creator-thread identity and native replacement frees | Scoped PASS for deltas; transferred-menu absolute accounting and shutdown snapshot are outside that proof |
| Aggregate USER/GDI no-growth | F11 Debug/Release pass the unchanged strict gate with first-stable-window admission; F5 failure preserved | Scoped PASS for compact owned targets in the native NoMonitor fallback graph; preserve viewport/quiescence/adapter boundaries |
| Unmodified startup, real Explorer membership and wallpaper success | Private-desktop admission limitations and explicit adapters are documented | Needs an isolated shell/user/session/VM permitting the actual startup behavior; current restrictions forbid the global setting write |
| Native heap/GPU/physical presentation | Weak/USER/GDI evidence supplies no such proof | Keep attributed allocation/presentation boundaries and PERF-010/021 EXECUTION-R2/restoration dependencies; CPU admission is not GPU execution evidence |

There is no whole-ID BLOCKED or ACCEPT stage. No candidate A/B or delivery
rebuild is required for these test-only observer/fixture changes. Any production
change would require a new native red and the full applicable candidate/C2
protocol; none is introduced here.

## Integrity and preserved failures

The previous full-graph checkpoint's **125,479 files / 7,960,521,776 bytes** were
independently rehashed in R0/prior-seal-reverification.json. The inherited toast,
native owner and DPI receipts remain intact. R0's snapshot-output wrapper error,
T1's C++ variable collision, P1/P2's brush calibration failures, T6/T9 diagnostic
build errors, F6's int/uint fixture build error, F7–F10's native layout failures,
and the I1/F3/F4 verifier revision
failures remain preserved. No failed run is repurposed as successful evidence.

The ledger remains **41,782,945 bytes / 41,325 data rows**, SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
The old file is the complete byte-exact prefix; no suffix is appended.
