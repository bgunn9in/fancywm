"""Seal verified PERF-016 continuation, preserving all prior evidence and failures."""
import argparse
import csv
import hashlib
import json
import struct
import subprocess
import zipfile
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'artifacts/performance'
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def write(p,value):
    with p.open('x',encoding='utf-8') as f: json.dump(value,f,indent=2)
def main():
    p=argparse.ArgumentParser(); p.add_argument('--id',required=True); a=p.parse_args()
    dest=BASE/a.id; assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id
    command=['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]
    result=subprocess.run(command,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace'); assert result.returncode==0,result.stderr
    write(dest/'snapshot-command.json',dict(Command=command,ExitCode=0,Stdout=result.stdout,Stderr=result.stderr))
    validation=dest/'validation'; validation.mkdir()
    commands=[]
    def run(label,cmd,expected=0):
        started=datetime.now(timezone.utc).isoformat()
        result=subprocess.run(cmd,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace')
        write(validation/(label+'-command.json'),dict(Command=cmd,StartedUtc=started,EndedUtc=datetime.now(timezone.utc).isoformat(),
            ExitCode=result.returncode,Stdout=result.stdout,Stderr=result.stderr))
        assert (result.returncode==0) if expected==0 else (result.returncode!=0),(label,result.stderr)
        commands.append(dict(Label=label,ExitCode=result.returncode)); print(label,flush=True)
    candidate=BASE/'FWM-OWNERS-CONSTRUCTION-20260912-C1R2'
    run('checkpoint',['python','scripts/performance/Review-NativeOwnersCheckpoint.py','--candidate',str(candidate),'--output',str(validation/'checkpoint-review.json')])
    entries=[('owners','Verify-NativeOwnerLifetime.py',candidate,['--baseline',str(BASE/'FWM-OWNERS-CONSTRUCTION-20260912-B3')]),
        ('teardown','Verify-NativeTeardown.py',BASE/'FWM-NATIVE-TEARDOWN-20260912-R2',[]),
        ('providers','Verify-NativeProviders.py',BASE/'FWM-NATIVE-PROVIDERS-20260912-R2',[]),
        ('browser','Verify-NativeHelpBrowser.py',BASE/'FWM-NATIVE-HELP-BROWSER-20260912-R6',[])]
    verified=[]
    for label,script,source,extra in entries:
        output=validation/(label+'-replay.json'); cmd=['python','scripts/performance/'+script,'--snapshot',str(source),'--output',str(output),*extra]
        run(label+'-replay',cmd); assert sha(output)==sha(source/'verification.json'),label
        before=sha(output); run(label+'-exclusive-output-control',cmd,expected=1); assert sha(output)==before
        verified.append(dict(Id=source.name,ReceiptSHA256=before,ExclusiveOutputProtected=True))
    old=BASE/'FWM-TOAST-NATIVE-20260912-R2'
    run('old-toast-replay',['python',str(old/'verify-native.py'),'--output',str(validation/'toast-replay.json')])
    run('runtime-environment',['dotnet','--info'])
    run('owned-process-cleanup',['pwsh','-NoProfile','-Command',
        "$owned = @(Get-CimInstance Win32_Process | Where-Object { ($_.Name -in @('FancyWM.NativeTeardownHarness.exe','msedgewebview2.exe','testhost.exe')) -and ($_.CommandLine -match 'FWM-(OWNERS|NATIVE-)|NativeOwnerLifetimeTest') }); $owned | Select-Object ProcessId,ParentProcessId,CreationDate,Name,CommandLine | ConvertTo-Json -Depth 4; if ($owned.Count -ne 0) { exit 1 }"])
    delivery=BASE/'FWM-OWNERS-CONSTRUCTION-20260912-C2'; data=read(delivery/'validation/delivery-verification.json')
    assert data['Verdict']=='PASS' and not data['StageAccepted'] and not data['PerformanceClaim'] and not data['LedgerAppended']
    assert sha(delivery/'manifest.csv')==data['SourceManifestSHA256']
    with (delivery/'manifest.csv').open(encoding='utf-8-sig',newline='') as f:
        for r in csv.DictReader(f): assert sha(delivery/'source'/r['Path'])==r['SHA256']
    for test in data['Tests']:
        trx=delivery/'validation'/f"{test['Configuration']}-{test['Project']}.trx"; assert sha(trx)==test['TRXSHA256']
        ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}; rows=ET.parse(trx).getroot().findall('.//t:UnitTestResult',ns)
        leaves=[r for r in rows if r.find('t:InnerResults',ns) is None]
        assert len(leaves)==test['PassedLeaves'] and all(r.get('outcome')=='Passed' for r in rows)
    loghashes={sha(p) for p in (delivery/'validation').glob('*.log')}
    assert len(data['Commands'])==15
    for c in data['Commands']: assert c['ExitCode']==0 and c['LogSHA256'] in loghashes
    for package in data['Packages']:
        archive=delivery/package['Path']; assert sha(archive)==package['SHA256'] and archive.stat().st_size==package['Bytes']
        with zipfile.ZipFile(archive) as z:
            dll=z.read('FancyWM.GUI/FancyWM.dll'); assert hashlib.sha256(dll).hexdigest().upper()==package['EmbeddedSHA256']
            assert struct.unpack_from('<H',dll,struct.unpack_from('<I',dll,0x3c)[0]+4)[0]==0x8664
            identity=next(e for e in ET.fromstring(z.read('AppxManifest.xml')) if e.tag.endswith('Identity')); assert identity.get('ProcessorArchitecture')=='x64'
        assert not any(package[k] for k in ['Installed','Launched','Published'])
    write(validation/'delivery-replay.json',dict(Verdict='PASS',ReceiptSHA256=sha(delivery/'validation/delivery-verification.json'),Tests=data['Tests'],Packages=data['Packages'],NewTestsRun=False))
    # All files already present in R0 remain byte-exact except the stated source
    # correction, live documents and this session's checkpoint review script.
    allowed={'FancyWM/Windows/OverlayHost.cs','FancyWM/Windows/OverlayHost.OverlayWindow.cs',
        'PERFORMANCE_STATUS.md','PERFORMANCE_TODO.md','docs/performance/AUDIT.md','docs/performance/IMPLEMENTATION_RESULTS.md',
        'docs/performance/TOAST_NATIVE_LIFETIME.md','scripts/performance/README.md','scripts/performance/Review-NativeOwnersCheckpoint.py'}
    inherited=[]; changes=[]
    with (BASE/'FWM-OWNERS-NATIVE-20260912-R0/manifest.csv').open(encoding='utf-8-sig',newline='') as f:
        for r in csv.DictReader(f):
            rel=r['Path'].replace('\\','/'); assert sha(BASE/'FWM-OWNERS-NATIVE-20260912-R0/source'/rel)==r['SHA256']
            assert (ROOT/rel).is_file(),rel
            if sha(ROOT/rel)!=r['SHA256']:
                assert rel in allowed,rel; changes.append(rel)
            else: inherited.append(rel)
    write(validation/'inherited-preservation.json',dict(UnchangedFiles=len(inherited),DeclaredChangedFiles=changes,DeletedFiles=[]))
    prefixes=['FWM-OWNERS-NATIVE-20260912-','FWM-OWNERS-CONSTRUCTION-20260912-','FWM-NATIVE-TEARDOWN-20260912-',
        'FWM-NATIVE-PROVIDERS-20260912-','FWM-NATIVE-HELP-BROWSER-20260912-']
    roots=sorted(p for p in BASE.iterdir() if p.is_dir() and any(p.name.startswith(prefix) for prefix in prefixes))
    evidence=[]
    for root in roots:
        before=len(evidence)
        for path in sorted(root.rglob('*')):
            if path.is_file(): evidence.append(dict(Path=str(path.relative_to(BASE)),Bytes=path.stat().st_size,SHA256=sha(path)))
        print(f'Sealed {root.name}: {len(evidence)-before} files',flush=True)
    write(dest/'evidence-manifest.json',evidence)
    write(dest/'verification.json',dict(Verdict='PASS',RecordedUtc=datetime.now(timezone.utc).isoformat(),Checkpoint=a.id,
        WholeIdStatus='IN_PROGRESS',ProductionChanged=True,ProductionDeltaFiles=2,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,
        EvidenceFiles=len(evidence),EvidenceManifestSHA256=sha(dest/'evidence-manifest.json'),EvidenceRoots=[p.name for p in roots],VerifiedReceipts=verified,
        DeliveryReceiptSHA256=sha(delivery/'validation/delivery-verification.json'),CheckpointReviewSHA256=sha(validation/'checkpoint-review.json'),
        InheritedPreservationSHA256=sha(validation/'inherited-preservation.json'),Commands=commands,
        Gaps=['Full unchanged Startup.AppMain/MainWindow graph requires isolated session/VM: MainWindow writes SPI_SETWINARRANGING',
            'Full graph repeated retention and app hard-crash handler path; existing minimal graph OS crash cleanup does not prove managed cleanup',
            'Shown SettingsWindow immediate collection precedes WPF inactive-view cache maintenance; post-maintenance result is scoped',
            'Native heap/GPU allocation attribution and physical presentation not established; PERF-010/021 instrumentation contracts unchanged']))
    print(json.dumps(dict(Checkpoint=a.id,VerificationSHA256=sha(dest/'verification.json'),EvidenceManifestSHA256=sha(dest/'evidence-manifest.json'),Files=len(evidence))),flush=True)
if __name__=='__main__': main()
