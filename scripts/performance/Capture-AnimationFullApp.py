"""Capture the verified frozen full application, with isolated owned targets.

This is an observational baseline. Capture success is not stage acceptance.
Only the recording instance and child processes created here are stopped.
"""
import argparse
import csv
import ctypes as C
import datetime
import importlib.util
import os
import pathlib
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def main():
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot-id', required=True)
    parser.add_argument('--build-snapshot', type=pathlib.Path, required=True)
    parser.add_argument('--behavior-receipt', type=pathlib.Path, required=True)
    parser.add_argument('--topology-receipt', type=pathlib.Path, required=True)
    parser.add_argument('--runs', type=int, choices=range(1, 6), default=5)
    parser.add_argument('--iterations', type=int, choices=range(1, 21), default=8)
    parser.add_argument('--process-cpu-only', action='store_true',
        help='Explicitly omit ETW; retain quantized process CPU and native geometry, with ETW/GPU/presentation pending')
    args = parser.parse_args()
    root = pathlib.Path.cwd()
    scripts = root / 'scripts/performance'
    helpers = module('validation_runner', scripts / 'Run-AnimationFullAppValidation.py')
    verifier = module('validation_verifier', scripts / 'Verify-AnimationFullAppValidation.py')
    sha, read, write = helpers.sha, helpers.read, helpers.write
    assert re.fullmatch('[A-Z0-9-]+', args.snapshot_id)
    base = root / 'artifacts/performance' / args.snapshot_id
    assert not base.exists(), 'Fresh immutable snapshot ID required'
    elevated = bool(C.WinDLL('shell32').IsUserAnAdmin())
    wpr = pathlib.Path(os.environ['SystemRoot']) / 'System32/wpr.exe'
    xperf = pathlib.Path(r'C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe')
    assert wpr.is_file() and xperf.is_file()
    before = subprocess.run([str(wpr), '-status'], capture_output=True,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    assert before.returncode == 0 and b'not recording' in before.stdout, 'An existing WPR recording was not modified'
    build = args.build_snapshot.resolve()
    behavior_path = args.behavior_receipt.resolve()
    behavior = read(behavior_path)
    assert behavior['Verdict'] == 'PASS'
    assert [c['Configuration'] for c in behavior['Configurations']] == ['Debug', 'Release']
    validation = pathlib.Path(behavior['Snapshot'])
    provenance = read(validation / 'provenance.json')
    assert pathlib.Path(provenance['BuildSnapshot']) == build
    assert sha(validation / 'validation-run.json') == behavior['RunSHA256']
    assert sha(validation / 'validation-evidence.json') == behavior['EvidenceSHA256']
    assert sha(args.topology_receipt) == provenance['TopologyReceiptSHA256']
    gate = read(args.topology_receipt)
    assert gate['Verdict'] == 'ACCEPT' and gate['Stage'] == 'owner topology / presentation'
    assert gate['Processes'] == 15 and gate['HardwarePresentationTargets'] == 7320
    assert sha(build / 'manifest.csv') == provenance['BuildManifestSHA256']
    assert sha(build / 'development-validation.json') == provenance['BuildValidationSHA256']
    assert read(build / 'development-validation.json')['BuildsPassed']
    for name, digest in helpers.production_manifest(build).items():
        assert sha(root / name) == sha(build / 'source' / name) == digest, name
    for name, digest in provenance['Fixture'].items():
        assert sha(root / name) == sha(build / 'source' / name) == digest, name

    snapshot = subprocess.run(['pwsh', '-NoProfile', '-File',
        str(scripts / 'Save-ImplementationSnapshot.ps1'), '-SnapshotId', args.snapshot_id],
        capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    assert base.is_dir()
    (base / 'snapshot.stdout').write_bytes(snapshot.stdout)
    (base / 'snapshot.stderr').write_bytes(snapshot.stderr)
    assert snapshot.returncode == 0
    native = base / 'native'
    native.mkdir()
    preflight = base / 'preflight'
    preflight.mkdir()
    (preflight / 'wpr-before.stdout').write_bytes(before.stdout)
    (preflight / 'wpr-before.stderr').write_bytes(before.stderr)
    commands = []

    def run(label, command, directory=preflight, timeout=180):
        command = [str(value) for value in command]
        started = datetime.datetime.now(datetime.timezone.utc).isoformat()
        timed_out = False
        with (directory / (label + '.stdout')).open('xb') as stdout, \
             (directory / (label + '.stderr')).open('xb') as stderr:
            process = subprocess.Popen(command, stdout=stdout, stderr=stderr,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
            try:
                code = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                timed_out = True
                killed = subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                    capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
                (directory / (label + '-timeout-kill.stdout')).write_bytes(killed.stdout)
                (directory / (label + '-timeout-kill.stderr')).write_bytes(killed.stderr)
                code = process.wait(timeout=30)
        record = dict(Name=label, Arguments=command, ProcessId=process.pid, ExitCode=code,
            TimedOut=timed_out, StartedUtc=started,
            FinishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
        write(directory / (label + '-command.json'), record)
        commands.append(record)
        print(label, 'exit', code, flush=True)
        assert code == 0 and not timed_out, f'{label} failed; retained command and logs'

    run('behavior-reverification', [sys.executable, '-B', scripts / 'Verify-AnimationFullAppValidation.py',
        '--snapshot', validation, '--output', preflight / 'behavior-reverified.json'])
    desktop = module('fullapp_desktop', scripts / 'FullAppDesktop.py')
    topology_capture = root / 'artifacts/performance' / gate['Cases'][0]['Capture']
    native_manifest = topology_capture / 'native-evidence.csv'
    witness = next(row for row in gate['Inputs'] if pathlib.Path(row['Path']).resolve() == native_manifest.resolve())
    assert sha(native_manifest) == witness['SHA256']
    with native_manifest.open(encoding='utf-8-sig', newline='') as stream:
        summary_witness = next(row for row in csv.DictReader(stream) if row['Path'].replace('\\', '/') == 'run-1/summary.json')
    topology_summary_path = topology_capture / 'native/run-1/summary.json'
    assert sha(topology_summary_path) == summary_witness['SHA256']
    topology_summary = read(topology_summary_path)
    expected_workareas = [topology_summary['Displays'][0]['WorkArea']]
    expected_dpis = sorted({dpi for config in behavior['Configurations'] for dpi in config['NativeDpi']})
    current_desktop = desktop.snapshot()
    issues = desktop.control_failures(current_desktop, expected_dpis, expected_workareas)
    write(preflight / 'native-desktop.json', dict(Current=current_desktop, ExpectedDpi=expected_dpis,
        ExpectedWorkAreas=expected_workareas, Failures=issues, ProbeSHA256=sha(scripts / 'FullAppDesktop.py'),
        TopologySummaryPath=str(topology_summary_path), TopologySummarySHA256=sha(topology_summary_path)))
    if issues:
        write(base / 'capture-run.json', dict(PerfId='PERF-010', Stage='Full-app CPU/GPU/presentation measurements',
            CapturePassed=False, StageAccepted=False, Error='Native desktop controls: ' + '; '.join(issues),
            Commands=commands, WprRecordingStarted=False, LedgerAppend=False, ProductionDeltaThisCapture=False,
            MsixInstalled=False, MsixLaunched=False, Published=False))
        write(base / 'preflight-evidence.json', [dict(Path=p.relative_to(preflight).as_posix(),
            Bytes=p.stat().st_size, SHA256=sha(p)) for p in sorted(preflight.rglob('*')) if p.is_file()])
        raise RuntimeError('Native desktop preflight rejected before tracing or application launch: ' + '; '.join(issues))
    run('file-inventory', ['rg', '--files', '--hidden', '--no-ignore'], timeout=600)
    inventory = (preflight / 'file-inventory.stdout').read_text(encoding='utf-8').splitlines()
    etls = []
    for name in sorted(inventory):
        if not args.process_cpu_only and pathlib.Path(name).suffix.lower() == '.etl':
            path = root / name
            etls.append(dict(Path=name, Bytes=path.stat().st_size, SHA256=sha(path)))
    write(preflight / 'etl-inventory.json', etls)
    print('Preflight retained', len(inventory), 'paths and', len(etls), 'ETL hashes', flush=True)
    binaries = {}
    for project in ['FancyWM.FullAppHarness', 'FancyWM.Perf010Targets']:
        name = 'Release-' + project
        records = read(build / 'validation' / (name + '-binary-manifest.json'))
        source = build / ('binaries-' + name)
        verifier.verify_files(source, records)
        archive = base / ('binaries-' + name)
        shutil.copytree(source, archive)
        verifier.verify_files(archive, records)
        write(base / (name + '-binary-manifest.json'), records)
        binaries[project] = archive / (project + '.exe')

    baseline = root / 'artifacts/performance/FWM-ANIMATION-NATIVE-BASELINE-20260910-N3/native/builtin-profiles.wprp'
    capture_profile = native / 'builtin-profiles.wprp'
    shutil.copyfile(baseline, capture_profile)
    markers = native / 'AnimationFullApp.wprp'
    shutil.copyfile(scripts / markers.name, markers)
    assert sha(capture_profile) == sha(baseline)
    assert sha(markers) == sha(base / 'source/scripts/performance' / markers.name)
    name = ET.parse(capture_profile).getroot().find('Profiles/Profile').attrib['Name']
    profile_spec = str(capture_profile) + '!' + name
    run('export-current-profiles', [wpr, '-exportprofile', 'CPU+GPU+DesktopComposition',
        preflight / 'current-builtin-profiles.wprp', '-filemode'])
    run('profile-details', [wpr, '-profiledetails', profile_spec, '-filemode'])
    run('environment', ['pwsh', '-NoProfile', '-Command',
        '[ordered]@{ RecordedUtc=[DateTimeOffset]::UtcNow; Interactive=[Environment]::UserInteractive; '
        'OS=[Environment]::OSVersion.VersionString; LogicalProcessors=[Environment]::ProcessorCount; '
        'Video=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate); '
        'Processes=@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CreationDate,ThreadCount) } | ConvertTo-Json -Depth 5'])
    run('token-groups', ['whoami', '/groups'])
    run('token-privileges', ['whoami', '/priv'])
    assert read(preflight / 'environment.stdout')['Interactive']
    write(base / 'provenance.json', dict(BuildSnapshot=str(build),
        BuildManifestSHA256=sha(build / 'manifest.csv'), BuildValidationSHA256=sha(build / 'development-validation.json'),
        BehaviorReceipt=str(behavior_path), BehaviorReceiptSHA256=sha(behavior_path),
        TopologyReceipt=str(args.topology_receipt.resolve()), TopologyReceiptSHA256=sha(args.topology_receipt),
        Fixture=provenance['Fixture'], ProductionChangesFromTopology=provenance['ProductionChangesFromTopology'],
        RunnerSHA256=sha(pathlib.Path(__file__)), Runs=args.runs, Iterations=args.iterations, Warmups=2,
        Mode='measurement', Configuration='Release', FullApplication=True, Elevated=elevated,
        ProcessCpuOnly=args.process_cpu_only, EtlInventoryCollected=not args.process_cpu_only,
        BuiltinProfileSource=str(baseline), BuiltinProfileSHA256=sha(capture_profile),
        MarkerProfileSHA256=sha(markers), LedgerBeforeSHA256=sha(root / 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'),
        Tools=[dict(Path=str(path), SHA256=sha(path)) for path in [wpr, xperf, pathlib.Path(sys.executable)]]))

    kernel = C.WinDLL('kernel32')
    kernel.SetThreadExecutionState.argtypes = [C.c_uint]
    kernel.SetThreadExecutionState.restype = C.c_uint
    previous = kernel.SetThreadExecutionState(0x80000003)
    assert previous
    owned_recording = False
    passed = False
    error = None
    trace = native / 'fullapp.etl'
    def check_desktop(label):
        current = desktop.snapshot()
        problems = desktop.control_failures(current, expected_dpis, expected_workareas)
        write(native / (label + '-desktop.json'), dict(Current=current, Failures=problems))
        assert not problems, 'Native desktop controls changed: ' + '; '.join(problems)
    try:
        write(native / 'windows-before.json', helpers.inventory_windows())
        check_desktop('before-tracing')
        run('wpr-before-start', [wpr, '-status'])
        assert b'not recording' in (preflight / 'wpr-before-start.stdout').read_bytes()
        try:
            if not args.process_cpu_only:
                run('wpr-start', [wpr, '-start', profile_spec, '-start', str(markers) + '!AnimationFullApp',
                    '-filemode', '-instancename', args.snapshot_id], native)
                owned_recording = True
            for index in range(1, args.runs + 1):
                path = native / f'run-{index}'
                check_desktop(f'run-{index}-before')
                run(f'run-{index}', [binaries['FancyWM.FullAppHarness'], path,
                    binaries['FancyWM.Perf010Targets'], args.iterations, 'measurement'], native)
                check_desktop(f'run-{index}-after')
                summary = read(path / 'fullapp-summary.json')
                assert summary['Error'] is None and not summary['Validation']
                assert summary['EtwProviderEnabled'] == (not args.process_cpu_only)
                assert all(summary[key] for key in ['MainWindowShutdownCompleted', 'AppTerminationCompleted', 'TargetProcessExited', 'PerMonitorV2'])
                assert read(path / 'targets/cleanup.json')['AllDestroyed']
        finally:
            try:
                if owned_recording:
                    run('wpr-stop', [wpr, '-stop', trace, '-instancename', args.snapshot_id], native, timeout=900)
            finally:
                run('wpr-after', [wpr, '-status', '-instancename', args.snapshot_id], native)
                assert b'not recording' in (native / 'wpr-after.stdout').read_bytes(), 'Preserve and stop the owned instance to a fresh ETL; do not cancel'
        exports = [] if args.process_cpu_only else [
            ('trace-header', ['tracestats']), ('trace-stats', ['tracestats', '-detail']),
            ('cpu-by-thread', ['cswitch', '-process', '-thread']), ('sampled-profile', ['profile', '-detail']),
            ('markers', ['dumper', '-provider', '{a5bbdb9a-4198-4e10-b6d9-8a50ef9e7656}', '-add_fieldnames'])]
        for label, options in exports:
            extension = '.txt' if label.startswith('trace-') else '.csv'
            run(label, [xperf, '-i', trace, '-o', native / (label + extension), '-a', *options], native, timeout=600)
        passed = True
    except BaseException as failure:
        error = repr(failure)
        raise
    finally:
        write(native / 'windows-after.json', helpers.inventory_windows())
        restored = kernel.SetThreadExecutionState(previous)
        write(native / 'execution-state.json', dict(Previous=f'0x{previous:08X}', RestoreReturn=f'0x{restored:08X}'))
        write(base / 'capture-run.json', dict(PerfId='PERF-010', Stage='Full-app CPU/GPU/presentation measurements',
            CapturePassed=passed, StageAccepted=False, Error=error, Commands=commands,
            WprRecordingStarted=owned_recording, LedgerAppend=False, ProductionDeltaThisCapture=False,
            MsixInstalled=False, MsixLaunched=False, Published=False))
        for folder in [native, preflight]:
            write(base / (folder.name + '-evidence.json'), [dict(Path=p.relative_to(folder).as_posix(),
                Bytes=p.stat().st_size, SHA256=sha(p)) for p in sorted(folder.rglob('*')) if p.is_file()])
    print('Capture retained; independent CPU/GPU/presentation verification is required.', flush=True)


if __name__ == '__main__':
    main()
