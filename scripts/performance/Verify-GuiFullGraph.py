"""Verify instrumented native runs without relaxing the existing no-growth gate."""
import argparse, copy, importlib.util, json
from collections import Counter
from pathlib import Path
def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
g=module('graph','Verify-FullGraphLifetime.py');a=module('gui','Verify-GuiAttribution.py')
def analyze(rr,pid):
    live={};timers={};phases=[];api=Counter();event_windows=set()
    for r in rr:
        api[(r['Api'],r['Action'])]+=1
        h=r['Handle'];family=r['Family']
        if r['Action']=='acquire' and r['Result'] and family in ['Icon','Cursor','Menu','Accelerator','Hook','EventHook']:
            if h not in live:live[h]=r
        if r['Action']=='release' and r['Result'] and family!='Timer':live.pop(h,None)
        if family=='Timer':
            key=(r['Argument'],r['Thread'] if not r['Argument'] else 0,h)
            if r['Action']=='set' and r['Result']:timers[key]=r
            if r['Action']=='release' and r['Result']:timers.pop(key,None)
        if r['Action']=='event-create':event_windows.add(h)
        if r['Action']=='event-destroy':event_windows.discard(h)
        if r['Action']=='mark' and r['Phase'] in [9,19,29,999]:
            phase=r['Phase'];witness=[w for w in rr if w['Phase']==phase and w['Action']=='witness']
            a.require(all(w['Result']==pid for w in witness),'foreign native window witness')
            phases.append(dict(Phase=phase,GDI=r['Argument'],USER=r['Result'],SeenUnreleasedByFamily=dict(Counter(v['Family'] for v in live.values())),
                SeenUnreleased=[dict(Handle=k,Api=v['Api'],Family=v['Family'],Phase=v['Phase'],Sequence=v['Sequence'],Stack=v['Stack']) for k,v in sorted(live.items())],
                ApiObservedTimers=len(timers),OwnWindowsFromEvents=len(event_windows),OwnWindowWitnesses=[dict(Hwnd=w['Handle'],Thread=w['Argument'],ClassAtom=w['Flags']) for w in witness]))
    return dict(ApiCounts=[dict(Api=k[0],Action=k[1],Count=v) for k,v in sorted(api.items())],Phases=phases,
        Limits=['Seen-unreleased API handles are not a complete USER handle table','Nested aliases, shared loads, implicit kernel cleanup and direct syscall paths are not silently counted as leaks','Native window events are own-PID filtered and asynchronous','Instrumentation timing is not performance evidence'])
def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--extended',action='store_true');args=p.parse_args();root=args.snapshot;a.require(not args.output.exists(),'receipt exists')
    source=g.provenance(root,True);runs=g.read(root/'runs.json');a.require([(r['Configuration'],r['Mode']) for r in runs['Processes']]==[('Debug','graceful'),('Release','graceful')],'configuration/process coverage')
    a.require(all(r['ExitCode']==0 for r in runs['Builds']) and len(runs['Builds'])==4,'build coverage')
    observer=g.read(root/'gui-trace-provenance.json');observed=[];allrows=[];controls=[]
    for run in runs['Processes']:
        config=run['Configuration'];folder=root/'runs'/f'{config}-graceful';rr=g.rows(folder/'observations.jsonl');allrows.append(rr)
        g.check(rr,'graceful',True,50,False)
        contract=g.single(rr,'GuiCounterContract');a.require((contract['GdiFlag'],contract['UserFlag'],contract['Schema'])==(0,1,2),'GUI label contract')
        control=g.single(rr,'GuiCounterControl');a.require(control['Result']==0 and control['Bitmaps']==control['Menus']==control['Icons']==24,'native calibration result')
        a.require(run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run.get('TimedOut') and not run['InputSent'] and not run['DesktopSwitched'],'native process teardown')
        a.require(g.sha(root/'validation'/f'{config}-graceful.log')==run['LogSHA256'],'native raw log')
        a.require(g.single(rr,'Process')['Pid']==run['ProcessId'] and g.single(rr,'Process')['Desktop']==run['Desktop'],'process identity')
        summary=g.read(folder/'summary.json');a.require(summary['SurvivingHwnds']==[] and all(summary[k] for k in ['DispatcherStopped','MainCompleted','WorkspaceWorkersStopped','MicaStopped','HooksCompleted','StartupReturned','ProviderDisposed','FullGraphWithControlledSetting']),'graceful completion')
        a.require(summary['SettingWrites']==2 and summary['OriginalArranging']==summary['FinalArranging'] and summary['CrashCleanups']==0 and summary['ProgramExits']==1,'graceful globals/order')
        a.require(g.read(folder/'target-exit.json')['ExitCode']==0 and g.read(folder/'targets/cleanup.json')['AllDestroyed'],'owned target cleanup')
        native=a.rows(folder/'gui-api.jsonl');calibration=a.calibration(native,installed=46 if args.extended else 30)
        a.require(g.sha(root/'binaries'/config/'FancyWM.GuiTrace.dll')==observer['SHA256'],'observer binary identity')
        epochs=[r for r in rr if r['Kind']=='Epoch'];markers=[r for r in native if r['Action']=='mark' and r['Phase'] in [9,19,29]]
        a.require(len(markers)==3 and all((r['USER'],r['GDI'])==(m['Result'],m['Argument']) for r,m in zip(epochs,markers)),'independent native/managed counter match')
        try:g.check(rr,'graceful',True,50,True);gui=True;reason=None
        except ValueError as e:gui=False;reason=str(e)
        observed.append(dict(Configuration=config,Pid=run['ProcessId'],GuiCriterionPassed=gui,GuiFailure=reason,Calibration=calibration,Epochs=epochs,NativeAttribution=analyze(native,run['ProcessId'])))
    for case in ['missing-scenario','retained-owner','surviving-HWND','stopped-dispatcher']:
        bad=copy.deepcopy(allrows[0])
        if case=='missing-scenario':bad.remove(next(r for r in bad if r['Kind']=='Lifetime'))
        if case=='retained-owner':next(r for r in bad if r['Kind']=='Epoch')['Retained']={'WindowNode':1}
        if case=='surviving-HWND':next(r for r in bad if r['Kind']=='Lifetime')['SurvivingHwnds']=[123]
        if case=='stopped-dispatcher':next(r for r in bad if r['Kind']=='Epoch')['DispatcherAlive']=False
        try:g.check(bad,'graceful',True,50,False)
        except ValueError as e:controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    a.write(args.output,dict(Verdict='OBSERVATIONS_VERIFIED',VerificationPassed=True,GuiCriterionPassed=all(r['GuiCriterionPassed'] for r in observed),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,Processes=2,SourceFilesVerified=source,ObservationRows=sum(map(len,allrows)),Configurations=observed,NegativeControls=controls,
        RawFiles=[dict(Path=str(f.relative_to(root)),Bytes=f.stat().st_size,SHA256=g.sha(f)) for f in sorted((root/'runs').rglob('*')) if f.is_file()]))
    print(json.dumps(dict(Verdict='OBSERVATIONS_VERIFIED',GuiCriterionPassed=all(r['GuiCriterionPassed'] for r in observed),SHA256=g.sha(args.output))))
if __name__=='__main__':main()
