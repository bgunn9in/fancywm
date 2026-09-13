"""Advance live pointers to verified offline cohorts, retaining all history."""
import hashlib,json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
def main():
    entry=json.loads((ROOT/'artifacts/performance/FWM-HEAP-COHORT-20260913-R0/entry-review.json').read_text(encoding='utf-8'))
    for rel,link in [('PERFORMANCE_STATUS.md','docs/performance/'),('PERFORMANCE_TODO.md','docs/performance/'),('docs/performance/AUDIT.md',''),('docs/performance/IMPLEMENTATION_RESULTS.md',''),('scripts/performance/README.md','../../docs/performance/')]:
        p=ROOT/rel;old=p.read_bytes();assert hashlib.sha256(old).hexdigest().upper()==entry['Documents'][rel]['SHA256'],rel
        first,separator,rest=old.partition(b'\n');nl='\r\n' if first.endswith(b'\r') else '\n'
        text=f'''Latest verified continuation (2026-09-13): `FWM-PERF016-COHORT-20260913-R1` —
[complete baseline heap replay]({link}NATIVE_HEAP_COHORTS.md).
The retained F4 Debug/Release traces now reproduce every busy default-heap
address/size from locked epoch 0 through epochs 1/2/shutdown, including initially
untraced blocks. Native control covers 48 moved realloc pairs; independent
witness checks cover 771,654 phase rows and 73,811 realloc successors.
At epoch 2, Debug retires 2,123 original generations and has 3,498 new live
generations; Release retires 2,867 and has 5,889 new live generations.
This is scoped baseline inventory/lifetime replay PASS. Heap no-growth remains
false, and native generation retirement does not prove logical owner cleanup.
Zero new native application/TRX/crash/DPI/ETL runs; only offline analysis and
receipt verification. Production, inherited five deltas, PERF-002 250 ms and
ledger are unchanged. No performance claim or stage acceptance.
PERF-016 stays IN_PROGRESS. Unmodified startup/membership/wallpaper requires
an appropriate isolated shell/user environment; original allocation stacks,
logical ownership, broader GUI no-growth and GPU/presentation remain unproved.
Earlier next actions are historical where superseded.
'''
        updated=first+separator+nl.encode()+text.replace('\n',nl).encode()+rest
        if rel=='PERFORMANCE_TODO.md':
            rows=[l for l in updated.splitlines(keepends=True) if l.startswith(b'| PERF-016 |')];assert len(rows)==1
            row='| PERF-016 | 2 | IN_PROGRESS | Scoped native owners/startup graph with adapters; complete default-heap post-baseline replay | Prior native red/candidate/C2, six crashes and F3/F4 weak/teardown receipts retained. New offline T2/D1/D2: full busy inventory, generation retirements and realloc successor witnesses | Unmodified shell/membership/wallpaper environment; heap and changing-display GUI no-growth not passed; original allocation stacks/logical owners and GPU/presentation remain unproved |'+nl
            updated=updated.replace(rows[0],row.encode(),1)
        p.write_bytes(updated)
    print('Five live documents advanced; historical continuation text retained')
if __name__=='__main__':main()
