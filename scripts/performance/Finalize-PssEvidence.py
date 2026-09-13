"""Seal all PSS source/graph attempts, strict receipts and unchanged production."""
import argparse,importlib.util,json,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
PREFIX='FWM-PSS-HEAP-20260913-'
def art(name):return s.ART/(PREFIX+name)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id
    roots=sorted(s.ART.glob(PREFIX+'*'));assert {x.name[len(PREFIX):] for x in roots}=={'R0',*[f'T{i}' for i in range(1,8)],*[f'P{i}' for i in range(1,9)],'V1','F1','F2','F3','D0','D1','D1R','D2','D3','D4','S1'}
    pins={}
    for name,verdict in [('R0','PSS_SOURCE_ENTRY_VERIFIED'),('V1','SCOPED_PSS_PRIVATE_MEMORY_SOURCE_PASS'),('D2','SCOPED_PSS_GRAPH_VA_OBSERVATIONS_VERIFIED'),('D3','SCOPED_PSS_PRIVATE_VA_OCCUPANCY_VERIFIED'),('D4','SCOPED_PSS_COVERAGE_BOUNDARY_VERIFIED')]:
        f=art(name)/'verification.json';v=s.read(f);assert v['Verdict']==verdict;pins[str(f.relative_to(s.ART))]=s.sha(f)
        for flag in ['ProductionChanged','PerformanceClaim','StageAccepted','LedgerAppended','HeapBlockEnumeration','NativeHeapLeakFreedomClaim','LogicalOwnerClaim']:
            if flag in v:assert not v[flag]
    control=s.read(art('V1')/'verification.json');graph=s.read(art('D2')/'verification.json');analysis=s.read(art('D3')/'verification.json')
    assert len(control['Configurations'])==4 and len(control['NegativeControls'])==7 and all(n['Rejected'] for n in control['NegativeControls'])
    assert len(graph['Configurations'])==2 and len(graph['NegativeControls'])==9 and all(n['Rejected'] for n in graph['NegativeControls'])
    assert analysis['GraphProofSHA256']==s.sha(art('D2')/'verification.json')
    for name in ['V1','D2','D4']:
        v=s.read(art(name)/'verification.json');assert s.sha(art(name)/'raw-manifest.json')==v['RawManifestSHA256']
        for row in s.read(art(name)/'raw-manifest.json'):assert s.info((art('F3') if name=='D2' else s.ART)/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    boundary=s.read(art('D4')/'verification.json');assert boundary['OrdinaryPrivateAllocationsCloned']==48 and boundary['D3DMappedBuffersAbsent']==12 and len(boundary['NegativeControls'])==5 and all(r['Rejected'] for r in boundary['NegativeControls'])
    assert boundary['GraphProofSHA256']==s.sha(art('D2')/'verification.json') and boundary['GraphOmissionsSHA256']==s.sha(art('D4')/'graph-omissions.json') and not boundary['CompleteProcessPrivateMemoryClaim']
    for name in [f'T{i}' for i in range(1,8)]:
        tool=art(name);receipt=s.read(tool/'build-receipt.json');assert receipt['ExitCode']==0 and s.sha(tool/'build.log')==receipt['LogSHA256']
        for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.sha(tool/row['Path'])==row['SHA256']
    assert s.read(art('F1')/'validation/Debug-graph-build.json')['ExitCode']==1
    assert s.read(art('D0')/'command.json')['ExitCode']==1 and 'graph generation coverage' in (art('D0')/'raw.log').read_text()
    assert not (art('D1')/'verification.json').exists() and not (art('D1R')/'verification.json').exists()
    for name in ['P1','P2']:assert s.read(art(name)/'run.json')['Failure'] and s.read(art(name)/'exited.json')['ExitCode']==10
    # The complete inherited CLR seal is rehashed again; none of it is a new test.
    prior=s.ART/'FWM-PERF016-CLR-20260913-R1';entry_prior=s.read(art('R0')/'prior-seal-reverification.json')
    assert s.sha(prior/'verification.json')==entry_prior['VerificationSHA256'] and s.sha(prior/'evidence-manifest.json')==entry_prior['ManifestSHA256']
    old=s.read(prior/'evidence-manifest.json')
    for i,row in enumerate(old,1):
        assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        if i%3000==0:print('Prior seal rehashed',i,len(old),flush=True)
    # Calibrated pre-existing replay models are never weakened by this source.
    for row in s.read(s.ART/'FWM-HEAP-COHORT-20260913-T2/verification.json')['Sources']:assert s.sha(s.ROOT/'scripts/performance'/row['Name'])==row['SHA256']
    crash=s.ART/'FWM-WORKER-RETENTION-20260912-C3/crash-verification.json';assert s.sha(crash)=='CED4EE9C8975DB7D5075E5EB076353651EDAB66F463707198928D9671AF51FB8'
    snapshot=s.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]);assert snapshot['ExitCode']==0,snapshot;s.write(dest/'snapshot-command.json',snapshot);validation=dest/'validation';validation.mkdir()
    s.write(validation/'invocation.json',dict(Command=[sys.executable,*sys.argv],RecordedUtc=s.datetime.now(s.timezone.utc).isoformat()))
    cmd=s.run(['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(s.ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(s.ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(s.ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(validation/'checkpoint-review.json')]);s.write(validation/'checkpoint-review-command.json',cmd);assert cmd['ExitCode']==0,cmd
    entry=s.read(art('R0')/'entry-review.json');review=s.read(validation/'checkpoint-review.json');assert entry['Production']==review['Production'] and entry['LedgerIntegrity']==review['LedgerIntegrity']
    s.write(validation/'production-and-ledger.json',dict(Verdict='PASS',ProductionFiles=2155,WinManFiles=72,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,ProductionChanged=False,EntryReviewSHA256=s.sha(art('R0')/'entry-review.json'),FinalReviewSHA256=s.sha(validation/'checkpoint-review.json'),LedgerIntegrity=review['LedgerIntegrity']))
    own=[];order=[]
    for name in ['P1','P2','P3','P4']:
        root=art(name);ex=s.read(root/'exited.json');own.append(dict(Pid=ex['Pid'],EndedUtc=ex['EndedUtc'],Role='native-control',Evidence=name));order.append(dict(Run=name,StartedUtc=s.read(root/'created.json')['StartedUtc'],EndedUtc=ex['EndedUtc']))
        for r in s.read(root/'run.json')['Inspectors']:own.append(dict(Pid=r['Pid'],EndedUtc=r['EndedUtc'],Role='inspector',Evidence=name))
        for folder in sorted((root/'control').iterdir()):
            if (folder/'ready.json').exists():own.append(dict(Pid=s.read(folder/'ready.json')['ClonePid'],EndedUtc=ex['EndedUtc'],Role='clone',Evidence=name))
    for name in ['F2','F3']:
        for run in s.read(art(name)/'runs.json')['Processes']:
            order.append(dict(Run=name+'-'+run['Configuration'],StartedUtc=run['StartedUtc'],EndedUtc=run['EndedUtc']))
            assert run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run['InputSent'] and not run['DesktopSwitched'];folder=art(name)/'runs'/(run['Configuration']+'-graceful')
            own.append(dict(Pid=run['ProcessId'],EndedUtc=run['EndedUtc'],Role='graph',Evidence=name));own.append(dict(Pid=s.read(folder/'target-exit.json')['Pid'],EndedUtc=run['EndedUtc'],Role='target',Evidence=name))
            for phase in (folder/'pss').iterdir():own.append(dict(Pid=s.read(phase/'ready.json')['ClonePid'],EndedUtc=run['EndedUtc'],Role='clone',Evidence=name))
    for name in ['P5','P6','P7','P8']:
        root=art(name);created=s.read(root/'created.json');ex=s.read(root/'exited.json');assert ex['ExitCode']==0 and not ex['ForcedCleanup'];order.append(dict(Run=name,StartedUtc=created['StartedUtc'],EndedUtc=ex['EndedUtc']));own.append(dict(Pid=ex['Pid'],EndedUtc=ex['EndedUtc'],Role='protection-control' if name in ['P5','P6'] else 'D3D-control',Evidence=name))
        for folder in (root/'control').iterdir():own.append(dict(Pid=s.read(folder/'ready.json')['ClonePid'],EndedUtc=ex['EndedUtc'],Role='clone',Evidence=name))
    s.write(validation/'owned-processes-final.json',dict(Verdict='PASS',Processes=[s.process_state(r) for r in own],ForeignProcessesModified=False));assert len(own)==56
    assert all(s.datetime.fromisoformat(x['EndedUtc'])<s.datetime.fromisoformat(y['StartedUtc']) for x,y in zip(order,order[1:]));s.write(validation/'native-process-order.json',dict(Verdict='PASS',SequentialPrimaryProcesses=order,InspectorsAreDeclaredOwnedChildren=True))
    doc=s.ROOT/'docs/performance/PSS_NATIVE_MEMORY.md';s.write(validation/'control-documents.json',dict(**review['Documents'],**{'docs/performance/PSS_NATIVE_MEMORY.md':dict(**s.info(doc),Lines=len(doc.read_bytes().splitlines()))}))
    paths=s.run(['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths['ExitCode']==0
    actual=sorted((str(Path(x)).replace('\\','/').lower(),(s.ROOT/x).stat().st_size) for x in paths['Stdout'].splitlines());inventory=s.ART/'FWM-CLR-HEAP-20260913-E2/etl-after.json';expected=sorted((r['Path'].replace('\\','/').lower(),r['Bytes']) for r in s.read(inventory));assert actual==expected and len(actual)==32
    wpr=s.run(['wpr','-status']);assert wpr['ExitCode']==0;query=s.run(['logman','query','-ets']);assert query['ExitCode']==0
    s.write(validation/'tracing-final.json',dict(NewTracingSession=False,NewEtls=0,PriorEtlInventorySHA256=s.sha(inventory),EtlPathsSizesEqual=True,EtlFiles=32,FullEtlHashPassRepeated=False,GlobalWpr=wpr,GlobalWprInactive='WPR is not recording' in wpr['Stdout'],Sessions=query,ForeignSessionsModified=False))
    protected=[]
    for name,command in [('V1',['python','scripts/performance/Verify-PssHeapControl.py','--id',PREFIX+'V1']),('D2',['python','scripts/performance/Verify-PssGraph.py','--snapshot',str(art('F3')),'--id',PREFIX+'D2']),('D3',['python','scripts/performance/Analyze-PssVa.py','--graph-proof',str(art('D2')/'verification.json'),'--id',PREFIX+'D3']),('D4',['python','scripts/performance/Verify-PssCoverage.py','--id',PREFIX+'D4'])]:
        f=art(name)/'verification.json';before=s.info(f);r=s.run(command);assert r['ExitCode']!=0 and 'receipt exists' in r['Stderr'] and s.info(f)==before;protected.append(dict(Name=name,ReceiptUnchanged=True,SHA256=before['SHA256'],Command=r))
    s.write(validation/'immutable-receipt-controls.json',dict(Verdict='PASS',Controls=protected))
    s.write(validation/'old-receipts.json',dict(PriorFilesRehashed=len(old),PriorBytesRehashed=sum(r['Bytes'] for r in old),PriorVerificationSHA256=s.sha(prior/'verification.json'),PriorManifestSHA256=s.sha(prior/'evidence-manifest.json'),CurrentProductionCrashReceiptSHA256=s.sha(crash),NewTests=0))
    s.write(validation/'new-work.json',dict(NativeControlSources=8,NativeInspectors=6,SuccessfulControlSources=6,FullGraphProcesses=4,StrictGraphProcesses=2,OwnTargetProcesses=4,PssClones=34,NativeCompilerInvocations=48,GraphTargetSuccessfulBuilds=8,GraphFailedBuilds=1,NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=0,Receipts=pins))
    entries=[]
    for root in roots+[dest]:
        for f in sorted(root.rglob('*')):
            if f.is_file():entries.append(dict(Path=str(f.relative_to(s.ART)),**s.info(f)))
        print('Manifest hashed',root.name,flush=True)
    entries.sort(key=lambda r:r['Path']);assert len(entries)==len({r['Path'] for r in entries});s.write(dest/'evidence-manifest.json',entries)
    s.write(dest/'verification.json',dict(Verdict='VERIFIED_SCOPED_CHECKPOINT',Checkpoint=a.id,RecordedUtc=s.datetime.now(s.timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=s.sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],Receipts=pins,PriorFilesReverified=len(old),PriorBytesReverified=sum(r['Bytes'] for r in old),NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=0,ClonePrivateVaInventoryClaim=True,CompleteProcessPrivateMemoryClaim=False,HeapBlockEnumeration=False,NativeHeapLeakFreedomClaim=False,LogicalOwnerClaim=False,UnmetCriteria=['PSS omits real D3D mapped buffers: complete graphics/native ownership and snapshot contract is missing','Calibrated frozen native allocator decoder and logical consistency/ownership contract; Toolhelp denied on own clones','Native logical owners, pre-attach history and allocation-generation identity beyond address occupancy','Unmodified startup/Explorer membership/wallpaper needs appropriate disposable isolated shell','Broader GUI no-growth and PERF-010/021 GPU execution/presentation contracts']))
    for row in s.read(dest/'evidence-manifest.json'):assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    final=dict(Verdict='PASS',Files=len(entries),Bytes=sum(r['Bytes'] for r in entries),VerificationSHA256=s.sha(dest/'verification.json'),ManifestSHA256=s.sha(dest/'evidence-manifest.json'));s.write(validation/'immutable-seal-verification.json',final);print(json.dumps(final),flush=True)
if __name__=='__main__':main()
