"""Owned native calibration of ETW allocation timestamps across HeapLock."""
import argparse,importlib.util,json,shutil,subprocess,time
from pathlib import Path
spec=importlib.util.spec_from_file_location('a',Path(__file__).with_name('Run-NativeHeapAdmission.py'));a=importlib.util.module_from_spec(spec);spec.loader.exec_module(a);n=a.n
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);args=p.parse_args();dest=a.ROOT/'artifacts/performance'/args.id;assert not dest.exists() and n.datetime.now().strftime('%Y%m%d') in args.id;dest.mkdir();(dest/'source').mkdir();(dest/'work').mkdir()
    for f in [Path(__file__),Path(__file__).parent/'native-heap/LockBoundary.cpp']:shutil.copyfile(f,dest/'source'/f.name)
    compiler=Path('C:/Program Files/Microsoft Visual Studio/18/Professional/VC/Auxiliary/Build/vcvars64.bat')
    body=f'@echo off\ncall "{compiler}" > "{dest / "compiler-environment.log"}"\nif errorlevel 1 exit /b %errorlevel%\ncd /d "{dest / "work"}"\ncl /nologo /std:c++17 /EHsc /W4 /WX /Zi /Od /MT "{dest / "source/LockBoundary.cpp"}" /link /OUT:"{dest / "LockBoundary.exe"}" /PDB:"{dest / "LockBoundary.pdb"}"\n'
    with (dest/'build.cmd').open('x',encoding='utf-8',newline='\r\n') as f:f.write(body)
    assert a.captured(dest,'build',['cmd.exe','/d','/c',dest/'build.cmd'],120).returncode==0
    prior=a.ROOT/'artifacts/performance/FWM-CLR-HEAP-20260913-E1/verification.json';assert n.read(prior)['OriginalFilesUnchanged']
    n.write(dest/'provenance.json',dict(PreviousInventorySHA256=n.sha(prior),ExeSHA256=n.sha(dest/'LockBoundary.exe'),Sources={f.name:n.sha(f) for f in (dest/'source').iterdir()},BuildOnlyBeforeInventory=True))
    child=None;session='';active=False;failure=None;lines=[]
    try:
        cmd=[str(dest/'LockBoundary.exe'),str(dest/'control')];child=subprocess.Popen(cmd,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=0x08000000);n.write(dest/'created.json',dict(Command=cmd,Pid=child.pid,StartedUtc=n.now(),ExeSHA256=n.sha(dest/'LockBoundary.exe')))
        ready=a.line(child.stdout);lines.append(ready);assert json.loads(ready)['Pid']==child.pid
        session='FWM_OWNED_HEAP_LOCK_'+str(child.pid)+'_'+str(time.time_ns());n.write(dest/'own-session.json',dict(Pid=child.pid,Session=session))
        assert a.captured(dest,'session-before',['logman','query',session,'-ets']).returncode&4294967295==0x80300002
        start=a.captured(dest,'heap-start',[a.XPERF,'-start',session,'-heap','-Pids',child.pid,'-BufferSize',1024,'-MinBuffers',16,'-MaxBuffers',16,'-stackwalk','HeapAlloc+HeapRealloc','-f',dest/'heap.etl']);active=start.returncode==0;assert active
        child.stdin.write(b'\n');child.stdin.flush();done=a.line(child.stdout);lines.append(done);rr=json.loads(done);assert rr['Done'] and rr['Pid']==child.pid and rr['Result']==0 and rr['Saved'] and rr['WorkerExit']==0
        assert a.captured(dest,'heap-stop',[a.XPERF,'-stop',session]).returncode==0;active=False
        child.stdin.write(b'\n');child.stdin.flush();assert child.wait(timeout=20)==0
    except Exception as e:failure=repr(e)
    finally:
        if active:a.captured(dest,'heap-stop-recovery',[a.XPERF,'-stop',session])
        if child:
            forced=child.poll() is None
            if forced:child.kill();child.wait(timeout=20)
            with (dest/'control.stdout').open('xb') as f:f.write(b''.join(lines)+child.stdout.read())
            with (dest/'control.stderr').open('xb') as f:f.write(child.stderr.read())
            n.write(dest/'exited.json',dict(Pid=child.pid,ExitCode=child.returncode,ForcedCleanup=forced,Exited=True,EndedUtc=n.now()))
        if session:assert a.captured(dest,'session-after',['logman','query',session,'-ets']).returncode&4294967295==0x80300002
        n.write(dest/'run.json',dict(Failure=failure,Session=session,OwnSessionInactive=True,ProductionChanged=False,PerformanceClaim=False))
    if failure:raise RuntimeError(failure)
    decoder=a.ROOT/'artifacts/performance/FWM-NATIVE-HEAP-20260913-T3/HeapDecode.exe';assert a.captured(dest,'native-decode',[decoder,dest/'heap.etl',dest/'native'],300).returncode==0
    print(dest,flush=True)
if __name__=='__main__':main()
