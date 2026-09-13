"""Strict, exclusive verification of scoped full startup graph receipts."""
import argparse, copy, csv, hashlib, json
from datetime import datetime
from pathlib import Path

def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def require(v,m):
    if not v: raise ValueError(m)
def rows(p): return [json.loads(s) for s in p.read_text().splitlines()]
def single(rr,kind):
    found=[r for r in rr if r['Kind']==kind]; require(len(found)==1,'missing/duplicate '+kind); return found[0]
def check(rr,mode,controlled=False,warmup=10,check_gui=True,dynamic_graph=False):
    p=single(rr,'Process'); g=single(rr,'GraphReady')
    require(p['Mode']==mode and p['Architecture']=='X64' and p['Desktop'].startswith('FWM_OWNED_'),'process identity')
    require(not any(r['Kind'] in ['Failure','IdleFailure'] for r in rr),'native failure')
    require(g['Pid']==p['Pid'] and g['Desktop']==p['Desktop'] and len(g['Hwnds'])>=4 and len(set(g['Hwnds']))==len(g['Hwnds']) and min(g['Hwnds'])>0,'native graph handles')
    require(g['StartupAppMain'] and g['ControlledSettingWrite'] and g['Services']==['FancyWM.TilingService'] and g['Workspace']=='WinMan.Windows.Win32Workspace' and g['VirtualDesktopManager']=='WinMan.Windows.Win32VirtualDesktopManager','full graph boundary')
    require(g['MouseHook']>0 and g['KeyboardHook']>0 and g['MouseDesktop']==g['KeyboardDesktop']==p['Desktop'] and g['AnimationRunning'] and g['MicaRunning'],'real providers')
    writes=[r for r in rr if r['Kind']=='WindowArrangingWriteIntercepted']
    require([r['Call'] for r in writes]==list(range(1,len(writes)+1)) and writes[0]['Requested'] is False and all(r['NativeValue']==p['OriginalArranging'] for r in writes),'global setting changed')
    require(len(writes)==(2 if mode in ['graceful','handler'] else 1),'setting lifetime coverage')
    if len(writes)==2: require(writes[1]['Requested']==p['OriginalArranging'],'restore intent')
    if mode=='graceful':
        lifetimes=[r for r in rr if r['Kind']=='Lifetime']; settings=[r for r in rr if r['Kind']=='SettingsClosed']
        require(len(lifetimes)==len(settings)==warmup+100,'missing lifetime scenario')
        target=single(rr,'TargetReady'); require(target['Desktop']==p['Desktop'] and target['Pid']!=p['Pid'],'target identity')
        expected=[(e,c) for e,n in [(0,warmup),(1,50),(2,50)] for c in range(n)]
        require([(r['Epoch'],r['Cycle']) for r in lifetimes]==expected,'epoch coverage')
        for r in lifetimes:
            require(r['TargetPid']==target['Pid'] and r['Count']==[1,10,50][r['Cycle']%3] and len(r['Hwnds'])==r['Count'] and min(r['Hwnds'])>0 and len(set(r['Hwnds']))==r['Count'],'target count/PID/HWND')
            require(r['SurvivingHwnds']==[] and r['Captured']==(3*r['Count']+4 if controlled else r['Count']+3) and r['DispatcherAlive'],'surviving HWND/owner coverage')
            require(r['BackendNodes']==0 and r['VirtualDesktopLayoutAvailable'] is False,'unsupported tiling claim')
        require(all(r['Hwnd']>0 and r['Destroyed'] and r['DispatcherAlive'] for r in settings),'settings close')
        epochs=[r for r in rr if r['Kind']=='Epoch']
        require([r['Epoch'] for r in epochs]==[0,1,2] and [r['Captured'] for r in epochs]==(([3161,6322,9483] if warmup==50 else [592,3753,6914]) if controlled else ([1137,2274,3411] if warmup==50 else [214,1351,2488])) and [r['Cycles'] for r in epochs]==[warmup,50,50],'retention coverage')
        require(all(r['Retained']=={} and r['DispatcherAlive'] and r['ViewMaintenance'] and r['FixedStartupOwnersIntentionallyAlive'] for r in epochs),'retained owner/stopped dispatcher')
        samples=[r for r in rr if r['Kind']=='GuiSample']; require([(r['Epoch'],r['Sample']) for r in samples]==[(e,i) for e in range(3) for i in range(20)],'GUI sample coverage')
        warm=[r for r in samples if r['Epoch']==0]
        for e in range(3) if check_gui else []:
            tail=[r for r in samples if r['Epoch']==e][-5:]
            require(all(r['DispatcherAlive'] for r in tail) and len({(r['USER'],r['GDI']) for r in tail})==1,'GUI samples not settled')
            require(epochs[e]['USER']==tail[-1]['USER'] and epochs[e]['GDI']==tail[-1]['GDI'] and epochs[e]['USER']>0 and epochs[e]['GDI']>0,'GUI epoch/sample mismatch')
            require(epochs[e]['USER']<=min(r['USER'] for r in warm) and epochs[e]['GDI']<=min(r['GDI'] for r in warm),'USER/GDI growth')
        if controlled:
            adapter=single(rr,'MembershipAdapter'); removed=single(rr,'MembershipAdapterRemoved')
            require(adapter['TargetPid']==target['Pid'] and adapter['Desktop']==p['Desktop'] and adapter['NativeCurrentDesktop'] and adapter['NativeWindowMembership'] is False and removed['Calls']>0,'membership adapter boundary')
            require(all(r['ControlledMembership'] and r['MembershipCalls']>0 for r in lifetimes),'missing controlled tiling workload')
        passive=[r for r in rr if r['Kind']=='PassiveRetention']; require([r['Epoch'] for r in passive]==[0,1,2],'missing passive samples')
        require(all(r['Retained']=={'SettingsWindow':1,'SettingsViewModel':1,'PageNavigation':1} for r in passive),'passive retention boundary changed')
        closed=[r for r in rr if r['Kind']=='Closed']; exit=single(rr,'ApplicationExit')
        require(len(closed)==4 and all(r['DispatcherAlive'] for r in closed) and exit['Closed']==4 and exit['DispatcherAlive'] and exit['MainCompleted'] and exit['AnimationCompleted'] and exit['HooksCompleted'],'graceful cleanup/order')
        if dynamic_graph:
            acquired=[r for r in rr if r['Kind']=='GraphWindowAcquired']; ended=[r for r in rr if r['Kind']=='GraphWindowClosed']
            require(len(acquired)>=4 and [r['Id'] for r in acquired]==list(range(1,len(acquired)+1)) and len(ended)==len(acquired),'graph generation coverage')
            require(len({r['Id'] for r in ended})==len(ended),'duplicate graph generation close')
            for a in acquired:
                e=next((r for r in ended if r['Id']==a['Id']),None)
                require(e is not None and a['Hwnd']==e['Hwnd']>0 and a['Type']==e['Type'] and a['Pid']==p['Pid'] and a['DispatcherAlive'] and e['DispatcherAlive'] and rr.index(a)<rr.index(e)<rr.index(exit),'native graph generation lifetime/order')
            snapshots=[r for r in rr if r['Kind']=='GraphWindowCheckpoint']
            require([r['Label'] for r in snapshots]==['0','1','2','exit','shutdown'],'graph generation checkpoint coverage')
            for snap in snapshots:
                require(snap['Acquired']-snap['Closed']==len(snap['Active'])==snap['ApplicationGraphWindows'],'application graph membership')
                index=rr.index(snap);prior_acquired={r['Id']:r for r in acquired if rr.index(r)<index};prior_closed={r['Id'] for r in ended if rr.index(r)<index}
                require(snap['Acquired']==len(prior_acquired) and snap['Closed']==len(prior_closed) and {r['Id'] for r in snap['Active']}==prior_acquired.keys()-prior_closed,'native generation checkpoint history')
                require(all(r['Hwnd']==prior_acquired[r['Id']]['Hwnd'] and r['Type']==prior_acquired[r['Id']]['Type'] for r in snap['Active']),'native active generation identity')
                require(all(r['NativeAlive'] and r['Hwnd']>0 for r in snap['Active']),'dead active graph generation')
                if snap['Label'] in ['exit','shutdown']:require(snap['Acquired']==snap['Closed']==len(acquired) and snap['Active']==snap['SurvivingNativeHwnds']==[],'surviving native graph generation')
            main=[r for r in ended if r['Type']=='FancyWM.MainWindow'];require(len(main)==1 and rr.index(single(rr,'TerminateRequested'))<rr.index(main[0])<rr.index(exit)<rr.index(single(rr,'ProgramExit')),'dynamic main shutdown order')
        else:
            require(rr.index(single(rr,'TerminateRequested'))<rr.index(closed[0])<rr.index(exit)<rr.index(single(rr,'ProgramExit')),'shutdown order')
        require(single(rr,'ProgramExit')['Restart'] is False and not any(r['Kind'] in ['ErrorDialog','CrashCleanup'] for r in rr),'unexpected shutdown path')
    else:
        require(single(rr,'CrashEntered')['DispatcherAlive'],'crash did not enter live graph')
        require(not any(r['Kind'] in ['Epoch','ApplicationExit','ProgramExit'] for r in rr),'crash promoted to graceful')
        if mode=='handler':
            dialog=single(rr,'ErrorDialog'); require(dialog['Hwnd']>0 and dialog['OriginalException'] and dialog['RestartDisabled'] and dialog['Desktop']==p['Desktop'],'production error dialog identity')
            require(single(rr,'ErrorDialogClosed')['Hwnd']==dialog['Hwnd'] and single(rr,'ErrorDialogClosed')['Restart'] is False,'dialog quit')
            require(any(r['Kind']=='CrashCleanup' for r in rr) and sum(r['Kind']=='Closed' for r in rr)==3,'partial managed handler cleanup')
        else: require(not any(r['Kind'] in ['Closed','ErrorDialog','CrashCleanup'] for r in rr),'unexpected managed crash cleanup')

def provenance(root,controlled=False):
    with (root/'manifest.csv').open(encoding='utf-8-sig',newline='') as f: source=list(csv.DictReader(f))
    for r in source: require(sha(root/'source'/r['Path'])==r['SHA256'],'frozen source provenance')
    adapter=read(root/'adapters.json'); require(adapter['ChangedFiles']==['FancyWM/Startup.cs','FancyWM/Utilities/SystemParameters.cs']+(['winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktop.cs'] if controlled else []) and len(adapter['Changes'])==(7 if controlled else 5),'adapter boundary')
    expected={r['Path'].replace('\\','/'): (root/'source'/r['Path']).read_bytes() for r in source}
    for c in adapter['Changes']:
        raw=expected[c['Path']]; require(hashlib.sha256(raw).hexdigest().upper()==c['BeforeSHA256'],'adapter before hash')
        old=c['Old'].replace('\n','\r\n').encode() if b'\r\n' in raw else c['Old'].encode(); new=c['New'].replace('\n','\r\n').encode() if b'\r\n' in raw else c['New'].encode()
        require(raw.count(old)==1,'adapter match'); expected[c['Path']]=raw.replace(old,new); require(hashlib.sha256(expected[c['Path']]).hexdigest().upper()==c['AfterSHA256'],'adapter after hash')
    for rel,data in expected.items(): require((root/'work'/rel).read_bytes()==data,'extra build-copy source delta')
    for config in ['Debug','Release']:
        for r in read(root/f'{config}-binary-manifest.json'): require((root/r['Path']).stat().st_size==r['Bytes'] and sha(root/r['Path'])==r['SHA256'],'binary provenance')
    return len(source)

def main():
    p=argparse.ArgumentParser(); p.add_argument('--snapshot',type=Path,required=True); p.add_argument('--output',type=Path,required=True); p.add_argument('--controlled-membership',action='store_true'); p.add_argument('--warmup',type=int,choices=[10,50],default=10); a=p.parse_args(); require(not a.output.exists(),'receipt exists')
    data=read(a.snapshot/'runs.json'); runs=data['Processes']; modes=['graceful','failfast','terminate','handler']
    require([(r['Configuration'],r['Mode']) for r in runs]==[(c,m) for c in ['Debug','Release'] for m in modes],'process order')
    require(len({r['Desktop'] for r in runs})==8 and all(datetime.fromisoformat(x['EndedUtc'])<datetime.fromisoformat(y['StartedUtc']) for x,y in zip(runs,runs[1:])),'process isolation')
    require(all(b['ExitCode']==0 for b in data['Builds']) and len(data['Builds'])==4,'build failure')
    sources=provenance(a.snapshot,a.controlled_membership); allrows=[]
    for run in runs:
        config,mode=run['Configuration'],run['Mode']; root=a.snapshot/'runs'/f'{config}-{mode}'; rr=rows(root/'observations.jsonl'); check(rr,mode,a.controlled_membership,a.warmup); allrows.append(rr)
        require(single(rr,'Process')['Pid']==run['ProcessId'] and single(rr,'Process')['Desktop']==run['Desktop'],'process receipt identity')
        require(run['OwnedDesktopHandleClosed'] and not run.get('TimedOut') and not run['InputSent'] and not run['DesktopSwitched'] and run['DesktopAfterExit']['Rows']==[],'owned process teardown')
        require(sha(a.snapshot/'validation'/f'{config}-{mode}.log')==run['LogSHA256'],'raw log hash')
        if mode=='graceful':
            s=read(root/'summary.json'); require(run['ExitCode']==0 and s['Verdict']=='PASS' and s['SurvivingHwnds']==[] and s['Closed']==4 and s['UnmodifiedStartup'] is False,'graceful native summary')
            require(all(s[k] for k in ['StartupReturned','DispatcherStopped','ProviderDisposed','HooksCompleted','MainCompleted','WorkspaceWorkersStopped','MicaStopped','FullGraphWithControlledSetting']),'surviving provider')
            require(s['SettingWrites']==2 and s['OriginalArranging']==s['FinalArranging'] and s['CrashCleanups']==0 and s['ProgramExits']==1,'shutdown globals/order')
            require(read(root/'target-exit.json')['ExitCode']==0 and read(root/'targets/cleanup.json')['AllDestroyed'],'target cleanup')
        else:
            require(run['ExitCode']=={'failfast':0x80131623,'terminate':0xE000F016,'handler':0xE0434352}[mode] and run['SurvivingHwnds']==[] and not run['ManagedProcessExitMarker'],'abrupt native exit')
            require(len(run['AliveBeforeCrash'])==len(single(rr,'GraphReady')['Hwnds']) and all(h['Pid']==run['ProcessId'] for h in run['AliveBeforeCrash']),'native pre-crash identity')
            if mode=='handler':
                raw=(a.snapshot/'validation'/f'{config}-{mode}.log').read_bytes(); require(b'Program+OwnedFullGraphException' in raw and b'FancyWM.App.HandleException' in raw and b'FancyWM.App.OnUnhandledException' in raw and b'NullReferenceException' not in raw,'original exception/rethrow')
    controls=[]
    for case in ['missing-scenario','surviving-HWND','retained-owner','global-setting-changed','foreign-target','worker-not-live','unsupported-tiling-claim','missing-handler-dialog']:
        mode='handler' if case=='missing-handler-dialog' else 'graceful'; rr=copy.deepcopy(allrows[3 if mode=='handler' else 0])
        if case=='missing-scenario': rr.remove(next(r for r in rr if r['Kind']=='Lifetime'))
        if case=='surviving-HWND': next(r for r in rr if r['Kind']=='Lifetime')['SurvivingHwnds']=[123]
        if case=='retained-owner': next(r for r in rr if r['Kind']=='Epoch')['Retained']={'SettingsWindow':1}
        if case=='global-setting-changed': next(r for r in rr if r['Kind']=='WindowArrangingWriteIntercepted')['NativeValue']=not rr[0]['OriginalArranging']
        if case=='foreign-target': next(r for r in rr if r['Kind']=='Lifetime')['TargetPid']=rr[0]['Pid']
        if case=='worker-not-live': single(rr,'GraphReady')['MicaRunning']=False
        if case=='unsupported-tiling-claim': next(r for r in rr if r['Kind']=='Lifetime')['VirtualDesktopLayoutAvailable']=True
        if case=='missing-handler-dialog': rr.remove(single(rr,'ErrorDialog'))
        try: check(rr,mode,a.controlled_membership,a.warmup)
        except ValueError as e: controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else: raise ValueError('negative control accepted')
    receipt=dict(Verdict='PASS',Scope='Full Startup/AppMain graph with test-copy adapters; workspace/settings workload',Processes=8,ObservationRows=sum(map(len,allrows)),SourceFilesVerified=sources,NegativeControls=controls,
        WholeIdStatus='IN_PROGRESS',UnmodifiedStartup=False,TilingWorkload=a.controlled_membership,ControlledTargetMembership=a.controlled_membership,Warmup=a.warmup,ManagedHardCrashCompletion=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,
        Limits=['Own profile, exact BAML URI, intercepted WindowArranging write, 1500ms post-App.Run delay','No Explorer virtual-desktop membership for private-desktop targets; no WindowNode/layout workload','Passive last SettingsWindow/view-model/page navigator retained; post-public-WPF-maintenance weak references all zero','Fixed startup owners intentionally alive during epochs','Crash handler closes three windows; OS reclamation is separate from managed completion','No native heap/GPU/presentation claim'],
        RawFiles=[dict(Path=str(f.relative_to(a.snapshot)),Bytes=f.stat().st_size,SHA256=sha(f)) for f in sorted((a.snapshot/'runs').rglob('*')) if f.is_file()])
    with a.output.open('x',encoding='utf-8') as f: json.dump(receipt,f,indent=2)
    print(json.dumps(dict(Verdict='PASS',Rows=receipt['ObservationRows'],NegativeControls=len(controls),SHA256=sha(a.output))))
if __name__=='__main__': main()
