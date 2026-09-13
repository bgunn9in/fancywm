"""Verify real Win32Workspace and ThemeEngineManager repeated native lifetimes."""
import argparse
import collections
import copy
import csv
import hashlib
import json
from datetime import datetime
from pathlib import Path

def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def require(value,message):
    if not value: raise ValueError(message)
def check(rows):
    require(len(rows)==345 and rows[0]['Kind']=='Process' and rows[0]['Mode']=='providers','missing scenario/row')
    require(not any(r['Kind']=='Failure' for r in rows),'native failure')
    p=rows[0]; require(p['Architecture']=='X64','architecture')
    acquired=[r for r in rows if r['Kind']=='WorkspaceAcquired']; closed=[r for r in rows if r['Kind']=='WorkspaceClosed']
    require(len(acquired)==len(closed)==110,'workspace coverage')
    for a,c in zip(acquired,closed):
        require(a['Hwnd']==c['Hwnd'] and a['Hwnd']>0 and a['Thread']>0 and a['Desktop']==p['Desktop'],'native HWND identity')
        require(a['DisplayCount']>0 and a['VirtualDesktopManager']=='WinMan.Windows.Win32VirtualDesktopManager' and a['Workers']==3,'native provider acquisition')
        require(c['Destroyed'] and c['WorkersAlive']==0 and c['Errors']==0,'surviving HWND/worker or error')
    themes=[r for r in rows if r['Kind']=='ThemeClosed']
    require([r['Cycle'] for r in themes]==list(range(1,111)),'theme coverage')
    for r in themes:
        require(r['WatcherAcquired'] and r['ReloadApplied'] and r['WatcherReleased'] and r['StaleNotificationInert'],'theme native callback lifetime')
        require(r['Subscriptions']==r['Timers']==0 and r['WorkerRunning'] is False,'theme cleanup')
    epochs=[r for r in rows if r['Kind']=='Retention']
    require(len(epochs)==4 and {(r['Owner'],r['Epoch']) for r in epochs}=={(o,e) for o in ['workspace','theme'] for e in [1,2]},'missing epoch')
    for r in epochs:
        require(r['Cycles']==r['Epoch']*50 and r['References']==r['Cycles']*({'workspace':5,'theme':2}[r['Owner']]),'retention coverage')
        require(r['Live']==0 and r['DispatcherAlive'],'retained owner/stopped dispatcher')
        require(r['InitialUser']>0 and r['InitialUser']==r['User'] and r['InitialGdi']==r['Gdi'],'USER/GDI growth')
    shutdown=[r for r in rows if r['Kind']=='Closed']
    require(len(shutdown)==5 and all(r['DispatcherAlive'] for r in shutdown),'native shutdown coverage')
    exits=[r for r in rows if r['Kind']=='ApplicationExit']
    require(len(exits)==1 and exits[0]['DispatcherAlive'] and exits[0]['HooksCompleted'] and exits[0]['Closed']==5,'shutdown order')
    for kind in ['HookAcquired','HookStopped']:
        hooks=[r for r in rows if r['Kind']==kind]
        require(collections.Counter(r['Type'] for r in hooks)=={'LowLevelMouseHook':1,'LowLevelKeyboardHook':1},'DI hooks coverage')
        if kind=='HookAcquired': require(all(r['Hook']>0 and r['Thread']>0 and r['Desktop']==p['Desktop'] for r in hooks),'hook identity')
        else: require(all(r['Hook']==r['Thread']==0 and r['Completed'] for r in hooks),'hook cleanup')
def main():
    p=argparse.ArgumentParser(); p.add_argument('--snapshot',type=Path,required=True); p.add_argument('--output',type=Path,required=True); a=p.parse_args()
    require(not a.output.exists(),'receipt exists'); data=read(a.snapshot/'runs.json')
    runs=data['Processes']; require([(r['Configuration'],r['Mode']) for r in runs]==[('Debug','providers'),('Release','providers')],'process order')
    require(datetime.fromisoformat(runs[0]['EndedUtc'])<datetime.fromisoformat(runs[1]['StartedUtc']),'overlapping processes')
    require(len({r['Desktop'] for r in runs})==2,'reused desktop')
    for build in data['Builds']:
        require(build['ExitCode']==0,'build failure')
        for f in build['Files']: require((a.snapshot/f['Path']).stat().st_size==f['Bytes'] and sha(a.snapshot/f['Path'])==f['SHA256'],'binary provenance')
    with (a.snapshot/'manifest.csv').open(encoding='utf-8-sig',newline='') as f: sources=list(csv.DictReader(f))
    for row in sources: require(sha(a.snapshot/'source'/row['Path'])==row['SHA256'],'source provenance')
    allrows=[]
    for run in runs:
        config=run['Configuration']; root=a.snapshot/'runs'/f'{config}-providers'
        rows=[json.loads(line) for line in (root/'observations.jsonl').read_text().splitlines()]; check(rows); allrows.append(rows)
        require(rows[0]['Pid']==run['ProcessId'] and rows[0]['Desktop']==run['Desktop'],'process identity')
        require(run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run['InputSent'] and not run['DesktopSwitched'],'owned process teardown')
        require(sha(a.snapshot/'validation'/f'{config}-providers.log')==run['LogSHA256'],'raw log provenance')
        s=read(root/'summary.json')
        require(s['Verdict']=='PASS' and s['Closed']==5 and s['SurvivingHwnds']==[] and s['DispatcherStopped'] and s['HooksCompleted'] and s['ProviderDisposed'],'application shutdown')
        for n in range(1,111):
            theme=root/'themes'/f'{n:04d}'
            require((theme/'before.css').is_file() and 'owned native watcher change' in (theme/'owned.css').read_text() and 'owned post-disposal notification' in (theme/'owned.css').read_text(),'native file mutation evidence')
    controls=[]
    for case in ['missing-scenario','surviving-HWND','retained-owner','worker-survived','reload-not-applied','stale-callback-admitted']:
        rows=copy.deepcopy(allrows[0])
        if case=='missing-scenario': rows.pop(1)
        if case=='surviving-HWND': next(r for r in rows if r['Kind']=='WorkspaceClosed')['Destroyed']=False
        if case=='retained-owner': next(r for r in rows if r['Kind']=='Retention')['Live']=1
        if case=='worker-survived': next(r for r in rows if r['Kind']=='WorkspaceClosed')['WorkersAlive']=1
        if case=='reload-not-applied': next(r for r in rows if r['Kind']=='ThemeClosed')['ReloadApplied']=False
        if case=='stale-callback-admitted': next(r for r in rows if r['Kind']=='ThemeClosed')['StaleNotificationInert']=False
        try: check(rows)
        except ValueError as e: controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else: raise ValueError('negative accepted')
    result=dict(Verdict='PASS',Processes=2,ObservationRows=sum(map(len,allrows)),SourceFilesVerified=len(sources),NegativeControls=controls,
        NativeWorkspaceRetention='PASS',NativeThemeWatcherRetention='PASS',WholeIdStatus='IN_PROGRESS',FullStartupGraph=False,
        ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,
        Limits=['No MainWindow/full startup graph','USER/GDI and weak references do not prove native heap/GPU retention','Desktop enumeration failures retained as unsuccessful; acquired HWNDs verified directly'],
        RawFiles=[dict(Path=str(p.relative_to(a.snapshot)),SHA256=sha(p)) for p in sorted((a.snapshot/'runs').rglob('*')) if p.is_file()])
    with a.output.open('x',encoding='utf-8') as f: json.dump(result,f,indent=2)
    print(json.dumps(dict(Verdict='PASS',Processes=2,ObservationRows=result['ObservationRows'],NegativeControls=len(controls),SHA256=sha(a.output))))
if __name__=='__main__': main()
