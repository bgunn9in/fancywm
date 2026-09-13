"""Run the pinned offline parser; never launch the measured application."""
import argparse, importlib.util, subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('b',Path(__file__).with_name('Build-NativeHeap.py'));b=importlib.util.module_from_spec(spec);spec.loader.exec_module(b)
def main():
    p=argparse.ArgumentParser();p.add_argument('--tool',type=Path,required=True);p.add_argument('--snapshot',type=Path,required=True);a=p.parse_args();tool=a.tool.resolve();root=a.snapshot.resolve()
    proof=b.json.loads((tool/'provenance.json').read_text(encoding='utf-8'))
    for row in proof['Binaries']:assert b.sha(tool/row['Path'])==row['SHA256']
    for config in ['Debug','Release']:
        folder=root/'runs'/(config+'-graceful');pid=b.json.loads((folder/'summary.json').read_text(encoding='utf-8'))['Pid'];out=folder/'traceevent-stacks';assert not out.exists()
        cmd=[str(tool/'binaries/HeapStackReader.exe'),str(folder/'heap.etl'),str(out),str(pid)];began=b.now()
        with (folder/'traceevent-reader.stdout').open('xb') as stdout,(folder/'traceevent-reader.stderr').open('xb') as stderr:
            r=subprocess.run(cmd,stdout=stdout,stderr=stderr,timeout=300)
        b.write(folder/'traceevent-reader-command.json',dict(Command=cmd,ExitCode=r.returncode,StartedUtc=began,EndedUtc=b.now(),ToolProvenanceSHA256=b.sha(tool/'provenance.json'),TraceEventPackageVersion='3.2.6',NewNativeApplicationRun=False));assert r.returncode==0
        print(config,'full stacks decoded',flush=True)
if __name__=='__main__':main()
