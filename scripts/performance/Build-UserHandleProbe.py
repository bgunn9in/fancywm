"""Build only diagnostic tools; every output directory must be fresh."""
import argparse,hashlib,json,shutil,subprocess
from pathlib import Path
from datetime import datetime
ROOT=Path(__file__).resolve().parents[2]
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def write(p,v):
    with p.open('x',encoding='utf-8') as f:json.dump(v,f,indent=2)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=ROOT/'artifacts/performance'/a.id
    assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();source=dest/'source';source.mkdir()
    for name in ['Schema.cpp','EtwProbe.cpp','Decode.cpp','Realtime.cpp','RealtimeProbe.cpp','InputContextProbe.cpp']:shutil.copyfile(ROOT/'scripts/performance/gui-attribution'/name,source/name)
    shutil.copyfile(Path(__file__),source/Path(__file__).name)
    work=dest/'work';work.mkdir();compiler=Path('C:/Program Files/Microsoft Visual Studio/18/Professional/VC/Auxiliary/Build/vcvars64.bat')
    body=f'@echo off\ncall "{compiler}" > "{dest / "compiler-environment.log"}"\nif errorlevel 1 exit /b %errorlevel%\ncd /d "{work}"\n'
    for name in ['Schema','EtwProbe','Decode','RealtimeProbe','InputContextProbe']:body+=f'cl /nologo /std:c++17 /EHsc /W4 /Zi /Od /MT "{source / (name+".cpp")}" /link user32.lib advapi32.lib tdh.lib imm32.lib /OUT:"{dest / (name+".exe")}" /PDB:"{dest / (name+".pdb")}"\nif errorlevel 1 exit /b %errorlevel%\n'
    body+=f'cl /nologo /std:c++17 /EHsc /W4 /Zi /Od /MT /LD "{source / "Realtime.cpp"}" /link user32.lib advapi32.lib /OUT:"{dest / "FancyWM.OwnedUserTrace.dll"}" /PDB:"{dest / "FancyWM.OwnedUserTrace.pdb"}"\nexit /b %errorlevel%\n'
    with (dest/'build.cmd').open('x',encoding='utf-8',newline='\r\n') as f:f.write(body)
    cmd=['cmd.exe','/d','/c',str(dest/'build.cmd')]
    with (dest/'build.log').open('xb') as log:r=subprocess.run(cmd,cwd=dest,stdout=log,stderr=subprocess.STDOUT,timeout=120)
    write(dest/'build-receipt.json',dict(Command=cmd,ExitCode=r.returncode,Source=[dict(Path=f.name,SHA256=sha(f)) for f in sorted(source.iterdir())],LogSHA256=sha(dest/'build.log')));assert not r.returncode
    write(dest/'binaries.json',[dict(Path=f.name,Bytes=f.stat().st_size,SHA256=sha(f)) for f in sorted(dest.iterdir()) if f.suffix in ['.exe','.pdb','.dll']]);print(dest)
if __name__=='__main__':main()
