"""Build a pinned Microsoft Detours source copy and our process-local observer."""
import argparse, hashlib, json, shutil, subprocess
from datetime import datetime, timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def write(p,v):
    with p.open('x',encoding='utf-8') as f:json.dump(v,f,indent=2)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args()
    dest=ROOT/'artifacts/performance'/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id
    dest.mkdir(); source=dest/'source';source.mkdir()
    tool=ROOT/'artifacts/performance/FWM-GUI-ATTRIBUTION-20260912-R0/tools'
    provenance=json.loads((tool/'provenance.json').read_text(encoding='utf-8'))
    assert sha(tool/'detours.zip')==provenance['SHA256']
    shutil.copytree(tool/('Detours-'+provenance['Commit']),source/'detours')
    shutil.copyfile(ROOT/'scripts/performance/gui-attribution/Trace.cpp',source/'Trace.cpp')
    shutil.copyfile(ROOT/'scripts/performance/gui-attribution/Control.cpp',source/'Control.cpp')
    shutil.copyfile(Path(__file__),source/'Build-GuiTracer.py')
    write(dest/'source-provenance.json',dict(Dependency=provenance,Files=[dict(Path=str(f.relative_to(dest)),Bytes=f.stat().st_size,SHA256=sha(f)) for f in sorted(source.rglob('*')) if f.is_file()]))
    work=dest/'work';shutil.copytree(source,work)
    compiler=Path('C:/Program Files/Microsoft Visual Studio/18/Professional/VC/Auxiliary/Build/vcvars64.bat');assert compiler.exists()
    batch=dest/'build.cmd'
    with batch.open('x',encoding='utf-8',newline='\r\n') as f:f.write(f'''@echo off
call "{compiler}" > "{dest / 'compiler-environment.log'}"
if errorlevel 1 exit /b %errorlevel%
cd /d "{work / 'detours/src'}"
nmake /nologo
if errorlevel 1 exit /b %errorlevel%
cd /d "{work}"
cl /nologo /std:c++17 /EHsc /W4 /Zi /Od /MT /LD /I"{work / 'detours/include'}" Trace.cpp /link "{work / 'detours/lib.X64/detours.lib'}" user32.lib gdi32.lib /OUT:"{dest / 'FancyWM.GuiTrace.dll'}" /PDB:"{dest / 'FancyWM.GuiTrace.pdb'}"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /EHsc /W4 /Zi /Od /MT Control.cpp /link user32.lib /OUT:"{dest / 'FancyWM.GuiTrace.exe'}" /PDB:"{dest / 'FancyWM.GuiTraceControl.pdb'}"
exit /b %errorlevel%
''')
    cmd=['cmd.exe','/d','/c',str(batch)];began=datetime.now(timezone.utc).isoformat()
    with (dest/'build.log').open('xb') as log:r=subprocess.run(cmd,cwd=dest,stdout=log,stderr=subprocess.STDOUT,timeout=180)
    write(dest/'build-receipt.json',dict(Command=cmd,ExitCode=r.returncode,StartedUtc=began,EndedUtc=datetime.now(timezone.utc).isoformat(),CompilerSetup=str(compiler),CompilerSetupSHA256=sha(compiler),LogSHA256=sha(dest/'build.log')))
    assert r.returncode==0,'See immutable build.log'
    write(dest/'binary-manifest.json',[dict(Path=f.name,Bytes=f.stat().st_size,SHA256=sha(f)) for f in sorted(dest.glob('FancyWM.GuiTrace.*'))])
    print(dest)
if __name__=='__main__':main()
