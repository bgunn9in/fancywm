"""Freeze and independently hash the complete continuation, including failures."""
import argparse, hashlib, json, subprocess, zipfile
from datetime import datetime, timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]; ART=ROOT/'artifacts/performance'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def write(p,v):
    with p.open('x',encoding='utf-8') as f:json.dump(v,f,indent=2)
def info(p):return dict(Bytes=p.stat().st_size,SHA256=sha(p))
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--retention',type=Path);a=p.parse_args();dest=ART/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id
    cmd=['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]
    result=subprocess.run(cmd,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace');assert result.returncode==0,result.stderr
    write(dest/'snapshot-command.json',dict(Command=cmd,ExitCode=0,Stdout=result.stdout,Stderr=result.stderr));validation=dest/'validation';validation.mkdir()
    cmd=['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(validation/'checkpoint-review.json')]
    with (validation/'checkpoint-review.log').open('xb') as f:result=subprocess.run(cmd,cwd=ROOT,stdout=f,stderr=subprocess.STDOUT)
    write(validation/'checkpoint-review-command.json',dict(Command=cmd,ExitCode=result.returncode));assert result.returncode==0
    prior=ART/'FWM-PERF016-NATIVE-20260912-R1';assert sha(prior/'verification.json')=='67B0422944865A6CDB1D42D0B7F4FF5E178EDBDA302A281531FE485CA8338E8D'
    assert sha(prior/'evidence-manifest.json')=='817CA9C94226ED80930F2ACD560DF595C7A208DF146286B267E7A2F3C9B7DF98'
    old=read(prior/'evidence-manifest.json')
    for r in old:assert info(ART/r['Path'])=={k:r[k] for k in ['Bytes','SHA256']},r['Path']
    write(validation/'prior-evidence-reverification.json',dict(Verdict='PASS',Files=len(old),Bytes=sum(r['Bytes'] for r in old),PriorVerification=info(prior/'verification.json'),PriorManifest=info(prior/'evidence-manifest.json'),NewTest=False))
    pinned={
        'FWM-FULLGRAPH-MICA-20260912-C1/verification.json':'9B7684DB4F01311A040EF1047BED741BBC2C7DA6A408A609FB2FE77DFEFD9F08',
        'FWM-FULLGRAPH-RETENTION-20260912-R7/verification.json':'E9F32209B40AF234DD9AE04386033A6EBB664AF5D9B77CCFBB78E07219952BBC',
        'FWM-WORKER-RETENTION-20260912-C2/verification.json':'4AA10633826EE18E41F82B9BF0339A9599C3576F858CD6787D0079F5270CD6A8',
        'FWM-WORKER-RETENTION-20260912-C3/crash-verification.json':'CED4EE9C8975DB7D5075E5EB076353651EDAB66F463707198928D9671AF51FB8'}
    for rel,h in pinned.items():assert sha(ART/rel)==h and read(ART/rel)['Verdict']=='PASS'
    diagnostic=ART/'FWM-WORKER-RETENTION-20260912-C5/resource-observations-verification.json'
    assert sha(diagnostic)=='7AFBA4976B67435CA04349FDADDA364CACD504C868AE3043CDBF927B2E670F36'
    observed=read(diagnostic);assert observed['Verdict']=='CRITERION_UNMET' and observed['VerificationPassed'] and not observed['CriterionPassed']
    pinned[str(diagnostic.relative_to(ART))]=sha(diagnostic)
    delivery=ART/'FWM-FULLGRAPH-DELIVERY-20260912-C2/validation/delivery-verification.json';d=read(delivery);assert d['Verdict']=='PASS'
    assert [(t['Configuration'],t['PassedLeaves']) for t in d['Tests']]==[('Debug',2069),('Debug',160),('Debug',31),('Release',2125),('Release',160),('Release',31)]
    pinned[str(delivery.relative_to(ART))]=sha(delivery)
    if a.retention:assert read(a.retention)['Verdict']=='PASS';pinned[str(a.retention.resolve().relative_to(ART))]=sha(a.retention)
    # Verify that both archived packages contain the actual current dependency
    # binary as well as the main binary already checked by the delivery gate.
    packages=[]
    for package in d['Packages']:
        archive=delivery.parents[1]/package['Path'];assert sha(archive)==package['SHA256']
        with zipfile.ZipFile(archive) as z:
            entries=[n for n in z.namelist() if n.endswith('/WinMan.Windows.dll')];assert len(entries)==1
            embedded=z.read(entries[0]);h=hashlib.sha256(embedded).hexdigest().upper()
        matches=[str(f.relative_to(ROOT)) for base in [ROOT/'FancyWM.GUI/bin',ROOT/'FancyWM/bin',ROOT/'winman-windows/src/WinMan.Windows/bin'] for f in base.rglob('WinMan.Windows.dll') if package['Configuration'] in f.parts and sha(f)==h]
        assert matches,'packaged dependency has no matching current build output'
        packages.append(dict(Configuration=package['Configuration'],PackageSHA256=sha(archive),Entry=entries[0],EmbeddedSHA256=h,MatchingBuiltFiles=sorted(matches),Installed=False,Launched=False))
    write(validation/'package-dependency-verification.json',dict(Verdict='PASS',Packages=packages))
    patch=ROOT/'patches/winman-windows/PERF-001-recent-timer.patch';oldpatch=(ART/'FWM-WORKER-RETENTION-20260912-B1/source/patches/winman-windows/PERF-001-recent-timer.patch').read_bytes();newpatch=patch.read_bytes();assert newpatch.startswith(oldpatch)
    write(validation/'dependency-patch-integrity.json',dict(PrefixBytes=len(oldpatch),PrefixSHA256=hashlib.sha256(oldpatch).hexdigest().upper(),SuffixBytes=len(newpatch)-len(oldpatch),SuffixSHA256=hashlib.sha256(newpatch[len(oldpatch):]).hexdigest().upper(),CurrentSHA256=sha(patch),Files=len(read(ROOT/'patches/winman-windows/manifest.json')['files'])))
    roots=sorted(p for p in ART.iterdir() if p.is_dir() and p.name.startswith(('FWM-FULLGRAPH-','FWM-WORKER-RETENTION-')))
    entries=[dict(Path=str(f.relative_to(ART)),**info(f)) for r in roots+[dest] for f in sorted(r.rglob('*')) if f.is_file()]
    entries.sort(key=lambda r:r['Path']);assert len({r['Path'] for r in entries})==len(entries)
    write(dest/'evidence-manifest.json',entries)
    receipt=dict(Verdict='PASS',Checkpoint=a.id,RecordedUtc=datetime.now(timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=True,ProductionDeltaFiles=5,NewProductionDeltaFiles=3,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,
        NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],PriorFilesReverified=len(old),Receipts=pinned,ControlledTilingGuiRetention=bool(a.retention),UnmodifiedStartup=False,ManagedHardCrashCompletion=False,
        UnmetCriteria=['Tiling GDI no-growth/allocation-free attribution','Unmodified startup in an isolated shell/user environment','Genuine private-target virtual-desktop membership and wallpaper success','Native heap/GPU/physical presentation outside current evidence'])
    write(dest/'verification.json',receipt)
    # A second independent pass verifies the immutable file inventory.
    for r in read(dest/'evidence-manifest.json'):assert info(ART/r['Path'])=={k:r[k] for k in ['Bytes','SHA256']},r['Path']
    write(validation/'immutable-seal-verification.json',dict(Verdict='PASS',Files=len(entries),Bytes=sum(r['Bytes'] for r in entries),VerificationSHA256=sha(dest/'verification.json'),ManifestSHA256=sha(dest/'evidence-manifest.json')))
    print(json.dumps(dict(Checkpoint=a.id,Files=len(entries),VerificationSHA256=sha(dest/'verification.json'),ManifestSHA256=sha(dest/'evidence-manifest.json'))))
if __name__=='__main__':main()
