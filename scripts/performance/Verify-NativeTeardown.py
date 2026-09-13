"""Strict verification of auxiliary windows, native hooks and abrupt termination."""
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
def check(rows,mode):
    require(not any(r['Kind']=='Failure' for r in rows),'production/fixture failure')
    require(sum(r['Kind']=='Process' for r in rows)==1,'process provenance')
    process=rows[0]; require(process['Architecture']=='X64' and process['Mode']==mode,'runtime controls')
    if mode=='graceful':
        require(len(rows)==901,'missing/duplicate graceful observation')
        windows=[r for r in rows if r['Kind']=='WindowLifetime']
        require(collections.Counter(r['Owner'] for r in windows)=={o:110 for o in ['startup','about','error','message']},'native window lifetime coverage')
        require(all(r['Hwnd']>0 and r['Closed']==1 and r['Destroyed'] is True and r['SourceDisposed'] is True for r in windows),'surviving window/source')
        epochs=[r for r in rows if r['Kind']=='Retention']
        require(len(epochs)==10 and {(r['Owner'],r['Epoch']) for r in epochs}=={(o,e) for o in ['startup','about','error','message','native-hooks'] for e in [1,2]},'missing/duplicate retention epoch')
        for r in epochs:
            require(r['Cycles']==r['Epoch']*50 and r['References']==r['Cycles']*({'startup':3,'native-hooks':4}.get(r['Owner'],2)),'retention coverage')
            require(r['Live']==0 and r['DispatcherAlive'] is True,'retained owner or stopped dispatcher')
            require(r['InitialUser']>0 and r['InitialUser']==r['User'] and r['InitialGdi']==r['Gdi'],'USER/GDI growth')
        acquired=[r for r in rows if r['Kind']=='HookAcquired']; stopped=[r for r in rows if r['Kind']=='HookStopped']
        for group in [acquired,stopped]:
            require(collections.Counter(r['Type'] for r in group)=={'LowLevelMouseHook':111,'LowLevelKeyboardHook':111},'native hook coverage')
        require(all(r['Hook']>0 and r['Thread']>0 and r['Desktop']==process['Desktop'] for r in acquired),'fake/foreign native hook')
        require(all(r['Hook']==0 and r['Thread']==0 and r['Completed'] is True for r in stopped),'retained native hook')
        closed=[r for r in rows if r['Kind']=='Closed']
        require(len(closed)==5 and {r['Type'] for r in closed}=={'StartupWindow','AboutWindow','ErrorMessageBox','MessageBox','SettingsWindow'} and all(r['DispatcherAlive'] for r in closed),'graceful native closure')
        exits=[r for r in rows if r['Kind']=='ApplicationExit']
        require(len(exits)==1 and exits[0]['DispatcherAlive'] and exits[0]['HooksCompleted'] and exits[0]['Closed']==5,'App shutdown order')
    else:
        require(len(rows)==4,'unexpected managed crash cleanup or missing crash entry')
        require([r['Kind'] for r in rows]==['Process','HookAcquired','HookAcquired','CrashEntered'],'hard crash order')
        require(rows[-1]['Mode']==mode and rows[-1]['Closed']==0 and rows[-1]['DispatcherAlive'] is True,'crash was not entered with live owners')
        require(all(r['Hook']>0 and r['Thread']>0 and r['Desktop']==process['Desktop'] for r in rows[1:3]),'hard crash hook witness')
def main():
    p=argparse.ArgumentParser(); p.add_argument('--snapshot',type=Path,required=True); p.add_argument('--output',type=Path,required=True); a=p.parse_args()
    require(not a.output.exists(),'receipt exists'); data=read(a.snapshot/'runs.json')
    require(len(data['Builds'])==2 and all(r['ExitCode']==0 for r in data['Builds']),'Debug/Release builds')
    processes=data['Processes']; require([(r['Configuration'],r['Mode']) for r in processes]==[(c,m) for c in ['Debug','Release'] for m in ['graceful','failfast','terminate']],'process coverage/order')
    for before,after in zip(processes,processes[1:]): require(datetime.fromisoformat(before['EndedUtc'])<datetime.fromisoformat(after['StartedUtc']),'overlapping processes')
    require(len({r['Desktop'] for r in processes})==6 and all(r['Desktop'].startswith('FWM_OWNED_') for r in processes),'fresh owned desktops')
    for build in data['Builds']:
        for f in build['Files']: require((a.snapshot/f['Path']).stat().st_size==f['Bytes'] and sha(a.snapshot/f['Path'])==f['SHA256'],'binary provenance')
    with (a.snapshot/'manifest.csv').open(encoding='utf-8-sig',newline='') as f: sources=list(csv.DictReader(f))
    for row in sources: require(sha(a.snapshot/'source'/row['Path'])==row['SHA256'],'source provenance')
    allrows={}; total=0
    for run in processes:
        c,m=run['Configuration'],run['Mode']; root=a.snapshot/'runs'/f'{c}-{m}'
        rows=[json.loads(line) for line in (root/'observations.jsonl').read_text().splitlines()]; check(rows,m)
        total+=len(rows); allrows[f'{c}-{m}']=rows
        require(rows[0]['Pid']==run['ProcessId'] and rows[0]['Desktop']==run['Desktop'],'native identity')
        require(run['InputSent'] is False and run['DesktopSwitched'] is False and run['OwnedDesktopHandleClosed'] is True,'ownership constraints')
        require(run['DesktopAfterExit']['Rows']==[],'remaining observed windows')
        require(sha(a.snapshot/'validation'/f'{c}-{m}.log')==run['LogSHA256'],'raw log provenance')
        if m=='graceful':
            summary=read(root/'summary.json')
            require(run['ExitCode']==0 and summary['Verdict']=='PASS' and summary['Closed']==5 and summary['HooksCompleted'] and summary['ProviderDisposed'] and summary['DispatcherStopped'],'graceful completion')
            require(summary['SurvivingHwnds']==[] and len(summary['Hwnds'])==5 and run['ManagedProcessExitMarker'],'graceful resources')
        else:
            ready=read(root/'crash-ready.json'); require(sha(root/'crash-ready.json')==run['CrashReadySHA256'],'ready provenance')
            require(run['ExitCode']!=0 and run['SurvivingHwnds']==[] and run['ManagedProcessExitMarker'] is False,'hard crash exit/cleanup')
            if m=='terminate': require(run['ExitCode']==0xE000F016,'termination exit code')
            require({r['Hwnd'] for r in run['AliveBeforeCrash']}==set(ready['Handles']) and len(ready['Handles'])==5,'live HWND witnesses')
            require(all(r['Pid']==run['ProcessId'] for r in run['AliveBeforeCrash']),'foreign crash HWND')
            require(not (root/'summary.json').exists(),'crash incorrectly claimed graceful cleanup')
    controls=[]
    for case in ['retained-owner','surviving-HWND','hook-not-stopped','missing-epoch','managed-cleanup-on-crash']:
        mode='terminate' if case=='managed-cleanup-on-crash' else 'graceful'; altered=copy.deepcopy(allrows['Debug-'+mode])
        if case=='retained-owner': next(r for r in altered if r['Kind']=='Retention')['Live']=1
        if case=='surviving-HWND': next(r for r in altered if r['Kind']=='WindowLifetime')['Destroyed']=False
        if case=='hook-not-stopped': next(r for r in altered if r['Kind']=='HookStopped')['Hook']=1
        if case=='missing-epoch': next(r for r in altered if r['Kind']=='Retention')['Epoch']=9
        if case=='managed-cleanup-on-crash': altered.append(dict(Kind='Closed',Type='StartupWindow'))
        try: check(altered,mode)
        except ValueError as error: controls.append(dict(Case=case,Rejected=True,Reason=str(error)))
        else: raise ValueError('negative accepted')
    result=dict(Verdict='PASS',Processes=6,ObservationRows=total,SourceFilesVerified=len(sources),NegativeControls=controls,
        NativeAuxiliaryRetention='PASS',NativeHookRetention='PASS',MinimalAppGracefulShutdown='PASS',OwnedHardCrash='OS_CLEANUP_VERIFIED_MANAGED_CLEANUP_NOT_EXECUTED',
        FullStartupGraph=False,WholeIdStatus='IN_PROGRESS',ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,
        Limits=['No MainWindow/Win32Workspace/full startup graph','USER/GDI and weak references are not native heap/GPU evidence',
                'EnumDesktopWindows zero-return/zero-last-error scans are retained as unsuccessful, not an empty-desktop proof; cleanup uses acquired HWND identity and process exit'],
        RawFiles=[dict(Path=str(p.relative_to(a.snapshot)),SHA256=sha(p)) for p in sorted((a.snapshot/'runs').rglob('*')) if p.is_file()])
    with a.output.open('x',encoding='utf-8') as f: json.dump(result,f,indent=2)
    print(json.dumps(dict(Verdict='PASS',Processes=6,ObservationRows=total,NegativeControls=len(controls),SHA256=sha(a.output))))
if __name__=='__main__': main()
