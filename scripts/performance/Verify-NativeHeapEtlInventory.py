"""Final all-file ETL integrity; exact allowed owned additions, no exclusions."""
import argparse, importlib.util, subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('n',Path(__file__).with_name('Build-NativeHeap.py'));n=importlib.util.module_from_spec(spec);spec.loader.exec_module(n)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--before',type=Path,required=True);a=p.parse_args();dest=n.ROOT/'artifacts/performance'/a.id
    assert not dest.exists() and n.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    cmd=['rg','--files','--hidden','--no-ignore','-g','*.[eE][tT][lL]'];r=subprocess.run(cmd,cwd=n.ROOT,capture_output=True);assert r.returncode==0
    with (dest/'paths.raw').open('xb') as f:f.write(r.stdout)
    n.write(dest/'command.json',dict(Command=cmd,ExitCode=r.returncode,StartedUtc=n.now()))
    rows=[]
    for i,rel in enumerate(sorted(set(r.stdout.decode('utf-8').splitlines()))):
        file=(n.ROOT/rel).resolve();assert file.is_relative_to(n.ROOT);before=file.stat();digest=n.sha(file);after=file.stat();assert (before.st_size,before.st_mtime_ns)==(after.st_size,after.st_mtime_ns)
        rows.append(dict(Path=rel,Bytes=after.st_size,SHA256=digest));print('ETL verified',i+1,flush=True)
    old=n.json.loads(a.before.read_text(encoding='utf-8'));current={x['Path']:x for x in rows}
    for x in old:assert current[x['Path']]==x,x['Path']
    oldnames={x['Path'] for x in old};added=[x for x in rows if x['Path'] not in oldnames]
    expected=['FWM-NATIVE-HEAP-20260913-E1/cpu-admission.etl','FWM-NATIVE-HEAP-20260913-E3/heap.etl','FWM-NATIVE-HEAP-20260913-F4/runs/Debug-graceful/heap.etl','FWM-NATIVE-HEAP-20260913-F4/runs/Release-graceful/heap.etl']
    assert {x['Path'].replace('\\','/').removeprefix('artifacts/performance/') for x in added}==set(expected)
    n.write(dest/'etl-after.json',rows)
    n.write(dest/'verification.json',dict(Verdict='PASS',PriorFiles=len(old),PriorBytes=sum(x['Bytes'] for x in old),PriorInventorySHA256=n.sha(a.before),AfterInventorySHA256=n.sha(dest/'etl-after.json'),NewOwnedEtls=added,OriginalFilesUnchanged=True,IncludesIgnored=True,SortedPaths=True,RecordedUtc=n.now()))
    print('ETL INTEGRITY PASS',len(rows),flush=True)
if __name__=='__main__':main()
