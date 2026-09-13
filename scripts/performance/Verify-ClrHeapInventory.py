"""Rehash every ETL, including ignored files, preserving the full old inventory."""
import argparse,importlib.util,shutil,subprocess
from datetime import datetime
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Prepare-ClrHeapSource.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--lock-boundary',action='store_true');a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/'Verify-ClrHeapInventory.py')
    prior=s.ART/'FWM-CLR-HEAP-20260913-R0/etl-before.json';before=s.s.read(prior);old={r['Path'].replace('\\','/').lower():r for r in before}
    r=s.capture(dest,'paths',['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert r.returncode==0;rows=[]
    for i,rel in enumerate(sorted(set(r.stdout.decode('utf-8').splitlines())),1):
        file=(s.ROOT/rel).resolve();assert file.is_relative_to(s.ROOT);start=file.stat();digest=s.s.sha(file);end=file.stat();assert (start.st_size,start.st_mtime_ns)==(end.st_size,end.st_mtime_ns)
        row=dict(Path=rel,Bytes=start.st_size,SHA256=digest);key=rel.replace('\\','/').lower()
        if key in old:assert row==old[key],rel
        rows.append(row);print('Verified ETL',i,rel,start.st_size,flush=True)
    actual={r['Path'].replace('\\','/').lower() for r in rows};assert old.keys()<=actual
    expected={'artifacts/performance/fwm-clr-heap-20260913-r0/cpu-admission.etl','artifacts/performance/fwm-clr-heap-20260913-p1/heap.etl','artifacts/performance/fwm-clr-heap-20260913-p2/heap.etl','artifacts/performance/fwm-clr-heap-20260913-f1/runs/debug-graceful/heap.etl','artifacts/performance/fwm-clr-heap-20260913-f1/runs/release-graceful/heap.etl'}
    if a.lock_boundary:expected.add('artifacts/performance/fwm-clr-heap-20260913-l1/heap.etl')
    assert actual-old.keys()==expected and len(rows)==26+len(expected)
    s.s.write(dest/'etl-after.json',rows);sessions=[]
    for suffix in ['P1','P2']:
        own=s.s.read(s.ART/('FWM-CLR-HEAP-20260913-'+suffix)/'owned-sessions.json');sessions.extend([own['Clr'],own['Heap']])
    for config in ['Debug','Release']:
        run=s.ART/'FWM-CLR-HEAP-20260913-F1/runs'/(config+'-graceful');sessions.extend([s.s.read(run/'clr-created.json')['Session'],s.s.read(run/'heap-session.json')['Session']])
    if a.lock_boundary:sessions.append(s.s.read(s.ART/'FWM-CLR-HEAP-20260913-L1/own-session.json')['Session'])
    for i,name in enumerate(sessions):assert s.capture(dest,'own-session-'+str(i),['logman','query',name,'-ets']).returncode&4294967295==0x80300002
    global_wpr=s.capture(dest,'wpr-final',['wpr','-status']);assert global_wpr.returncode==0
    own=s.capture(dest,'own-cpu-final',['wpr','-status','-instancename','FWM_OWNED_CLR_CPU_FWM-CLR-HEAP-20260913-R0']);assert own.returncode==0 and b'WPR is not recording' in own.stdout
    s.s.write(dest/'verification.json',dict(Verdict='COMPLETE_CLR_HEAP_ETL_INVENTORY_PASS',Files=len(rows),Bytes=sum(r['Bytes'] for r in rows),PriorFiles=len(before),PriorBytes=sum(r['Bytes'] for r in before),OriginalFilesUnchanged=True,IncludesIgnored=True,NewEtls=len(expected),NewPaths=sorted(expected),PriorInventorySHA256=s.s.sha(prior),InventorySHA256=s.s.sha(dest/'etl-after.json'),OwnedSessionsInactive=True,OwnSessions=sessions,GlobalWprInactive=b'WPR is not recording' in global_wpr.stdout,ForeignSessionsModified=False,PerformanceClaim=False))
    print('Complete immutable ETL inventory PASS',s.s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
