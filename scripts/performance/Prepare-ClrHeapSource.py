"""Fresh current identity/token/inventory and isolated CPU admission for CLR work."""
import argparse,importlib.util,json,os,subprocess,sys
from datetime import datetime,timezone
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
ROOT=s.ROOT;ART=s.ART
def capture(dest,name,cmd,timeout=60):
    started=datetime.now(timezone.utc).isoformat();r=subprocess.run(cmd,cwd=ROOT,capture_output=True,timeout=timeout,creationflags=0x08000000)
    for suffix,data in [('stdout',r.stdout),('stderr',r.stderr)]:
        with (dest/(name+'.'+suffix)).open('xb') as f:f.write(data)
    s.write(dest/(name+'.json'),dict(Command=list(map(str,cmd)),ExitCode=r.returncode,ExitHex=f'0x{r.returncode&4294967295:08X}',StartedUtc=started,EndedUtc=datetime.now(timezone.utc).isoformat()))
    return r
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=ART/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    prior=ART/'FWM-PERF016-COHORT-20260913-R1'
    assert s.sha(prior/'verification.json')=='2201A71A5EACD8337FBD27C20B77ADB0BA91A84FE9F04D09B13BF13A054534AE'
    assert s.sha(prior/'evidence-manifest.json')=='CF83AA2D45DFE1357BD0BE28BB40E624101CAF7C4A5D32D59EF9C66F1E1D0E8C'
    rows=s.read(prior/'evidence-manifest.json')
    for row in rows:assert s.info(ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']},row['Path']
    s.write(dest/'prior-seal-reverification.json',dict(Verdict='PASS',Checkpoint=prior.name,Files=len(rows),Bytes=sum(r['Bytes'] for r in rows),VerificationSHA256=s.sha(prior/'verification.json'),ManifestSHA256=s.sha(prior/'evidence-manifest.json'),NewNativeTests=0))
    cmd=['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(dest/'entry-review.json')]
    r=capture(dest,'entry-command',cmd);assert r.returncode==0
    old=s.read(prior/'validation/checkpoint-review.json');now=s.read(dest/'entry-review.json');assert all(old[k]==now[k] for k in ['Production','Documents','LedgerIntegrity'])
    script="[pscustomobject]@{Time=[DateTime]::UtcNow.ToString('o'); Identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name; Elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); OS=[Environment]::OSVersion.VersionString; ProcessId=$PID} | ConvertTo-Json"
    r=capture(dest,'identity',['pwsh','-NoProfile','-Command',script]);assert r.returncode==0 and json.loads(r.stdout.decode('utf-8-sig'))['Elevated']
    for name,cmd in [('token',['whoami','/all']),('wpr-before',['wpr','-status']),('sessions-before',['logman','query','-ets'])]:assert capture(dest,name,cmd).returncode==0
    r=capture(dest,'etl-paths',['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert r.returncode==0
    rows=[];paths=sorted(set(r.stdout.decode('utf-8').splitlines()))
    for i,rel in enumerate(paths):
        f=(ROOT/rel).resolve();assert f.is_relative_to(ROOT);before=f.stat();digest=s.sha(f);after=f.stat();assert (before.st_size,before.st_mtime_ns)==(after.st_size,after.st_mtime_ns)
        rows.append(dict(Path=rel,Bytes=before.st_size,SHA256=digest));print('Complete ETL inventory',i+1,len(paths),flush=True)
    inventory=ART/'FWM-NATIVE-HEAP-20260913-E4/etl-after.json';oldrows=s.read(inventory)
    assert rows==oldrows and len(rows)==26
    s.write(dest/'etl-before.json',rows);s.write(dest/'etl-verification.json',dict(Verdict='PASS',Files=len(rows),Bytes=sum(r['Bytes'] for r in rows),IncludesIgnored=True,CaseInsensitive=True,OriginalFilesUnchanged=True,PriorInventorySHA256=s.sha(inventory),InventorySHA256=s.sha(dest/'etl-before.json')))
    # CPU admission is separate, not a dependency or performance measurement
    # of the upcoming CLR/native control. Never stop a foreign recording.
    before=capture(dest,'wpr-before-cpu',['wpr','-status']);instance='FWM_OWNED_CLR_CPU_'+a.id;active=False;code=None;failure=None
    if b'WPR is not recording' in before.stdout:
        try:
            cpu=capture(dest,'cpu-start',['wpr','-start','CPU','-filemode','-instancename',instance]);code=cpu.returncode;active=code==0
            if active:
                etl=dest/'cpu-admission.etl';assert not etl.exists();stop=capture(dest,'cpu-stop',['wpr','-stop',str(etl),'-instancename',instance]);assert stop.returncode==0;active=False
        except Exception as e:failure=repr(e)
        finally:
            if active:
                etl=dest/'cpu-recovery.etl';assert not etl.exists();stop=capture(dest,'cpu-stop-recovery',['wpr','-stop',str(etl),'-instancename',instance]);assert stop.returncode==0
        own=capture(dest,'wpr-own-after',['wpr','-status','-instancename',instance]);assert own.returncode==0 and b'WPR is not recording' in own.stdout
    s.write(dest/'cpu-admission.json',dict(ExitCode=code,ExitHex=f'0x{code&4294967295:08X}' if code is not None else None,KnownFailureRepeated=code is not None and (code&4294967295)==0xC5585011,Failure=failure,OwnInstanceInactive=True,ForeignRecordingSkipped=code is None,PerformanceMeasurement=False,PerformanceReject=False,NewPreflightVerdict=False))
    after=capture(dest,'wpr-after',['wpr','-status']);assert after.returncode==0
    s.write(dest/'verification.json',dict(Verdict='CURRENT_CLR_SOURCE_PREPARATION_VERIFIED',OwnPid=os.getpid(),RecordedUtc=datetime.now(timezone.utc).isoformat(),IdentitySHA256=s.sha(dest/'identity.stdout'),EntryReviewSHA256=s.sha(dest/'entry-review.json'),InventorySHA256=s.sha(dest/'etl-before.json'),CpuAdmissionSHA256=s.sha(dest/'cpu-admission.json'),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS'))
    print('CLR source preparation verified',s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
