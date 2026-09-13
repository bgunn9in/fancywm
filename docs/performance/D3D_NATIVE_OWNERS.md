# Native D3D ownership observations — 2026-09-13

Current continuation: `FWM-PERF016-D3D-20260913-R1`.
PERF-016 remains **IN_PROGRESS**. ProductionChanged=false,
PerformanceClaim=false, StageAccepted=false, LedgerAppended=false.
This extends [the PSS checkpoint](PSS_NATIVE_MEMORY.md) with a calibrated
process-local D3D9 private-data observer and separate D3D11 destruction controls.
The final immutable manifest and repository/process checks are in that checkpoint.

## Verified full startup graph observations

`FWM-D3D-LIFETIME-20260913-F2` runs the production startup/service graph in
Debug and Release with the declared test-copy adapters. Each process completes
50 warmup plus 100 post-warmup lifetimes on a live STA dispatcher. All 9,483
workload weak owners clear after the existing public WPF view-cache maintenance;
the passive last SettingsWindow/view-model/navigation retention remains recorded.
All four graph HWND generations close, native hooks/workers/provider teardown
complete, and Application exit occurs before dispatcher termination. Each
owned target exits with its HWNDs destroyed. D3 verifies these facts and five
PSS clone lifetimes per configuration; all clones disappear before the next work.

The exact T9 observer DLL calibrated by V3 is loaded before Startup.Main and
the startup PSS sample. It observes native WPF D3D9 calls and attaches private
IUnknown data without retaining resource COM references. D4 independently
replays registrations, releases, native call entry/exit nesting and checkpoints:

| Configuration | Registered private owners | Released private owners | External Lock/Unlock pairs | Active after warmup / epoch 1 / epoch 2 | Active after shutdown |
|---|---:|---:|---:|---|---:|
| Debug | 84,060 | 84,060 | 1,005 | 45 / 45 / 45 | 0 |
| Release | 83,919 | 83,919 | 1,016 | 45 / 45 / 45 | 0 |

Each live checkpoint is derived from generation history, not only equal
counters. The details receipt records surviving warmup and newly created
cohorts. All registered owners release while the graph process is still alive,
before observer detachment; process exit is not their release evidence.
Every observed top-level map has a matching native API completion, with no
unowned top-level maps, unknown texture parents or failed hooked HRESULTs.
No nested maps were delivered in these graph runs; nested delivery is exercised
by the independent producer controls. Raw native caller PCs are retained, but
are not promoted to attributed module/GPU execution or presentation identities.

The observer covers CreateTexture, CreateVertexBuffer, CreateIndexBuffer,
CreateRenderTarget, CreateDepthStencilSurface, CreateOffscreenPlainSurface,
their three applicable Ex surface variants, GetSurfaceLevel and the calibrated
texture/surface/buffer Lock/Unlock implementations. Cube/volume textures,
implicit swap-chain/backbuffer owners, shaders, queries, state blocks, driver
allocations and other unhooked implementations remain outside this source.
Absence of an event cannot establish absence of those resource types.

## Native calibration and contract boundaries

V1 verifies four fresh native processes, Debug/Release for each API:

- D3D11: 150 buffer lifetimes per configuration (100 post-warmup), 182 actual
  destruction callbacks including 30 views, device and context; 360 Map/Unmap
  pairs; 30 worker-thread final releases and 30 unregistered callbacks that
  never fire. Explicit retainers, view and context ownership delay destruction.
  Releasing the external immediate-context reference does not destroy it while
  the device is held; final device release delivers context then device callbacks.
- D3D9: 180 private-data generations per configuration across 150 texture
  lifetimes, explicit retainers, texture/surface ownership and worker release.
  Thirty FreePrivateData cases per configuration release the private IUnknown
  while GetLevelDesc still succeeds on the real texture. This is a direct
  counterexample to equating private-data release with COM resource destruction.

V3 calibrates the final intercepted source in two further independent native
producer processes. Each configuration verifies 1,050 resource/private-data
generations, 600 external Lock/Unlock pairs, 150 nested Surface.LockRect calls
inside Texture.LockRect, and 1,350 native call entry/exit pairs. Producer journals
independently supply canonical IUnknown identities and returned map pointers.
Normal and Ex APIs, address reuse, surface-parent retention and extra references
are covered. Every callback and checkpoint is matched to its generation.

V1 rejects 14 negative controls; V3 rejects nine. D3 rejects nine managed/native
window/PSS controls; D4 rejects eleven ownership, missing map/call boundary,
checkpoint, PID, HWND and retained-managed-owner controls. Verifiers create
receipts exclusively. Finalization checks their refusal to overwrite receipts.

The runtime contract is intentionally narrow. Microsoft's
[ID3DDestructionNotifier documentation](https://learn.microsoft.com/en-us/windows/win32/api/d3dcommon/nn-d3dcommon-id3ddestructionotifier)
documents destruction callbacks for supported D3D11/12 objects. Probe P1 obtains
and exercises this interface on the actual hardware D3D11 device/context/buffer.
P2 receives E_NOINTERFACE (0x80004002) from all four tested D3D9Ex objects.
[D3D9 private surface data](https://learn.microsoft.com/en-us/windows/win32/direct3d9/private-surface-data)
has a separate IUnknown ownership contract. Neither source supplies a physical
GPU allocation-release or display-presentation contract.

## PSS and instrumentation limits

| Configuration | Clone-private committed bytes: warmup / epoch 1 / epoch 2 | After shutdown | Private no-growth | USER/GDI criterion |
|---|---|---:|---|---|
| Debug | 256913408 / 264028160 / 261468160 | 182628352 | False | False |
| Release | 248287232 / 257421312 / 257163264 | 178872320 | False | False |

These false no-growth results remain false. The observer intentionally retains
generation metadata and pointer aliases until process termination so late
private-data callbacks can safely reference them. Its DLL is never unloaded
while callbacks could remain. This diagnostic memory is included in PSS; no
production leak, optimization or performance delta is inferred from those sums.
Its seed device also initializes the D3D runtime before application startup.

None of the ten PSS capture intervals lies wholly inside an observed map
lifetime. Therefore no graph PSS omission is assigned to a specific D3D owner
from these pointer observations. A pointer outside its Lock/Unlock interval,
address reuse or PAGE_WRITECOMBINE alone cannot provide that identity. The
previous ordinary-allocation cloning and actual omitted D3D11-buffer evidence
remain unchanged. PSS still has no complete native/graphics-memory claim.

F1 and F2 use the ordinary owned target layout (AutoSplitCount=5, native minimum
unchanged), recorded in GuiTargetLayoutContract. The older PSS F3 used compact
targets (AutoSplitCount=50, minimum 8). Within each new Debug/Release run the
workload is fixed; these totals are not an A/B comparison to older PSS F3.
No user input is sent, private desktops are never switched onto the input
desktop, and no display, driver or security setting is changed.

## Preserved failed fixtures and earlier observations

All T1–T9 builds and P1–P13 process receipts remain immutable evidence. P3
fails the original context-destruction timing assumption after completing its
150 workload cases. T4 corrects that fixture assumption; P4/P5 then pass.
P8/P9 finish their native producer runs but V0 rejects the missing plain-surface
map identity. T8 adds the actual distinct plain-surface method implementations.
V2 verifies P10/P11's explicit producer calls and remains a valid finite-scope
receipt; it does not verify every runtime-internal map call.

The expanded D2 replay fails on 150 runtime-internal surface locks in each of
P10/P11: their completion occurs through the outer texture API, with no matching
Surface.UnlockRect API call. F1's managed/PSS D1 receipt remains valid, but F1
has no complete D3D map-boundary claim. T9 adds native entry/exit scopes with
thread, unique call, parent and depth identities. V3 and F2/D4 verify this new
source. B1 reproduces the old four verifier failures using frozen code and
checks the unsupported-interface and failed-context boundaries without starting
new native tests. X1/X2 and failed verifier roots are retained.

There is no new production defect or production change. Only test harness,
native source, verifier and documentation files change. Production C2 delivery
gates, standalone toast, dedicated DPI and unchanged hard-crash runs are not
repeated. Current-production C3 six-crash receipts are reverified; OS reclamation
remains distinct from managed cleanup.

## Remaining criterion mapping

| Criterion | Verified evidence | Missing evidence / PASS condition |
|---|---|---|
| Native toast/settings/overlay/auxiliary owners | Earlier native-owner receipts preserved; F2 repeats affected settings and graph HWND closure | Preserve stated constructor/failure/ownership scopes; do not reopen completed standalone toast |
| Full graph managed retention and graceful teardown | F2/D3, 100 post-warmup lifetimes/configuration, zero of 9,483 workload weak owners and native provider cleanup | Unmodified startup and all fixed-singleton ownership are not inferred from adapted graphs |
| D3D9 private-data retention | V3 calibration and F2/D4, repeated stable counts and zero active registered owners before process exit | PASS only for registered private IUnknown owners; complete COM/GPU-resource ownership remains open |
| D3D11 object destruction | P4/P5/V1 callback and ownership scenarios | Native controls only; no full-graph D3D11 or physical allocation claim |
| Complete native/graphics memory | Prior PSS omission controls plus new map/owner generations | Need a resource/backing-allocation identity and lifetime source covering omitted mappings and unhooked owners; no map spans current captures |
| Native allocator blocks and logical owners | Earlier CLR/native stack evidence, heap-lock and PSS boundaries preserved | Calibrated allocator decoder with atomic state, pre-attach history and logical-owner contract still missing |
| Whole-process memory and USER/GDI no-growth | F2/D3 records growth; observer metadata is explicitly included | These no-growth criteria have no new PASS; scoped D3D owner counts do not replace them |
| Minimal WPF, full graph and hard crash | Earlier separate scopes and current-production C3 six-crash receipt preserved | No new crash-handler or production delta; no repeat of unchanged successful crash experiments |
| Unmodified shell/startup/membership/wallpaper | Explicit profile/BAML/setting-write/owned-membership adapters | Requires appropriate disposable isolated shell/user/VM; foreign Explorer state is not modified |
| PERF-010/021 execution and presentation | EXECUTION-R2 and successful actual-DPI restoration retained | Attributed GPU execution and full-frame/non-client presentation identity contracts, then candidate GPU A/B |

The newly available documented callback sources, native producer controls and
adapted full-graph use are executed. The next evidence-dependent step is a
verified complete resource/backing-allocation lifetime source or atomic native
allocator decoder, calibrated before another full-graph memory claim. Repeating
the same captures, elevation, CPU admission or display changes does not supply
that missing contract. PERF-016 stays IN_PROGRESS; there is no whole-ID BLOCKED,
invented ACCEPT stage or ledger append.

## Integrity and exact runs

R0 and finalization rehash the prior PSS seal: 24,129 files / 1,580,239,617 bytes.
Original toast, DPI restoration and current-production C3 are rechecked via the
checkpoint review. These are old receipt checks, not new test runs. All 2,155
production files, including 72 winman-windows files, match the inherited state
with only the five previously justified corrections. HEAD/tree/submodules,
root/index diff checks, document sizes/lines/SHA256 and ledger prefix are checked.

Ledger remains 41,782,945 bytes / 41,325 data rows, SHA256
`84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`.
The full old ledger is the byte-identical prefix; suffix length is zero.
Finalization checks all 41 owned process/clone identities. New work consists of
13 native control processes (12 exit zero, P3 fails), four graph processes,
four owned targets and 20 PSS clones. There are nine native tool builds and
eight successful graph/target builds. New TRX, crash, dedicated DPI and ETL
runs: zero. No tracing session is started; all 32 ETL paths/sizes are unchanged,
and read-only WPR/session checks are saved. No new full ETL rehash is claimed.

Principal commands (build/process receipts retain exact paths, order and
environment; raw stdout/stderr, native journals and provenance are archived):

```powershell
python scripts/performance/Build-D3DLifetime.py --id FWM-D3D-LIFETIME-20260913-T9
python scripts/performance/Verify-HookedD3D9.py --id FWM-D3D-LIFETIME-20260913-V3 --runs FWM-D3D-LIFETIME-20260913-P12 FWM-D3D-LIFETIME-20260913-P13
python scripts/performance/Run-FullGraphLifetime.py --id FWM-D3D-LIFETIME-20260913-F2 --owned-membership --warmup 50 --modes graceful --pss-tool artifacts/performance/FWM-PSS-HEAP-20260913-T5 --d3d9-tool artifacts/performance/FWM-D3D-LIFETIME-20260913-T9
python scripts/performance/Verify-PssGraph.py --snapshot artifacts/performance/FWM-D3D-LIFETIME-20260913-F2 --id FWM-D3D-LIFETIME-20260913-D3
python scripts/performance/Verify-D3D9Graph.py --graph artifacts/performance/FWM-D3D-LIFETIME-20260913-F2 --pss-proof artifacts/performance/FWM-D3D-LIFETIME-20260913-D3/verification.json --id FWM-D3D-LIFETIME-20260913-D4
```

Receipt SHA256:

- R0: `3917FE94358B8C4A0B7BDDD6F1FA68DBE615B2A492287A033D3C288420276275`.
- V1: `62EF95B9FB0547E04B626C8E18AAC6E6618246B456959A1D1D5AA6EB3D9DF6A3`.
- V2: `05B24E886B38AC13DD74321090685DA4BD826C095FB83C7D7FD288B21E719720`.
- V3: `834A960771929092A30997488ABAD9A99CA408B04B1545E6A7E54F6BA0406662`.
- B1: `D975724EBD81F58A6D219911E350FCEE26DAEB55DB5A21563408819E7B1C568D`.
- D1: `D4853FBF4DE8AEA92038175BE86BA0E857E12600F8F1D567DA45CABE95E0B35D`.
- D3: `A3FA758B33A014E59AB3595BAC43036B8405FA587E31347EC85A54017A6A6011`.
- D4: `24268F53F4AF132605350D1CFDBF2CB54886CF53E9306841A771BB83D2B52A43`.
