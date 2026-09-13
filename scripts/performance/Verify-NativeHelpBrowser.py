"""Strict scoped native HelpPage/WebView2 lifetime verification."""
import argparse
import copy
import csv
import hashlib
import json
from datetime import datetime
from pathlib import Path

def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def require(v,m):
    if not v: raise ValueError(m)
def check(rows):
    require(len(rows)==118 and rows[0]['Kind']=='Process' and rows[0]['Mode']=='browser','missing native scenario')
    require(not any(r['Kind']=='Failure' for r in rows),'native failure')
    envs=[r for r in rows if r['Kind']=='BrowserEnvironment']; require(len(envs)==1,'browser environment')
    env=envs[0]; require(env['Pid']>0 and env['SharedKeeper'] and env['KeeperHwnd']>0 and env['RuntimeVersion'] and env['Executable'].endswith('msedgewebview2.exe'),'native browser identity')
    lives=[r for r in rows if r['Kind']=='BrowserLifetime']; require([r['Cycle'] for r in lives]==list(range(1,111)),'native lifetime coverage')
    for r in lives:
        require(r['Hwnd']>0 and r['BrowserPid']==env['Pid'],'native host/browser identity')
        require(all(r[k] is True for k in ['NativeScriptCompleted','CachedPageReused','HostDestroyed','SourceDisposed','BrowserDisposed','StaleNavigationInert']),'native owner not released')
    epochs=[r for r in rows if r['Kind']=='Retention']; require([r['Epoch'] for r in epochs]==[1,2],'retention epoch coverage')
    before=[r for r in rows if r['Kind']=='BeforeFrameworkMaintenance']; require([r['Epoch'] for r in before]==[1,2],'missing pre-maintenance observations')
    for r in before:
        require(r['DispatcherAlive'] and r['Cycles']==r['Epoch']*50 and r['Live'] in [0,2],'unbounded pre-maintenance retention')
        if r['Live']==2:
            require(r['LiveIdentities']==[{'Index':r['Cycles']*6-6,'Type':'FancyWM.Windows.SettingsWindow'},
                {'Index':r['Cycles']*6-4,'Type':'FancyWM.ViewModels.SettingsViewModel'}],'unexpected retained owner')
    for r in epochs:
        require(r['Owner']=='help-browser' and r['Cycles']==r['Epoch']*50 and r['References']==r['Cycles']*6,'weak coverage')
        require(r['Live']==0 and r['DispatcherAlive'],'retained owner or stopped dispatcher')
        require(r['InitialUser']>0 and r['User']==r['InitialUser'] and r['Gdi']==r['InitialGdi'],'USER/GDI growth')
    exits=[r for r in rows if r['Kind']=='BrowserProcessExited']; require(len(exits)==1 and exits[0]['Pid']==env['Pid'] and exits[0]['Reason']=='Normal' and exits[0]['DispatcherAlive'],'browser runtime teardown')
    require(rows[-1]['Kind']=='BrowserApplicationExit' and rows[-1]['DispatcherAlive'] and rows[-1]['HooksAcquired'] is False,'application shutdown scope/order')
    require(rows.index(exits[0])>rows.index(epochs[-1]),'browser exited before live retention')
def main():
    p=argparse.ArgumentParser(); p.add_argument('--snapshot',type=Path,required=True); p.add_argument('--output',type=Path,required=True); a=p.parse_args()
    require(not a.output.exists(),'receipt exists'); data=read(a.snapshot/'runs.json'); runs=data['Processes']
    require([(r['Configuration'],r['Mode']) for r in runs]==[('Debug','browser'),('Release','browser')],'Debug/Release process order')
    require(datetime.fromisoformat(runs[0]['EndedUtc'])<datetime.fromisoformat(runs[1]['StartedUtc']),'overlap')
    for build in data['Builds']:
        require(build['ExitCode']==0,'build failed')
        for f in build['Files']: require((a.snapshot/f['Path']).stat().st_size==f['Bytes'] and sha(a.snapshot/f['Path'])==f['SHA256'],'binary provenance')
    with (a.snapshot/'manifest.csv').open(encoding='utf-8-sig',newline='') as f: sources=list(csv.DictReader(f))
    for r in sources: require(sha(a.snapshot/'source'/r['Path'])==r['SHA256'],'source provenance')
    allrows=[]
    for run in runs:
        root=a.snapshot/'runs'/f"{run['Configuration']}-browser"
        rows=[json.loads(line) for line in (root/'observations.jsonl').read_text().splitlines()]; check(rows); allrows.append(rows)
        require(rows[0]['Pid']==run['ProcessId'] and rows[0]['Desktop']==run['Desktop'],'native process identity')
        require(run['ExitCode']==0 and not run['HooksAcquired'] and not run['InputSent'] and not run['DesktopSwitched'] and run['OffscreenOwnedWindowsOnly'],'ownership boundary')
        require(run['ForegroundBefore']==run['ForegroundAfter'],'foreground changed')
        require(sha(a.snapshot/'validation'/f"{run['Configuration']}-browser.log")==run['LogSHA256'],'raw log provenance')
        summary=read(root/'summary.json'); browser=read(root/'browser-summary.json')
        require(summary['Verdict']=='PASS' and summary['DispatcherStopped'] and summary['ProviderDisposed'] and summary['SurvivingHwnds']==[] and not summary['HooksAcquired'],'application teardown')
        require(browser['Exited'] and browser['KeeperDestroyed'] and browser['Cycles']==110 and browser['SharedKeeper'],'browser process collection teardown')
        require(Path(browser['DataFolder']).resolve()==(root/'owned-browser-data').resolve(),'foreign browser data')
    controls=[]
    for case in ['missing-scenario','surviving-HWND','retained-owner','browser-not-disposed','browser-process-not-exited','no-native-script']:
        rows=copy.deepcopy(allrows[0])
        if case=='missing-scenario': rows.pop(2)
        if case=='surviving-HWND': next(r for r in rows if r['Kind']=='BrowserLifetime')['HostDestroyed']=False
        if case=='retained-owner': next(r for r in rows if r['Kind']=='Retention')['Live']=1
        if case=='browser-not-disposed': next(r for r in rows if r['Kind']=='BrowserLifetime')['BrowserDisposed']=False
        if case=='browser-process-not-exited': next(r for r in rows if r['Kind']=='BrowserProcessExited')['Reason']='Failed'
        if case=='no-native-script': next(r for r in rows if r['Kind']=='BrowserLifetime')['NativeScriptCompleted']=False
        try: check(rows)
        except ValueError as e: controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else: raise ValueError('negative accepted')
    raw=[p for p in (a.snapshot/'runs').rglob('*') if p.is_file() and 'owned-browser-data' not in p.parts]
    result=dict(Verdict='PASS',Processes=2,ObservationRows=sum(map(len,allrows)),NativeHelpPageRetention='PASS',SourceFilesVerified=len(sources),NegativeControls=controls,
        ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS',FullStartupGraph=False,
        Limits=['Zero weak references is measured after three ordinary public CollectionViewSource.GetDefaultView activities and live dispatcher settling; pre-maintenance last SettingsWindow/viewmodel retention is reported separately',
            'Shared browser keeper remains alive during 100 post-warmup page/controller cycles, then normally exits','Local about:blank replaces remote help content','No native heap/GPU or physical presentation claim','Application.Shutdown, not full Startup.AppMain/App.Terminate'],
        RawFiles=[dict(Path=str(p.relative_to(a.snapshot)),SHA256=sha(p)) for p in sorted(raw)])
    with a.output.open('x',encoding='utf-8') as f: json.dump(result,f,indent=2)
    print(json.dumps(dict(Verdict='PASS',Processes=2,ObservationRows=result['ObservationRows'],NegativeControls=len(controls),SHA256=sha(a.output))))
if __name__=='__main__': main()
