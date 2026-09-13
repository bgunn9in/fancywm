"""Immutable native D3D source builds; Debug/Release and precise provenance."""
import argparse,importlib.util,shutil,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();(dest/'source').mkdir();(dest/'work').mkdir()
    for f in [Path(__file__),*sorted((Path(__file__).parent/'d3d-lifetime').glob('*'))]:
        if f.is_file():shutil.copyfile(f,dest/'source'/f.name)
    observer=(dest/'source/Observer9.cpp').exists()
    if observer:
        tool=s.ART/'FWM-GUI-ATTRIBUTION-20260912-R0/tools';pin=s.read(tool/'provenance.json');assert s.sha(tool/'detours.zip')==pin['SHA256'];shutil.copytree(tool/('Detours-'+pin['Commit']),dest/'source/detours');shutil.copytree(dest/'source/detours',dest/'work/detours');s.write(dest/'detours-provenance.json',pin)
    compiler=Path('C:/Program Files/Microsoft Visual Studio/18/Professional/VC/Auxiliary/Build/vcvars64.bat');body=f'@echo off\ncall "{compiler}" > "{dest / "compiler-environment.log"}"\nif errorlevel 1 exit /b %errorlevel%\n'
    if observer:body+=f'cd /d "{dest / "work/detours/src"}"\nnmake /nologo\nif errorlevel 1 exit /b %errorlevel%\n'
    for config,opt in [('Debug','/Od'),('Release','/O2')]:
        out=dest/'binaries'/config;out.mkdir(parents=True);work=dest/'work'/config;work.mkdir();body+=f'cd /d "{work}"\n'
        for source in sorted((dest/'source').glob('*.cpp')):
            native=source.stem=='Observer9';flags=f'/LD /I"{dest / "work/detours/include"}"' if native else '';libs=f'"{dest / "work/detours/lib.X64/detours.lib"}"' if native else '';extension='.dll' if native else '.exe'
            body+=f'cl /nologo /std:c++17 /EHsc /W4 /WX /Zi {opt} /MT {flags} "{source}" /link {libs} /OUT:"{out / (source.stem+extension)}" /PDB:"{out / (source.stem+".pdb")}"\nif errorlevel 1 exit /b %errorlevel%\n'
    with (dest/'build.cmd').open('x',encoding='utf-8',newline='\r\n') as f:f.write(body)
    cmd=['cmd.exe','/d','/c',str(dest/'build.cmd')];start=s.datetime.now(s.timezone.utc).isoformat()
    with (dest/'build.log').open('xb') as log:r=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,timeout=180)
    s.write(dest/'build-receipt.json',dict(Command=cmd,ExitCode=r.returncode,StartedUtc=start,EndedUtc=s.datetime.now(s.timezone.utc).isoformat(),CompilerSHA256=s.sha(compiler),LogSHA256=s.sha(dest/'build.log')));assert r.returncode==0
    s.write(dest/'provenance.json',dict(Sources=[dict(Path=str(f.relative_to(dest)),**s.info(f)) for f in sorted((dest/'source').rglob('*')) if f.is_file()],Binaries=[dict(Path=str(f.relative_to(dest)),**s.info(f)) for f in sorted((dest/'binaries').rglob('*')) if f.is_file()]))
    print(dest,flush=True)
if __name__=='__main__':main()
