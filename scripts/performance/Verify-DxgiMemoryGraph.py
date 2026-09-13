"""Full startup graph DXGI process usage, finite epochs and native teardown."""
import argparse,copy,importlib.util,json,shutil,sys
from pathlib import Path
import dxgi_memory_model as model
def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
s=module('s','Finalize-NativeHeapEvidence.py');g=module('g','Verify-FullGraphLifetime.py')
PHASES=[-1,0,1,2,99]
def check(native,managed,pid,adapters):
    actual,queries=model.check(native,pid,PHASES,5);assert actual==adapters,'adapter identity changed since calibration'
    start=g.single(managed,'DxgiMemoryStarted');assert start['Pid']==pid and start['Code']==0 and start['Module']>0 and start['NodeIndex']==0 and not start['ReservationChanged']
    ns=model.one(native,'started');assert start['QpcBegin']<ns['Qpc']<start['QpcEnd']
    calls=[r for r in managed if r['Kind']=='DxgiMemorySample'];assert [(r['Phase'],r['Sample']) for r in calls]==[(p,i) for p in PHASES for i in range(5)]
    for r in calls:
        assert r['Pid']==pid and r['Code']==0 and r['DispatcherAlive']==(r['Phase']!=99)
        found=[q for q in queries if (q['Phase'],q['Sample'])==(r['Phase'],r['Sample'])]
        assert len(found)==2*len(adapters) and all(r['QpcBegin']<q['QpcBegin']<=q['QpcEnd']<r['QpcEnd'] for q in found)
    for e in range(3):
        epoch=next(r for r in managed if r['Kind']=='Epoch' and r['Epoch']==e);last=next(r for r in calls if (r['Phase'],r['Sample'])==(e,4))
        assert managed.index(epoch)<managed.index(last)
        bound=next(r for r in managed if r['Kind']=='GraphWindowCheckpoint' and r['Label']==str(e));assert managed.index(last)<managed.index(bound)
    result=[]
    for adapter in adapters:
        for segment in [0,1]:
            slices=[]
            for phase in PHASES:
                qq=[q for q in queries if q['Luid']==adapter['Luid'] and q['Segment']==segment and q['Phase']==phase]
                values=[q['CurrentUsage'] for q in qq];slices.append(dict(Phase=phase,CurrentUsageBytes=values,Min=min(values),Max=max(values),Stable=len(set(values))==1))
            noGrowth=all(r['Stable'] for r in slices[1:4]) and all(r['Max']<=slices[1]['Max'] for r in slices[2:4])
            result.append(dict(Luid=adapter['Luid'],Node=0,Segment=segment,Samples=slices,ObservedUsageNoGrowth=noGrowth,ShutdownUsageZero=slices[-1]['Max']==0))
    return result
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--graph',required=True,type=Path);a=p.parse_args();root=a.graph.resolve();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-DxgiMemoryGraph.py','dxgi_memory_model.py','Verify-FullGraphLifetime.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    s.write(dest/'invocation.json',dict(Command=[sys.executable,*sys.argv],RecordedUtc=s.datetime.now(s.timezone.utc).isoformat()))
    sources=g.provenance(root,True);runs=s.read(root/'runs.json');processes=runs['Processes'];assert [(r['Configuration'],r['Mode']) for r in processes]==[('Debug','graceful'),('Release','graceful')]
    assert len(runs['Builds'])==4 and all(r['ExitCode']==0 for r in runs['Builds']) and not runs['RootProductionChanged'] and not runs['UnmodifiedStartup']
    assert s.datetime.fromisoformat(processes[0]['EndedUtc'])<s.datetime.fromisoformat(processes[1]['StartedUtc']) and processes[0]['Desktop']!=processes[1]['Desktop']
    proof=s.read(root/'dxgi-provenance.json');tool=Path(proof['Tool']);cal=s.ART/'FWM-DXGI-MEMORY-20260913-V1/verification.json';calibration=s.read(cal)
    assert s.sha(cal)==proof['CalibrationSHA256']=='F18AC09ACD9326CC57198B75BCBB255889981111D53D696921BA47937F09F7ED'
    assert proof['ToolProvenanceSHA256']==s.sha(tool/'provenance.json') and proof['NoTracingSession'] and not proof['ReservationChanged'] and not proof['HookInterception'] and not proof['CompleteGraphicsMemoryClaim']
    for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    for row in s.read(cal.parent/'raw-manifest.json'):assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    assert s.sha(tool/'source/DxgiMemory.cpp')==s.sha(root/'source/scripts/performance/dxgi-memory/DxgiMemory.cpp')
    result=[];negative=[];own=[]
    for run in processes:
        config=run['Configuration'];pid=run['ProcessId'];folder=root/'runs'/(config+'-graceful');rr=g.rows(folder/'observations.jsonl');native=g.rows(folder/'dxgi.jsonl')
        g.check(rr,'graceful',True,50,False,True);assert g.single(rr,'Process')['Pid']==pid and g.single(rr,'Process')['Desktop']==run['Desktop']
        assert run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run.get('TimedOut') and not run['InputSent'] and not run['DesktopSwitched'] and run['DesktopAfterExit']['Rows']==[]
        assert s.sha(root/'validation'/f'{config}-graceful.log')==run['LogSHA256']
        summary=s.read(folder/'summary.json');assert summary['Verdict']=='PASS' and summary['Pid']==pid and not summary['SurvivingHwnds'] and summary['Closed']==4
        assert all(summary[k] for k in ['StartupReturned','DispatcherStopped','ProviderDisposed','HooksCompleted','MainCompleted','WorkspaceWorkersStopped','MicaStopped'])
        assert summary['SettingWrites']==2 and summary['OriginalArranging']==summary['FinalArranging'] and summary['ProgramExits']==1 and summary['CrashCleanups']==0
        target=s.read(folder/'target-exit.json');assert target['ExitCode']==0 and s.read(folder/'targets/cleanup.json')['AllDestroyed']
        observer=s.read(root/f'{config}-dxgi-binary.json');assert s.sha(root/observer['Path'])==observer['SHA256']==s.sha(tool/'binaries'/config/'DxgiMemory.dll')
        control=next(c for c in calibration['Configurations'] if c['Configuration']==config and c['PartialReleaseFence']);assert control['ObserverSHA256']==observer['SHA256']
        details=check(native,rr,pid,control['Adapters']);stop=s.read(folder/'dxgi-stopped.json');ns=model.one(native,'stopped');assert stop['Pid']==pid and stop['Code']==0 and stop['ModuleUnloaded'] and stop['QpcBegin']<ns['Qpc']<stop['QpcEnd']
        try:g.check(rr,'graceful',True,50,True,True);gui=True;reason=None
        except ValueError as e:gui=False;reason=str(e)
        result.append(dict(Configuration=config,Pid=pid,Queries=sum(r['Event']=='sample' for r in native),MemoryGroups=details,ObservedUsageNoGrowth=all(d['ObservedUsageNoGrowth'] for d in details),GuiCriterionPassed=gui,GuiFailure=reason,WeakWorkloadOwners=9483,RetainedWorkloadOwners=0,NativeGraphGenerations=sum(r['Kind']=='GraphWindowAcquired' for r in rr),ObserverModuleUnloaded=True))
        for ownpid,role in [(pid,'graph'),(target['Pid'],'target')]:own.append(s.process_state(dict(Pid=ownpid,EndedUtc=run['EndedUtc'],Role=role)))
        if config=='Debug':
            for case in ['missing-lifetime','surviving-HWND','retained-owner','missing-graph-close','missing-memory-sample','stopped-dispatcher','foreign-PID','wrong-LUID','wrong-segment','wrong-query-order']:
                bad=copy.deepcopy(rr);nn=copy.deepcopy(native);q=next(r for r in nn if r['Event']=='sample' and r['Phase']==1)
                if case=='missing-lifetime':bad.remove(next(r for r in bad if r['Kind']=='Lifetime'))
                if case=='surviving-HWND':next(r for r in bad if r['Kind']=='Lifetime')['SurvivingHwnds']=[123]
                if case=='retained-owner':next(r for r in bad if r['Kind']=='Epoch')['Retained']={'SettingsWindow':1}
                if case=='missing-graph-close':bad.remove(next(r for r in bad if r['Kind']=='GraphWindowClosed'))
                if case=='missing-memory-sample':nn.remove(q)
                if case=='stopped-dispatcher':next(r for r in bad if r['Kind']=='DxgiMemorySample' and r['Phase']==1)['DispatcherAlive']=False
                if case=='foreign-PID':q['Pid']+=1
                if case=='wrong-LUID':q['Luid']+=1
                if case=='wrong-segment':q['Segment']=3
                if case=='wrong-query-order':q['QpcBegin']=q['QpcEnd']+1
                try:g.check(bad,'graceful',True,50,False,True);check(nn,bad,pid,control['Adapters'])
                except (AssertionError,ValueError,KeyError,StopIteration):negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
            for case in ['injected-usage-growth','unstable-usage-samples']:
                nn=copy.deepcopy(native)
                for adapter in control['Adapters']:
                    for segment in [0,1]:
                        baseline=max(r['CurrentUsage'] for r in nn if r['Event']=='sample' and r['Phase']==0 and r['Luid']==adapter['Luid'] and r['Segment']==segment)
                        for r in nn:
                            if r['Event']=='sample' and r['Phase']==1 and r['Luid']==adapter['Luid'] and r['Segment']==segment:r['CurrentUsage']=baseline+(4096 if case=='injected-usage-growth' or r['Sample']==0 else 0)
                altered=check(nn,rr,pid,control['Adapters']);assert all(not d['ObservedUsageNoGrowth'] for d in altered),'false no-growth gate'
                negative.append(dict(Case=case,Rejected=True,QueryCoverageStillValid=True,NoGrowthClaimRejected=True))
        print(config,'usage no growth',result[-1]['ObservedUsageNoGrowth'],'GUI',gui,flush=True)
    raw=[dict(Path=str(f.relative_to(root)),**s.info(f)) for f in sorted((root/'runs').rglob('*')) if f.is_file()];s.write(dest/'raw-manifest.json',raw);s.write(dest/'owned-processes.json',own)
    s.write(dest/'verification.json',dict(Verdict='SCOPED_GRAPH_DXGI_USAGE_OBSERVATIONS_VERIFIED',Graph=str(root),SourceFilesVerified=sources,CalibrationSHA256=s.sha(cal),Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,PhysicalAllocationIdentityClaim=False,ImmediateReleaseAccountingClaim=False,CompleteGraphicsMemoryClaim=False,AllNodesCoverageClaim=False,NativeHeapLeakFreedomClaim=False,NewTracingSession=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
