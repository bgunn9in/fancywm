"""Verify either the controlled tiling retention or current-source hard crashes."""
import argparse, copy, importlib.util, json
from pathlib import Path
spec=importlib.util.spec_from_file_location('graph',Path(__file__).with_name('Verify-FullGraphLifetime.py'));g=importlib.util.module_from_spec(spec);spec.loader.exec_module(g)
def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--scope',choices=['retention','crash'],required=True);a=p.parse_args();g.require(not a.output.exists(),'receipt exists')
    source=g.provenance(a.snapshot,True); data=g.read(a.snapshot/'runs.json'); selected=[r for r in data['Processes'] if (r['Mode']=='graceful')==(a.scope=='retention')]
    modes=['graceful'] if a.scope=='retention' else ['failfast','terminate','handler'];g.require([(r['Configuration'],r['Mode']) for r in selected]==[(c,m) for c in ['Debug','Release'] for m in modes],'process coverage/order')
    allrows=[]
    for run in selected:
        c,m=run['Configuration'],run['Mode'];root=a.snapshot/'runs'/f'{c}-{m}';rr=g.rows(root/'observations.jsonl');g.check(rr,m,True,50);allrows.append(rr)
        g.require(g.single(rr,'Process')['Pid']==run['ProcessId'] and g.single(rr,'Process')['Desktop']==run['Desktop'],'native identity')
        g.require(run['OwnedDesktopHandleClosed'] and not run.get('TimedOut') and not run['InputSent'] and not run['DesktopSwitched'],'owned teardown')
        g.require(g.sha(a.snapshot/'validation'/f'{c}-{m}.log')==run['LogSHA256'],'raw log hash')
        if m=='graceful':
            s=g.read(root/'summary.json');g.require(run['ExitCode']==0 and s['Verdict']=='PASS' and s['SurvivingHwnds']==[] and all(s[k] for k in ['DispatcherStopped','MainCompleted','WorkspaceWorkersStopped','MicaStopped','HooksCompleted']),'native summary')
            inv=[r for r in rr if r['Kind']=='NativeInventory'];g.require([r['Epoch'] for r in inv]==[0,1,2] and all(r['WorkspaceWindows']==[] for r in inv),'workspace native inventory')
            g.require(all(r['Windows']==inv[0]['Windows'] for r in inv),'fixed startup HWND inventory changed')
        else:
            g.require(run['ExitCode']=={'failfast':0x80131623,'terminate':0xE000F016,'handler':0xE0434352}[m] and run['SurvivingHwnds']==[] and not run['ManagedProcessExitMarker'],'abrupt native exit')
            if m=='handler':
                raw=(a.snapshot/'validation'/f'{c}-{m}.log').read_bytes();g.require(b'Program+OwnedFullGraphException' in raw and b'FancyWM.App.HandleException' in raw and b'FancyWM.App.OnUnhandledException' in raw and b'NullReferenceException' not in raw,'original handler exception')
    controls=[]
    cases=['missing-scenario','retained-owner','surviving-HWND','GDI-growth'] if a.scope=='retention' else ['missing-handler-dialog','wrong-native-hook','global-setting-change']
    for case in cases:
        rr=copy.deepcopy(allrows[0 if a.scope=='retention' else 2]); mode='graceful' if a.scope=='retention' else 'handler'
        if case=='missing-scenario':rr.remove(next(r for r in rr if r['Kind']=='Lifetime'))
        if case=='retained-owner':next(r for r in rr if r['Kind']=='Epoch')['Retained']={'WindowReference':1}
        if case=='surviving-HWND':next(r for r in rr if r['Kind']=='Lifetime')['SurvivingHwnds']=[1]
        if case=='GDI-growth':next(r for r in rr if r['Kind']=='Epoch' and r['Epoch']==2)['GDI']+=100
        if case=='missing-handler-dialog':rr.remove(g.single(rr,'ErrorDialog'))
        if case=='wrong-native-hook':g.single(rr,'GraphReady')['MouseHook']=0
        if case=='global-setting-change':next(r for r in rr if r['Kind']=='WindowArrangingWriteIntercepted')['NativeValue']=not rr[0]['OriginalArranging']
        try:g.check(rr,mode,True,50)
        except ValueError as e:controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted')
    receipt=dict(Verdict='PASS',Scope=a.scope,Processes=len(selected),SourceFilesVerified=source,Observations=sum(map(len,allrows)),NegativeControls=controls,WholeIdStatus='IN_PROGRESS',ControlledTargetMembership=True,UnmodifiedStartup=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,ManagedHardCrashCompletion=False,
        Limits=['Own profile, setting-write interception, BAML resolution, 1500ms post-App.Run gap','Native target membership is controlled, native current-desktop identity remains real','Post-maintenance weak/USER/GDI scope; no native heap/GPU/presentation claim','OS crash reclamation is separate from partial managed handler cleanup'],
        RawFiles=[dict(Path=str(f.relative_to(a.snapshot)),SHA256=g.sha(f),Bytes=f.stat().st_size) for f in sorted((a.snapshot/'runs').rglob('*')) if f.is_file()])
    with a.output.open('x',encoding='utf-8') as f:json.dump(receipt,f,indent=2)
    print(json.dumps(dict(Verdict='PASS',Scope=a.scope,SHA256=g.sha(a.output))))
if __name__=='__main__':main()
