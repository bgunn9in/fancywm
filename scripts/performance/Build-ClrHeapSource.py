"""Freeze and build the own-process CLR reader/control and collectible plugin."""
import argparse,importlib.util,shutil,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('b',Path(__file__).with_name('Build-NativeHeap.py'));b=importlib.util.module_from_spec(spec);spec.loader.exec_module(b)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();root=b.ROOT/'artifacts/performance'/a.id
    assert not root.exists() and b.datetime.now().strftime('%Y%m%d') in a.id;root.mkdir();(root/'source').mkdir()
    shutil.copyfile(Path(__file__),root/'source/Build-ClrHeapSource.py')
    projects=[('clr-heap-source','ClrHeapSource'),('clr-heap-control','ClrHeapControl'),('clr-heap-plugin','ClrHeapPlugin')]
    for folder,name in projects:shutil.copytree(b.ROOT/'scripts/performance'/folder,root/'source'/folder)
    shutil.copytree(root/'source',root/'work');builds=[]
    for config in ['Debug','Release']:
        for folder,name in projects:
            dest=root/'binaries'/config/name;cmd=['dotnet','build',name+'.csproj','-c',config,'-o',str(dest),'-p:ImportDirectoryBuildProps=false','-p:ImportDirectoryBuildTargets=false','-p:UseSharedCompilation=false','-nodeReuse:false','-v','minimal'];started=b.now()
            with (root/(config+'-'+name+'.log')).open('xb') as log:r=subprocess.run(cmd,cwd=root/'work'/folder,stdout=log,stderr=subprocess.STDOUT,timeout=180)
            receipt=dict(Command=cmd,WorkingDirectory=str(root/'work'/folder),StartedUtc=started,EndedUtc=b.now(),ExitCode=r.returncode);b.write(root/(config+'-'+name+'.json'),receipt);builds.append(receipt)
            assert r.returncode==0,(config,name)
    packages=root/'packages';packages.mkdir();assets=b.json.loads((root/'work/clr-heap-source/obj/project.assets.json').read_text(encoding='utf-8'))
    for name,lib in assets['libraries'].items():
        if lib['type']!='package':continue
        folder=next(Path(p)/lib['path'] for p in assets['packageFolders'] if (Path(p)/lib['path']).exists())
        for f in folder.glob('*.nupkg'):shutil.copyfile(f,packages/f.name)
    b.write(root/'provenance.json',dict(Builds=builds,Sources=[dict(Path=str(f.relative_to(root)),SHA256=b.sha(f)) for f in sorted((root/'source').rglob('*')) if f.is_file()],Packages=[dict(Path=str(f.relative_to(root)),SHA256=b.sha(f)) for f in sorted(packages.iterdir())],Binaries=[dict(Path=str(f.relative_to(root)),SHA256=b.sha(f)) for f in sorted((root/'binaries').rglob('*')) if f.is_file()]))
    print(root)
if __name__=='__main__':main()
