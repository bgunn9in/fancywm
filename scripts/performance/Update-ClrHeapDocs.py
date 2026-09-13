"""Advance live documents only after caller, boundary and witness receipts pass."""
import hashlib,json
from datetime import datetime
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];ART=ROOT/'artifacts/performance';PREFIX='FWM-CLR-HEAP-20260913-'
def read(name):return json.loads((ART/(PREFIX+name)/'verification.json').read_text(encoding='utf-8'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def main():
    assert datetime.now().strftime('%Y%m%d')=='20260913'
    analysis=read('D2');witness=read('D3');inventory=read('E2');boundary=read('L1');cal=read('V1')
    assert analysis['Verdict']=='SCOPED_FULLGRAPH_CLR_NATIVE_CALLERS_VERIFIED' and witness['Verdict']=='SCOPED_CLR_NATIVE_CALLER_WITNESSES_PASS'
    assert inventory['OriginalFilesUnchanged'] and inventory['OwnedSessionsInactive'] and len(boundary['Pairs'])==24
    assert all(x['EventStreamOnly'] and not x['CompleteDefaultHeapSnapshotsMatch'] for x in analysis['Configurations'])
    totals={key:sum(c[key] for c in analysis['Configurations']) for key in ['ClrRecords','NativeRecords','NativeStacks','NativeStackFrames','SelectedLiveStackWitnesses']}
    caller_count=sum(c['ManagedCallerWitnesses'] for c in witness['Configurations']);phase_rows=sum(c['PhaseRows'] for c in witness['Configurations'])
    graph=ART/'FWM-CLR-HEAP-20260913-F1';g=json.loads((graph/'FWM-CLR-HEAP-20260913-D2-graph-verification.json').read_text(encoding='utf-8'))
    data=[]
    for config in analysis['Configurations']:
        for phase in config['Phases']:
            data.append('| '+config['Configuration']+' '+phase['Phase']+' | '+str(phase['Blocks'])+' | '+str(phase['Categories'].get('TracedManagedCaller',0))+' | '+str(phase['BlocksWithFancyWmOrWinManFrame'])+' |')
    pins='\n'.join('- `'+name+'/verification.json`: `'+sha(ART/(PREFIX+name)/'verification.json')+'`.' for name in ['V1','N1','L1','D2','D3','E2'])
    report=ROOT/'docs/performance/CLR_NATIVE_HEAP_ATTRIBUTION.md';assert not report.exists()
    text=f'''# PERF-016 CLR/native caller attribution — 2026-09-13

Checkpoint `FWM-PERF016-CLR-20260913-R1` continues the
[retained cohort checkpoint](NATIVE_HEAP_COHORTS.md). PERF-016 remains
**IN_PROGRESS**. Production and all five inherited justified deltas are
unchanged. This is diagnostic native evidence with no performance claim,
stage acceptance, ledger append or MSIX execution.

## New source and calibration

The new external collector uses pinned TraceEvent 3.2.6, a fresh own session,
`NoRestartOnCreate`, PID-filtered CLR Loader/JIT/rundown providers and native
ControlTrace stop counters. Foreign events are rejected before storage;
both concurrent excluded control processes execute their real workload,
and the collector receives zero foreign events. Completed rundown precedes
all attributed allocations. Method/module load sequences, QPC boundaries,
address ranges and emitted ReJIT IDs distinguish code generations; a metadata
ID or address alone is not a generation identity. The independent Python
decoder checks raw method/module payloads against the SDK fields.

[CLR method events](https://learn.microsoft.com/en-us/dotnet/framework/performance/method-etw-events)
and the [pinned parser source](https://github.com/microsoft/perfview/blob/v3.2.6/src/TraceEvent/Parsers/ClrTraceEventParser.cs)
define the captured fields. Caller attribution does not supply object identity,
inlined logical owners, native DLL load lifetimes or ownership transfer.

T1/T2 preserve both builds; T2 adds incremental failure journals and safe
cleanup of remaining native buffers. P1 Debug and P2 Release each run an
included and excluded own process. Their 2 epochs cover 48 allocate/realloc/free
chains per process. **96 traced chains** across Debug/Release verify warmed
rundown code, newly JIT-compiled code and collectible plugin code. Four traced
plugin unloads occur while the corresponding native buffers remain live;
later realloc/free witnesses prove that code unload is not native owner cleanup.
The excluded processes execute another 96 chains as PID-filter controls.
V1 rejects 9 semantic negative controls per configuration; N1 rejects 9 raw
PID/QPC/version/address/module/tail/count corruptions. V0's first verifier
failure is retained: a pointer can have temporary allocations of other sizes
inside the first reflective JIT-call bracket. Exact pointer/size/thread/QPC
matching resolves that ambiguity without changing the native runs.

## Full startup graph and the failed atomic-snapshot assumption

F1 executes fresh Debug/Release Startup/AppMain/DI graphs on separate owned
non-input desktops. Each has 50 warmup and 100 post-warmup lifetimes and
0/9,483 workload weak owners after public WPF cache maintenance on its live
dispatcher. Every acquired graph HWND closes; settings HWNDs, target windows,
native hooks, workers, Mica/provider and graceful Application/dispatcher order
pass the retained strict checks and negative controls. Startup singleton owners
remain intentionally referenced during epochs. Existing profile/BAML/setting/
membership adapters remain; this is not unmodified Explorer startup.

CLR capture starts before production startup; heap capture starts at epoch 0.
F1 uses the fixture defaults of zero extra GUI quiescence and zero stable-window
admission, while older F4 used 45 seconds/30 seconds. Lifetime counts remain
comparable; heap/GUI totals are not a performance comparison across these
fixtures. Actual target HWND DPI is recorded in raw protocols; no display
setting or input was changed and no PERF-010 DPI experiment was repeated.
F1's default-heap no-growth gate is **false**. The scoped USER/GDI gates are
{', '.join(x['Configuration']+'='+str(x['GuiCriterionPassed']) for x in g['Configurations'])}; they do not replace earlier GUI receipts.

D1 fails the unchanged strict baseline-seeded replay: three 32-byte native
blocks are allocated on another thread within Debug's HeapWalk interval and
are absent from its snapshot. X1 retains their raw address/size/TID/QPC and
later free witnesses. No atomic heap/replay PASS is issued for F1.

L1 tests that boundary in a separate own native process. All **12 allocations
of 32 bytes complete while the main thread still holds the successful
HeapLock**; all 12 allocations of 64 KiB wait for unlock. Raw ETW allocation
events match independent call/lock intervals and exact pointers; 5 negative
controls reject missing/wrong identities and false completion boundaries.
This is observed behavior of this environment and these allocator paths.
It does not identify a general allocator implementation or a production defect.
[HeapLock documentation](https://learn.microsoft.com/en-us/windows/win32/api/heapapi/nf-heapapi-heaplock)
describes serialized access, but that description is insufficient to promote
these observations to a universally atomic whole-process snapshot.

The older F4 exact snapshot equalities remain verified observations of those
four boundaries. They are not broadened into a general atomicity contract.
Their immutable receipts and calibrated replay code remain byte-exact.
No repeated capture is selected merely to obtain a matching snapshot.

## Verified event-stream callers

D2 independently verifies **{totals['ClrRecords']:,} CLR records**, including
raw method/module decoding, and **{totals['NativeStacks']:,} full native stacks /
{totals['NativeStackFrames']:,} PCs** from {totals['NativeRecords']:,} native records.
Native ProcessTrace and the separate TraceEvent stack reader agree byte-for-byte;
both readers and CLR session stop report zero lost events/buffers.
The unchanged realloc model reconstructs recorded allocation-event generations.
It does not seed F1 with a purported atomic baseline. Snapshot disagreements
are exported explicitly, with `CompleteDefaultHeapSnapshotsMatch=false`.

D3 reopens all {totals['SelectedLiveStackWitnesses']:,} selected native stacks,
{caller_count:,} managed caller witnesses and {phase_rows:,} phase rows, checks
the original native allocation addresses/sizes and raw stack tails, then checks
code-version/module-generation/QPC boundaries separately. Ten additional
negative controls reject stale methods, wrong PCs/versions/modules and tail
corruption. Rows below mean live **in the event stream at each cut**, not a
complete atomic native heap inventory or managed logical ownership.

| Configuration / cut | Traced stream-live blocks | With managed caller | With FancyWM/WinMan frame |
|---|---:|---:|---:|
{chr(10).join(data)}

Exact per-event stacks and names are in D2's `stack-callers.jsonl`; phase rows
and snapshot discrepancies are separate immutable exports. Unresolved native
frames and pre-attach allocations remain unknown. No native-heap, direct
VirtualAlloc, private-heap, GPU-leak or physical-presentation claim is made.

## Current criterion mapping

| Criterion | Verified evidence | Missing proof / PASS condition |
|---|---|---|
| Native toast/settings/overlay/auxiliary closure | Prior immutable native receipts; F1 affected full-graph settings/graph lifetimes | Preserve constructor/failure/cache-maintenance bounds; no standalone toast repeat |
| Full graph retention and graceful teardown | F1 Debug/Release 100 post-warmup lifetimes, zero workload weak owners, native resources closed | Existing startup/membership adapters and intentionally live singleton ownership remain |
| CLR caller identity across native allocations | New P1/P2/V1/N1 and F1/D2/D3 with real code unload/address reuse and zero trace loss | Scoped PASS for emitted code generations and native stack callers; not logical owners |
| Atomic complete native heap lifetime / no-growth | F1 snapshots and D1/X1 mismatch; L1 demonstrates concurrent small allocation during HeapLock | Need a calibrated snapshot/serialization and ownership source valid for these paths; changing QPC cuts or retrying until equality is not PASS |
| Native logical owner and pre-attach lifetime | Raw event-stream allocations/reallocs/frees now carry observed managed callers | Caller names do not expose native ownership transfer or pre-attach history; no such identity contract is present in these providers |
| Minimal WPF, full graph and hard crash | Earlier separate receipts; current-production C3's six owned crashes retained | OS reclamation remains separate from managed cleanup; no production/crash-handler delta justifies a repeat |
| Unmodified startup / Explorer membership / wallpaper | Prior environment findings; F1 repeats explicit isolated adapters | Requires an appropriate disposable shell/user/session/VM; current host restrictions remain |
| Changing-display GUI no-growth | Earlier scoped F11 PASS; F1 and earlier mixed/false observations retained | New controlled environment and applicable native lifetime evidence; no inferred counter PASS |
| PERF-010/021 GPU/presentation | EXECUTION-R2 and actual DPI restoration receipt retained | New attributed GPU execution and presentation identity contracts, then candidate GPU A/B |

Available CLR/native payloads and selected caller witnesses have been decoded
and checked. The failed atomicity condition is reproduced by a bounded native
control. These providers do not expose a stronger snapshot or logical-owner
contract; repeating the same captures, elevation or another CPU trace cannot
supply it. No production optimization/candidate is justified by these counts.

## Integrity and retained failures

R0 rehashes the prior cohort seal: 2,633 files / 223,547,560 bytes, preserving
verification `2201A71A5EACD8337FBD27C20B77ADB0BA91A84FE9F04D09B13BF13A054534AE`.
This is old receipt verification, not new tests. T1, V0 and D1 remain archived.
New native work comprises four managed/native control processes, two full-graph
processes with two own target processes, four CLR collector processes and one
native lock-boundary process. Builds and offline decoders are recorded
separately. There are zero new TRX, hard-crash or dedicated DPI runs. CPU
admission succeeds independently and is not a performance measurement.

E2 rehashes all **{inventory['Files']} ETLs**, including ignored files. The 26 old
paths/sizes/SHA256 values are unchanged; six new ETLs are one CPU admission,
two CLR/native controls, two full-graph heap traces and one lock control.
All nine own heap/CLR sessions are inactive. Final review rechecks Git/root/
index/recursive submodules, pinned HEAD/tree, all 2,155 production files
(72 winman-windows), control document sizes/lines/SHA256 and old receipts.
PERF-002 remains 250 ms. Ledger remains 41,782,945 bytes / 41,325 data rows,
SHA256 `84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11`;
its full old bytes are the prefix, with an empty suffix.

Receipt hashes (artifact prefix `{PREFIX}`):

{pins}
'''
    report.write_text(text,encoding='utf-8',newline='\n')
    entry=json.loads((ART/(PREFIX+'R0')/'entry-review.json').read_text(encoding='utf-8'))
    for rel,link in [('PERFORMANCE_STATUS.md','docs/performance/'),('PERFORMANCE_TODO.md','docs/performance/'),('docs/performance/AUDIT.md',''),('docs/performance/IMPLEMENTATION_RESULTS.md',''),('scripts/performance/README.md','../../docs/performance/')]:
        p=ROOT/rel;old=p.read_bytes();assert hashlib.sha256(old).hexdigest().upper()==entry['Documents'][rel]['SHA256'],rel
        first,separator,rest=old.partition(b'\n');nl='\r\n' if first.endswith(b'\r') else '\n'
        notice=f'''Latest verified continuation (2026-09-13): `FWM-PERF016-CLR-20260913-R1` —
[CLR/native caller attribution and heap boundary evidence]({link}CLR_NATIVE_HEAP_ATTRIBUTION.md).
New Debug/Release controls verify 96 traced allocation/realloc/free chains,
collectible code unload/address reuse and PID filtering. New F1 full startup
graphs retain zero of 9,483 workload weak owners per process and complete
native graceful teardown. D2/D3 verify {totals['NativeStacks']:,} full native stacks and
{caller_count:,} managed caller witnesses with exact code-generation/QPC bounds.
D1's strict baseline heap replay failure is preserved. Native L1 reproduces
12 small allocations completing while HeapLock is held; F1 has no atomic
whole-heap replay PASS. Older finite snapshot equalities remain preserved.
Heap no-growth/logical ownership, unmodified shell/startup and broader GUI/
GPU/presentation criteria remain open; PERF-016 stays IN_PROGRESS. Production,
five inherited deltas, PERF-002 250 ms and ledger are unchanged. No performance
claim, stage acceptance, new TRX/crash or dedicated DPI run. All 32 ETLs are
rehashed with the 26 old files unchanged. Earlier next actions are historical
where superseded; see the report for exact remaining evidence contracts.
'''
        updated=first+separator+nl.encode()+notice.replace('\n',nl).encode()+rest
        if rel=='PERFORMANCE_TODO.md':
            rr=[r for r in updated.splitlines(keepends=True) if r.startswith(b'| PERF-016 |')];assert len(rr)==1
            row='| PERF-016 | 2 | IN_PROGRESS | Scoped native owners/startup graph with adapters; CLR/native caller identity | Prior native candidates/C2/crashes retained; new F1 weak/teardown and calibrated code-generation/stack witnesses | Atomic heap boundary fails native small-allocation control; logical owners/pre-attach history, unmodified shell and broader GUI/GPU/presentation contracts remain missing |'+nl
            updated=updated.replace(rr[0],row.encode(),1)
        p.write_bytes(updated)
    print('New verified CLR report and five live pointers written')
if __name__=='__main__':main()
