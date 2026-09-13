"""Private VA observations plus explicitly calibrated shared-commit changes."""
import argparse,copy,importlib.util,json,shutil
import pss_shared_model as shared
from pathlib import Path
def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
p=module('pss','Verify-PssHeapControl.py');s=p.s;g=module('graph','Verify-FullGraphLifetime.py')
LABELS=['startup','0','1','2','shutdown']
def observations(rr,files,pid):
    created=[r for r in rr if r['Kind']=='PssCreated'];released=[r for r in rr if r['Kind']=='PssReleased']
    assert [r['Label'] for r in created]==[r['Label'] for r in released]==list(files)==LABELS
    result=[]
    for label,c,r in zip(LABELS,created,released):
        data=files[label];j=lambda name:json.loads(data[name]);ready=j('ready.json');end=j('released.json');capture=j('capture.json');clone=ready['ClonePid']
        assert clone>0 and clone!=pid and c['ClonePid']==r['ClonePid']==end['ClonePid']==clone
        assert c['Pid']==r['Pid']==ready['Pid']==end['Pid']==capture['Pid']==pid
        assert c['CaptureCode']==r['CaptureCode']==r['FirstQuery']==r['SecondQuery']==r['ReleaseCode']==0
        assert r['DispatcherAlive']==(label!='shutdown') and not r['HeapBlockEnumeration'] and not r['LogicalOwnerClaim'] and not r['CloneExplicitlyResumed']
        assert rr.index(c)<rr.index(r) and r['QpcBegin']<=capture['Begin']<capture['End']<=r['QpcEnd']
        assert capture['Flags']==536877441 and capture['CaptureCode']==0
        assert ready['RegionCount']==ready['WalkRows'] and ready['WalkCode']==259 and ready['MarkerFreeCode']==0 and not ready['CloneExplicitlyResumed']
        assert end['FreeCode']==0 and end['RetainedHandle'] and end['RetainedHandleClosed'] and end['AfterCloseOpenError']==87 and 1<=end['AbsenceProbes']<=200 and not end['CloneExplicitlyResumed']
        assert end['HeldOpenError']==0 and end['CloneWait']==258
        source=p.regions(data['snapshot-va.bin'],pid,clone,259);assert len(source)==ready['RegionCount']
        first=p.regions(data['clone-va-0.bin'],pid,clone,0);second=p.regions(data['clone-va-1.bin'],pid,clone,0);shared_changes=shared.transitions(first,second)
        for step in range(2):
            a=j(f'clone-va-{step}.bin.activity.json');assert a['Pid']==pid and a['ClonePid']==clone and a['GetTimesPassed'] and a['Kernel']==a['User']==0
        committed=[v for v in first if v[4]==0x1000 and v[6]==0x20000]
        reserve=[v for v in first if v[4]==0x2000 and v[6]==0x20000]
        result.append(dict(Label=label,ClonePid=clone,WholeCloneVaStable=first==second,SharedCommitChanges=shared_changes,PrivateVaStable=True,PrivateCommittedBytes=sum(v[2] for v in committed),PrivateReservedBytes=sum(v[2] for v in reserve),PrivateAllocations=len({v[1] for v in committed+reserve}),SourceMapPrivateCommittedBytes=sum(v[2] for v in source if v[4]==0x1000 and v[6]==0x20000),CloneRegions=len(first),SourceRegions=len(source),CloneAbsentBeforeNextWorkload=True))
    assert all(x['QpcEnd']<y['QpcBegin'] for x,y in zip(released,released[1:]))
    for label in ['0','1','2']:
        epoch=next(r for r in rr if r['Kind']=='Epoch' and str(r['Epoch'])==label);a=next(r for r in created if r['Label']==label);assert rr.index(epoch)<rr.index(a)
    live=result[1:4];return result,all(r['PrivateCommittedBytes']<=live[0]['PrivateCommittedBytes'] for r in live[1:])
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--snapshot',type=Path,required=True);parser.add_argument('--id',required=True);a=parser.parse_args();root=a.snapshot.resolve();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-PssSharedGraph.py','pss_shared_model.py','Verify-PssShared.py','Verify-PssGraph.py','Verify-PssHeapControl.py','Verify-FullGraphLifetime.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    shared_cal=s.ART/'FWM-D3D-MAP-20260913-V5/verification.json';assert s.sha(shared_cal)=='09847EF4D105C34C696346D0554D75FB34C12398D227464A6BFC80C01380B3BD'
    assert s.read(shared_cal)['Verdict']=='NATIVE_PSS_SHARED_COMMIT_PROPAGATION_VERIFIED'
    for row in s.read(shared_cal.parent/'raw-manifest.json'):assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    sources=g.provenance(root,True);runs=s.read(root/'runs.json');pr=runs['Processes'];assert [(r['Configuration'],r['Mode']) for r in pr]==[('Debug','graceful'),('Release','graceful')]
    assert len(runs['Builds'])==4 and all(r['ExitCode']==0 for r in runs['Builds']) and not runs['RootProductionChanged'] and not runs['UnmodifiedStartup']
    assert s.datetime.fromisoformat(pr[0]['EndedUtc'])<s.datetime.fromisoformat(pr[1]['StartedUtc']) and pr[0]['Desktop']!=pr[1]['Desktop']
    proof=s.read(root/'pss-provenance.json');tool=Path(proof['Tool']);cal=s.ART/'FWM-PSS-HEAP-20260913-V1/verification.json'
    assert proof['CalibrationSHA256']==s.sha(cal) and proof['ToolProvenanceSHA256']==s.sha(tool/'provenance.json') and not proof['HeapBlockEnumeration'] and proof['NoTracingSession']
    for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.sha(tool/row['Path'])==row['SHA256']
    result=[];negative=[];processes=[]
    for run in pr:
        config=run['Configuration'];folder=root/'runs'/(config+'-graceful');pid=run['ProcessId'];rr=g.rows(folder/'observations.jsonl');g.check(rr,'graceful',True,50,False,True)
        assert g.single(rr,'Process')['Pid']==pid and g.single(rr,'Process')['Desktop']==run['Desktop']
        assert run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run.get('TimedOut') and not run['InputSent'] and not run['DesktopSwitched'] and run['DesktopAfterExit']['Rows']==[]
        assert s.sha(root/'validation'/f'{config}-graceful.log')==run['LogSHA256']
        summary=s.read(folder/'summary.json');assert summary['Verdict']=='PASS' and summary['Pid']==pid and not summary['SurvivingHwnds'] and summary['Closed']==4
        assert all(summary[k] for k in ['StartupReturned','DispatcherStopped','ProviderDisposed','HooksCompleted','MainCompleted','WorkspaceWorkersStopped','MicaStopped'])
        assert summary['SettingWrites']==2 and summary['OriginalArranging']==summary['FinalArranging'] and summary['ProgramExits']==1 and summary['CrashCleanups']==0
        target=s.read(folder/'target-exit.json');assert target['ExitCode']==0 and s.read(folder/'targets/cleanup.json')['AllDestroyed']
        observer=s.read(root/f'{config}-pss-binary.json');assert s.sha(root/observer['Path'])==observer['SHA256']==s.sha(tool/'binaries'/config/'FancyWM.NativePss.dll')
        files={label:{f.name:f.read_bytes() for f in (folder/'pss'/label).iterdir() if f.is_file()} for label in LABELS};epochs,gate=observations(rr,files,pid)
        try:g.check(rr,'graceful',True,50,True,True);gui=True;reason=None
        except ValueError as e:gui=False;reason=str(e)
        result.append(dict(Configuration=config,Pid=pid,NativeSnapshots=epochs,PrivateCommittedNoGrowth=gate,GuiCriterionPassed=gui,GuiFailure=reason,WeakWorkloadOwners=9483,RetainedWorkloadOwners=0,NativeGraphGenerations=sum(r['Kind']=='GraphWindowAcquired' for r in rr)))
        for own,role in [(pid,'graph'),(target['Pid'],'target'),*[(e['ClonePid'],'clone') for e in epochs]]:processes.append(s.process_state(dict(Pid=own,EndedUtc=run['EndedUtc'],Role=role)))
        if config=='Debug':
            for case in ['missing-lifetime','surviving-HWND','retained-owner','missing-graph-close','missing-snapshot','stopped-dispatcher','retained-clone','foreign-clone','changed-clone-map']:
                bad=copy.deepcopy(rr);ff=copy.deepcopy(files)
                if case=='missing-lifetime':bad.remove(next(r for r in bad if r['Kind']=='Lifetime'))
                if case=='surviving-HWND':next(r for r in bad if r['Kind']=='Lifetime')['SurvivingHwnds']=[123]
                if case=='retained-owner':next(r for r in bad if r['Kind']=='Epoch')['Retained']={'SettingsWindow':1}
                if case=='missing-graph-close':bad.remove(next(r for r in bad if r['Kind']=='GraphWindowClosed'))
                if case=='missing-snapshot':del ff['1']
                if case=='stopped-dispatcher':next(r for r in bad if r['Kind']=='PssReleased' and r['Label']=='1')['DispatcherAlive']=False
                if case=='retained-clone':ff['1']['released.json']=json.dumps({**json.loads(ff['1']['released.json']),'AfterCloseOpenError':0}).encode()
                if case=='foreign-clone':ff['1']['ready.json']=json.dumps({**json.loads(ff['1']['ready.json']),'ClonePid':pid}).encode()
                if case=='changed-clone-map':ff['1']['clone-va-1.bin']=ff['2']['clone-va-1.bin']
                try:g.check(bad,'graceful',True,50,False,True);observations(bad,ff,pid)
                except (ValueError,AssertionError,KeyError):negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
    raw=[dict(Path=str(f.relative_to(root)),**s.info(f)) for f in sorted((root/'runs').rglob('*')) if f.is_file()];s.write(dest/'raw-manifest.json',raw);s.write(dest/'owned-processes.json',processes)
    s.write(dest/'verification.json',dict(Verdict='SCOPED_PSS_GRAPH_PRIVATE_VA_WITH_SHARED_COMMITS_VERIFIED',SharedControlSHA256=s.sha(shared_cal),WholeCloneVaStable=all(e['WholeCloneVaStable'] for r in result for e in r['NativeSnapshots']),SharedChangesAreOnlyReservedToCommitted=True,Snapshot=str(root),SourceFilesVerified=sources,Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),PrivateCommittedNoGrowth=all(r['PrivateCommittedNoGrowth'] for r in result),HeapBlockEnumeration=False,NativeHeapLeakFreedomClaim=False,LogicalOwnerClaim=False,NewTracingSession=False,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS'))
    print(json.dumps(dict(SHA256=s.sha(dest/'verification.json'),Configurations=result)),flush=True)
if __name__=='__main__':main()
