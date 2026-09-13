"""Full delivery gate for the two-file native-construction correctness delta."""
import argparse
import csv
import hashlib
import json
import shutil
import struct
import subprocess
import zipfile
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
MSBUILD=r'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe'
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def now(): return datetime.now(timezone.utc).isoformat()
def write(p,v):
    with p.open('x',encoding='utf-8') as f: json.dump(v,f,indent=2)
def manifest(p):
    with (p/'manifest.csv').open(encoding='utf-8-sig',newline='') as f: return {x['Path']:x['SHA256'] for x in csv.DictReader(f)}
def main():
    p=argparse.ArgumentParser(); p.add_argument('--id',required=True); p.add_argument('--candidate',type=Path,required=True); p.add_argument('--test-additions',type=int,default=0); p.add_argument('--scope',default='Native construction rollback correctness delivery'); a=p.parse_args()
    dest=ROOT/'artifacts/performance'/a.id; assert not dest.exists()
    assert datetime.now().strftime('%Y%m%d') in a.id
    snapshot=subprocess.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id],cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace')
    assert snapshot.returncode==0,snapshot.stderr
    write(dest/'snapshot-command.json',dict(ExitCode=0,Stdout=snapshot.stdout,Stderr=snapshot.stderr))
    frozen=manifest(dest); candidate=manifest(a.candidate)
    for rel,h in frozen.items():
        if Path(rel).parts[0].startswith('FancyWM') or Path(rel).parts[0] in ['winman','winman-windows','ModernWpf']:
            assert candidate.get(rel)==h and sha(ROOT/rel)==h,rel
    validation=dest/'validation'; validation.mkdir(); commands=[]; packages=[]; tests=[]
    def run(label,cmd,timeout=900):
        log=validation/(label+'.log'); assert not log.exists()
        started=now(); write(validation/(label+'-command.json'),dict(Command=cmd,StartedUtc=started,Order=len(commands)+1))
        with log.open('xb') as f:
            child=subprocess.Popen(cmd,cwd=ROOT,stdout=f,stderr=subprocess.STDOUT)
            try: code=child.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                subprocess.run(['taskkill','/PID',str(child.pid),'/T','/F'],capture_output=True); code=child.wait(timeout=15)
        record=dict(Command=cmd,ProcessId=child.pid,StartedUtc=started,EndedUtc=now(),ExitCode=code,LogSHA256=sha(log))
        write(validation/(label+'-receipt.json'),record); commands.append(record)
        print(json.dumps(dict(Label=label,ExitCode=code,EndedUtc=record['EndedUtc'])),flush=True)
        assert code==0,(label,code)
    run('dependency-patch-check',['pwsh','-NoProfile','-File','scripts/performance/Apply-DependencyPatches.ps1','-Mode','Check'])
    for config in ['Debug','Release']:
        run(config+'-restore',[MSBUILD,'FancyWM.sln','/t:Restore','/p:Platform=x64','/p:RuntimeIdentifier=win-x64','/p:Configuration='+config,'/v:minimal'])
        run(config+'-gui',['dotnet','build','FancyWM.GUI/FancyWM.GUI.csproj','--no-restore','--configuration',config,'--property:WarningLevel=0'])
        package_output=dest/'package-output'/config; assert not package_output.exists(); package_output.mkdir(parents=True)
        run(config+'-full',[MSBUILD,'FancyWM.sln','/p:Platform=x64','/p:Configuration='+config,
            '/p:GenerateTemporaryStoreCertificate=False','/p:AppxPackageDir='+str(package_output)+'\\','/v:minimal'])
        for source in sorted(package_output.rglob('*.msix')):
            archive=dest/'release'/config/source.name; assert not archive.exists(); archive.parent.mkdir(parents=True,exist_ok=True)
            with source.open('rb') as f,archive.open('xb') as g: shutil.copyfileobj(f,g)
            assert sha(source)==sha(archive)
            with zipfile.ZipFile(archive) as z:
                dll=z.read('FancyWM.GUI/FancyWM.dll'); embedded=hashlib.sha256(dll).hexdigest().upper()
                assert struct.unpack_from('<H',dll,struct.unpack_from('<I',dll,0x3c)[0]+4)[0]==0x8664
                built=ROOT/'FancyWM/bin/x64'/config/'net10.0-windows10.0.18362.0/win-x64/FancyWM.dll'
                assert sha(built)==embedded
                appx=ET.fromstring(z.read('AppxManifest.xml'))
                identity=next(x for x in appx if x.tag.endswith('Identity')); assert identity.get('ProcessorArchitecture')=='x64'
            packages.append(dict(Configuration=config,Path=str(archive.relative_to(dest)),Bytes=archive.stat().st_size,SHA256=sha(archive),
                EmbeddedSHA256=embedded,EmbeddedBytes=len(dll),Architecture='AMD64',Installed=False,Launched=False,Published=False))
        assert len([x for x in packages if x['Configuration']==config])==1
        for project in ['FancyWM.Tests','FancyWM.Layouts.Tests','FancyWM.ThemeEngine.Tests']:
            run(config+'-'+project,['dotnet','test',project+'/'+project+'.csproj','--configuration',config,'--no-restore',
                '--logger',f'trx;LogFileName={config}-{project}.trx','--results-directory',str(validation)])
            trx=validation/f'{config}-{project}.trx'; tree=ET.parse(trx).getroot(); ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
            results=tree.findall('.//t:UnitTestResult',ns); leaves=[r for r in results if r.find('t:InnerResults',ns) is None]
            expected=((2067 if config=='Debug' else 2123)+a.test_additions) if project=='FancyWM.Tests' else 160 if project=='FancyWM.Layouts.Tests' else 31
            assert len(leaves)==expected and all(r.get('outcome')=='Passed' for r in results),(config,project,len(leaves),expected)
            if project=='FancyWM.Tests':
                assert sum(r.get('testName','').startswith('OverlayNativeConstruction') for r in leaves)==5
                assert sum(r.get('testName','').startswith('OverlayNested') for r in leaves)==3
                assert sum(r.get('testName','').startswith('OverlayPadding') for r in leaves)==4
            tests.append(dict(Configuration=config,Project=project,PassedLeaves=len(leaves),TRXSHA256=sha(trx)))
    run('dependency-replay',['pwsh','-NoProfile','-File','scripts/performance/Test-DependencyPatches.ps1','-SnapshotId',a.id])
    run('nested-measurement-reverification',['python','scripts/performance/Verify-OverlayPaddingReuse.py',
        'artifacts/performance/FWM-OVERLAY-NESTED-NOTIFICATION-20260912-M1','--output',str(validation/'nested-measurement-reverification.json')])
    for rel,h in frozen.items():
        assert sha(dest/'source'/rel)==h and sha(ROOT/rel)==h,rel
    ledger=ROOT/'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
    assert ledger.stat().st_size==41782945 and sha(ledger)=='84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11'
    write(validation/'delivery-verification.json',dict(Verdict='PASS',Scope=a.scope,
        WholeIdStatus='IN_PROGRESS',StageAccepted=False,PerformanceClaim=False,LedgerAppended=False,Tests=tests,Packages=packages,Commands=commands,
        SourceFilesVerified=len(frozen),SourceManifestSHA256=sha(dest/'manifest.csv'),Candidate=str(a.candidate),LedgerSHA256=sha(ledger)))
    print('Full native-owner delivery verified.',flush=True)
if __name__=='__main__': main()
