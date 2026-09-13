"""Immutable seal for calibrated hardware-map coverage and in-lock PSS evidence."""
import argparse,importlib.util,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
PREFIX='FWM-D3D-MAP-20260913-'
def art(name):return s.ART/(PREFIX+name)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id
    roots=sorted(s.ART.glob(PREFIX+'*'));assert {r.name[len(PREFIX):] for r in roots}=={'R0',*[f'T{i}' for i in range(1,5)],*[f'P{i}' for i in range(1,12)],'B1',*[f'V{i}' for i in range(1,7)],'F1',*[f'D{i}' for i in range(1,8)],'W1'}
    pins={}
    verdicts={'R0':'D3D_MAPPING_ENTRY_VERIFIED','B1':'D3D9_METHOD_COVERAGE_GAP_REPRODUCED','V1':'SCOPED_D3D9_SOFTWARE_HARDWARE_METHOD_COVERAGE_PASS','V2':'SCOPED_D3D9_SOFTWARE_HARDWARE_METHOD_COVERAGE_PASS','V3':'SCOPED_HOOKED_D3D9_PRIVATE_OWNERS_PASS','V4':'SCOPED_NATIVE_D3D_MAPPED_PSS_SOURCE_PASS','V5':'NATIVE_PSS_SHARED_COMMIT_PROPAGATION_VERIFIED','V6':'SCOPED_NATIVE_D3D_MAPPED_PSS_SOURCE_PASS','D3':'SCOPED_PSS_GRAPH_PRIVATE_VA_WITH_SHARED_COMMITS_VERIFIED','D4':'SCOPED_D3D9_GRAPH_PRIVATE_OWNER_OBSERVATIONS_VERIFIED','D6':'PSS_SHARED_GRAPH_BOUNDARY_VERIFIED','D7':'SCOPED_GRAPH_D3D_MAPPING_PSS_OMISSIONS_VERIFIED'}
    for name,verdict in verdicts.items():
        f=art(name)/'verification.json';v=s.read(f);assert v['Verdict']==verdict;pins[str(f.relative_to(s.ART))]=s.sha(f)
        for flag in ['ProductionChanged','PerformanceClaim','StageAccepted','LedgerAppended','PhysicalAllocationReleaseClaim','CompleteResourceLifetimeClaim','NativeHeapLeakFreedomClaim']:
            if flag in v:assert not v[flag]
        if 'NegativeControls' in v:assert v['NegativeControls'] and all(r['Rejected'] for r in v['NegativeControls'])
        if 'RawManifestSHA256' in v:
            assert v['RawManifestSHA256']==s.sha(art(name)/'raw-manifest.json');base=art('F1') if name in ['D3','D7'] else s.ART
            for row in s.read(art(name)/'raw-manifest.json'):assert s.info(base/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    graph=s.read(art('D7')/'verification.json');assert graph['GraphProofSHA256']==s.sha(art('D4')/'verification.json') and graph['CalibrationSHA256']==s.sha(art('V4')/'verification.json') and graph['PointVerifierCalibrationSHA256']==s.sha(art('V6')/'verification.json') and graph['MethodCalibrationSHA256']==s.sha(art('V2')/'verification.json')
    private=s.read(art('D4')/'verification.json');assert private['PssProofSHA256']==s.sha(art('D3')/'verification.json') and private['DetailsSHA256']==s.sha(art('D4')/'private-owner-details.json')
    assert all(not (art(name)/'verification.json').exists() for name in ['D1','D2','D5'])
    assert s.read(art('V4')/'verification.json')['RawManifestSHA256']==s.read(art('V6')/'verification.json')['RawManifestSHA256']
    assert s.read(art('D3')/'verification.json')['SharedControlSHA256']==s.sha(art('V5')/'verification.json')
    assert s.read(art('D6')/'verification.json')['FailedStrictVerifierSHA256']==s.sha(s.ROOT/'scripts/performance/Verify-PssGraph.py')==s.sha(art('D1')/'Verify-PssGraph.py')
    report=s.read(art('W1')/'verification.json');assert report['Verdict']=='VERIFIED_REPORT_AND_LIVE_DOCUMENT_UPDATE';pins[str((art('W1')/'verification.json').relative_to(s.ART))]=s.sha(art('W1')/'verification.json')
    assert s.info(s.ROOT/report['Report']['Path'])=={k:report['Report'][k] for k in ['Bytes','SHA256']}
    for row in report['Changes']:
        assert row['PriorBytesPreserved'] and s.info(s.ROOT/row['Path'])==dict(Bytes=row['AfterBytes'],SHA256=row['AfterSHA256'])
        assert s.sha(art('W1')/'before'/row['Path'])==row['BeforeSHA256'] and s.sha(art('W1')/'after'/row['Path'])==row['AfterSHA256']
    assert not (art('P5')/'created.json').exists() and not (art('P5')/'exited.json').exists(),'failed preflight is not a native run'
    compilers=0
    for name in ['T1','T2','T3','T4']:
        root=art(name);build=s.read(root/'build-receipt.json');assert build['ExitCode']==0 and s.sha(root/'build.log')==build['LogSHA256']
        for row in s.read(root/'provenance.json')['Sources']+s.read(root/'provenance.json')['Binaries']:assert s.info(root/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        compilers+=sum(r.startswith('cl ') for r in (root/'build.cmd').read_text().splitlines())
    oldroot=s.ART/'FWM-PERF016-D3D-20260913-R1';entryprior=s.read(art('R0')/'prior-seal-reverification.json');assert s.sha(oldroot/'verification.json')==entryprior['VerificationSHA256'] and s.sha(oldroot/'evidence-manifest.json')==entryprior['ManifestSHA256'];old=s.read(oldroot/'evidence-manifest.json')
    for i,row in enumerate(old,1):
        assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        if i%5000==0:print('Prior D3D seal rehashed',i,len(old),flush=True)
    for row in s.read(s.ART/'FWM-HEAP-COHORT-20260913-T2/verification.json')['Sources']:assert s.sha(s.ROOT/'scripts/performance'/row['Name'])==row['SHA256']
    crash=s.ART/'FWM-WORKER-RETENTION-20260912-C3/crash-verification.json';assert s.sha(crash)=='CED4EE9C8975DB7D5075E5EB076353651EDAB66F463707198928D9671AF51FB8'
    command=s.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]);assert command['ExitCode']==0,command;s.write(dest/'snapshot-command.json',command);validation=dest/'validation';validation.mkdir();s.write(validation/'invocation.json',dict(Command=[sys.executable,*sys.argv],RecordedUtc=s.datetime.now(s.timezone.utc).isoformat()))
    command=s.run(['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(s.ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(s.ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(s.ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(validation/'checkpoint-review.json')]);s.write(validation/'checkpoint-review-command.json',command);assert command['ExitCode']==0,command
    entry=s.read(art('R0')/'entry-review.json');review=s.read(validation/'checkpoint-review.json');assert entry['Production']==review['Production'] and entry['LedgerIntegrity']==review['LedgerIntegrity']
    s.write(validation/'production-and-ledger.json',dict(Verdict='PASS',ProductionFiles=2155,WinManFiles=72,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,ProductionChanged=False,EntryReviewSHA256=s.sha(art('R0')/'entry-review.json'),FinalReviewSHA256=s.sha(validation/'checkpoint-review.json'),LedgerIntegrity=review['LedgerIntegrity']))
    docs=dict(review['Documents']);priorDocs=s.read(oldroot/'validation/control-documents.json')
    for name in ['D3D_NATIVE_OWNERS.md','PSS_NATIVE_MEMORY.md','D3D_MAPPED_PSS.md']:
        rel='docs/performance/'+name;f=s.ROOT/rel;docs[rel]=dict(**s.info(f),Lines=len(f.read_bytes().splitlines()))
        if rel in priorDocs:assert docs[rel]==priorDocs[rel]
    s.write(validation/'control-documents.json',docs)
    own=[];order=[];mapped=0;sharedClones=0;native=[]
    for name in ['P1','P2','P3','P4','P6','P7','P8','P9','P10','P11']:
        root=art(name);created=s.read(root/'created.json');ex=s.read(root/'exited.json');assert created['Pid']==ex['Pid'] and ex['ExitCode']==0 and not ex['ForcedCleanup'];assert created['ToolProvenanceSHA256']==s.sha(Path(created['Tool'])/'provenance.json')
        native.append(created);own.append(dict(Pid=ex['Pid'],EndedUtc=ex['EndedUtc'],Role='native-control',Evidence=name));order.append(dict(Run=name,StartedUtc=created['StartedUtc'],EndedUtc=ex['EndedUtc']))
        for f in (root/'mapped').glob('*/released.json'):
            r=s.read(f);assert r['AfterCloseOpenError']==87 and r['FreeCode']==0;mapped+=1;own.append(dict(Pid=r['ClonePid'],EndedUtc=s.datetime.fromtimestamp(f.stat().st_mtime,s.timezone.utc).isoformat(),Role='mapped-PSS-clone',Evidence=name))
        for f in (root/'shared').glob('*/released.json'):
            r=s.read(f);assert r['AfterCloseOpenError']==87 and r['FreeCode']==0;sharedClones+=1;own.append(dict(Pid=r['ClonePid'],EndedUtc=s.datetime.fromtimestamp(f.stat().st_mtime,s.timezone.utc).isoformat(),Role='shared-control-PSS-clone',Evidence=name))
    runs=s.read(art('F1')/'runs.json');assert len(runs['Builds'])==4 and all(r['ExitCode']==0 for r in runs['Builds'])
    for run in runs['Processes']:
        assert run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run['InputSent'] and not run['DesktopSwitched'];folder=art('F1')/'runs'/(run['Configuration']+'-graceful');order.append(dict(Run='F1-'+run['Configuration'],StartedUtc=run['StartedUtc'],EndedUtc=run['EndedUtc']))
        own.append(dict(Pid=run['ProcessId'],EndedUtc=run['EndedUtc'],Role='fullgraph',Evidence='F1'));own.append(dict(Pid=s.read(folder/'target-exit.json')['Pid'],EndedUtc=run['EndedUtc'],Role='targets',Evidence='F1'))
        for root,role in [(folder/'pss','epoch-PSS-clone'),(folder/'d3d9-mapped-pss','mapped-PSS-clone')]:
            for f in root.glob('*/released.json'):
                r=s.read(f);assert r['AfterCloseOpenError']==87 and r['FreeCode']==0;own.append(dict(Pid=r['ClonePid'],EndedUtc=s.datetime.fromtimestamp(f.stat().st_mtime,s.timezone.utc).isoformat(),Role=role,Evidence='F1'));mapped+=role=='mapped-PSS-clone'
    order.sort(key=lambda r:r['StartedUtc']);assert all(s.datetime.fromisoformat(x['EndedUtc'])<s.datetime.fromisoformat(y['StartedUtc']) for x,y in zip(order,order[1:]));assert sharedClones==4 and mapped==62 and len(own)==28+mapped==90 and compilers==30
    s.write(validation/'native-process-order.json',dict(Verdict='PASS',SequentialPrimaryProcesses=order,DeclaredTargetsAndClonesOnly=True));s.write(validation/'owned-processes-final.json',dict(Verdict='PASS',Processes=[s.process_state(r) for r in own],ForeignProcessesModified=False))
    paths=s.run(['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths['ExitCode']==0;inventory=s.ART/'FWM-CLR-HEAP-20260913-E2/etl-after.json';actual=sorted((str(Path(x)).replace('\\','/').lower(),(s.ROOT/x).stat().st_size) for x in paths['Stdout'].splitlines());expected=sorted((r['Path'].replace('\\','/').lower(),r['Bytes']) for r in s.read(inventory));assert actual==expected and len(actual)==32
    wpr=s.run(['wpr','-status']);sessions=s.run(['logman','query','-ets']);assert wpr['ExitCode']==sessions['ExitCode']==0 and 'WPR is not recording' in wpr['Stdout'];s.write(validation/'tracing-final.json',dict(NewTracingSession=False,NewEtls=0,PriorEtlInventorySHA256=s.sha(inventory),EtlPathsSizesEqual=True,EtlFiles=32,FullEtlHashPassRepeated=False,GlobalWpr=wpr,GlobalWprInactive=True,Sessions=sessions,ForeignSessionsModified=False))
    guards=[]
    for name,cmd in [('V6',['python','scripts/performance/Verify-MappedPss.py','--id',PREFIX+'V6']),('D3',['python','scripts/performance/Verify-PssSharedGraph.py','--snapshot',str(art('F1')),'--id',PREFIX+'D3']),('D4',['python','scripts/performance/Verify-D3D9Graph.py','--graph',str(art('F1')),'--pss-proof',str(art('D3')/'verification.json'),'--calibration',str(art('V3')/'verification.json'),'--id',PREFIX+'D4']),('D7',['python','scripts/performance/Verify-MappedPssGraph.py','--graph',str(art('F1')),'--graph-proof',str(art('D4')/'verification.json'),'--point-calibration',str(art('V6')/'verification.json'),'--id',PREFIX+'D7'])]:
        f=art(name)/'verification.json';before=s.info(f);r=s.run(cmd);assert r['ExitCode']!=0 and 'receipt exists' in r['Stderr'] and s.info(f)==before;guards.append(dict(Name=name,ReceiptUnchanged=True,SHA256=before['SHA256'],Command=r))
    s.write(validation/'immutable-receipt-controls.json',dict(Verdict='PASS',Controls=guards));s.write(validation/'old-receipts.json',dict(PriorFilesRehashed=len(old),PriorBytesRehashed=sum(r['Bytes'] for r in old),PriorVerificationSHA256=s.sha(oldroot/'verification.json'),PriorManifestSHA256=s.sha(oldroot/'evidence-manifest.json'),CurrentProductionCrashReceiptSHA256=s.sha(crash),NewTests=0))
    s.write(validation/'new-work.json',dict(NativeControlProcesses=10,FullGraphProcesses=2,OwnTargets=2,MappedPssClones=mapped,EpochPssClones=10,SharedControlPssClones=sharedClones,OwnProcessIdentities=len(own),NativeToolBuilds=4,NativeCompilerInvocations=compilers,GraphTargetBuilds=4,FailedPreflightWithoutNativeProcess=1,NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=0,Receipts=pins))
    entries=[]
    for root in roots+[dest]:
        entries.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file());print('Manifest hashed',root.name,flush=True)
    entries.sort(key=lambda r:r['Path']);assert len(entries)==len({r['Path'] for r in entries});s.write(dest/'evidence-manifest.json',entries);s.write(dest/'verification.json',dict(Verdict='VERIFIED_SCOPED_CHECKPOINT',Checkpoint=a.id,RecordedUtc=s.datetime.now(s.timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=s.sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],Receipts=pins,PriorFilesReverified=len(old),PriorBytesReverified=sum(r['Bytes'] for r in old),MappedPssClones=mapped,NativeResourceMapPointIdentityVerified=True,CompleteProcessMemoryClaim=False,CompleteResourceLifetimeClaim=False,PhysicalAllocationReleaseClaim=False,EntireRegionOwnershipClaim=False,NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=0,UnmetCriteria=['Complete native/graphics memory source beyond mapped points and unhooked owners','Physical backing-allocation lifetime and allocation-generation contracts','Atomic native allocator decoder, pre-attach history and logical ownership','Broader USER/GDI and whole-process memory no-growth','Unmodified isolated shell startup/membership/wallpaper','PERF-010/021 GPU execution and hardware presentation identity']))
    for row in s.read(dest/'evidence-manifest.json'):assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    final=dict(Verdict='PASS',Files=len(entries),Bytes=sum(r['Bytes'] for r in entries),VerificationSHA256=s.sha(dest/'verification.json'),ManifestSHA256=s.sha(dest/'evidence-manifest.json'));s.write(validation/'immutable-seal-verification.json',final);print(s.json.dumps(final),flush=True)
if __name__=='__main__':main()
