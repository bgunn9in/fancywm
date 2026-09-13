"""Run the owned native input-context calibration on a private desktop."""
import argparse,importlib.util,os,re,subprocess,shutil
from pathlib import Path
spec=importlib.util.spec_from_file_location('native',Path(__file__).with_name('Run-NativeTeardown.py'));n=importlib.util.module_from_spec(spec);spec.loader.exec_module(n)

def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--tool',type=Path,required=True);p.add_argument('--realtime',type=Path,required=True);a=p.parse_args()
    dest=n.ROOT/'artifacts/performance'/a.id;assert not dest.exists() and n.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();(dest/'validation').mkdir()
    tool=a.tool.resolve();dll=a.realtime.resolve();exe=tool/'InputContextProbe.exe';assert n.read(tool/'build-receipt.json')['ExitCode']==0
    assert n.sha(dll)==n.read(n.ROOT/'artifacts/performance/FWM-GUI-ATTRIBUTION-20260913-E3/runs.json')['RealtimeDLLSHA256']
    shutil.copyfile(__file__,dest/Path(__file__).name);os.environ['FWM_GUI_ETW_DLL']=str(dll)
    n.write(dest/'invocation.json',dict(Command=['python',__file__,*os.sys.argv[1:]],Tool=str(tool),ProbeSHA256=n.sha(exe),RealtimeDLLSHA256=n.sha(dll),SourceSHA256=n.sha(tool/'source/InputContextProbe.cpp')))
    try:record=n.launch(dest,exe,'Native','graceful',timeout_seconds=60)
    finally:
        record=n.read(dest/'validation/Native-graceful-receipt.json');folder=dest/'runs/Native-graceful/realtime';admission=n.read(folder/'admission.json');summary=n.read(folder/'summary.json') if (folder/'summary.json').exists() else {}
        assert admission['Pid']==record['ProcessId'] and re.fullmatch(r'FWM_OWNED_GUI_RT_'+str(record['ProcessId'])+r'_\d+',admission['SessionName'])
        if summary.get('StopCode')!=0:subprocess.run(['logman','stop',admission['SessionName'],'-ets'],capture_output=True,check=True)
        cmd=['logman','query',admission['SessionName'],'-ets'];r=subprocess.run(cmd,capture_output=True,text=True,encoding='utf-8',errors='replace');assert r.returncode!=0 and 'not found' in (r.stdout+r.stderr).lower()
        n.write(dest/'cleanup.json',dict(Command=cmd,ExitCode=r.returncode,Stdout=r.stdout,Stderr=r.stderr,OwnedSessionStopped=True,ForeignSessionsModified=False))
    n.write(dest/'runs.json',dict(Processes=[record],ProductionChanged=False,PerformanceClaim=False,StageAccepted=False))
if __name__=='__main__':main()
