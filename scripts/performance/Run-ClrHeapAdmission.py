"""Two owned CLR processes: target PID and concurrently active excluded PID."""
import argparse, importlib.util, json, os, queue, shutil, subprocess, threading, time
from pathlib import Path
spec=importlib.util.spec_from_file_location('a',Path(__file__).with_name('Run-NativeHeapAdmission.py'));a=importlib.util.module_from_spec(spec);spec.loader.exec_module(a)
n=a.n;ROOT=a.ROOT

def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--tool',type=Path,required=True);p.add_argument('--config',choices=['Debug','Release'],required=True);args=p.parse_args()
    dest=ROOT/'artifacts/performance'/args.id;assert not dest.exists() and n.datetime.now().strftime('%Y%m%d') in args.id;dest.mkdir()
    shutil.copyfile(__file__,dest/'Run-ClrHeapAdmission.py')
    prep=ROOT/'artifacts/performance/FWM-CLR-HEAP-20260913-R0';assert n.read(prep/'verification.json')['Verdict']=='CURRENT_CLR_SOURCE_PREPARATION_VERIFIED'
    tool=args.tool.resolve();provenance=n.read(tool/'provenance.json')
    for row in provenance['Binaries']+provenance['Sources']:assert n.sha(tool/row['Path'])==row['SHA256']
    bins=tool/'binaries'/args.config;observer=ROOT/'artifacts/performance/FWM-NATIVE-HEAP-20260913-T2/FancyWM.NativeHeap.dll'
    assert n.sha(observer)==n.read(observer.parent/'verification.json')['ObserverSHA256']
    n.write(dest/'invocation.json',dict(Command=['python',__file__,*os.sys.argv[1:]],Configuration=args.config,Tool=str(tool),ToolProvenanceSHA256=n.sha(tool/'provenance.json'),PreparationSHA256=n.sha(prep/'verification.json'),ObserverSHA256=n.sha(observer),XperfSHA256=n.sha(a.XPERF),PerformanceClaim=False))
    children=[];heap='';clr='';heap_active=False;failure=None;commands=[]
    def launch(label,cmd):
        proc=subprocess.Popen(list(map(str,cmd)),stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=0x08000000)
        q=queue.Queue();streams=[]
        for stream,suffix in [(proc.stdout,'stdout'),(proc.stderr,'stderr')]:
            def drain(stream=stream,suffix=suffix):
                with (dest/(label+'.'+suffix)).open('xb') as f:
                    for line in iter(stream.readline,b''):
                        f.write(line);f.flush()
                        if suffix=='stdout':q.put(line)
                if suffix=='stdout':q.put(b'')
            t=threading.Thread(target=drain,daemon=True);t.start();streams.append(t)
        receipt=dict(Label=label,Command=list(map(str,cmd)),Pid=proc.pid,StartedUtc=n.now(),ExeSHA256=n.sha(Path(cmd[0])))
        n.write(dest/(label+'-created.json'),receipt);children.append((label,proc,q,streams,receipt));return children[-1]
    def expect(child,key):
        value=json.loads(child[2].get(timeout=60));assert value.get(key) is True,value
        assert value.get('OwnPid',value.get('Pid'))==(children[0][1].pid if child[0]=='collector' else child[1].pid),value
        return value
    def send(child,value):child[1].stdin.write((value+'\n').encode());child[1].stdin.flush()
    try:
        a.captured(dest,'wpr-before',['wpr','-status'])
        for role in ['target','excluded']:
            child=launch(role,[bins/'ClrHeapControl/ClrHeapControl.exe',dest/role,role,bins/'ClrHeapPlugin/ClrHeapPlugin.dll',observer]);expect(child,'Ready')
        target,excluded=children[:];pid=target[1].pid
        clr='FWM_OWNED_CLR_'+str(pid)+'_'+str(time.time_ns());heap='FWM_OWNED_CLR_HEAP_'+str(pid)+'_'+str(time.time_ns())
        n.write(dest/'owned-sessions.json',dict(OwnPid=pid,Clr=clr,Heap=heap))
        for name,session in [('clr',clr),('heap',heap)]:assert a.captured(dest,name+'-before',['logman','query',session,'-ets']).returncode&4294967295==0x80300002
        collector=launch('collector',[bins/'ClrHeapSource/ClrHeapSource.exe',dest/'clr',pid,clr]);expect(collector,'Ready')
        started=a.captured(dest,'heap-start',[a.XPERF,'-start',heap,'-heap','-Pids',pid,'-BufferSize',1024,'-MinBuffers',16,'-MaxBuffers',16,'-stackwalk','HeapAlloc+HeapRealloc','-f',dest/'heap.etl'])
        heap_active=started.returncode==0;assert heap_active
        send(target,'run');send(excluded,'run');expect(target,'Done');expect(excluded,'Done')
        assert a.captured(dest,'heap-stop',[a.XPERF,'-stop',heap]).returncode==0;heap_active=False
        send(collector,'stop');assert collector[1].wait(timeout=40)==0
        for child in [target,excluded]:send(child,'exit');assert child[1].wait(timeout=20)==0
    except Exception as e:failure=repr(e)
    finally:
        if heap_active:a.captured(dest,'heap-stop-recovery',[a.XPERF,'-stop',heap])
        for label,proc,q,streams,receipt in children:
            forced=proc.poll() is None
            if forced:proc.kill();proc.wait(timeout=20)
            for t in streams:t.join(timeout=5)
            n.write(dest/(label+'-exited.json'),dict(**receipt,EndedUtc=n.now(),ExitCode=proc.returncode,ForcedCleanup=forced,Exited=proc.poll() is not None))
        for name,session in [('clr',clr),('heap',heap)]:
            if not session:continue
            query=a.captured(dest,name+'-after',['logman','query',session,'-ets'])
            if query.returncode==0:
                a.captured(dest,name+'-stop-recovery',['logman','stop',session,'-ets']);query=a.captured(dest,name+'-after-recovery',['logman','query',session,'-ets'])
            assert query.returncode&4294967295==0x80300002
        a.captured(dest,'wpr-after',['wpr','-status'])
        n.write(dest/'run.json',dict(Failure=failure,OwnProcessesExited=all(c[1].poll() is not None for c in children),OwnSessionsInactive=True,Configuration=args.config,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False))
    if failure:raise RuntimeError(failure)
    pid=children[0][1].pid
    decoder=ROOT/'artifacts/performance/FWM-NATIVE-HEAP-20260913-T3/HeapDecode.exe'
    reader=ROOT/'artifacts/performance/FWM-NATIVE-HEAP-20260913-T5/binaries/Release/HeapStackReader.exe'
    # Read the already calibrated tool's actual output layout.
    if not reader.exists():reader=next((ROOT/'artifacts/performance/FWM-NATIVE-HEAP-20260913-T5').rglob('HeapStackReader.exe'))
    for label,cmd in [('native-decode',[decoder,dest/'heap.etl',dest/'native']),('stack-decode',[reader,dest/'heap.etl',dest/'stack-reader',pid])]:
        assert a.captured(dest,label,cmd,timeout=300).returncode==0
    print(dest,flush=True)
if __name__=='__main__':main()
