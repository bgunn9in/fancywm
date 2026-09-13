"""Own bounded native graphics process only; no tracing or global UI actions."""
import argparse,importlib.util,shutil,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--tool',type=Path,required=True);p.add_argument('--program',required=True);p.add_argument('--config',choices=['Debug','Release'],required=True);p.add_argument('extra',nargs='*');a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    assert s.read(s.ART/'FWM-D3D-LIFETIME-20260913-R0/verification.json')['Verdict']=='D3D_LIFETIME_ENTRY_VERIFIED';tool=a.tool.resolve();manifest=s.read(tool/'provenance.json')
    for row in manifest['Sources']+manifest['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    cmd=[str(tool/'binaries'/a.config/(a.program+'.exe')),*a.extra];start=s.datetime.now(s.timezone.utc).isoformat()
    with (dest/'raw.stdout').open('xb') as out,(dest/'raw.stderr').open('xb') as err:
        proc=subprocess.Popen(cmd,stdout=out,stderr=err,creationflags=0x08000000,cwd=dest);s.write(dest/'created.json',dict(Command=cmd,Pid=proc.pid,StartedUtc=start,Configuration=a.config,Tool=str(tool),ToolProvenanceSHA256=s.sha(tool/'provenance.json')));forced=False
        try:code=proc.wait(timeout=120)
        except subprocess.TimeoutExpired:forced=True;proc.kill();code=proc.wait(timeout=20)
    s.write(dest/'exited.json',dict(Pid=proc.pid,ExitCode=code,ExitHex=f'0x{code&4294967295:08X}',EndedUtc=s.datetime.now(s.timezone.utc).isoformat(),ForcedCleanup=forced,NewTracingSession=False));assert code==0 and not forced
    print(dest,flush=True)
if __name__=='__main__':main()
