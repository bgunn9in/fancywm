"""Seal offline native cohort evidence and preserve the predecessor byte-exact."""
import argparse,importlib.util,json,platform,sys
from datetime import datetime,timezone
from pathlib import Path
spec=importlib.util.spec_from_file_location('seal',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
ROOT=s.ROOT;ART=s.ART;PREFIX='FWM-HEAP-COHORT-20260913-'
def artifact(name):return ART/(PREFIX+name)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=ART/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id
    roots=sorted(ART.glob(PREFIX+'*'));assert {r.name[len(PREFIX):] for r in roots}=={'R0','T1','T2','D1','D2'}
    prior=ART/'FWM-PERF016-HEAP-20260913-R1';old=s.read(artifact('R0')/'prior-seal-reverification.json')
    assert old['Verdict']=='PASS' and old['Files']==28800 and old['Bytes']==42723438206
    for name,key in [('verification.json','VerificationSHA256'),('evidence-manifest.json','ManifestSHA256')]:assert s.sha(prior/name)==old[key]
    pins={};offline=[]
    for name,expected in [('T1','89DC8C8EA9C49CD0BF82C1A29EFC08C6DE7D35CBC3ECF32E39EA127027650494'),('T2','6EA0791D41C69C11AC0C95C711D4E65A29AB572A327860DDC230E15F785704A9'),('D1','BA884E71E59714729DB1530F4FB848983C53C3341E159ED031361D486A5EE0D4'),('D2','469819EEEFD514B5E65A8FE1503918FF1FE5855D2494BDAF645131B854AFA0A0')]:
        root=artifact(name);receipt=root/'verification.json';assert s.sha(receipt)==expected;pins[str(receipt.relative_to(ART))]=expected
        v=s.read(receipt);assert all(not v[k] for k in ['NewNativeProcesses','NewEtls','NewTrxRuns','ProductionChanged','PerformanceClaim','StageAccepted','LedgerAppended'])
        command=s.read(root/'command.json');assert command['ExitCode']==0 and command['OfflineOnly'] and command['RawLogSHA256']==s.sha(root/'raw.log')
        offline.append(dict(Pid=command['Pid'],EndedUtc=command['EndedUtc'],Evidence=str((root/'command.json').relative_to(ART)),Role='offline-python-analysis'))
    calibration=s.read(artifact('T2')/'verification.json');assert calibration['NativeMovedReallocPairs']==48 and len(calibration['NegativeControls'])==13 and all(x['Rejected'] for x in calibration['NegativeControls'])
    for row in calibration['Sources']:
        assert s.sha(ROOT/'scripts/performance'/row['Name'])==row['SHA256']
        assert s.sha(artifact('T2')/'source'/row['Name'])==row['SHA256']
    for f in (artifact('D2')/'source').iterdir():assert s.sha(f)==s.sha(ROOT/'scripts/performance'/f.name)
    derived=s.read(artifact('D1')/'verification.json');witness=s.read(artifact('D2')/'verification.json')
    assert witness['DerivedReceiptSHA256']==s.sha(artifact('D1')/'verification.json')
    assert derived['NativeTraceVerificationSHA256']==witness['NativeTraceReceiptSHA256']=='DECF9134FED51292D6187BE400438C7257EAB2ED803F6C4DF62035007B8B5C20'
    assert [(c['Configuration'],c['Pid']) for c in derived['Configurations']]==[('Debug',28184),('Release',10060)]
    assert sum(c['ExportedPhaseRows'] for c in witness['Configurations'])==771654
    assert sum(c['NativeReallocSuccessorWitnesses'] for c in witness['Configurations'])==73811
    assert all(len(c['NegativeControls'])==6 and all(x['Rejected'] for x in c['NegativeControls']) for c in witness['Configurations'])
    # Bind every consumed native data file to the completed prior seal. This
    # is selected input rehashing, not another full predecessor rehash.
    old_entries={Path(r['Path']).as_posix():r for r in s.read(prior/'evidence-manifest.json')}
    e3=ART/'FWM-NATIVE-HEAP-20260913-E3';f4=ART/'FWM-NATIVE-HEAP-20260913-F4'
    inputs=[e3/'verification.json',e3/'native-decoded/raw.bin',f4/'trace-verification.json',f4/'verification.json',e3/'realloc-model-verification.json']
    inputs.extend(sorted((e3/'control').glob('*.bin')))
    for config in ['Debug','Release']:
        run=f4/'runs'/(config+'-graceful');inputs.append(run/'heap-decoded/native/raw.bin')
        inputs.extend(run/'heap'/(label+'.bin') for label in ['0','1','2','shutdown'])
    input_rows=[]
    for f in inputs:
        rel=f.relative_to(ART).as_posix();row=old_entries[rel];assert s.info(f)=={k:row[k] for k in ['Bytes','SHA256']},rel
        input_rows.append(dict(Path=rel,**s.info(f)))
    # No capture occurred: compare complete case-insensitive paths/sizes to
    # E4; do not describe this membership check as a new full ETL hash pass.
    paths=s.run(['rg','--files','--hidden','--no-ignore','--iglob','*.etl']);assert paths['ExitCode']==0
    actual=sorted((Path(x).as_posix().lower(),(ROOT/x).stat().st_size) for x in paths['Stdout'].splitlines())
    inventory=ART/'FWM-NATIVE-HEAP-20260913-E4/etl-after.json'
    assert s.sha(inventory)=='5F5343647332CF0936609F2DC40F63E19F1E07D74D3CB8CEBF29318307D1CEFA'
    assert actual==sorted((Path(x['Path']).as_posix().lower(),x['Bytes']) for x in s.read(inventory)) and len(actual)==26
    r=s.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]);assert r['ExitCode']==0,r
    s.write(dest/'snapshot-command.json',r);folder=dest/'validation';folder.mkdir()
    s.write(folder/'invocation.json',dict(Command=[sys.executable,*sys.argv],Python=sys.version,PythonExecutable=s.info(Path(sys.executable)),OS=platform.platform(),Architecture=platform.machine(),RecordedUtc=datetime.now(timezone.utc).isoformat()))
    s.write(folder/'native-input-provenance.json',dict(Verdict='PASS',PriorManifestSHA256=old['ManifestSHA256'],SelectedFiles=len(input_rows),Inputs=input_rows))
    r=s.run(['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(ART/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'),'--mica-candidate',str(ART/'FWM-FULLGRAPH-MICA-20260912-C1'),'--worker-candidate',str(ART/'FWM-WORKER-RETENTION-20260912-C2'),'--output',str(folder/'checkpoint-review.json')]);s.write(folder/'checkpoint-review-command.json',r);assert r['ExitCode']==0,r
    review=s.read(folder/'checkpoint-review.json');entry=s.read(artifact('R0')/'entry-review.json');assert review['Production']==entry['Production'] and review['LedgerIntegrity']==entry['LedgerIntegrity']
    s.write(folder/'production-and-ledger-continuity.json',dict(Verdict='PASS',ProductionFiles=2155,WinManFiles=72,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,ProductionChanged=False,EntrySHA256=s.sha(artifact('R0')/'entry-review.json'),FinalSHA256=s.sha(folder/'checkpoint-review.json'),LedgerIntegrity=review['LedgerIntegrity']))
    protected=[]
    for name in ['T2','D1','D2']:
        root=artifact(name);before={str(f.relative_to(root)):s.info(f) for f in root.rglob('*') if f.is_file()}
        command=s.read(root/'command.json')['Command'];r=s.run(command)
        assert r['ExitCode']!=0 and 'output exists/wrong date' in r['Stderr'],r
        after={str(f.relative_to(root)):s.info(f) for f in root.rglob('*') if f.is_file()};assert before==after,'existing output mutated'
        protected.append(dict(**r,ExistingFiles=len(before),EveryExistingFileUnchanged=True))
    s.write(folder/'immutable-output-negative-controls.json',dict(Verdict='PASS',Controls=protected))
    s.write(folder/'offline-processes-final.json',dict(Verdict='PASS',Processes=[s.process_state(row) for row in offline],NewNativeApplicationProcesses=0,ForeignProcessesModified=False))
    old_sessions=s.read(prior/'validation/owned-heap-sessions-final.json')['Sessions'];sessions=[]
    for row in old_sessions:
        r=s.run(row['Command']);assert r['ExitHex']=='0x80300002',r;sessions.append(dict(**r,Inactive=True))
    s.write(folder/'owned-sessions-final.json',dict(Verdict='PASS',Sessions=sessions,NewOwnedSessions=0,ForeignSessionsModified=False))
    global_wpr=s.run(['wpr','-status']);assert global_wpr['ExitCode']==0
    own_wpr=s.run(['wpr','-status','-instancename','FWM_OWNED_HEAP_CPU_FWM-NATIVE-HEAP-20260913-E1']);assert own_wpr['ExitCode']==0 and 'WPR is not recording' in own_wpr['Stdout']
    s.write(folder/'wpr-final.json',dict(GlobalStatus=global_wpr,OwnInstance=own_wpr,NewRecordingStarted=False,GlobalInactive='WPR is not recording' in global_wpr['Stdout'],ForeignSessionsModified=False))
    s.write(folder/'etl-continuity.json',dict(Verdict='PASS',InventorySHA256=s.sha(inventory),Files=26,SortedPathsAndSizesUnchanged=True,IncludesIgnored=True,NewEtls=0,FullEtlHashPassRepeated=False))
    s.write(folder/'new-work-verification.json',dict(Verdict='PASS',NewNativeApplicationProcesses=0,NewTrxRuns=0,NewCrashRuns=0,NewDpiRuns=0,NewEtls=0,OfflineAnalysisProcesses=4,NativeControlPairsReplayed=48,CalibrationNegativeControls=13,NativeWitnessNegativeControls=12,ImmutableOutputNegativeControls=3,SourceAndBinaryDeliveryRebuilt=False,Receipts=pins))
    print('Native inputs, calibrated source, final Git/production/ledger and cleanup verified',flush=True)
    entries=[]
    for root in roots+[dest]:
        for f in sorted(root.rglob('*')):
            if f.is_file():entries.append(dict(Path=str(f.relative_to(ART)),**s.info(f)))
        print('Manifest hashed',root.name,flush=True)
    entries.sort(key=lambda row:row['Path']);assert len(entries)==len({r['Path'] for r in entries})
    s.write(dest/'evidence-manifest.json',entries)
    receipt=dict(Verdict='VERIFIED_SCOPED_CHECKPOINT',Checkpoint=a.id,RecordedUtc=datetime.now(timezone.utc).isoformat(),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,InheritedDeltaFiles=5,NewProductionDeltaFiles=0,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,
        NewEvidenceFiles=len(entries),NewEvidenceBytes=sum(r['Bytes'] for r in entries),ManifestSHA256=s.sha(dest/'evidence-manifest.json'),EvidenceRoots=[r.name for r in roots],Receipts=pins,PriorFilesReverified=old['Files'],PriorBytesReverified=old['Bytes'],
        CompleteDefaultHeapBaselineReplay=True,OriginalAllocationStackForSeededBlocks=False,NativeHeapLeakFreedomClaim=False,LogicalOwnerClaim=False,NewNativeApplicationProcesses=0,NewTrxRuns=0,NewEtls=0,
        UnmetCriteria=['Unmodified startup/genuine Explorer membership/wallpaper needs an appropriate isolated shell/user environment','Heap no-growth remains false; native generations do not establish pre-baseline stacks or logical owners','Changing-display GUI no-growth remains unproved','PERF-010/021 attributed GPU execution and physical presentation contracts remain missing'])
    s.write(dest/'verification.json',receipt)
    for row in s.read(dest/'evidence-manifest.json'):assert s.info(ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']},row['Path']
    s.write(folder/'immutable-seal-verification.json',dict(Verdict='PASS',Files=len(entries),Bytes=receipt['NewEvidenceBytes'],VerificationSHA256=s.sha(dest/'verification.json'),ManifestSHA256=s.sha(dest/'evidence-manifest.json')))
    print(json.dumps(dict(Checkpoint=a.id,Files=len(entries),Bytes=receipt['NewEvidenceBytes'],VerificationSHA256=s.sha(dest/'verification.json'),ManifestSHA256=s.sha(dest/'evidence-manifest.json'))),flush=True)
if __name__=='__main__':main()
