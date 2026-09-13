"""Reverify PSS checkpoint before a new process-local graphics lifetime source."""
import argparse,importlib.util,shutil
from pathlib import Path
spec=importlib.util.spec_from_file_location('p',Path(__file__).with_name('Prepare-ClrHeapSource.py'));p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p);s=p.s
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--id',required=True);a=parser.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    prior=s.ART/'FWM-PERF016-PSS-20260913-R1';assert s.sha(prior/'verification.json')=='7429B1E14D333224534FC8B5E7E00F2D08122F9B4D32A3361BBAF0A64A7EFDB5';assert s.sha(prior/'evidence-manifest.json')=='1788D3438643A9B2EF8A1038C721282B8E8449F5C0A7A6C5B64853E336B96A44'
    rows=s.read(prior/'evidence-manifest.json')
    for i,row in enumerate(rows,1):
        assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        if i%5000==0:print('Prior PSS seal rehashed',i,len(rows),flush=True)
    s.write(dest/'prior-seal-reverification.json',dict(Verdict='PASS',Files=len(rows),Bytes=sum(r['Bytes'] for r in rows),VerificationSHA256=s.sha(prior/'verification.json'),ManifestSHA256=s.sha(prior/'evidence-manifest.json'),NewNativeTests=0))
    cmd=['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(s.ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(s.ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(s.ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(dest/'entry-review.json')];assert p.capture(dest,'entry-command',cmd).returncode==0
    old=s.read(prior/'validation/checkpoint-review.json');entry=s.read(dest/'entry-review.json');assert all(entry[k]==old[k] for k in ['Production','Documents','LedgerIntegrity'])
    doc=s.ROOT/'docs/performance/PSS_NATIVE_MEMORY.md';assert s.info(doc)=={k:s.read(prior/'validation/control-documents.json')['docs/performance/PSS_NATIVE_MEMORY.md'][k] for k in ['Bytes','SHA256']}
    identity="[pscustomobject]@{Time=[DateTime]::UtcNow.ToString('o'); Identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name; Elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); OS=[Environment]::OSVersion.VersionString} | ConvertTo-Json"
    for name,cmd in [('identity',['pwsh','-NoProfile','-Command',identity]),('token',['whoami','/all']),('wpr',['wpr','-status']),('sessions',['logman','query','-ets'])]:assert p.capture(dest,name,cmd).returncode==0
    paths=p.capture(dest,'etl-paths',['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths.returncode==0
    inventory=s.ART/'FWM-CLR-HEAP-20260913-E2/etl-after.json';actual=sorted((r.replace('\\','/').lower(),(s.ROOT/r).stat().st_size) for r in paths.stdout.decode('utf-8').splitlines());expected=sorted((r['Path'].replace('\\','/').lower(),r['Bytes']) for r in s.read(inventory));assert actual==expected and len(actual)==32
    s.write(dest/'verification.json',dict(Verdict='D3D_LIFETIME_ENTRY_VERIFIED',PriorFiles=len(rows),PriorBytes=sum(r['Bytes'] for r in rows),PriorManifestSHA256=s.sha(prior/'evidence-manifest.json'),EntryReviewSHA256=s.sha(dest/'entry-review.json'),PssReportSHA256=s.sha(doc),EtlPathsAndSizesUnchanged=True,EtlFiles=32,PriorEtlInventorySHA256=s.sha(inventory),FullEtlHashPassRepeated=False,NewTracingSession=False,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False))
    print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
