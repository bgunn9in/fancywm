"""Seal scoped native heap evidence; preserve every failed fixture and receipt."""
import argparse, ctypes, hashlib, json, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];ART=ROOT/'artifacts/performance';PREFIX='FWM-NATIVE-HEAP-20260913-'
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def info(p):return dict(Bytes=p.stat().st_size,SHA256=sha(p))
def write(p,d):
    with p.open('x',encoding='utf-8') as f:json.dump(d,f,indent=2)
def run(args):
    r=subprocess.run(args,cwd=ROOT,capture_output=True)
    return dict(Command=args,ExitCode=r.returncode,ExitHex=f'0x{r.returncode&4294967295:08X}',Stdout=r.stdout.decode('utf-8',errors='replace'),Stderr=r.stderr.decode('utf-8',errors='replace'))
def artifact(suffix):return ART/(PREFIX+suffix)

def process_state(row):
    k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.OpenProcess.argtypes=[ctypes.c_ulong,ctypes.c_int,ctypes.c_ulong];k.OpenProcess.restype=ctypes.c_void_p
    k.GetProcessTimes.argtypes=[ctypes.c_void_p,*([ctypes.POINTER(ctypes.c_ulonglong)]*4)]
    k.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_ulong];k.CloseHandle.argtypes=[ctypes.c_void_p]
    handle=k.OpenProcess(0x100000|0x1000,False,row['Pid']);state='absent'
    if handle:
        try:
            values=[ctypes.c_ulonglong() for _ in range(4)];assert k.GetProcessTimes(handle,*[ctypes.byref(x) for x in values])
            born=values[0].value/10000000-11644473600
            if born>datetime.fromisoformat(row['EndedUtc']).timestamp():state='PID reused after recorded own exit; no action'
            else:assert k.WaitForSingleObject(handle,0)==0,'own native process still alive';state='own process signaled exited'
        finally:k.CloseHandle(handle)
    else:assert ctypes.get_last_error()==87,'process absence unverifiable'
    return dict(**row,State=state)

def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=ART/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id
    roots=sorted(ART.glob(PREFIX+'*'));assert {r.name[len(PREFIX):] for r in roots}=={'R0',*[f'T{i}' for i in range(1,6)],*[f'F{i}' for i in range(1,5)],*[f'E{i}' for i in range(5)]}
    pins={};graph_processes=[];own_processes=[];builds=[];graph_stats=[]
    for suffix,name,expected in [
        ('T1','verification.json','F2931D14B1E4A439DFA74557914399B2C9A5F0519A286E14F8BEC6607CE713C2'),
        ('T2','verification.json','41F9665101074A39CD50D41593DC6912B61F497B6FC4DC6677A54CE61E8B830F'),
        ('F3','verification-v2.json','7A2C2758870DFC859DAE3F8AF126CFBCBD1E41D9A7BE2AA9A3DAD1B88867ED39'),
        ('F4','verification.json','04E8F5298A210D5EB1C05C8A1EC0B8882B77E1BC70EA93302105371BFBEB7385'),
        ('E3','verification.json','8E4E0F517DF4226C46BB3452ED400C2BEDD1B616D013D5D22C7F94BE36AF196F'),
        ('E3','realloc-model-verification.json','20CAA254086859D45A2080E0E5FFDE9DF17634E6FF675353AD77C60CAF2EAB38'),
        ('T5','verification.json','9DB2C1CE120D53B6A19E0CA9C963CE6212DB77C1CEABFFC6BBDAC388FF66AC30'),
        ('F4','trace-verification.json','DECF9134FED51292D6187BE400438C7257EAB2ED803F6C4DF62035007B8B5C20'),
        ('F4','observed-scope-summary.json','08AF03F4C358FE9AC3A6AA8B0DD9134F0CA5028564DFE680D754902760EE449E')]:
        f=artifact(suffix)/name;assert sha(f)==expected,f;pins[str(f.relative_to(ART))]=expected
    trace=read(artifact('F4')/'trace-verification.json')
    assert sum(c['IndependentDecoderVerification']['AllocationRows'] for c in trace['Configurations'])==6790750
    assert sum(c['IndependentDecoderVerification']['FullStackFrames'] for c in trace['Configurations'])==163322214
    assert all(len(c['NegativeControls'])==3 and all(x['Rejected'] for x in c['NegativeControls']) for c in trace['Configurations'])
    assert sha(ROOT/'scripts/performance/heap_trace_model.py')==read(artifact('E3')/'realloc-model-verification.json')['ModelSHA256']
    assert sha(ROOT/'scripts/performance/Verify-HeapGraphTrace.py')==sha(artifact('F4')/'trace-analysis-attempt-3/Verify-HeapGraphTrace.py')
    assert read(artifact('F4')/'trace-analysis-attempt-3/command.json')['ExitCode']==0
    for suffix in ['F1','F3','F4']:
        root=artifact(suffix);runs=read(root/'runs.json');assert not runs['RootProductionChanged'] and not runs['UnmodifiedStartup']
        assert len(runs['Builds'])==4 and all(b['ExitCode']==0 for b in runs['Builds']);builds.extend(runs['Builds'])
        assert [(r['Configuration'],r['Mode']) for r in runs['Processes']]==[('Debug','graceful'),('Release','graceful')]
        for r in runs['Processes']:
            graph_processes.append(r);assert r['ExitCode']==0 and r['OwnedDesktopHandleClosed'] and not r['InputSent'] and not r['DesktopSwitched']
            own_processes.append(dict(Pid=r['ProcessId'],EndedUtc=r['EndedUtc'],Evidence=f'{root.name}/runs.json',Role='fullgraph'))
            folder=root/'runs'/(r['Configuration']+'-graceful');target=read(folder/'target-exit.json');assert target['ExitCode']==0 and read(folder/'targets/cleanup.json')['AllDestroyed']
            own_processes.append(dict(Pid=target['Pid'],EndedUtc=r['EndedUtc'],Evidence=str((folder/'target-exit.json').relative_to(ART)),Role='targets'))
        if suffix!='F1':
            g=read(root/('verification-v2.json' if suffix=='F3' else 'verification.json'))
            assert g['Verdict']=='SCOPED_NATIVE_HEAP_OBSERVATIONS_VERIFIED' and not g['DefaultHeapSnapshotNoGrowth']
            assert len(g['NegativeControls'])==9 and all(c['Rejected'] for c in g['NegativeControls'])
            for row in g['RawFiles']:assert info(root/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']},row['Path']
            for c in g['Configurations']:
                assert c['RetainedWorkloadOwners']==0 and c['WorkloadWeakOwners']==9483
                folder=root/'runs'/(c['Configuration']+'-graceful');summary=read(folder/'summary.json')
                assert all(summary[k] for k in ['ProviderDisposed','MainCompleted','WorkspaceWorkersStopped','MicaStopped','HooksCompleted','StartupReturned','DispatcherStopped'])
                rr=[json.loads(l) for l in (folder/'observations.jsonl').read_text(encoding='utf-8').splitlines()]
                acquires=[r for r in rr if r['Kind']=='GraphWindowAcquired'];closes=[r for r in rr if r['Kind']=='GraphWindowClosed']
                assert len(acquires)==len(closes)
                graph_stats.append(dict(Run=suffix,Configuration=c['Configuration'],GraphGenerations=len(acquires),WorkloadWeakOwners=9483,RetainedWorkloadOwners=0,GuiCriterionPassed=c['GuiCriterionPassed'],DefaultHeapSnapshotNoGrowth=c['DefaultHeapSnapshotNoGrowth']))
    assert len(graph_processes)==6 and all(datetime.fromisoformat(x['EndedUtc'])<datetime.fromisoformat(y['StartedUtc']) for x,y in zip(graph_processes,graph_processes[1:]))
    assert read(artifact('F2')/'validation/Debug-graph-build.json')['ExitCode']!=0
    assert not (artifact('F1')/'verification.json').exists()
    for suffix in ['T1','T2','E2','E3']:
        f=artifact(suffix)/('control-process.json' if suffix.startswith('T') else 'admission-result.json');r=read(f);assert r['ExitCode']==0
        own_processes.append(dict(Pid=r['Pid'],EndedUtc=r['EndedUtc'],Evidence=str(f.relative_to(ART)),Role='native-control'))
    recovery=artifact('E1')/'recovery.json';r=read(recovery);assert r['Exited']
    own_processes.append(dict(Pid=r['Pid'],EndedUtc=datetime.fromtimestamp(recovery.stat().st_mtime,timezone.utc).isoformat(),Evidence=str(recovery.relative_to(ART)),Role='failed-admission-control'))
    assert len(own_processes)==17
    old=read(artifact('R0')/'prior-seal-reverification.json');assert old['Files']==77230 and old['Bytes']==4710391610
    for file,key in [('verification.json','VerificationSHA256'),('evidence-manifest.json','ManifestSHA256')]:assert sha(ART/'FWM-PERF016-GUI-20260913-R1'/file)==old[key]
    etl=read(artifact('E4')/'verification.json');assert etl['OriginalFilesUnchanged'] and etl['PriorFiles']==22 and len(etl['NewOwnedEtls'])==4
    inventory=read(artifact('E4')/'etl-after.json');assert sha(artifact('E4')/'etl-after.json')==etl['AfterInventorySHA256']
    # Verify inventory membership/size again without silently treating this as
    # another full old-ETL hash pass. E4 contains that completed full hash pass.
    paths=run(['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths['ExitCode']==0
    actual=sorted((Path(x).as_posix().lower(),(ROOT/x).stat().st_size) for x in paths['Stdout'].splitlines())
    expected=sorted((Path(x['Path']).as_posix().lower(),x['Bytes']) for x in inventory)
    assert actual==expected and len(actual)==26
    for row in etl['NewOwnedEtls']:assert info(ROOT/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    pins[str((artifact('E4')/'verification.json').relative_to(ART))]=sha(artifact('E4')/'verification.json')
    # All prerequisites are checked before creating the final snapshot.
    cmd=['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id];r=run(cmd);assert r['ExitCode']==0,r
    write(dest/'snapshot-command.json',r);v=dest/'validation';v.mkdir()
    write(v/'finalizer-invocation.json',dict(Command=[sys.executable,*sys.argv],RecordedUtc=datetime.now(timezone.utc).isoformat()))
    cmd=['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(v/'checkpoint-review.json')]
    r=run(cmd);write(v/'checkpoint-review-command.json',r);assert r['ExitCode']==0,r
    final=read(v/'checkpoint-review.json');entry=read(artifact('R0')/'entry-review.json');assert final['Production']==entry['Production'] and final['LedgerIntegrity']==entry['LedgerIntegrity']
    write(v/'production-and-ledger-continuity.json',dict(Verdict='PASS',ProductionFiles=2155,WinManFiles=72,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,ProductionChanged=False,EntrySHA256=sha(artifact('R0')/'entry-review.json'),FinalSHA256=sha(v/'checkpoint-review.json'),LedgerIntegrity=final['LedgerIntegrity']))
    protected=[]
    for script,suffix,file in [('Verify-HeapGraphTrace.py','F4','trace-verification.json'),('Summarize-NativeHeapEvidence.py','F4','observed-scope-summary.json')]:
        root=artifact(suffix);receipt=root/file;before=info(receipt);r=run(['python','scripts/performance/'+script,'--snapshot',str(root),'--output',str(receipt)])
        assert r['ExitCode']!=0 and 'receipt exists' in r['Stderr'] and info(receipt)==before
        protected.append(dict(**r,ReceiptSHA256=before['SHA256'],Unchanged=True))
    write(v/'receipt-overwrite-negative-controls.json',dict(Verdict='PASS',Controls=protected,EarlierControls=str((artifact('R0')/'receipt-overwrite-controls.json').relative_to(ART))))
    sessions=[read(artifact(s)/'admission-result.json')['Session'] for s in ['E2','E3']]+[read(recovery)['Session']]
    sessions.extend(read(artifact('F4')/'validation'/f'{c}-heap-cleanup.json')['Session'] for c in ['Debug','Release'])
    results=[]
    for session in sessions:
        r=run(['logman','query',session,'-ets']);assert r['ExitHex']=='0x80300002',r;results.append(dict(**r,Inactive=True))
    write(v/'owned-heap-sessions-final.json',dict(Verdict='PASS',Sessions=results,ForeignSessionsModified=False))
    wpr=[]
    for cmd in [['wpr','-status'],['wpr','-status','-instancename','FWM_OWNED_HEAP_CPU_FWM-NATIVE-HEAP-20260913-E1']]:
        r=run(cmd);assert r['ExitCode']==0 and 'WPR is not recording' in r['Stdout'],r;wpr.append(dict(**r,Inactive=True))
    write(v/'wpr-final.json',dict(Verdict='PASS',Queries=wpr))
    write(v/'owned-processes-final.json',dict(Verdict='PASS',Processes=[process_state(r) for r in own_processes],ForeignProcessesModified=False))
    write(v/'new-work-verification.json',dict(Verdict='PASS',NewGraphProcesses=6,StrictlyVerifiedGraphProcesses=4,RejectedGraphVerificationProcesses=2,GraphTargetBuilds=12,FailedFixtureBuilds=1,NativeControlProcesses=5,ControlCompletedProcesses=4,NewTrxRuns=0,NewCrashRuns=0,NewDpiRuns=0,OfflineReadersAreNativeRuns=False,Configurations=graph_stats,Receipts=pins))
    write(v/'etl-continuity.json',dict(Verdict='PASS',E4FullHashVerificationSHA256=sha(artifact('E4')/'verification.json'),InventoryPathsAndSizesStillEqual=True,Files=26,NewEtlsRehashed=4,OldFullHashPassRepeated=False))
    print('Prerequisites, source snapshot, final Git/production/ledger and own cleanup verified',flush=True)
    entries=[]
    for root in roots+[dest]:
        for f in sorted(root.rglob('*')):
            if f.is_file():entries.append(dict(Path=str(f.relative_to(ART)),**info(f)))
        print('Manifest hashed',root.name,flush=True)
    entries.sort(key=lambda r:r['Path']);assert len(entries)==len({r['Path'] for r in entries})
    write(dest/'evidence-manifest.json',entries)
    receipt=dict(Verdict='VERIFIED_SCOPED_CHECKPOINT',Checkpoint=a.id,RecordedUtc=datetime.now(timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,NewProductionDeltaFiles=0,InheritedDeltaFiles=5,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,
        NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],Receipts=pins,PriorFilesReverified=old['Files'],PriorBytesReverified=old['Bytes'],NewGraphProcesses=6,StrictlyVerifiedGraphProcesses=4,NewTrxRuns=0,NativeHeapLeakFreedomClaim=False,UnmodifiedStartup=False,ManagedHardCrashCompletion=False,
        UnmetCriteria=['Unmodified startup and genuine Explorer membership/wallpaper success require an appropriate isolated shell/user environment','Broader GUI no-growth under changing native display remains unproved; new false gates preserved','Complete native allocation/owner lifetime beyond post-attach default-heap witnesses is unproved; heap no-growth false','PERF-010/021 attributed GPU execution and physical presentation contracts remain missing'])
    write(dest/'verification.json',receipt)
    for i,row in enumerate(read(dest/'evidence-manifest.json')):
        assert info(ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']},row['Path']
        if i and i%5000==0:print('Independent seal rehash',i,flush=True)
    write(v/'immutable-seal-verification.json',dict(Verdict='PASS',Files=len(entries),Bytes=receipt['NewEvidenceBytes'],VerificationSHA256=sha(dest/'verification.json'),ManifestSHA256=sha(dest/'evidence-manifest.json')))
    print(json.dumps(dict(Checkpoint=a.id,Files=len(entries),Bytes=receipt['NewEvidenceBytes'],VerificationSHA256=sha(dest/'verification.json'),ManifestSHA256=sha(dest/'evidence-manifest.json'))),flush=True)
if __name__=='__main__':main()
