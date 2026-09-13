"""Build pinned independent Microsoft TraceEvent parser in a fresh tool root."""
import argparse, importlib.util, shutil, subprocess, os
from pathlib import Path
spec=importlib.util.spec_from_file_location('b',Path(__file__).with_name('Build-NativeHeap.py'));b=importlib.util.module_from_spec(spec);spec.loader.exec_module(b)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();root=b.ROOT/'artifacts/performance'/a.id
    assert not root.exists() and b.datetime.now().strftime('%Y%m%d') in a.id;root.mkdir()
    shutil.copytree(b.ROOT/'scripts/performance/heap-stack-reader',root/'source');shutil.copyfile(Path(__file__),root/'source/Build-HeapStackReader.py');shutil.copytree(root/'source',root/'work')
    cmd=['dotnet','build','HeapStackReader.csproj','-c','Release','-o',str(root/'binaries'),'-p:ImportDirectoryBuildProps=false','-p:ImportDirectoryBuildTargets=false','-p:UseSharedCompilation=false','-nodeReuse:false','-v','minimal']
    began=b.now()
    with (root/'build.log').open('xb') as log:r=subprocess.run(cmd,cwd=root/'work',stdout=log,stderr=subprocess.STDOUT,timeout=180)
    b.write(root/'build-receipt.json',dict(Command=cmd,WorkingDirectory=str(root/'work'),StartedUtc=began,EndedUtc=b.now(),ExitCode=r.returncode,LogSHA256=b.sha(root/'build.log')));assert r.returncode==0
    assets=b.json.loads((root/'work/obj/project.assets.json').read_text(encoding='utf-8'));packages=root/'packages';packages.mkdir()
    for name,library in assets['libraries'].items():
        if library['type']!='package':continue
        folder=next(Path(p)/library['path'] for p in assets['packageFolders'] if (Path(p)/library['path']).exists())
        for package in folder.glob('*.nupkg'):shutil.copyfile(package,packages/package.name)
    b.write(root/'provenance.json',dict(Sources=[dict(Path=str(f.relative_to(root)),SHA256=b.sha(f)) for f in sorted((root/'source').rglob('*')) if f.is_file()],Packages=[dict(Path=f.name,SHA256=b.sha(f)) for f in sorted(packages.iterdir())],Binaries=[dict(Path=str(f.relative_to(root)),SHA256=b.sha(f)) for f in sorted((root/'binaries').rglob('*')) if f.is_file()],AssetsSHA256=b.sha(root/'work/obj/project.assets.json')))
    print(root)
if __name__=='__main__':main()
