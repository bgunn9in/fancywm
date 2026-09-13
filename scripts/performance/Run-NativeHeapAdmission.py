"""PID-only xperf heap admission, independently from CPU admission/measurement."""
import argparse, importlib.util, json, os, subprocess, time, queue, threading
from pathlib import Path
spec=importlib.util.spec_from_file_location('native',Path(__file__).with_name('Run-NativeTeardown.py'));n=importlib.util.module_from_spec(spec);spec.loader.exec_module(n)
ROOT=n.ROOT;XPERF=Path('C:/Program Files (x86)/Windows Kits/10/Windows Performance Toolkit/xperf.exe')
def line(stream):
    result=queue.Queue()
    threading.Thread(target=lambda:result.put(stream.readline()),daemon=True).start()
    return result.get(timeout=60)
def captured(dest,name,cmd,timeout=60):
    cmd=list(map(str,cmd));began=n.now()
    r=subprocess.run(cmd,capture_output=True,timeout=timeout,creationflags=0x08000000)
    with (dest/(name+'.stdout')).open('xb') as f:f.write(r.stdout)
    with (dest/(name+'.stderr')).open('xb') as f:f.write(r.stderr)
    n.write(dest/(name+'.json'),dict(Command=cmd,ExitCode=r.returncode,ExitHex='0x%08X'%(r.returncode&4294967295),StartedUtc=began,EndedUtc=n.now()))
    return r
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--tool',type=Path,required=True);p.add_argument('--inventory',type=Path,required=True);p.add_argument('--environment',type=Path,required=True);p.add_argument('--cpu-receipt',type=Path);a=p.parse_args()
    assert n.read(a.inventory/'verification.json')['OriginalFilesUnchanged'] and len(n.read(a.inventory/'etl-after.json'))==22
    assert n.read(a.environment/'environment.stdout')['Elevated'] and b'WPR is not recording' in (a.environment/'wpr.stdout').read_bytes()
    dest=ROOT/'artifacts/performance'/a.id;assert not dest.exists() and n.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    n.write(dest/'invocation.json',dict(Command=['python',__file__,*os.sys.argv[1:]],InventorySHA256=n.sha(a.inventory/'etl-after.json'),EnvironmentSHA256=n.sha(a.environment/'environment.stdout'),XperfSHA256=n.sha(XPERF),PerformanceClaim=False))
    before=captured(dest,'wpr-before',['wpr','-status']);assert before.returncode==0 and b'WPR is not recording' in before.stdout
    instance='FWM_OWNED_HEAP_CPU_'+a.id
    if a.cpu_receipt:
        prior=n.read(a.cpu_receipt);assert prior['ExitCode']==0 and prior['InactiveAfter']
        n.write(dest/'cpu-preserved.json',dict(Path=str(a.cpu_receipt),SHA256=n.sha(a.cpu_receipt),NewCpuRun=False))
    else:
        cpu=captured(dest,'cpu-admission',['wpr','-start','CPU','-filemode','-instancename',instance])
        if cpu.returncode==0:
            etl=dest/'cpu-admission.etl';assert not etl.exists()
            stop=captured(dest,'cpu-stop',['wpr','-stop',etl,'-instancename',instance]);assert stop.returncode==0
        own=captured(dest,'wpr-own-after-cpu',['wpr','-status','-instancename',instance]);assert own.returncode==0 and b'WPR is not recording' in own.stdout
        n.write(dest/'cpu-admission-result.json',dict(ExitCode=cpu.returncode,ExitHex='0x%08X'%(cpu.returncode&4294967295),KnownFailureRepeated=(cpu.returncode&4294967295)==0xC5585011,NewPreflightVerdict=False,PerformanceReject=False,InactiveAfter=True))
    tool=a.tool.resolve();assert n.read(tool/'verification.json')['Verdict']=='SCOPED_NATIVE_CONTROL_PASS'
    cmd=[str(tool/'HeapControl.exe'),str(tool/'FancyWM.NativeHeap.dll'),str(dest/'control'),'--pipe']
    began=n.now();session='';created=False;child=None;code=None;lines=[];failure=None
    try:
        child=subprocess.Popen(cmd,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=0x08000000)
        n.write(dest/'created-process.json',dict(Command=cmd,Pid=child.pid,StartedUtc=began,ExeSHA256=n.sha(tool/'HeapControl.exe')))
        ready=line(child.stdout);lines.append(ready);assert json.loads(ready)['Pid']==child.pid and json.loads(ready)['Ready']
        session='FWM_OWNED_HEAP_'+str(child.pid)+'_'+str(time.time_ns())
        absent=captured(dest,'own-session-before',['logman','query',session,'-ets']);assert (absent.returncode&4294967295)==0x80300002
        etl=dest/'heap.etl';assert not etl.exists()
        start=captured(dest,'heap-start',[XPERF,'-start',session,'-heap','-Pids',child.pid,'-BufferSize',1024,'-MinBuffers',16,'-MaxBuffers',16,'-stackwalk','HeapAlloc+HeapRealloc','-f',etl])
        created=start.returncode==0
        child.stdin.write(b'\n');child.stdin.flush()
        completed=line(child.stdout);lines.append(completed);assert json.loads(completed)['Pid']==child.pid and json.loads(completed)['Result']==0
        if created:
            stop=captured(dest,'heap-stop',[XPERF,'-stop',session]);assert stop.returncode==0;created=False
        child.stdin.write(b'\n');child.stdin.flush();code=child.wait(timeout=20);assert code==0
        if etl.exists():
            captured(dest,'xperf-stats',[XPERF,'-i',etl,'-o',dest/'stats.txt','-a','tracestats'])
            captured(dest,'xperf-heap',[XPERF,'-i',etl,'-o',dest/'heap.csv','-a','heap','-pid',child.pid,'-stacks','-top',100000])
            captured(dest,'xperf-dump',[XPERF,'-i',etl,'-o',dest/'events.csv','-a','dumper'])
            captured(dest,'tracerpt',['tracerpt',etl,'-o',dest/'events.xml','-of','XML','-summary',dest/'summary.txt'])
    except Exception as e:failure=repr(e)
    finally:
        try:
            if created:
                recovery=captured(dest,'heap-stop-recovery',[XPERF,'-stop',session]);assert recovery.returncode==0
        finally:
            if child:
                if child.poll() is None:child.kill();child.wait(timeout=20)
                code=child.returncode
                lines.append(child.stdout.read())
                with (dest/'control.stdout').open('xb') as f:f.write(b''.join(lines))
                with (dest/'control.stderr').open('xb') as f:f.write(child.stderr.read())
        if session:
            query=captured(dest,'own-session-after',['logman','query',session,'-ets']);assert (query.returncode&4294967295)==0x80300002
        after=captured(dest,'wpr-after',['wpr','-status']);assert after.returncode==0 and b'WPR is not recording' in after.stdout
    n.write(dest/'admission-result.json',dict(Command=cmd,Pid=child.pid if child else None,StartedUtc=began,EndedUtc=n.now(),ExitCode=code,Failure=failure,Session=session,SessionStopped=True,OwnedProcessExited=child is not None and child.poll() is not None,NoRegistryChange=True,NoGlobalKernelSession=True,ForeignSessionsModified=False,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS'))
    print(json.dumps(n.read(dest/'admission-result.json')))
if __name__=='__main__':main()
