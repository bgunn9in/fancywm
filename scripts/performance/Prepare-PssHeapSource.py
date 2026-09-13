"""Reverify the complete CLR seal before a new non-tracing snapshot source."""
import argparse,importlib.util,json,shutil,subprocess
from datetime import datetime,timezone
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Prepare-ClrHeapSource.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    prior=s.ART/'FWM-PERF016-CLR-20260913-R1';assert s.s.sha(prior/'verification.json')=='17B05168417C5258C8822D4A3D09B626F623F54EEDB05C1CA0F21A77CE34C6B0';assert s.s.sha(prior/'evidence-manifest.json')=='FBF6734DFFC17FE5B2EDB943D120D619FF38369548FD3A7F95AD0E6C518C2BFA'
    rows=s.s.read(prior/'evidence-manifest.json')
    for i,row in enumerate(rows,1):
        assert s.s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']},row['Path']
        if i%2000==0:print('Prior seal rehashed',i,len(rows),flush=True)
    s.s.write(dest/'prior-seal-reverification.json',dict(Verdict='PASS',Files=len(rows),Bytes=sum(r['Bytes'] for r in rows),VerificationSHA256=s.s.sha(prior/'verification.json'),ManifestSHA256=s.s.sha(prior/'evidence-manifest.json'),NewNativeTests=0))
    cmd=['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(s.ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(s.ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(s.ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(dest/'entry-review.json')]
    assert s.capture(dest,'entry-command',cmd).returncode==0
    old=s.s.read(prior/'validation/checkpoint-review.json');entry=s.s.read(dest/'entry-review.json');assert all(entry[k]==old[k] for k in ['Production','Documents','LedgerIntegrity'])
    identity="[pscustomobject]@{Time=[DateTime]::UtcNow.ToString('o'); Identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name; Elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); OS=[Environment]::OSVersion.VersionString; ProcessId=$PID} | ConvertTo-Json"
    for name,cmd in [('identity',['pwsh','-NoProfile','-Command',identity]),('token',['whoami','/all']),('wpr',['wpr','-status']),('sessions',['logman','query','-ets'])]:assert s.capture(dest,name,cmd).returncode==0
    paths=s.capture(dest,'etl-paths',['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths.returncode==0
    inventory=s.ART/'FWM-CLR-HEAP-20260913-E2/etl-after.json';actual=sorted((r.replace('\\','/').lower(),(s.ROOT/r).stat().st_size) for r in paths.stdout.decode('utf-8').splitlines());expected=sorted((r['Path'].replace('\\','/').lower(),r['Bytes']) for r in s.s.read(inventory));assert actual==expected and len(actual)==32
    s.s.write(dest/'verification.json',dict(Verdict='PSS_SOURCE_ENTRY_VERIFIED',PriorFiles=len(rows),PriorBytes=sum(r['Bytes'] for r in rows),PriorManifestSHA256=s.s.sha(prior/'evidence-manifest.json'),EntryReviewSHA256=s.s.sha(dest/'entry-review.json'),EtlPathsAndSizesUnchanged=True,EtlFiles=32,PriorEtlInventorySHA256=s.s.sha(inventory),FullEtlHashPassRepeated=False,NewTracingSession=False,NoCpuAdmissionRequired=True,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS'))
    print('PSS entry verified',s.s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
