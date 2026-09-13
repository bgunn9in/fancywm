"""Own private allocation protection boundary; no device or policy changes."""
import argparse,importlib.util,shutil,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--config',choices=['Debug','Release'],required=True);p.add_argument('--d3d',action='store_true');args=p.parse_args();dest=s.ART/args.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in args.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    tool=s.ART/('FWM-PSS-HEAP-20260913-T7' if args.d3d else 'FWM-PSS-HEAP-20260913-T6');observer=s.ART/'FWM-PSS-HEAP-20260913-T5/binaries'/args.config/'FancyWM.NativePss.dll'
    # Execute exactly the observer calibrated by V1 and used in F3.
    assert s.sha(tool/'source/Pss.cpp')==s.sha(observer.parents[2]/'source/Pss.cpp')
    for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.sha(tool/row['Path'])==row['SHA256']
    cmd=[str(tool/'binaries'/args.config/('PssD3D.exe' if args.d3d else 'PssProtection.exe')),str(observer),str(dest/'control')];began=s.datetime.now(s.timezone.utc).isoformat()
    with (dest/'raw.stdout').open('xb') as out,(dest/'raw.stderr').open('xb') as err:
        process=subprocess.Popen(cmd,stdout=out,stderr=err,creationflags=0x08000000);s.write(dest/'created.json',dict(Command=cmd,Pid=process.pid,StartedUtc=began,Configuration=args.config,ObserverSHA256=s.sha(observer),ToolProvenanceSHA256=s.sha(tool/'provenance.json')));forced=False
        try:code=process.wait(timeout=60)
        except subprocess.TimeoutExpired:forced=True;process.kill();code=process.wait(timeout=20)
    s.write(dest/'exited.json',dict(Pid=process.pid,ExitCode=code,EndedUtc=s.datetime.now(s.timezone.utc).isoformat(),ForcedCleanup=forced,NewTracingSession=False));assert code==0 and not forced
    print(dest,flush=True)
if __name__=='__main__':main()
