"""Freeze and run isolated native-owner validation in sequential configurations."""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
def now(): return datetime.now(timezone.utc).isoformat()
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def write(p,v):
    with p.open('x',encoding='utf-8') as f: json.dump(v,f,indent=2)
def freeze(src,dst):
    assert not dst.exists(); dst.parent.mkdir(parents=True,exist_ok=True)
    with src.open('rb') as f, dst.open('xb') as g: shutil.copyfileobj(f,g)
    assert sha(src)==sha(dst)
    return dict(Path=str(dst),Bytes=dst.stat().st_size,SHA256=sha(dst))
def main():
    p=argparse.ArgumentParser(); p.add_argument('--id',required=True)
    p.add_argument('--configurations',nargs='+',default=['Debug','Release'])
    p.add_argument('--filter',default='FullyQualifiedName~NativeOwnerLifetimeTest')
    p.add_argument('--production-changed',action='store_true'); p.add_argument('--no-build',action='store_true'); args=p.parse_args()
    dest=ROOT/'artifacts/performance'/args.id; assert not dest.exists(), dest
    assert datetime.now().strftime('%Y%m%d') in args.id, 'ID must use actual local date'
    command=['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',args.id]
    started=now(); result=subprocess.run(command,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace')
    assert result.returncode==0, result.stderr
    write(dest/'snapshot-command.json',dict(Command=command,StartedUtc=started,EndedUtc=now(),ExitCode=result.returncode,Stdout=result.stdout,Stderr=result.stderr))
    env=dict(os.environ); env['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    info=subprocess.run(['dotnet','--info'],capture_output=True,text=True,encoding='utf-8',errors='replace')
    write(dest/'environment.json',dict(RecordedUtc=now(),Dotnet=info.stdout,DotnetExit=info.returncode,Machine=os.environ.get('COMPUTERNAME'),
        Architecture=os.environ.get('PROCESSOR_ARCHITECTURE'),Session=os.environ.get('SESSIONNAME'),
        TieredCompilation=env.get('DOTNET_TieredCompilation','runtime-default'),MockScope='AppState settings and display/workspace providers; autostart query false; no LowLevelMouseHook service',
        ProductionChanged=args.production_changed,PerformanceClaim=False,StageAccepted=False))
    runs=[]
    for configuration in args.configurations:
        results=dest/'validation'/configuration; results.mkdir(parents=True)
        cmd=['dotnet','test','FancyWM.Tests/FancyWM.Tests.csproj','-c',configuration,'--filter',args.filter,
             '--logger',f'trx;LogFileName={configuration}.trx','--results-directory',str(results),'--no-restore','-v','minimal']
        if args.no_build: cmd.append('--no-build')
        start=now(); write(dest/f'{configuration}-command.json',dict(Command=cmd,StartedUtc=start,Order=len(runs)+1))
        with (dest/f'{configuration}.log').open('xb') as log:
            child=subprocess.Popen(cmd,cwd=ROOT,stdout=log,stderr=subprocess.STDOUT,env=env)
            try: code=child.wait(timeout=240)
            except subprocess.TimeoutExpired:
                subprocess.run(['taskkill','/PID',str(child.pid),'/T','/F'],capture_output=True); code=child.wait(timeout=15)
        run=dict(Configuration=configuration,Command=cmd,ProcessId=child.pid,StartedUtc=start,EndedUtc=now(),ExitCode=code,LogSHA256=sha(dest/f'{configuration}.log'))
        binaries=ROOT/'FancyWM.Tests/bin'/configuration/'net10.0-windows10.0.18362.0'
        entries=[]
        for src in sorted(binaries.rglob('*')):
            rel=src.relative_to(binaries)
            if not src.is_file() or 'TestResults' in rel.parts or 'win-x64' in rel.parts: continue
            if src.suffix.lower() not in ['.dll','.exe','.pdb'] and not src.name.endswith(('.deps.json','.runtimeconfig.json')): continue
            entry=freeze(src,dest/'binaries'/configuration/rel); entry['Path']=str(Path(entry['Path']).relative_to(dest)); entries.append(entry)
        run['Binaries']=entries; write(dest/f'{configuration}-receipt.json',run); runs.append(run)
        print(json.dumps({k:run[k] for k in ['Configuration','ProcessId','StartedUtc','EndedUtc','ExitCode']}),flush=True)
        if code: break
    write(dest/'runs.json',runs)
    return 0 if len(runs)==len(args.configurations) and all(x['ExitCode']==0 for x in runs) else 1
if __name__=='__main__': raise SystemExit(main())
