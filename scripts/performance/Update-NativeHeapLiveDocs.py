"""Prepend the verified continuation while preserving prior text byte-for-byte."""
import hashlib,json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
def main():
    entry=json.loads((ROOT/'artifacts/performance/FWM-NATIVE-HEAP-20260913-R0/entry-review.json').read_text(encoding='utf-8'))
    for rel,link in [('PERFORMANCE_STATUS.md','docs/performance/'),('PERFORMANCE_TODO.md','docs/performance/'),('docs/performance/AUDIT.md',''),('docs/performance/IMPLEMENTATION_RESULTS.md',''),('scripts/performance/README.md','../../docs/performance/')]:
        p=ROOT/rel;old=p.read_bytes();assert hashlib.sha256(old).hexdigest().upper()==entry['Documents'][rel]['SHA256'],rel
        first,separator,rest=old.partition(b'\n');assert separator
        nl='\r\n' if first.endswith(b'\r') else '\n'
        text=f'''Latest verified continuation (2026-09-13): `FWM-PERF016-HEAP-20260913-R1` —
[native heap and graph HWND generations]({link}NATIVE_HEAP_LIFETIME.md).
F3/F4 Debug/Release verify every acquired/closed graph HWND generation and
graceful startup-graph teardown, with zero of 9,483 workload weak owners and
100 post-warmup lifetimes per process on the live dispatcher. Existing isolation
adapters remain. F4 verifies 6,790,750 alloc/realloc/free rows and 163,322,214
stack PCs against independent decoders and locked native heap snapshots.
This is scoped post-attach attribution PASS: default-heap no-growth is false;
new GUI no-growth gates are mixed/false and are preserved. Prior F11 scoped
GUI PASS is not broadened. No complete native-heap/GPU leak-freedom claim.
Six new graph executions include two F1 verifier failures; F2's build failure,
admission/decoder failures and all old evidence remain archived. Production,
the five inherited deltas, PERF-002 250 ms and ledger are unchanged. No new
TRX/crash/DPI run, production candidate, delivery gate or stage acceptance.
PERF-016 stays IN_PROGRESS. Unmodified startup/membership/wallpaper needs an
appropriate isolated shell/user environment. Broader heap owner lifetime and
changing-display GUI no-growth remain unproved; PERF-010/021 still needs new
GPU/presentation contracts. Earlier next actions are historical where superseded.
'''
        updated=first+separator+nl.encode()+text.replace('\n',nl).encode()+rest
        if rel=='PERFORMANCE_TODO.md':
            rows=[l for l in updated.splitlines(keepends=True) if l.startswith(b'| PERF-016 |')];assert len(rows)==1
            row='| PERF-016 | 2 | IN_PROGRESS | Scoped native owners, startup graph with adapters, dynamic graph HWNDs and post-attach heap attribution | Prior native red/candidate/C2 and six crashes preserved. New F3/F4: four verified graph processes, zero of 9,483 workload weak owners each; strict F4 native heap event/stack replay | Unmodified shell/membership/wallpaper environment; changing-display GUI and default-heap no-growth not passed; complete allocation-owner lifetime and GPU/presentation remain unproved |'+nl
            updated=updated.replace(rows[0],row.encode(),1)
        p.write_bytes(updated)
    print('Five live documents updated; historical continuation text preserved')
if __name__=='__main__':main()
