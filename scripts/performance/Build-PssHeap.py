"""Freeze and build the process-snapshot observer and own native controls."""
import argparse,importlib.util,shutil,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('b',Path(__file__).with_name('Build-NativeHeap.py'));b=importlib.util.module_from_spec(spec);spec.loader.exec_module(b)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=b.ROOT/'artifacts/performance'/a.id;assert not dest.exists() and b.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();(dest/'source').mkdir();(dest/'work').mkdir()
    for f in [Path(__file__),*[Path(__file__).parent/'pss-heap'/n for n in ['Pss.cpp','Control.cpp','Inspect.cpp','Protection.cpp','D3D.cpp']]]:shutil.copyfile(f,dest/'source'/f.name)
    compiler=Path('C:/Program Files/Microsoft Visual Studio/18/Professional/VC/Auxiliary/Build/vcvars64.bat');body=f'@echo off\ncall "{compiler}" > "{dest / "compiler-environment.log"}"\nif errorlevel 1 exit /b %errorlevel%\ncd /d "{dest / "work"}"\n'
    for config,optimization in [('Debug','/Od'),('Release','/O2')]:
        out=dest/'binaries'/config;out.mkdir(parents=True)
        for source,flags,name in [('Pss','/LD','FancyWM.NativePss.dll'),('Control','','PssControl.exe'),('Inspect','','PssInspect.exe'),('Protection','','PssProtection.exe'),('D3D','','PssD3D.exe')]:
            body+=f'cl /nologo /std:c++17 /EHsc /W4 /WX /Zi {optimization} /MT {flags} "{dest / "source" / (source+".cpp")}" /link /OUT:"{out / name}" /PDB:"{out / (source+".pdb")}"\nif errorlevel 1 exit /b %errorlevel%\n'
    with (dest/'build.cmd').open('x',encoding='utf-8',newline='\r\n') as f:f.write(body)
    cmd=['cmd.exe','/d','/c',str(dest/'build.cmd')];start=b.now()
    with (dest/'build.log').open('xb') as log:r=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,timeout=180)
    b.write(dest/'build-receipt.json',dict(Command=cmd,ExitCode=r.returncode,StartedUtc=start,EndedUtc=b.now(),CompilerSHA256=b.sha(compiler),LogSHA256=b.sha(dest/'build.log')));assert r.returncode==0
    b.write(dest/'provenance.json',dict(Sources=[dict(Path=str(f.relative_to(dest)),SHA256=b.sha(f)) for f in sorted((dest/'source').rglob('*')) if f.is_file()],Binaries=[dict(Path=str(f.relative_to(dest)),SHA256=b.sha(f)) for f in sorted((dest/'binaries').rglob('*')) if f.is_file()]))
    print(dest,flush=True)
if __name__=='__main__':main()
