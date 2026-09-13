"""Preserve every admission verifier attempt, source, command and raw log."""
import argparse,importlib.util,shutil,subprocess,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location('h',Path(__file__).with_name('Verify-NativeHeap.py'));h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--runs',nargs='+',required=True);a=p.parse_args();root=Path.cwd()/'artifacts/performance'/a.id
    h.require(not root.exists(),'attempt exists');root.mkdir()
    for file in ['Verify-ClrHeapRuns.py','Verify-ClrHeapAdmission.py','clr_heap_model.py']:shutil.copyfile(Path(__file__).with_name(file),root/file)
    results=[]
    for run in a.runs:
        folder=Path.cwd()/'artifacts/performance'/run;receipt=root/(run+'-verification.json');cmd=[sys.executable,'-u','scripts/performance/Verify-ClrHeapAdmission.py','--root',str(folder),'--output',str(receipt)]
        with (root/(run+'.stdout')).open('xb') as out,(root/(run+'.stderr')).open('xb') as err:r=subprocess.run(cmd,stdout=out,stderr=err,timeout=180)
        result=dict(Run=run,Command=cmd,ExitCode=r.returncode,ReceiptSHA256=h.sha(receipt) if receipt.exists() else None);h.write(root/(run+'-command.json'),result);results.append(result);print(result,flush=True)
        if r.returncode:raise RuntimeError('verifier failed; raw attempt retained')
    h.write(root/'verification.json',dict(Verdict='SCOPED_CLR_NATIVE_CALLER_SOURCE_PASS',Runs=results,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS',SourceSHA256={f.name:h.sha(f) for f in root.glob('*.py')}))
if __name__=='__main__':main()
