"""Verify the immutable predecessor before deriving new native heap lifetimes."""
import argparse,hashlib,json,subprocess
from datetime import datetime,timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];ART=ROOT/'artifacts/performance'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def write(p,d):
    with p.open('x',encoding='utf-8') as f:json.dump(d,f,indent=2)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=ART/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    prior=ART/'FWM-PERF016-HEAP-20260913-R1'
    assert sha(prior/'verification.json')=='1D6E923883AE7FA3ADA2D4E569F6157EDFD008F4A1357659B76E9688C755930C'
    assert sha(prior/'evidence-manifest.json')=='76CF7CC8FEFEF7BCD086E35D0F95D667D1ED3C293364A2948DE5B455C513DB28'
    seal=read(prior/'validation/immutable-seal-verification.json');assert seal['Verdict']=='PASS'
    cmd=['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(dest/'entry-review.json')]
    with (dest/'entry-review.log').open('xb') as f:r=subprocess.run(cmd,cwd=ROOT,stdout=f,stderr=subprocess.STDOUT)
    write(dest/'entry-command.json',dict(Command=cmd,ExitCode=r.returncode));assert r.returncode==0
    old=read(prior/'validation/checkpoint-review.json');new=read(dest/'entry-review.json')
    assert old['Production']==new['Production'] and old['Documents']==new['Documents'] and old['LedgerIntegrity']==new['LedgerIntegrity']
    print('Entry Git/production/documents/ledger byte-exact to heap checkpoint',flush=True)
    entries=read(prior/'evidence-manifest.json')
    for i,row in enumerate(entries):
        f=ART/row['Path'];assert f.stat().st_size==row['Bytes'] and sha(f)==row['SHA256'],row['Path']
        if i and i%5000==0:print('Prior seal rehashed',i,flush=True)
    write(dest/'prior-seal-reverification.json',dict(Verdict='PASS',Checkpoint=prior.name,Files=len(entries),Bytes=sum(r['Bytes'] for r in entries),VerificationSHA256=sha(prior/'verification.json'),ManifestSHA256=sha(prior/'evidence-manifest.json'),RecordedUtc=datetime.now(timezone.utc).isoformat(),NewNativeTests=0))
    print('Prior seal verified',len(entries),flush=True)
if __name__=='__main__':main()
