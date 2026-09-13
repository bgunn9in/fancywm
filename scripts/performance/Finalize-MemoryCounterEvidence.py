"""Immutable checkpoint for native GPU/process memory counters and graph lifetimes."""
import argparse,importlib.util,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
PREFIX='FWM-DXGI-MEMORY-20260913-'
def art(name):return s.ART/(PREFIX+name)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id
    roots=sorted(s.ART.glob(PREFIX+'*'));assert {r.name[len(PREFIX):] for r in roots}=={'R0','T1','T2','T3',*[f'P{i}' for i in range(1,7)],'V1','V2','F1','F2','F3',*[f'D{i}' for i in range(5)],'W1'}
    verdicts={'R0':'DXGI_MEMORY_ENTRY_VERIFIED','V1':'SCOPED_NATIVE_DXGI_MEMORY_SOURCE_PASS','V2':'SCOPED_NATIVE_PROCESS_COMMIT_SOURCE_PASS','D0':'DXGI_GRAPH_FIXTURE_CHECKPOINT_GAP_REPRODUCED','D2':'SCOPED_GRAPH_DXGI_USAGE_OBSERVATIONS_VERIFIED','D3':'SCOPED_GRAPH_DXGI_USAGE_OBSERVATIONS_VERIFIED','D4':'SCOPED_GRAPH_PRIVATE_COMMIT_OBSERVATIONS_VERIFIED','W1':'VERIFIED_REPORT_AND_LIVE_DOCUMENT_UPDATE'}
    pins={}
    for name,verdict in verdicts.items():
        f=art(name)/'verification.json';v=s.read(f);assert v['Verdict']==verdict;pins[str(f.relative_to(s.ART))]=s.sha(f)
        for flag in ['ProductionChanged','PerformanceClaim','StageAccepted','LedgerAppended','PhysicalAllocationIdentityClaim','ImmediateReleaseAccountingClaim','CompleteGraphicsMemoryClaim','NativeHeapLeakFreedomClaim']:
            if flag in v:assert not v[flag]
        if 'NegativeControls' in v:assert v['NegativeControls'] and all(r['Rejected'] for r in v['NegativeControls'])
        if 'RawManifestSHA256' in v:
            raw=art(name)/'raw-manifest.json';assert s.sha(raw)==v['RawManifestSHA256'];base=art('F2') if name=='D2' else art('F3') if name in ['D3','D4'] else s.ART
            for row in s.read(raw):assert s.info(base/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    assert s.read(art('D4')/'verification.json')['GraphProofSHA256']==s.sha(art('D3')/'verification.json')
    assert not (art('D1')/'verification.json').exists() and s.read(art('D0')/'strict-verifier-command.json')['ExitCode']!=0
    report=s.read(art('W1')/'verification.json');assert s.info(s.ROOT/report['Report']['Path'])=={k:report['Report'][k] for k in ['Bytes','SHA256']}
    for row in report['Changes']:
        assert row['PriorBytesPreserved'] and s.info(s.ROOT/row['Path'])==dict(Bytes=row['AfterBytes'],SHA256=row['AfterSHA256'])
        assert s.sha(art('W1')/'before'/row['Path'])==row['BeforeSHA256'] and s.sha(art('W1')/'after'/row['Path'])==row['AfterSHA256']
    compilers=0
    for name in ['T1','T2','T3']:
        root=art(name);build=s.read(root/'build-receipt.json');assert build['ExitCode']==0 and build['LogSHA256']==s.sha(root/'build.log')
        for row in s.read(root/'provenance.json')['Sources']+s.read(root/'provenance.json')['Binaries']:assert s.info(root/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        compilers+=sum(line.startswith('cl ') for line in (root/'build.cmd').read_text().splitlines())
    assert compilers==12
    first=art('T1')/'source';second=art('T2')/'source';assert [f.name for f in sorted(first.iterdir()) if f.is_file() and s.sha(f)!=s.sha(second/f.name)]==['Control.cpp']
    oldroot=s.ART/'FWM-PERF016-D3DMAP-20260913-R1';entryprior=s.read(art('R0')/'prior-seal-reverification.json')
    assert s.sha(oldroot/'verification.json')==entryprior['VerificationSHA256']=='D166CD81EDFCA746F6FA135F6D6BBB2AFE0A5DF3C5CD1957B1F9FE40E2AAA482'
    assert s.sha(oldroot/'evidence-manifest.json')==entryprior['ManifestSHA256']=='3EB188AD0D7A5D77CF7A85D51B058C475609CF594813C8E3A4BA1DDE8C52DE12'
    old=s.read(oldroot/'evidence-manifest.json')
    for i,row in enumerate(old,1):
        assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        if i%5000==0:print('Prior seal rehashed',i,len(old),flush=True)
    for row in s.read(s.ART/'FWM-HEAP-COHORT-20260913-T2/verification.json')['Sources']:assert s.sha(s.ROOT/'scripts/performance'/row['Name'])==row['SHA256']
    crash=s.ART/'FWM-WORKER-RETENTION-20260912-C3/crash-verification.json';assert s.sha(crash)=='CED4EE9C8975DB7D5075E5EB076353651EDAB66F463707198928D9671AF51FB8'
    cmd=s.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]);assert cmd['ExitCode']==0,cmd;s.write(dest/'snapshot-command.json',cmd);validation=dest/'validation';validation.mkdir()
    s.write(validation/'invocation.json',dict(Command=[sys.executable,*sys.argv],RecordedUtc=s.datetime.now(s.timezone.utc).isoformat()))
    cmd=s.run(['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(s.ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(s.ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(s.ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(validation/'checkpoint-review.json')]);s.write(validation/'checkpoint-review-command.json',cmd);assert cmd['ExitCode']==0,cmd
    entry=s.read(art('R0')/'entry-review.json');review=s.read(validation/'checkpoint-review.json');assert entry['Production']==review['Production'] and entry['LedgerIntegrity']==review['LedgerIntegrity']
    s.write(validation/'production-and-ledger.json',dict(Verdict='PASS',ProductionFiles=2155,WinManFiles=72,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,ProductionChanged=False,EntryReviewSHA256=s.sha(art('R0')/'entry-review.json'),FinalReviewSHA256=s.sha(validation/'checkpoint-review.json'),LedgerIntegrity=review['LedgerIntegrity']))
    docs=dict(review['Documents']);priorDocs=s.read(oldroot/'validation/control-documents.json')
    for name in ['D3D_NATIVE_OWNERS.md','PSS_NATIVE_MEMORY.md','D3D_MAPPED_PSS.md','DXGI_PROCESS_MEMORY.md']:
        rel='docs/performance/'+name;f=s.ROOT/rel;docs[rel]=dict(**s.info(f),Lines=len(f.read_bytes().splitlines()))
        if rel in priorDocs:assert docs[rel]==priorDocs[rel]
    s.write(validation/'control-documents.json',docs)
    own=[];order=[]
    for i in range(1,7):
        root=art(f'P{i}');created=s.read(root/'created.json');ex=s.read(root/'exited.json');assert created['Pid']==ex['Pid'] and ex['ExitCode']==0 and not ex['ForcedCleanup']
        own.append(dict(Pid=ex['Pid'],EndedUtc=ex['EndedUtc'],Role='native-control',Evidence=root.name));order.append(dict(Run=root.name,StartedUtc=created['StartedUtc'],EndedUtc=ex['EndedUtc']))
    graphBuilds=0
    for name in ['F1','F2','F3']:
        root=art(name);runs=s.read(root/'runs.json');assert len(runs['Builds'])==4 and all(r['ExitCode']==0 for r in runs['Builds']);graphBuilds+=4
        for run in runs['Processes']:
            assert run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run['InputSent'] and not run['DesktopSwitched'];folder=root/'runs'/(run['Configuration']+'-graceful')
            own.append(dict(Pid=run['ProcessId'],EndedUtc=run['EndedUtc'],Role='fullgraph',Evidence=name));own.append(dict(Pid=s.read(folder/'target-exit.json')['Pid'],EndedUtc=run['EndedUtc'],Role='targets',Evidence=name));order.append(dict(Run=name+'-'+run['Configuration'],StartedUtc=run['StartedUtc'],EndedUtc=run['EndedUtc']))
    order.sort(key=lambda r:r['StartedUtc']);assert all(s.datetime.fromisoformat(x['EndedUtc'])<s.datetime.fromisoformat(y['StartedUtc']) for x,y in zip(order,order[1:])) and len(own)==18 and graphBuilds==12
    s.write(validation/'native-process-order.json',dict(Verdict='PASS',SequentialPrimaryProcesses=order,DeclaredTargetsOnly=True));s.write(validation/'owned-processes-final.json',dict(Verdict='PASS',Processes=[s.process_state(r) for r in own],ForeignProcessesModified=False))
    rel='scripts/performance/fullgraph-lifetime/Program.GraphWindows.cs';before=(art('F1')/'source'/rel).read_bytes();after=(art('F2')/'source'/rel).read_bytes()
    oldguard=b'if (NativeHeapSnapshot == null && PssBeginCall == null) return;';newguard=b'if (NativeHeapSnapshot == null && PssBeginCall == null && DxgiSampleCall == null) return;';assert before.count(oldguard)==1 and before.replace(oldguard,newguard)==after
    s.write(validation/'fixture-correction.json',dict(Verdict='PASS',Path=rel,BeforeSHA256=s.sha(art('F1')/'source'/rel),AfterSHA256=s.sha(art('F2')/'source'/rel),Delta='Enable Application.Windows checkpoints for the DXGI-only fixture',FailedRunsPreserved=True,ProductionChanged=False))
    paths=s.run(['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths['ExitCode']==0;inventory=s.ART/'FWM-CLR-HEAP-20260913-E2/etl-after.json';actual=sorted((str(Path(x)).replace('\\','/').lower(),(s.ROOT/x).stat().st_size) for x in paths['Stdout'].splitlines());expected=sorted((r['Path'].replace('\\','/').lower(),r['Bytes']) for r in s.read(inventory));assert actual==expected and len(actual)==32
    wpr=s.run(['wpr','-status']);sessions=s.run(['logman','query','-ets']);assert wpr['ExitCode']==sessions['ExitCode']==0 and 'WPR is not recording' in wpr['Stdout']
    s.write(validation/'tracing-final.json',dict(NewTracingSession=False,NewEtls=0,PriorEtlInventorySHA256=s.sha(inventory),EtlPathsSizesEqual=True,EtlFiles=32,FullEtlHashPassRepeated=False,GlobalWpr=wpr,GlobalWprInactive=True,Sessions=sessions,ForeignSessionsModified=False))
    guards=[]
    for name,cmd in [('V1',['python','scripts/performance/Verify-DxgiMemoryControl.py','--id',PREFIX+'V1']),('V2',['python','scripts/performance/Verify-ProcessMemoryControl.py','--id',PREFIX+'V2']),('D3',['python','scripts/performance/Verify-DxgiMemoryGraph.py','--graph',str(art('F3')),'--id',PREFIX+'D3']),('D4',['python','scripts/performance/Verify-ProcessMemoryGraph.py','--graph',str(art('F3')),'--graph-proof',str(art('D3')/'verification.json'),'--id',PREFIX+'D4'])]:
        f=art(name)/'verification.json';before=s.info(f);r=s.run(cmd);assert r['ExitCode']!=0 and 'receipt exists' in r['Stderr'] and s.info(f)==before;guards.append(dict(Name=name,ReceiptUnchanged=True,SHA256=before['SHA256'],Command=r))
    s.write(validation/'immutable-receipt-controls.json',dict(Verdict='PASS',Controls=guards));s.write(validation/'old-receipts.json',dict(PriorFilesRehashed=len(old),PriorBytesRehashed=sum(r['Bytes'] for r in old),PriorVerificationSHA256=s.sha(oldroot/'verification.json'),PriorManifestSHA256=s.sha(oldroot/'evidence-manifest.json'),CurrentProductionCrashReceiptSHA256=s.sha(crash),NewTests=0))
    s.write(validation/'new-work.json',dict(NativeControlProcesses=6,FullGraphProcesses=6,OwnTargets=6,OwnProcessIdentities=len(own),NativeToolBuilds=3,NativeCompilerInvocations=compilers,GraphTargetBuilds=graphBuilds,GraphProcessesWithMissingWindowCheckpoints=2,NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewPssClones=0,NewEtls=0,Receipts=pins))
    entries=[]
    for root in roots+[dest]:
        entries.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file());print('Manifest hashed',root.name,flush=True)
    entries.sort(key=lambda r:r['Path']);assert len(entries)==len({r['Path'] for r in entries});s.write(dest/'evidence-manifest.json',entries)
    s.write(dest/'verification.json',dict(Verdict='VERIFIED_SCOPED_CHECKPOINT',Checkpoint=a.id,RecordedUtc=s.datetime.now(s.timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=s.sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],Receipts=pins,PriorFilesReverified=len(old),PriorBytesReverified=sum(r['Bytes'] for r in old),PrivateCommitAndDxgiUsageObserved=True,AtomicCpuGpuTotal=False,PhysicalAllocationIdentityClaim=False,CompleteGraphicsMemoryClaim=False,NativeHeapLeakFreedomClaim=False,NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=0,UnmetCriteria=['Broader process private commit and USER/GDI no-growth','Complete native/graphics allocation identities and logical owners','Atomic allocator decoder and pre-attach history','Unmodified isolated shell startup/membership/wallpaper','PERF-010/021 attributed GPU execution and hardware presentation identity']))
    for row in s.read(dest/'evidence-manifest.json'):assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    final=dict(Verdict='PASS',Files=len(entries),Bytes=sum(r['Bytes'] for r in entries),VerificationSHA256=s.sha(dest/'verification.json'),ManifestSHA256=s.sha(dest/'evidence-manifest.json'));s.write(validation/'immutable-seal-verification.json',final);print(s.json.dumps(final),flush=True)
if __name__=='__main__':main()
