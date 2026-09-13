import argparse, importlib.util, shutil, subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('b',Path(__file__).with_name('Build-NativeHeap.py'));b=importlib.util.module_from_spec(spec);spec.loader.exec_module(b)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=b.ROOT/'artifacts/performance'/a.id
    assert not dest.exists() and b.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();(dest/'source').mkdir();(dest/'work').mkdir()
    source=dest/'source/Decode.cpp';shutil.copyfile(b.ROOT/'scripts/performance/native-heap/Decode.cpp',source);shutil.copyfile(Path(__file__),dest/'source/Build-HeapDecoder.py')
    compiler=Path('C:/Program Files/Microsoft Visual Studio/18/Professional/VC/Auxiliary/Build/vcvars64.bat')
    with (dest/'build.cmd').open('x',encoding='utf-8',newline='\r\n') as f:f.write(f'@echo off\ncall "{compiler}" > "{dest / "compiler-environment.log"}"\nif errorlevel 1 exit /b %errorlevel%\ncd /d "{dest / "work"}"\ncl /nologo /std:c++17 /EHsc /W4 /WX /Zi /Od /MT "{source}" /link advapi32.lib /OUT:"{dest / "HeapDecode.exe"}" /PDB:"{dest / "HeapDecode.pdb"}"\nexit /b %errorlevel%\n')
    cmd=['cmd.exe','/d','/c',str(dest/'build.cmd')]
    with (dest/'build.log').open('xb') as log:r=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,timeout=90)
    b.write(dest/'build-receipt.json',dict(Command=cmd,ExitCode=r.returncode,SourceSHA256=b.sha(source),CompilerSHA256=b.sha(compiler),LogSHA256=b.sha(dest/'build.log')));assert r.returncode==0
    b.write(dest/'binary-manifest.json',[dict(Path=f.name,Bytes=f.stat().st_size,SHA256=b.sha(f)) for f in sorted(dest.iterdir()) if f.suffix in ['.exe','.pdb']]);print(dest)
if __name__=='__main__':main()
