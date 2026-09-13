"""Entry audit for native graphics method/mapping coverage, without tracing."""
import argparse,importlib.util,shutil
from pathlib import Path
spec=importlib.util.spec_from_file_location('p',Path(__file__).with_name('Prepare-ClrHeapSource.py'));p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p);s=p.s
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--id',required=True);a=parser.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    prior=s.ART/'FWM-PERF016-D3DMAP-20260913-R1';assert s.sha(prior/'verification.json')=='D166CD81EDFCA746F6FA135F6D6BBB2AFE0A5DF3C5CD1957B1F9FE40E2AAA482';assert s.sha(prior/'evidence-manifest.json')=='3EB188AD0D7A5D77CF7A85D51B058C475609CF594813C8E3A4BA1DDE8C52DE12';rows=s.read(prior/'evidence-manifest.json')
    for i,row in enumerate(rows,1):
        assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        if i%5000==0:print('Prior D3D seal rehashed',i,len(rows),flush=True)
    s.write(dest/'prior-seal-reverification.json',dict(Verdict='PASS',Files=len(rows),Bytes=sum(r['Bytes'] for r in rows),VerificationSHA256=s.sha(prior/'verification.json'),ManifestSHA256=s.sha(prior/'evidence-manifest.json'),NewNativeTests=0))
    cmd=['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(s.ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(s.ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(s.ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(dest/'entry-review.json')];assert p.capture(dest,'entry-command',cmd).returncode==0
    entry=s.read(dest/'entry-review.json');old=s.read(prior/'validation/checkpoint-review.json');assert all(entry[k]==old[k] for k in ['Production','Documents','LedgerIntegrity'])
    for name in ['D3D_NATIVE_OWNERS.md','PSS_NATIVE_MEMORY.md','D3D_MAPPED_PSS.md']:
        rel='docs/performance/'+name;assert s.info(s.ROOT/rel)=={k:s.read(prior/'validation/control-documents.json')[rel][k] for k in ['Bytes','SHA256']}
    for name,cmd in [('identity',['whoami','/all']),('wpr',['wpr','-status']),('sessions',['logman','query','-ets'])]:assert p.capture(dest,name,cmd).returncode==0
    paths=p.capture(dest,'etl-paths',['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths.returncode==0
    inventory=s.ART/'FWM-CLR-HEAP-20260913-E2/etl-after.json';actual=sorted((r.replace('\\','/').lower(),(s.ROOT/r).stat().st_size) for r in paths.stdout.decode('utf-8').splitlines());expected=sorted((r['Path'].replace('\\','/').lower(),r['Bytes']) for r in s.read(inventory));assert actual==expected and len(actual)==32
    s.write(dest/'verification.json',dict(Verdict='DXGI_MEMORY_ENTRY_VERIFIED',PriorFiles=len(rows),PriorBytes=sum(r['Bytes'] for r in rows),PriorManifestSHA256=s.sha(prior/'evidence-manifest.json'),EntryReviewSHA256=s.sha(dest/'entry-review.json'),EtlPathsAndSizesUnchanged=True,EtlFiles=32,FullEtlHashPassRepeated=False,NewTracingSession=False,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
