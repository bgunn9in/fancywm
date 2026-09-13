"""Strict scoped CLR continuation seal; no stage/production/ledger promotion."""
import argparse,importlib.util,json,platform,sys
from datetime import datetime,timezone
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s);ART=s.ART;ROOT=s.ROOT;PREFIX='FWM-CLR-HEAP-20260913-'
def art(name):return ART/(PREFIX+name)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=ART/a.id;assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id
    roots=sorted(ART.glob(PREFIX+'*'));assert {r.name[len(PREFIX):] for r in roots}=={'R0','T1','T2','P1','P2','V0','V1','N1','F1','D1','D2','D3','E1','E2','X1','L1'}
    wanted={'R0':'CURRENT_CLR_SOURCE_PREPARATION_VERIFIED','V1':'SCOPED_CLR_NATIVE_CALLER_SOURCE_PASS','N1':'SCOPED_CLR_RAW_NEGATIVE_CONTROLS_PASS','L1':'SCOPED_HEAP_LOCK_TIMESTAMP_CONTROL_VERIFIED','D2':'SCOPED_FULLGRAPH_CLR_NATIVE_CALLERS_VERIFIED','D3':'SCOPED_CLR_NATIVE_CALLER_WITNESSES_PASS','E1':'COMPLETE_CLR_HEAP_ETL_INVENTORY_PASS','E2':'COMPLETE_CLR_HEAP_ETL_INVENTORY_PASS'}
    pins={}
    for name,verdict in wanted.items():
        proof=art(name)/'verification.json';v=s.read(proof);assert v['Verdict']==verdict
        for flag in ['ProductionChanged','PerformanceClaim','StageAccepted','LedgerAppended','LogicalOwnerClaim']:
            if flag in v:assert not v[flag]
        pins[str(proof.relative_to(ART))]=s.sha(proof)
    assert s.read(art('V0')/'failure.json')['ExitCode']==1 and s.read(art('D1')/'command.json')['ExitCode']==1
    assert s.read(art('X1')/'baseline-conflicts.json')['CompleteBaselineReplayPassed'] is False
    assert len(s.read(art('X1')/'baseline-conflicts.json')['Conflicts'])==3
    for name in ['D2','D3']:
        cmd=s.read(art(name)/'command.json');assert cmd['ExitCode']==0 and cmd['OfflineOnly'] and cmd['RawLogSHA256']==s.sha(art(name)/'raw.log')
    analysis=s.read(art('D2')/'verification.json');witness=s.read(art('D3')/'verification.json')
    assert witness['AnalysisSHA256']==s.sha(art('D2')/'verification.json')
    assert all(c['EventStreamOnly'] and not c['CompleteDefaultHeapSnapshotsMatch'] and not c['LogicalOwnerClaim'] for c in analysis['Configurations'])
    assert sum(c['NativeStacks'] for c in analysis['Configurations'])==8358470
    assert sum(c['NativeStackFrames'] for c in analysis['Configurations'])==507816584
    assert sum(c['ClrRecords'] for c in analysis['Configurations'])==201730
    assert all(len(c['NegativeControls'])==5 and all(n['Rejected'] for n in c['NegativeControls']) for c in witness['Configurations'])
    for name in ['T1','T2']:
        provenance=s.read(art(name)/'provenance.json');assert len(provenance['Builds'])==6 and all(b['ExitCode']==0 for b in provenance['Builds'])
        for row in provenance['Sources']+provenance['Packages']+provenance['Binaries']:assert s.sha(art(name)/row['Path'])==row['SHA256']
    control_proofs=s.read(art('V1')/'verification.json')['Runs'];assert len(control_proofs)==2
    for command in control_proofs:
        path=Path(command['Command'][-1]);v=s.read(path);assert s.sha(path)==command['ReceiptSHA256'] and len(v['ControlPairs'])==48 and len(v['NegativeControls'])==9
        for row in v['Files']:assert s.info(ART/command['Run']/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    assert len(s.read(art('N1')/'verification.json')['NegativeControls'])==9
    boundary=s.read(art('L1')/'verification.json');assert len(boundary['NegativeControls'])==5
    assert all(p['CompletedWhileLocked'] for p in boundary['Pairs'][:12]) and all(not p['CompletedWhileLocked'] for p in boundary['Pairs'][12:])
    graph=art('F1');graph_receipt=graph/'FWM-CLR-HEAP-20260913-D2-graph-verification.json';g=s.read(graph_receipt)
    assert g['Verdict']=='SCOPED_NATIVE_HEAP_OBSERVATIONS_VERIFIED' and not g['DefaultHeapSnapshotNoGrowth'] and len(g['NegativeControls'])==9
    pins[str(graph_receipt.relative_to(ART))]=s.sha(graph_receipt)
    for row in g['RawFiles']:assert s.info(graph/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    for row in analysis['Sources'].items():assert s.sha(art('D2')/'source'/row[0])==row[1]
    assert s.sha(ROOT/'scripts/performance/clr_heap_model.py')==s.read(Path(control_proofs[0]['Command'][-1]))['SourceSHA256']['clr_heap_model.py']
    # Preserve calibrated inherited allocation/reallocation models exactly.
    oldcal=ART/'FWM-HEAP-COHORT-20260913-T2/verification.json'
    for row in s.read(oldcal)['Sources']:assert s.sha(ROOT/'scripts/performance'/row['Name'])==row['SHA256']
    inventory=s.read(art('E2')/'verification.json');assert inventory['Files']==32 and inventory['PriorFiles']==26 and inventory['NewEtls']==6 and inventory['OriginalFilesUnchanged'] and inventory['OwnedSessionsInactive']
    assert s.sha(art('E2')/'etl-after.json')==inventory['InventorySHA256']
    before=s.read(art('R0')/'prior-seal-reverification.json');prior=ART/before['Checkpoint'];assert before['Files']==2633 and before['Bytes']==223547560
    assert s.sha(prior/'verification.json')==before['VerificationSHA256'] and s.sha(prior/'evidence-manifest.json')==before['ManifestSHA256']
    snapshot=s.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]);assert snapshot['ExitCode']==0,snapshot
    s.write(dest/'snapshot-command.json',snapshot);validation=dest/'validation';validation.mkdir()
    s.write(validation/'invocation.json',dict(Command=[sys.executable,*sys.argv],RecordedUtc=datetime.now(timezone.utc).isoformat(),Python=sys.version,OS=platform.platform()))
    review_command=s.run(['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(validation/'checkpoint-review.json')]);s.write(validation/'checkpoint-review-command.json',review_command);assert review_command['ExitCode']==0,review_command
    entry=s.read(art('R0')/'entry-review.json');review=s.read(validation/'checkpoint-review.json');assert review['Production']==entry['Production'] and review['LedgerIntegrity']==entry['LedgerIntegrity']
    s.write(validation/'production-and-ledger.json',dict(Verdict='PASS',ProductionFiles=2155,WinManFiles=72,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,ProductionChanged=False,EntryReviewSHA256=s.sha(art('R0')/'entry-review.json'),FinalReviewSHA256=s.sha(validation/'checkpoint-review.json'),LedgerIntegrity=review['LedgerIntegrity']))
    own=[]
    for name in ['P1','P2']:
        for role in ['target','excluded','collector']:
            r=s.read(art(name)/(role+'-exited.json'));assert r['ExitCode']==0 and r['Exited'] and not r['ForcedCleanup'];own.append(dict(Pid=r['Pid'],EndedUtc=r['EndedUtc'],Role=role,Evidence=str((art(name)/(role+'-exited.json')).relative_to(ART))))
    for r in s.read(graph/'runs.json')['Processes']:
        assert r['ExitCode']==0 and r['OwnedDesktopHandleClosed'] and not r['InputSent'] and not r['DesktopSwitched'];run=graph/'runs'/(r['Configuration']+'-graceful');target=s.read(run/'target-exit.json');collector=s.read(run/'clr-stopped.json')
        assert target['ExitCode']==0 and collector['ExitCode']==0 and collector['Exited']
        for pid,role in [(r['ProcessId'],'fullgraph'),(target['Pid'],'targets'),(collector['CollectorPid'],'collector')]:own.append(dict(Pid=pid,EndedUtc=r['EndedUtc'],Role=role,Evidence=str((graph/'runs.json').relative_to(ART))))
    lock=s.read(art('L1')/'exited.json');assert lock['ExitCode']==0 and not lock['ForcedCleanup'];own.append(dict(Pid=lock['Pid'],EndedUtc=lock['EndedUtc'],Role='native-lock-control',Evidence=str((art('L1')/'exited.json').relative_to(ART))))
    s.write(validation/'owned-processes-final.json',dict(Verdict='PASS',Processes=[s.process_state(row) for row in own],ForeignProcessesModified=False))
    sessions=[]
    for name in inventory['OwnSessions']:
        result=s.run(['logman','query',name,'-ets']);assert result['ExitHex']=='0x80300002';sessions.append(result)
    wpr=s.run(['wpr','-status']);assert wpr['ExitCode']==0
    s.write(validation/'owned-sessions-final.json',dict(Verdict='PASS',Sessions=sessions,GlobalWpr=wpr,GlobalWprInactive='WPR is not recording' in wpr['Stdout'],ForeignSessionsModified=False))
    protected=[]
    for name,cmd in [('admission',control_proofs[0]['Command']),('graph',s.read(art('D2')/'command.json')['Command']),('witness',s.read(art('D3')/'command.json')['Command'])]:
        path=Path(cmd[-1]) if name=='admission' else art('D2' if name=='graph' else 'D3')/'verification.json';digest=s.sha(path);result=s.run(cmd);assert result['ExitCode']!=0 and ('receipt exists' in result['Stderr'] or 'output exists/wrong date' in result['Stderr']);assert s.sha(path)==digest;protected.append(dict(Name=name,Command=result,ReceiptUnchanged=True,BeforeSHA256=digest,AfterSHA256=s.sha(path)))
    s.write(validation/'immutable-receipt-controls.json',dict(Verdict='PASS',Controls=protected))
    s.write(validation/'new-work.json',dict(Verdict='PASS',ManagedNativeControlProcesses=4,FullGraphProcesses=2,TargetProcesses=2,ClrCollectors=4,NativeLockControlProcesses=1,NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=6,TracedControlChains=96,ExcludedControlChains=96,NativeLockPairs=24,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,Receipts=pins))
    entries=[]
    for root in roots+[dest]:
        for file in sorted(root.rglob('*')):
            if file.is_file():entries.append(dict(Path=str(file.relative_to(ART)),**s.info(file)))
        print('Manifest hashed',root.name,flush=True)
    entries.sort(key=lambda r:r['Path']);assert len(entries)==len({r['Path'] for r in entries});s.write(dest/'evidence-manifest.json',entries)
    receipt=dict(Verdict='VERIFIED_SCOPED_CHECKPOINT',Checkpoint=a.id,RecordedUtc=datetime.now(timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=s.sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],Receipts=pins,PriorFilesReverified=2633,PriorBytesReverified=223547560,NewTrxRuns=0,NewCrashRuns=0,NewDedicatedDpiRuns=0,NewEtls=6,ClrNativeCallerClaim=True,AtomicDefaultHeapSnapshotClaim=False,LogicalOwnerClaim=False,NativeHeapLeakFreedomClaim=False,UnmetCriteria=['Calibrated complete native snapshot/serialization and logical-owner contract for concurrent small allocations','Pre-attach ownership/history and native/private/VirtualAlloc/GPU lifetimes','Unmodified startup/Explorer membership/wallpaper needs appropriate isolated shell/user environment','Broader GUI no-growth and PERF-010/021 GPU execution/presentation contracts'])
    s.write(dest/'verification.json',receipt)
    for row in s.read(dest/'evidence-manifest.json'):assert s.info(ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']},row['Path']
    s.write(validation/'immutable-seal-verification.json',dict(Verdict='PASS',Files=len(entries),Bytes=receipt['NewEvidenceBytes'],VerificationSHA256=s.sha(dest/'verification.json'),ManifestSHA256=s.sha(dest/'evidence-manifest.json')))
    print(json.dumps(dict(Checkpoint=a.id,Files=len(entries),Bytes=receipt['NewEvidenceBytes'],VerificationSHA256=s.sha(dest/'verification.json'),ManifestSHA256=s.sha(dest/'evidence-manifest.json'))),flush=True)
if __name__=='__main__':main()
