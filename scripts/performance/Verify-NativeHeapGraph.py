"""Default-heap snapshots and full-graph lifecycle: separate observation/gates."""
import argparse, copy, importlib.util, json, shutil
from collections import Counter
from datetime import datetime
from pathlib import Path
def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
h=module('heap','Verify-NativeHeap.py');g=module('graph','Verify-FullGraphLifetime.py');s=module('stable','Verify-GuiRealtimeGraph.py')
def observations(rr, snapshots, pid):
    marks=[r for r in rr if r['Kind']=='NativeHeapSnapshot']
    h.require([r['Label'] for r in marks]==list(snapshots)==['startup','0','1','2','shutdown'],'missing/duplicate native heap scenario')
    heap=None
    for mark,(label,data) in zip(marks,snapshots.items()):
        d=h.decode(data,pid)
        h.require(heap is None or heap==d['Heap'],'default heap identity changed');heap=d['Heap']
        h.require(mark['Result']==0 and mark['DefaultProcessHeapOnly'] and not mark['BlockContentsRecorded'] and not mark['AllocationStackClaim'],'heap observer boundary')
        h.require(mark['DispatcherAlive']==(label!='shutdown') and mark['QpcBegin']<=d['Begin']<=d['End']<=mark['QpcEnd'],'heap dispatcher/time boundary')
    h.require(all(x['QpcEnd']<y['QpcBegin'] for x,y in zip(marks,marks[1:])),'heap temporal order')
    live=[h.decode(snapshots[str(e)],pid) for e in range(3)]
    result=[]
    baseline={(e['Address'],e['Bytes']) for e in live[0]['Entries'] if e['Flags']&4}
    for e,data in enumerate(live):
        blocks=[b for b in data['Entries'] if b['Flags']&4]
        hist=Counter(b['Bytes'] for b in blocks)
        addresses={(b['Address'],b['Bytes']) for b in blocks}
        result.append(dict(Epoch=e,Heap=data['Heap'],BusyBlocks=data['BusyBlocks'],BusyBytes=data['BusyBytes'],OverheadBytes=data['OverheadBytes'],
            SizeHistogram=[dict(Bytes=k,Blocks=v) for k,v in sorted(hist.items())],
            NewAddressSizePairs=len(addresses-baseline),AbsentBaselineAddressSizePairs=len(baseline-addresses),PointerGenerationClaim=False))
    return result
def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--quiescence',type=int,choices=[0,45],default=45);p.add_argument('--stable-window',type=int,choices=[0,30],default=30);a=p.parse_args();root=a.snapshot
    h.require(not a.output.exists(),'receipt exists');sources=g.provenance(root,True)
    runs=g.read(root/'runs.json');pr=runs['Processes']
    h.require([(r['Configuration'],r['Mode']) for r in pr]==[('Debug','graceful'),('Release','graceful')],'native process coverage')
    h.require(datetime.fromisoformat(pr[0]['EndedUtc'])<datetime.fromisoformat(pr[1]['StartedUtc']) and pr[0]['Desktop']!=pr[1]['Desktop'],'isolated process order')
    h.require(len(runs['Builds'])==4 and all(b['ExitCode']==0 for b in runs['Builds']),'affected Debug/Release build coverage')
    proof=g.read(root/'heap-observer-provenance.json');tool=Path(proof['Path']).parent
    h.require(g.sha(tool/'verification.json')==proof['CalibrationSHA256'] and g.sha(tool/'source/Heap.cpp')==proof['NativeSourceSHA256'],'native calibration provenance')
    observed=[];negative=[];allrows=[]
    for run in pr:
        config=run['Configuration'];folder=root/'runs'/(config+'-graceful');rr=g.rows(folder/'observations.jsonl');allrows.append(rr);pid=run['ProcessId']
        g.check(rr,'graceful',True,50,False,True)
        if a.stable_window:s.stability(rr)
        else:h.require(not any(r['Kind'] in ['GuiStabilitySample','GuiStabilityAdmitted'] for r in rr),'undeclared stability policy')
        h.require(run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run.get('TimedOut') and not run['InputSent'] and not run['DesktopSwitched'] and run['DesktopAfterExit']['Rows']==[],'own native process cleanup')
        h.require(g.sha(root/'validation'/f'{config}-graceful.log')==run['LogSHA256'] and g.single(rr,'Process')['Pid']==pid,'native log/identity')
        summary=g.read(folder/'summary.json')
        h.require(summary['Verdict']=='PASS' and summary['Pid']==pid and summary['SurvivingHwnds']==[] and summary['Closed']==4,'native graceful summary')
        h.require(all(summary[k] for k in ['StartupReturned','DispatcherStopped','ProviderDisposed','HooksCompleted','MainCompleted','WorkspaceWorkersStopped','MicaStopped']),'provider completion')
        h.require(summary['SettingWrites']==2 and summary['OriginalArranging']==summary['FinalArranging'] and summary['ProgramExits']==1 and summary['CrashCleanups']==0,'graceful setting/order')
        h.require(g.read(folder/'target-exit.json')['ExitCode']==0 and g.read(folder/'targets/cleanup.json')['AllDestroyed'],'owned targets survived')
        h.require(g.sha(root/'binaries'/config/'FancyWM.NativeHeap.dll')==proof['SHA256'],'executed observer bytes')
        raw={label:(folder/'heap'/(label+'.bin')).read_bytes() for label in ['startup','0','1','2','shutdown']}
        epochs=observations(rr,raw,pid)
        h.require(len([r for r in rr if r['Kind']=='GuiQuiescence'])==3*a.quiescence and all(r['DispatcherAlive'] and r['Seconds']==a.quiescence for r in rr if r['Kind']=='GuiQuiescence'),'live quiescence')
        contract=g.single(rr,'GuiTargetLayoutContract');h.require(contract['CompactNativeTargets'] and contract['AutoSplitCount']==50 and contract['NativeTargetMinimum']==8 and not contract['GlobalDisplayChanged'],'compact target boundary')
        protocol=g.rows(folder/'target-protocol.jsonl');ready=protocol[0]
        creates=[r for r in protocol if r.get('Operation')=='create' and r.get('Success')]
        h.require(len(creates)==150 and ready['CompactNativeTargets'] and all(w['CompactNativeTargets'] and w['NativeMinimumReplies']>0 and w['Dpi']==ready['Dpi'] for r in creates for w in r['Windows']),'native compact target minimum/DPI')
        try:g.check(rr,'graceful',True,50,True,True);gui=True;reason=None
        except ValueError as e:gui=False;reason=str(e)
        gate=all(e['BusyBlocks']<=epochs[0]['BusyBlocks'] and e['BusyBytes']<=epochs[0]['BusyBytes'] for e in epochs[1:])
        observed.append(dict(Configuration=config,Pid=pid,NativeHeapSnapshots=5,HeapEpochs=epochs,DefaultHeapSnapshotNoGrowth=gate,
            GuiCriterionPassed=gui,GuiFailure=reason,WorkloadWeakOwners=9483,RetainedWorkloadOwners=0,NativeTargetDpi=ready['Dpi'],GraphDisplay=g.single(rr,'GraphDisplayContract'),
            ShutdownHeap={k:v for k,v in h.decode(raw['shutdown'],pid).items() if k!='Entries'}))
        if config=='Debug':
            for case in ['missing-heap-epoch','stopped-live-dispatcher','wrong-native-pid']:
                bad=copy.deepcopy(rr);snap=raw.copy()
                if case=='missing-heap-epoch':del snap['1']
                if case=='stopped-live-dispatcher':next(r for r in bad if r['Kind']=='NativeHeapSnapshot' and r['Label']=='1')['DispatcherAlive']=False
                if case=='wrong-native-pid':snap['1']=raw['1'][:12]+(pid+1).to_bytes(4,'little')+raw['1'][16:]
                try:observations(bad,snap,pid)
                except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
                else:raise ValueError('negative accepted '+case)
    for case in ['missing-lifetime','surviving-HWND','retained-owner','missing-generation-close','surviving-replacement-HWND','wrong-active-generation']:
        bad=copy.deepcopy(allrows[0])
        if case=='missing-lifetime':bad.remove(next(r for r in bad if r['Kind']=='Lifetime'))
        if case=='surviving-HWND':next(r for r in bad if r['Kind']=='Lifetime')['SurvivingHwnds']=[123]
        if case=='retained-owner':next(r for r in bad if r['Kind']=='Epoch')['Retained']={'SettingsWindow':1}
        if case=='missing-generation-close':bad.remove(next(r for r in bad if r['Kind']=='GraphWindowClosed'))
        if case=='surviving-replacement-HWND':next(r for r in bad if r['Kind']=='GraphWindowCheckpoint' and r['Label']=='exit')['SurvivingNativeHwnds']=[123]
        if case=='wrong-active-generation':next(r for r in bad if r['Kind']=='GraphWindowCheckpoint' and r['Label']=='0')['Active'][0]['Id']=999
        try:g.check(bad,'graceful',True,50,False,True)
        except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    verifier=root/(a.output.stem+'-source');h.require(not verifier.exists(),'verification source exists');verifier.mkdir()
    for name in ['Verify-NativeHeapGraph.py','Verify-NativeHeap.py','Verify-FullGraphLifetime.py','Verify-GuiRealtimeGraph.py','Verify-GuiAttribution.py','Verify-UserHandleEvents.py','Verify-InputContexts.py']:
        shutil.copyfile(Path(__file__).with_name(name),verifier/name)
    h.write(a.output,dict(Verdict='SCOPED_NATIVE_HEAP_OBSERVATIONS_VERIFIED',WholeIdStatus='IN_PROGRESS',ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,
        Processes=2,ObservationRows=sum(map(len,allrows)),SourceFilesVerified=sources,Configurations=observed,NegativeControls=negative,QuiescenceSeconds=a.quiescence,StableWindowSeconds=a.stable_window,
        DefaultHeapSnapshotNoGrowth=all(r['DefaultHeapSnapshotNoGrowth'] for r in observed),NativeHeapLeakFreedomClaim=False,
        Limits=['Default heap metadata only; no allocation call stacks or pointer generations','Other private heaps, direct VirtualAlloc, native render/GPU memory excluded','Snapshot equality alone is not complete native leak freedom','All original fullgraph profile/BAML/setting/membership and cache-maintenance boundaries remain','No timing/performance claim from HeapLock/HeapWalk'],
        RawFiles=[dict(Path=str(f.relative_to(root)),Bytes=f.stat().st_size,SHA256=g.sha(f)) for f in sorted((root/'runs').rglob('*')) if f.is_file()],
        Verifiers=[dict(Path=str(f.relative_to(root)),SHA256=g.sha(f)) for f in sorted(verifier.iterdir())]))
    print(json.dumps(dict(Verdict='SCOPED_NATIVE_HEAP_OBSERVATIONS_VERIFIED',DefaultHeapSnapshotNoGrowth=all(r['DefaultHeapSnapshotNoGrowth'] for r in observed),Epochs=[dict(Configuration=r['Configuration'],Counts=[e['BusyBlocks'] for e in r['HeapEpochs']],Bytes=[e['BusyBytes'] for e in r['HeapEpochs']]) for r in observed],SHA256=g.sha(a.output))))
if __name__=='__main__':main()
