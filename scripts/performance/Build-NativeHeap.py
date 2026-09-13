"""Process-local default-heap observer; immutable build and native calibration."""
import argparse, hashlib, json, shutil, subprocess
from pathlib import Path
from datetime import datetime, timezone
ROOT = Path(__file__).resolve().parents[2]
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def write(p, v):
    with p.open('x',encoding='utf-8') as f: json.dump(v,f,indent=2)
def now(): return datetime.now(timezone.utc).isoformat()
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args()
    dest=ROOT/'artifacts/performance'/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id
    dest.mkdir();source=dest/'source';source.mkdir();work=dest/'work';work.mkdir()
    for name in ['Heap.cpp','Control.cpp']: shutil.copyfile(ROOT/'scripts/performance/native-heap'/name,source/name)
    shutil.copyfile(Path(__file__),source/Path(__file__).name)
    compiler=Path('C:/Program Files/Microsoft Visual Studio/18/Professional/VC/Auxiliary/Build/vcvars64.bat')
    body=f'@echo off\ncall "{compiler}" > "{dest / "compiler-environment.log"}"\nif errorlevel 1 exit /b %errorlevel%\ncd /d "{work}"\n'
    for sourceName,flags,out in [('Heap','/LD','FancyWM.NativeHeap.dll'),('Control','','HeapControl.exe')]:
        body+=f'cl /nologo /std:c++17 /EHsc /W4 /WX /Zi /Od /MT {flags} "{source / (sourceName+".cpp")}" /link /OUT:"{dest / out}" /PDB:"{dest / (sourceName+".pdb")}"\nif errorlevel 1 exit /b %errorlevel%\n'
    with (dest/'build.cmd').open('x',encoding='utf-8',newline='\r\n') as f:f.write(body)
    cmd=['cmd.exe','/d','/c',str(dest/'build.cmd')];began=now()
    with (dest/'build.log').open('xb') as log:r=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,timeout=120)
    write(dest/'build-receipt.json',dict(Command=cmd,StartedUtc=began,EndedUtc=now(),ExitCode=r.returncode,CompilerSHA256=sha(compiler),LogSHA256=sha(dest/'build.log')))
    assert r.returncode==0
    write(dest/'source-manifest.json',[dict(Path=f.name,Bytes=f.stat().st_size,SHA256=sha(f)) for f in sorted(source.iterdir())])
    write(dest/'binaries.json',[dict(Path=f.name,Bytes=f.stat().st_size,SHA256=sha(f)) for f in sorted(dest.iterdir()) if f.suffix in ['.exe','.dll','.pdb']])
    cmd=[str(dest/'HeapControl.exe'),str(dest/'FancyWM.NativeHeap.dll'),str(dest/'control')];began=now()
    with (dest/'control.log').open('xb') as log:
        proc=subprocess.Popen(cmd,stdout=log,stderr=subprocess.STDOUT)
        try: code=proc.wait(timeout=60)
        except subprocess.TimeoutExpired:
            proc.kill();proc.wait();code=proc.returncode
    write(dest/'control-process.json',dict(Command=cmd,Pid=proc.pid,StartedUtc=began,EndedUtc=now(),ExitCode=code,Exited=proc.poll() is not None,LogSHA256=sha(dest/'control.log')))
    assert code==0
    print(dest)
if __name__=='__main__':main()
