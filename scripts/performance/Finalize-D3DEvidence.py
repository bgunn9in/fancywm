"""Seal native graphics attempts, scoped receipts and repository integrity."""
import argparse,importlib.util,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
PREFIX='FWM-D3D-LIFETIME-20260913-'
def art(name):return s.ART/(PREFIX+name)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id
    roots=sorted(s.ART.glob(PREFIX+'*'));assert {r.name[len(PREFIX):] for r in roots}=={'R0',*[f'T{i}' for i in range(1,10)],*[f'P{i}' for i in range(1,14)],'V0','V1','V2','V3','F1','F2','D1','D2','D3','D4','X1','X2','B1'}
    pins={}
    for name,verdict in [('R0','D3D_LIFETIME_ENTRY_VERIFIED'),('V1','SCOPED_D3D_NATIVE_NOTIFIER_CONTRACTS_PASS'),('V2','SCOPED_HOOKED_D3D9_PRIVATE_OWNERS_PASS'),('V3','SCOPED_HOOKED_D3D9_PRIVATE_OWNERS_PASS'),('B1','D3D_NATIVE_SOURCE_BOUNDARIES_VERIFIED'),('D1','SCOPED_PSS_GRAPH_VA_OBSERVATIONS_VERIFIED'),('D3','SCOPED_PSS_GRAPH_VA_OBSERVATIONS_VERIFIED'),('D4','SCOPED_D3D9_GRAPH_PRIVATE_OWNER_OBSERVATIONS_VERIFIED')]:
        f=art(name)/'verification.json';v=s.read(f);assert v['Verdict']==verdict;pins[str(f.relative_to(s.ART))]=s.sha(f)
        for flag in ['ProductionChanged','PerformanceClaim','StageAccepted','LedgerAppended','NativeHeapLeakFreedomClaim','CompleteResourceDestructionClaim','PhysicalAllocationReleaseClaim']:
            if flag in v:assert not v[flag]
        if 'NegativeControls' in v:assert v['NegativeControls'] and all(n['Rejected'] for n in v['NegativeControls'])
        if 'RawManifestSHA256' in v:
            assert s.sha(art(name)/'raw-manifest.json')==v['RawManifestSHA256']
            base=art('F1') if name=='D1' else art('F2') if name=='D3' else s.ART
            for row in s.read(art(name)/'raw-manifest.json'):assert s.info(base/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    graph=s.read(art('D4')/'verification.json');assert graph['PssProofSHA256']==s.sha(art('D3')/'verification.json') and graph['CalibrationSHA256']==s.sha(art('V3')/'verification.json') and graph['DetailsSHA256']==s.sha(art('D4')/'private-owner-details.json')
    assert len(graph['Configurations'])==2 and len(graph['NegativeControls'])==11
    assert not (art('V0')/'verification.json').exists() and not (art('D2')/'verification.json').exists()
    assert s.read(art('X1')/'command.json')['ExitCode']==1 and s.read(art('X2')/'command.json')['ExitCode']==0
    compilers=0
    for name in [f'T{i}' for i in range(1,10)]:
        root=art(name);receipt=s.read(root/'build-receipt.json');assert receipt['ExitCode']==0 and s.sha(root/'build.log')==receipt['LogSHA256']
        manifest=s.read(root/'provenance.json')
        for row in manifest['Sources']+manifest['Binaries']:assert s.info(root/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        compilers+=sum(line.startswith('cl ') for line in (root/'build.cmd').read_text(encoding='utf-8').splitlines())
    prior=s.ART/'FWM-PERF016-PSS-20260913-R1';entry_prior=s.read(art('R0')/'prior-seal-reverification.json')
    assert s.sha(prior/'verification.json')==entry_prior['VerificationSHA256'] and s.sha(prior/'evidence-manifest.json')==entry_prior['ManifestSHA256'];old=s.read(prior/'evidence-manifest.json')
    for i,row in enumerate(old,1):
        assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        if i%5000==0:print('Prior PSS seal rehashed',i,len(old),flush=True)
    for row in s.read(s.ART/'FWM-HEAP-COHORT-20260913-T2/verification.json')['Sources']:assert s.sha(s.ROOT/'scripts/performance'/row['Name'])==row['SHA256']
    crash=s.ART/'FWM-WORKER-RETENTION-20260912-C3/crash-verification.json';assert s.sha(crash)=='CED4EE9C8975DB7D5075E5EB076353651EDAB66F463707198928D9671AF51FB8'
    command=s.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]);assert command['ExitCode']==0,command;s.write(dest/'snapshot-command.json',command);validation=dest/'validation';validation.mkdir()
    s.write(validation/'invocation.json',dict(Command=[sys.executable,*sys.argv],RecordedUtc=s.datetime.now(s.timezone.utc).isoformat()))
    command=s.run(['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(s.ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(s.ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(s.ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(validation/'checkpoint-review.json')]);s.write(validation/'checkpoint-review-command.json',command);assert command['ExitCode']==0,command
    entry=s.read(art('R0')/'entry-review.json');review=s.read(validation/'checkpoint-review.json');assert entry['Production']==review['Production'] and entry['LedgerIntegrity']==review['LedgerIntegrity']
    s.write(validation/'production-and-ledger.json',dict(Verdict='PASS',ProductionFiles=2155,WinManFiles=72,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,ProductionChanged=False,EntryReviewSHA256=s.sha(art('R0')/'entry-review.json'),FinalReviewSHA256=s.sha(validation/'checkpoint-review.json'),LedgerIntegrity=review['LedgerIntegrity']))
    docs=dict(review['Documents'])
    for name in ['PSS_NATIVE_MEMORY.md','D3D_NATIVE_OWNERS.md']:
        f=s.ROOT/'docs/performance'/name;docs['docs/performance/'+name]=dict(**s.info(f),Lines=len(f.read_bytes().splitlines()))
    assert docs['docs/performance/PSS_NATIVE_MEMORY.md']['SHA256']==s.read(art('R0')/'verification.json')['PssReportSHA256'];s.write(validation/'control-documents.json',docs)
    own=[];order=[]
    for name in [f'P{i}' for i in range(1,14)]:
        root=art(name);created=s.read(root/'created.json');ex=s.read(root/'exited.json');assert ex['Pid']==created['Pid'] and not ex['ForcedCleanup'] and ex['ExitCode']==(2 if name=='P3' else 0)
        own.append(dict(Pid=ex['Pid'],EndedUtc=ex['EndedUtc'],Role='native-control',Evidence=name));order.append(dict(Run=name,StartedUtc=created['StartedUtc'],EndedUtc=ex['EndedUtc']))
        assert created['ToolProvenanceSHA256']==s.sha(Path(created['Tool'])/'provenance.json')
    for name in ['F1','F2']:
        runs=s.read(art(name)/'runs.json');assert len(runs['Builds'])==4 and all(r['ExitCode']==0 for r in runs['Builds'])
        for run in runs['Processes']:
            assert run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run['InputSent'] and not run['DesktopSwitched'];folder=art(name)/'runs'/(run['Configuration']+'-graceful')
            order.append(dict(Run=name+'-'+run['Configuration'],StartedUtc=run['StartedUtc'],EndedUtc=run['EndedUtc']))
            own.append(dict(Pid=run['ProcessId'],EndedUtc=run['EndedUtc'],Role='fullgraph',Evidence=name));own.append(dict(Pid=s.read(folder/'target-exit.json')['Pid'],EndedUtc=run['EndedUtc'],Role='targets',Evidence=name))
            for phase in (folder/'pss').iterdir():own.append(dict(Pid=s.read(phase/'ready.json')['ClonePid'],EndedUtc=run['EndedUtc'],Role='PSS-clone',Evidence=name))
    order.sort(key=lambda r:r['StartedUtc']);assert all(s.datetime.fromisoformat(x['EndedUtc'])<s.datetime.fromisoformat(y['StartedUtc']) for x,y in zip(order,order[1:]));assert len(own)==41
    s.write(validation/'native-process-order.json',dict(Verdict='PASS',SequentialPrimaryProcesses=order,DeclaredTargetsAndClonesOnly=True));s.write(validation/'owned-processes-final.json',dict(Verdict='PASS',Processes=[s.process_state(r) for r in own],ForeignProcessesModified=False))
    paths=s.run(['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths['ExitCode']==0;inventory=s.ART/'FWM-CLR-HEAP-20260913-E2/etl-after.json'
    actual=sorted((str(Path(x)).replace('\\','/').lower(),(s.ROOT/x).stat().st_size) for x in paths['Stdout'].splitlines());expected=sorted((r['Path'].replace('\\','/').lower(),r['Bytes']) for r in s.read(inventory));assert actual==expected and len(actual)==32
    wpr=s.run(['wpr','-status']);sessions=s.run(['logman','query','-ets']);assert wpr['ExitCode']==sessions['ExitCode']==0
    s.write(validation/'tracing-final.json',dict(NewTracingSession=False,NewEtls=0,PriorEtlInventorySHA256=s.sha(inventory),EtlPathsSizesEqual=True,EtlFiles=32,FullEtlHashPassRepeated=False,GlobalWpr=wpr,GlobalWprInactive='WPR is not recording' in wpr['Stdout'],Sessions=sessions,ForeignSessionsModified=False))
    guards=[]
    commands=[('V1',['python','scripts/performance/Verify-D3DNotifiers.py','--id',PREFIX+'V1']),('V3',['python','scripts/performance/Verify-HookedD3D9.py','--id',PREFIX+'V3','--runs',PREFIX+'P12',PREFIX+'P13']),('D3',['python','scripts/performance/Verify-PssGraph.py','--snapshot',str(art('F2')),'--id',PREFIX+'D3']),('D4',['python','scripts/performance/Verify-D3D9Graph.py','--graph',str(art('F2')),'--pss-proof',str(art('D3')/'verification.json'),'--id',PREFIX+'D4']),('B1',['python','scripts/performance/Verify-D3DBoundaries.py','--id',PREFIX+'B1'])]
    for name,cmd in commands:
        f=art(name)/'verification.json';before=s.info(f);r=s.run(cmd);assert r['ExitCode']!=0 and 'receipt exists' in r['Stderr'] and s.info(f)==before;guards.append(dict(Name=name,ReceiptUnchanged=True,SHA256=before['SHA256'],Command=r))
    s.write(validation/'immutable-receipt-controls.json',dict(Verdict='PASS',Controls=guards))
    s.write(validation/'old-receipts.json',dict(PriorFilesRehashed=len(old),PriorBytesRehashed=sum(r['Bytes'] for r in old),PriorVerificationSHA256=s.sha(prior/'verification.json'),PriorManifestSHA256=s.sha(prior/'evidence-manifest.json'),CurrentProductionCrashReceiptSHA256=s.sha(crash),NewTests=0))
    s.write(validation/'new-work.json',dict(NativeControlProcesses=13,SuccessfulControls=12,FailedControls=1,FullGraphProcesses=4,CompleteD3DJournalGraphProcesses=2,OwnTargets=4,PssClones=20,NativeToolBuilds=9,NativeCompilerInvocations=compilers,GraphTargetBuilds=8,NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=0,Receipts=pins))
    entries=[]
    for root in roots+[dest]:
        entries.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file());print('Manifest hashed',root.name,flush=True)
    entries.sort(key=lambda r:r['Path']);assert len(entries)==len({r['Path'] for r in entries});s.write(dest/'evidence-manifest.json',entries)
    s.write(dest/'verification.json',dict(Verdict='VERIFIED_SCOPED_CHECKPOINT',Checkpoint=a.id,RecordedUtc=s.datetime.now(s.timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=s.sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],Receipts=pins,PriorFilesReverified=len(old),PriorBytesReverified=sum(r['Bytes'] for r in old),NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=0,PrivateDataOwnerObservationClaim=True,CompleteD3DResourceCoverage=False,CompleteResourceDestructionClaim=False,NativeHeapLeakFreedomClaim=False,PhysicalAllocationReleaseClaim=False,UnmetCriteria=['Complete D3D/native memory ownership and physical allocation lifetime contract','Atomic native allocator decode, pre-attach history and logical consistency/ownership','Broader USER/GDI and whole-process memory no-growth','Unmodified startup/Explorer membership/wallpaper in appropriate disposable isolated shell','PERF-010/021 attributed GPU execution and hardware presentation identity']))
    for row in s.read(dest/'evidence-manifest.json'):assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    final=dict(Verdict='PASS',Files=len(entries),Bytes=sum(r['Bytes'] for r in entries),VerificationSHA256=s.sha(dest/'verification.json'),ManifestSHA256=s.sha(dest/'evidence-manifest.json'));s.write(validation/'immutable-seal-verification.json',final);print(s.json.dumps(final),flush=True)
if __name__=='__main__':main()
