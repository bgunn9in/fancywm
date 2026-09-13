"""Run the existing full-app behavior stage after a verified topology gate.

This runner archives an already frozen, built harness and records two sequential
Debug/Release processes. It creates no stage ACCEPT and performs no WPR capture.
"""
import argparse
import csv
import ctypes as C
import datetime
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys


def sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest().upper()


def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))


def write(path,value):
    with path.open('x',encoding='utf-8') as stream:json.dump(value,stream,indent=2)


def production_manifest(snapshot):
    roots={'FancyWM','FancyWM.GUI','FancyWM.Layouts','FancyWM.ThemeEngine','FancyWM.DllImports',
           'FancyWM.Package','ModernWpf','winman','winman-windows'}
    with (snapshot/'manifest.csv').open(encoding='utf-8-sig',newline='') as stream:
        return {row['Path'].replace('\\','/'):row['SHA256'] for row in csv.DictReader(stream)
                if pathlib.PurePosixPath(row['Path'].replace('\\','/')).parts[0] in roots
                or row['Path'] in ['Directory.Build.props','FancyWM.sln','version.json']}


def inventory_windows():
    user=C.WinDLL('user32',use_last_error=True)
    class Rect(C.Structure):_fields_=[(name,C.c_long) for name in ['Left','Top','Right','Bottom']]
    user.GetWindowRect.argtypes=[C.c_void_p,C.POINTER(Rect)]
    user.GetWindowThreadProcessId.argtypes=[C.c_void_p,C.POINTER(C.c_uint)]
    user.IsWindowVisible.argtypes=[C.c_void_p]
    callback_type=C.WINFUNCTYPE(C.c_bool,C.c_void_p,C.c_void_p)
    result=[]
    @callback_type
    def visit(hwnd,unused):
        pid=C.c_uint();tid=user.GetWindowThreadProcessId(hwnd,C.byref(pid));rect=Rect()
        valid=user.GetWindowRect(hwnd,C.byref(rect))
        result.append(dict(Hwnd=hwnd,ProcessId=pid.value,OwnerThreadId=tid,Visible=bool(user.IsWindowVisible(hwnd)),
                           Rectangle={name:getattr(rect,name) for name,_ in rect._fields_} if valid else None))
        return True
    user.EnumWindows.argtypes=[callback_type,C.c_void_p]
    assert user.EnumWindows(visit,None)
    return sorted(result,key=lambda row:row['Hwnd'])


def main():
    sys.stdout.reconfigure(encoding='utf-8',errors='replace')
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--snapshot-id',required=True)
    p.add_argument('--build-snapshot',type=pathlib.Path,required=True)
    p.add_argument('--topology-receipt',type=pathlib.Path,required=True)
    p.add_argument('--live-candidate-build',type=pathlib.Path,
        help='Required only for a declared baseline build; live production must equal this candidate')
    a=p.parse_args();root=pathlib.Path.cwd();base=root/'artifacts/performance'/a.snapshot_id
    assert not base.exists(),'Fresh immutable ID required'
    gate=read(a.topology_receipt)
    assert gate['Stage']=='owner topology / presentation' and gate['Verdict']=='ACCEPT'
    assert gate['Processes']==15 and gate['HardwarePresentationTargets']==7320
    build=a.build_snapshot.resolve();built=read(build/'development-validation.json')
    assert built['BuildsPassed'] and not built['FullAppExecuted']
    topology_capture=root/'artifacts/performance'/gate['Cases'][0]['Capture']
    topology_manifest=topology_capture/'manifest.csv'
    manifest_witness=next(row for row in gate['Inputs'] if pathlib.Path(row['Path']).resolve()==topology_manifest.resolve())
    assert sha(topology_manifest)==manifest_witness['SHA256']
    original=production_manifest(topology_capture);current=production_manifest(build)
    live=current
    if a.live_candidate_build:
        candidate=a.live_candidate_build.resolve()
        baseline_source=read(build/'baseline-source.json')
        assert built['BaselineSource']==baseline_source
        assert read(candidate/'development-validation.json')['BuildsPassed']
        assert sha(candidate/'manifest.csv')==baseline_source['CandidateManifestSHA256']
        assert sha(build/'manifest.csv')==baseline_source['BuildManifestSHA256']
        baseline=pathlib.Path(baseline_source['Snapshot'])
        assert sha(baseline/'manifest.csv')==baseline_source['BaselineManifestSHA256']
        assert current==production_manifest(baseline)
        live=production_manifest(candidate)
        assert sorted(n for n in current.keys()|live.keys() if current.get(n)!=live.get(n))==baseline_source['ReplacedProductionFiles']
    else:
        assert not built.get('BaselineSource'),'Declared baseline requires its verified live candidate'
    for name,digest in current.items():
        assert sha(build/'source'/name)==digest,name
    for name,digest in live.items():assert sha(root/name)==digest,name
    production_changes=[dict(Path=name,TopologySHA256=original.get(name),BuildSHA256=current.get(name))
                        for name in sorted(original.keys()|current.keys()) if original.get(name)!=current.get(name)]
    # This runner may be added after the build, but all executable full-app source
    # and the production manifest must match the frozen build byte for byte.
    fixture={path.relative_to(root).as_posix():sha(path) for path in (root/'scripts/performance/fullapp').rglob('*')
             if path.is_file() and not any(part in ['bin','obj'] for part in path.relative_to(root).parts)}
    fixture['FancyWM/app.manifest']=sha(root/'FancyWM/app.manifest')
    for name,digest in fixture.items():assert sha(build/'source'/name)==digest,name
    snapshot=subprocess.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.snapshot_id],capture_output=True)
    assert base.is_dir()
    (base/'snapshot.stdout').write_bytes(snapshot.stdout);(base/'snapshot.stderr').write_bytes(snapshot.stderr)
    assert snapshot.returncode==0
    runroot=base/'validation';runroot.mkdir();commands=[];passed=False
    def run(label,args,timeout=180):
        started=datetime.datetime.now(datetime.timezone.utc).isoformat();timed_out=False
        with (runroot/(label+'.stdout')).open('xb') as stdout,(runroot/(label+'.stderr')).open('xb') as stderr:
            process=subprocess.Popen(args,stdout=stdout,stderr=stderr,creationflags=subprocess.CREATE_NO_WINDOW)
            try:code=process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                timed_out=True
                # PID is the still-live Popen child, never a historical receipt PID.
                killed=subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],capture_output=True)
                (runroot/(label+'-timeout-kill.stdout')).write_bytes(killed.stdout)
                (runroot/(label+'-timeout-kill.stderr')).write_bytes(killed.stderr)
                code=process.wait(timeout=30)
        signed=C.c_int32(code).value
        record=dict(Name=label,Arguments=args,ProcessId=process.pid,ExitCode=code,SignedExitCode=signed,
                    HexExitCode=f'0x{C.c_int64(signed).value&4294967295:08X}',TimedOut=timed_out,
                    StartedUtc=started,FinishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
        write(runroot/(label+'-command.json'),record);commands.append(record)
        print(label,'exit',code,flush=True)
        if code or timed_out:
            print((runroot/(label+'.stderr')).read_bytes().decode('utf-8',errors='replace')[-6000:],flush=True)
            raise RuntimeError('Retained failed full-app validation process')
    write(base/'provenance.json',dict(BuildSnapshot=str(build),BuildValidationSHA256=sha(build/'development-validation.json'),
        BuildManifestSHA256=sha(build/'manifest.csv'),TopologyReceipt=str(a.topology_receipt.resolve()),
        TopologyReceiptSHA256=sha(a.topology_receipt),Fixture=fixture,RunnerSHA256=sha(pathlib.Path(__file__)),
        ProductionChangesFromTopology=production_changes))
    for config in ['Debug','Release']:
        for project in ['FancyWM.FullAppHarness','FancyWM.Perf010Targets']:
            name=config+'-'+project;source=build/('binaries-'+name)
            manifest=read(build/'validation'/(name+'-binary-manifest.json'))
            actual={path.relative_to(source).as_posix() for path in source.rglob('*') if path.is_file()}
            assert actual=={r['Path'] for r in manifest}
            for row in manifest:
                path=source/row['Path'];assert path.stat().st_size==row['Bytes'] and sha(path)==row['SHA256']
            archive=base/('binaries-'+name);shutil.copytree(source,archive)
            for row in manifest:assert sha(archive/row['Path'])==row['SHA256']
            write(base/(name+'-binary-manifest.json'),manifest)
    kernel=C.WinDLL('kernel32');kernel.SetThreadExecutionState.argtypes=[C.c_uint];kernel.SetThreadExecutionState.restype=C.c_uint
    previous=kernel.SetThreadExecutionState(0x80000003);assert previous
    try:
        write(runroot/'windows-before.json',inventory_windows())
        environment=['pwsh','-NoProfile','-Command',
          '[ordered]@{ RecordedUtc=[DateTimeOffset]::UtcNow; Identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name; '+
          'OS=[Environment]::OSVersion.VersionString; Interactive=[Environment]::UserInteractive; '+
          'Video=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate); '+
          'Processes=@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CreationDate,ThreadCount) } | ConvertTo-Json -Depth 5']
        run('environment',environment)
        for config in ['Debug','Release']:
            host=base/('binaries-'+config+'-FancyWM.FullAppHarness')/'FancyWM.FullAppHarness.exe'
            targets=base/('binaries-'+config+'-FancyWM.Perf010Targets')/'FancyWM.Perf010Targets.exe'
            run(config,[str(host),str(runroot/config),str(targets),'2','validation'])
            summary=read(runroot/config/'fullapp-summary.json')
            assert summary['Error'] is None and summary['MainWindowShutdownCompleted'] and summary['AppTerminationCompleted']
            assert summary['TargetProcessExited'] and summary['PerMonitorV2']
            assert read(runroot/config/'targets/cleanup.json')['AllDestroyed']
        passed=True
    finally:
        write(runroot/'windows-after.json',inventory_windows())
        restored=kernel.SetThreadExecutionState(previous)
        write(runroot/'execution-state.json',dict(Requested='0x80000003',Previous=f'0x{previous:08X}',RestoreReturn=f'0x{restored:08X}'))
        write(base/'validation-run.json',dict(PerfId='PERF-010',Stage='Full-app native / interactive validation',
              StageAccepted=False,ProcessesPassed=passed,Commands=commands,ProductionDelta=bool(production_changes),
              ProductionChangesFromTopology=production_changes,LedgerAppend=False,
              MsixInstalled=False,MsixLaunched=False,WprRecordingStarted=False))
        files=[]
        for path in sorted(runroot.rglob('*')):
            if path.is_file():files.append(dict(Path=path.relative_to(runroot).as_posix(),Bytes=path.stat().st_size,SHA256=sha(path)))
        write(base/'validation-evidence.json',files)
    assert passed


if __name__=='__main__':main()
