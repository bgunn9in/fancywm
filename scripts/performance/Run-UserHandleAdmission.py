"""Separate CPU admission and a new, own-PID Win32k handle-event capability probe."""
import argparse,importlib.util,subprocess,json,os,re
from pathlib import Path
from datetime import datetime
spec=importlib.util.spec_from_file_location('native',Path(__file__).with_name('Run-NativeTeardown.py'));n=importlib.util.module_from_spec(spec);spec.loader.exec_module(n)
ROOT=n.ROOT
def captured(dest,name,cmd):
    began=n.now();r=subprocess.run([str(x) for x in cmd],cwd=ROOT,capture_output=True,creationflags=0x08000000)
    with (dest/(name+'.stdout')).open('xb') as f:f.write(r.stdout)
    with (dest/(name+'.stderr')).open('xb') as f:f.write(r.stderr)
    n.write(dest/(name+'-command.json'),dict(Command=[str(x) for x in cmd],ExitCode=r.returncode,ExitHex='0x%08X'%(r.returncode&4294967295),StartedUtc=began,EndedUtc=n.now()))
    return r
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--environment',type=Path,required=True);p.add_argument('--tool',type=Path,required=True);p.add_argument('--observer',type=Path,required=True);p.add_argument('--cpu-admission',action='store_true');a=p.parse_args()
    env=a.environment.resolve();inventory=n.read(env/'etl-before.json');assert len(inventory)==19 and sum(x['Bytes'] for x in inventory)==111353266176
    assert n.read(env/'environment.json')['Elevated'];assert b'WPR is not recording' in (env/'wpr-before.stdout').read_bytes()
    dest=ROOT/'artifacts/performance'/a.id;assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();(dest/'validation').mkdir()
    n.write(dest/'invocation.json',dict(Command=['python',__file__,*os.sys.argv[1:]],EnvironmentReceiptSHA256=n.sha(env/'environment.json'),EtlInventorySHA256=n.sha(env/'etl-before.json'),CpuAdmissionOnly=a.cpu_admission,PerformanceClaim=False,StageAccepted=False))
    before=captured(dest,'wpr-before',['wpr','-status']);assert before.returncode==0 and b'WPR is not recording' in before.stdout
    if a.cpu_admission:
        instance='FWM_GUI_ADMISSION_'+a.id
        attempt=captured(dest,'cpu-admission',['wpr','-start','CPU','-filemode','-instancename',instance])
        if attempt.returncode==0:
            etl=dest/'cpu-admission.etl';assert not etl.exists()
            stop=captured(dest,'cpu-admission-stop',['wpr','-stop',etl,'-instancename',instance]);assert stop.returncode==0
        ownAfter=captured(dest,'wpr-own-after-cpu',['wpr','-status','-instancename',instance]);assert ownAfter.returncode==0 and b'WPR is not recording' in ownAfter.stdout
        after=captured(dest,'wpr-after-cpu',['wpr','-status']);assert after.returncode==0 and b'WPR is not recording' in after.stdout
        n.write(dest/'cpu-admission.json',dict(ExitCode=attempt.returncode,ExitHex='0x%08X'%(attempt.returncode&4294967295),KnownFailureRepeated=(attempt.returncode&4294967295)==0xC5585011,NewPreflightVerdict=False,PerformanceReject=False,InactiveAfter=True))
    tool=a.tool.resolve();observer=a.observer.resolve();assert n.read(tool/'build-receipt.json')['ExitCode']==0
    os.environ['FWM_GUI_TRACE_DLL']=str(observer)
    nativeError=None
    try:record=n.launch(dest,tool/'EtwProbe.exe','Native','graceful',timeout_seconds=60)
    except Exception as e:nativeError=repr(e);record=n.read(dest/'validation/Native-graceful-receipt.json')
    output=dest/'runs/Native-graceful';summary=n.read(output/'summary.json');admission=n.read(output/'admission.json')
    assert admission['Pid']==record['ProcessId'] and re.fullmatch(r'FWM_OWNED_GUI_'+str(record['ProcessId'])+r'_\d+',admission['SessionName'])
    if admission['OwnedSessionCreated'] and summary['StopCode']!=0:
        stopped=captured(dest,'owned-session-recovery',['logman','stop',admission['SessionName'],'-ets']);assert stopped.returncode==0
    query=captured(dest,'owned-session-after',['logman','query',admission['SessionName'],'-ets']);assert query.returncode!=0,'own session still active'
    after=captured(dest,'wpr-after',['wpr','-status']);assert after.returncode==0 and b'WPR is not recording' in after.stdout
    etl=output/'user-handles.etl'
    if etl.exists():
        decoder=captured(dest,'native-decode',[tool/'Decode.exe',etl,output/'decoded']);assert decoder.returncode==0
        tracerpt=captured(dest,'tracerpt',['tracerpt',etl,'-o',output/'tracerpt.xml','-of','XML','-summary',output/'tracerpt-summary.txt']);assert tracerpt.returncode==0
    n.write(dest/'admission-result.json',dict(NativeSummary=summary,Process=record,NativeError=nativeError,ObserverSHA256=n.sha(observer),ProbeSHA256=n.sha(tool/'EtwProbe.exe'),DecoderSHA256=n.sha(tool/'Decode.exe'),
        OwnedSessionStopped=True,WprInactive=True,ForeignSessionsModified=False,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS',LedgerAppended=False))
    print(json.dumps(dict(Id=a.id,Summary=summary,Decoded=n.read(output/'decoded/summary.json') if etl.exists() else None)))
if __name__=='__main__':main()
