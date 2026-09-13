"""Own native PSS producer and bounded inspector processes; no ETW session."""
import argparse,importlib.util,json,shutil,subprocess,queue,threading
from pathlib import Path
spec=importlib.util.spec_from_file_location('a',Path(__file__).with_name('Run-NativeHeapAdmission.py'));a=importlib.util.module_from_spec(spec);spec.loader.exec_module(a);n=a.n
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--tool',type=Path,required=True);p.add_argument('--config',choices=['Debug','Release'],required=True);args=p.parse_args();dest=a.ROOT/'artifacts/performance'/args.id;assert not dest.exists() and n.datetime.now().strftime('%Y%m%d') in args.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    entry=a.ROOT/'artifacts/performance/FWM-PSS-HEAP-20260913-R0/verification.json';assert n.read(entry)['Verdict']=='PSS_SOURCE_ENTRY_VERIFIED'
    tool=args.tool.resolve();manifest=n.read(tool/'provenance.json')
    for r in manifest['Sources']+manifest['Binaries']:assert n.sha(tool/r['Path'])==r['SHA256']
    bins=tool/'binaries'/args.config;cmd=[str(bins/'PssControl.exe'),str(bins/'FancyWM.NativePss.dll'),str(dest/'control')];child=None;lines=[];failure=None;inspectors=[]
    n.write(dest/'invocation.json',dict(Command=['python',__file__,*__import__('sys').argv[1:]],Configuration=args.config,Tool=str(tool),EntrySHA256=n.sha(entry),ToolProvenanceSHA256=n.sha(tool/'provenance.json'),NewTracingSession=False))
    try:
        child=subprocess.Popen(cmd,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=0x08000000);n.write(dest/'created.json',dict(Command=cmd,Pid=child.pid,StartedUtc=n.now()))
        for epoch in range(2):
            line=a.line(child.stdout);lines.append(line);ready=json.loads(line);assert ready['Ready'] and ready['Pid']==child.pid and ready['Epoch']==epoch and ready['CaptureCode']==0 and ready['Result']==0 and ready['ClonePid']>0 and ready['ClonePid']!=child.pid
            clone=ready['ClonePid'];inspect_cmd=[str(bins/'PssInspect.exe'),str(clone),str(ready['Heap']),str(dest/'control'/str(epoch)/'inspect')];started=n.now()
            with (dest/f'inspector-{epoch}.stdout').open('xb') as out,(dest/f'inspector-{epoch}.stderr').open('xb') as err:
                inspector=subprocess.Popen(inspect_cmd,stdout=out,stderr=err,creationflags=0x08000000);timedout=False
                try:code=inspector.wait(timeout=30)
                except subprocess.TimeoutExpired:timedout=True;inspector.kill();code=inspector.wait(timeout=20)
            record=dict(Command=inspect_cmd,Pid=inspector.pid,ClonePid=clone,SourcePid=child.pid,StartedUtc=started,EndedUtc=n.now(),ExitCode=code,TimedOut=timedout,Exited=True);n.write(dest/f'inspector-{epoch}.json',record);inspectors.append(record)
            child.stdin.write(b'\n');child.stdin.flush();line=a.line(child.stdout);lines.append(line);released=json.loads(line);assert released['Released'] and released['ClonePid']==clone and released['Epoch']==epoch and released['FreeCode']==0 and released['Result']==0
        assert child.wait(timeout=20)==0
    except Exception as e:failure=repr(e)
    finally:
        if child:
            forced=child.poll() is None
            if forced:
                # Let the control's owning thread release its snapshot first.
                try:child.stdin.write(b'\n\n');child.stdin.flush();child.wait(timeout=25)
                except (OSError,subprocess.TimeoutExpired):child.kill();child.wait(timeout=20)
            with (dest/'control.stdout').open('xb') as f:f.write(b''.join(lines)+child.stdout.read())
            with (dest/'control.stderr').open('xb') as f:f.write(child.stderr.read())
            n.write(dest/'exited.json',dict(Pid=child.pid,ExitCode=child.returncode,ForcedCleanup=forced,Exited=True,EndedUtc=n.now()))
        n.write(dest/'run.json',dict(Failure=failure,Configuration=args.config,Inspectors=inspectors,OwnProcessExited=child is not None and child.poll() is not None,NewTracingSession=False,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False))
    if failure:raise RuntimeError(failure)
    print(dest,flush=True)
if __name__=='__main__':main()
