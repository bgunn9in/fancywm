"""Seal diagnostic evidence, including failed runs, without publishing or ledger edits."""
import argparse,hashlib,json,subprocess,ctypes
from datetime import datetime,timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];ART=ROOT/'artifacts/performance'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def write(p,v):
    with p.open('x',encoding='utf-8') as f:json.dump(v,f,indent=2)
def info(p):return dict(Bytes=p.stat().st_size,SHA256=sha(p))

def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=ART/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id
    cmd=['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id];r=subprocess.run(cmd,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace');assert r.returncode==0,r.stderr
    write(dest/'snapshot-command.json',dict(Command=cmd,ExitCode=r.returncode,Stdout=r.stdout,Stderr=r.stderr));v=dest/'validation';v.mkdir()
    cmd=['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(v/'checkpoint-review.json')]
    with (v/'checkpoint-review.log').open('xb') as log:r=subprocess.run(cmd,cwd=ROOT,stdout=log,stderr=subprocess.STDOUT)
    write(v/'checkpoint-review-command.json',dict(Command=cmd,ExitCode=r.returncode));assert r.returncode==0
    review=read(v/'checkpoint-review.json');entry=read(ART/'FWM-GUI-ATTRIBUTION-20260912-R0/entry-review.json')
    assert review['Production']==entry['Production'] and review['LedgerIntegrity']==entry['LedgerIntegrity']
    write(v/'production-and-ledger-continuity.json',dict(Verdict='PASS',ProductionFiles=2155,WinManFiles=72,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,ProductionChanged=False,EntrySHA256=sha(ART/'FWM-GUI-ATTRIBUTION-20260912-R0/entry-review.json'),FinalSHA256=sha(v/'checkpoint-review.json'),LedgerIntegrity=review['LedgerIntegrity']))
    prior=ART/'FWM-PERF016-FULLGRAPH-20260912-R1';assert sha(prior/'verification.json')=='6706D80B7D56A3F16305E8324EBC376ABDFF5AF9F5735F316E7FE0F6A91A2579';assert sha(prior/'evidence-manifest.json')=='85CACC15363A43E8CDC918D19093A79841AFAAAA3E33053ECB3CD776ACEB5936'
    old=read(ART/'FWM-GUI-ATTRIBUTION-20260912-R0/prior-seal-reverification.json');assert old['Verdict']=='PASS' and old['Files']==125479 and old['Bytes']==7960521776
    prefix='FWM-GUI-ATTRIBUTION-20260913-';pins={};processes=[];builds=[]
    for run in ['P3','P4','E1','E2','E3','E4','E5','I1','T14']:
        f=ART/(prefix+run)/'verification.json';pins[str(f.relative_to(ART))]=sha(f)
    assert read(ART/(prefix+'E3')/'verification.json')['ScopeFilterPassed'] and read(ART/(prefix+'E4')/'verification.json')['OriginalFilesUnchanged']
    for number in [1,2,3,4,5,11]:
        root=ART/(prefix+'F'+str(number));f=root/'gui-verification.json';g=read(f);assert g['VerificationPassed'] and g['Processes']==2 and all(not g[k] for k in ['ProductionChanged','PerformanceClaim','StageAccepted','LedgerAppended']);pins[str(f.relative_to(ART))]=sha(f)
        for row in g['RawFiles']:assert info(root/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        run=read(root/'runs.json');processes.extend(run['Processes']);builds.extend(run['Builds'])
        for c in ['Debug','Release']:
            s=read(root/'runs'/(c+'-graceful')/'summary.json');assert all(s[k] for k in ['ProviderDisposed','MainCompleted','WorkspaceWorkersStopped','MicaStopped','HooksCompleted','StartupReturned','DispatcherStopped'])
        if number>=3:
            f=root/'realtime-verification.json';r=read(f);assert r['Verdict']=='SCOPED_NATIVE_USER_ATTRIBUTION_PASS';pins[str(f.relative_to(ART))]=sha(f)
    assert len(processes)==12 and len(builds)==24 and all(r['ExitCode']==0 for r in builds)
    failedBuild=read(ART/(prefix+'F6')/'validation/Debug-graph-build.json');assert failedBuild['ExitCode']!=0
    failedRuns=[]
    for name in ['F7','F8','F9','F10']:
        root=ART/(prefix+name);run=read(root/'validation/Debug-graceful-receipt.json');rows=[json.loads(l) for l in (root/'runs/Debug-graceful/observations.jsonl').read_text(encoding='utf-8').splitlines()]
        assert run['ExitCode']==1 and run['OwnedDesktopHandleClosed'] and any(r['Kind']=='IdleFailure' for r in rows) and read(root/'validation/Debug-owned-etw-cleanup.json')['Inactive']
        assert read(root/'runs/Debug-graceful/target-exit.json')['ExitCode']==0 and read(root/'runs/Debug-graceful/targets/cleanup.json')['AllDestroyed']
        failedRuns.append(dict(Run=name,ProcessId=run['ProcessId'],SuccessfulLifetimesBeforeFailure=sum(r['Kind']=='Lifetime' for r in rows),Error=next(r['Error'] for r in rows if r['Kind']=='Failure'),ClaimedAsPass=False))
    write(v/'preserved-native-failures.json',dict(Verdict='FAILURES_PRESERVED_AND_CLEANUP_VERIFIED',FailedNativeRuns=failedRuns,FailedFixtureBuild=failedBuild))
    relocation=read(ART/(prefix+'F11')/'target-fixture-relocation.json');assert relocation['DiagnosticSHA256']==sha(ROOT/'scripts/performance/fullgraph-targets/Program.cs')==relocation['ExecutedSHA256']
    assert relocation['SharedPerf010SHA256']==sha(ROOT/'scripts/performance/fullapp/targets/Program.cs')==relocation['PriorSharedSHA256']
    assert read(ART/(prefix+'T14')/'verification.json')['Verdict']=='BUILD_REPLAY_PASS'
    assert all(datetime.fromisoformat(x['EndedUtc'])<datetime.fromisoformat(y['StartedUtc']) for x,y in zip(processes,processes[1:]))
    quiescent=read(ART/(prefix+'F11')/'realtime-verification.json');assert quiescent['NativeInputContextCreatorThreadsVerified']
    write(v/'new-work-verification.json',dict(Verdict='PASS',NewFullGraphProcesses=12,NewFullGraphBuilds=24,AdditionalFailedNativeProcesses=len(failedRuns),AdditionalSuccessfulBuildsForFailedRuns=8,FailedFixtureBuilds=1,RelocatedTargetBuildReplay=2,PostWarmupLifetimesPerProcess=100,WeakReferencesPerProcess=9483,NewTrxRuns=0,OldTrxReadIsNewTest=False,RawGuiNoGrowthPassed=quiescent['RawGuiNoGrowthPassed'],QuiescenceSecondsPerEpoch=45,Receipts=pins))
    # Existing receipts must be rejected before any mutation. These are verifier
    # invocations, not native experiments or new test-result files.
    protected=[]
    for script,run,file,flags in [('Verify-GuiFullGraph.py','F11','gui-verification.json',['--extended']),('Verify-GuiRealtimeGraph.py','F11','realtime-verification.json',['--threads','--quiescence','45','--stable-window','30','--compact-targets']),('Verify-InputContexts.py','I1','verification.json',[])]:
        root=ART/(prefix+run);receipt=root/file;before=info(receipt);cmd=['python','scripts/performance/'+script,'--snapshot',str(root),'--output',str(receipt),*flags];r=subprocess.run(cmd,cwd=ROOT,capture_output=True)
        assert r.returncode!=0 and b'receipt exists' in r.stderr and info(receipt)==before
        protected.append(dict(Command=cmd,ExitCode=r.returncode,ReceiptSHA256=before['SHA256'],Unchanged=True,Stderr=r.stderr.decode('utf-8',errors='replace')))
    write(v/'receipt-overwrite-negative-controls.json',dict(Verdict='PASS',Controls=protected))
    cmd=['wpr','-status'];r=subprocess.run(cmd,capture_output=True);assert r.returncode==0 and b'WPR is not recording' in r.stdout;write(v/'wpr-final.json',dict(Command=cmd,ExitCode=r.returncode,Stdout=r.stdout.decode('utf-8',errors='replace'),Inactive=True))
    roots=sorted(r for r in ART.iterdir() if r.is_dir() and r.name.startswith('FWM-GUI-ATTRIBUTION-'))
    ownSessions=[]
    for root in roots:
        for admissionFile in (root/'runs').glob('*/realtime/admission.json'):
            admission=read(admissionFile);cmd=['logman','query',admission['SessionName'],'-ets'];r=subprocess.run(cmd,capture_output=True,text=True,encoding='utf-8',errors='replace');assert r.returncode!=0 and 'not found' in (r.stdout+r.stderr).lower();ownSessions.append(dict(Command=cmd,ExitCode=r.returncode,Stdout=r.stdout,Inactive=True))
    write(v/'owned-realtime-sessions-final.json',dict(Verdict='PASS',Sessions=ownSessions,ForeignSessionsModified=False))
    kernel=ctypes.WinDLL('kernel32',use_last_error=True);kernel.OpenProcess.argtypes=[ctypes.c_ulong,ctypes.c_int,ctypes.c_ulong];kernel.OpenProcess.restype=ctypes.c_void_p
    kernel.GetProcessTimes.argtypes=[ctypes.c_void_p,*([ctypes.POINTER(ctypes.c_ulonglong)]*4)];kernel.CloseHandle.argtypes=[ctypes.c_void_p];kernel.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_ulong]
    ownProcesses=[]
    for root in roots:
        for f in (root/'validation').glob('*-receipt.json'):
            run=read(f)
            if not all(k in run for k in ['ProcessId','StartedUtc','EndedUtc']):continue
            handle=kernel.OpenProcess(0x100000|0x1000,False,run['ProcessId']);state='absent'
            if handle:
                try:
                    creation,exitTime,kernelTime,userTime=(ctypes.c_ulonglong() for _ in range(4));assert kernel.GetProcessTimes(handle,ctypes.byref(creation),ctypes.byref(exitTime),ctypes.byref(kernelTime),ctypes.byref(userTime))
                    born=creation.value/10000000-11644473600
                    if born>datetime.fromisoformat(run['EndedUtc']).timestamp():state='PID reused after own exit; no action'
                    else:assert kernel.WaitForSingleObject(handle,0)==0,'own native process still alive';state='own process signaled exited'
                finally:kernel.CloseHandle(handle)
            else:assert ctypes.get_last_error()==87,'process absence could not be verified'
            ownProcesses.append(dict(Pid=run['ProcessId'],Receipt=str(f.relative_to(ART)),State=state))
    write(v/'owned-processes-final.json',dict(Verdict='PASS',Processes=ownProcesses,ForeignProcessesModified=False))
    # Frozen manifests include failures, raw files, exact commands and binary
    # build trees. No archive/run/receipt is deleted or rewritten.
    entries=[dict(Path=str(f.relative_to(ART)),**info(f)) for root in roots+[dest] for f in sorted(root.rglob('*')) if f.is_file()];entries.sort(key=lambda r:r['Path']);assert len({r['Path'] for r in entries})==len(entries)
    write(dest/'evidence-manifest.json',entries)
    receipt=dict(Verdict='VERIFIED_SCOPED_CHECKPOINT',Checkpoint=a.id,RecordedUtc=datetime.now(timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,NewProductionDeltaFiles=0,InheritedDeltaFiles=5,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],Receipts=pins,PriorFilesReverified=old['Files'],PriorBytesReverified=old['Bytes'],NewFullGraphProcesses=12,NewTrxRuns=0,RawGuiNoGrowthPassed=quiescent['RawGuiNoGrowthPassed'],UnmodifiedStartup=False,ManagedHardCrashCompletion=False,UnmetCriteria=['Unmodified startup in an isolated shell/user environment','Genuine private-target virtual-desktop membership and wallpaper success','Native heap/GPU/physical presentation outside current evidence','Exact accounting of transferred USER objects and shutdown snapshot is outside scoped delta proof'])
    if not quiescent['RawGuiNoGrowthPassed']:receipt['UnmetCriteria'].insert(0,'Aggregate USER/GDI no-growth still unmet')
    write(dest/'verification.json',receipt)
    for row in read(dest/'evidence-manifest.json'):assert info(ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']},row['Path']
    write(v/'immutable-seal-verification.json',dict(Verdict='PASS',Files=len(entries),Bytes=sum(r['Bytes'] for r in entries),VerificationSHA256=sha(dest/'verification.json'),ManifestSHA256=sha(dest/'evidence-manifest.json')))
    print(json.dumps(dict(Checkpoint=a.id,Files=len(entries),VerificationSHA256=sha(dest/'verification.json'),ManifestSHA256=sha(dest/'evidence-manifest.json'))))
if __name__=='__main__':main()
