"""Compare the validated overlay builds in five alternating native process pairs.

Uses the unchanged full-app measurement fixture and frozen complete Release
archives. Records quantized process CPU; ETW/GPU/hardware presentation stay pending.
"""
import argparse
import csv
import ctypes as C
import datetime
import importlib.util
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--snapshot-id', required=True)
    p.add_argument('--baseline-behavior', type=pathlib.Path, required=True)
    p.add_argument('--candidate-behavior', type=pathlib.Path, required=True)
    a = p.parse_args()
    root = pathlib.Path.cwd()
    scripts = root / 'scripts/performance'
    helper = module('behavior_runner', scripts / 'Run-AnimationFullAppValidation.py')
    verifier = module('behavior_verifier', scripts / 'Verify-AnimationFullAppValidation.py')
    measurement = module('measurement', scripts / 'Verify-AnimationFullAppMeasurement.py')
    desktop = module('desktop', scripts / 'FullAppDesktop.py')
    sha, read, write = helper.sha, helper.read, helper.write
    assert re.fullmatch('[A-Z0-9-]+', a.snapshot_id)
    base = root / 'artifacts/performance' / a.snapshot_id
    assert not base.exists(), 'Fresh immutable ID required'
    snapshot = subprocess.run(['pwsh', '-NoProfile', '-File', str(scripts / 'Save-ImplementationSnapshot.ps1'),
        '-SnapshotId', a.snapshot_id], capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    assert base.is_dir()
    (base / 'snapshot.stdout').write_bytes(snapshot.stdout)
    (base / 'snapshot.stderr').write_bytes(snapshot.stderr)
    assert snapshot.returncode == 0
    preflight, native = base / 'preflight', base / 'native'
    preflight.mkdir()
    native.mkdir()
    inputs, binaries, productions, fixtures, records = {}, {}, {}, {}, {}
    for variant, path in [('A', a.baseline_behavior.resolve()), ('B', a.candidate_behavior.resolve())]:
        command = [sys.executable, '-B', str(scripts / 'Verify-AnimationFullAppValidation.py'),
            '--snapshot', str(path.parents[1]), '--output', str(preflight / (variant + '-behavior.json'))]
        result = subprocess.run(command, capture_output=True, timeout=180, creationflags=subprocess.CREATE_NO_WINDOW)
        (preflight / (variant + '-behavior.stdout')).write_bytes(result.stdout)
        (preflight / (variant + '-behavior.stderr')).write_bytes(result.stderr)
        assert result.returncode == 0 and sha(preflight / (variant + '-behavior.json')) == sha(path)
        behavior = read(path)
        assert behavior['Verdict'] == 'PASS'
        assert {dpi for c in behavior['Configurations'] for dpi in c['NativeDpi']} == {96}
        provenance = read(path.parents[1] / 'provenance.json')
        build = pathlib.Path(provenance['BuildSnapshot'])
        assert sha(build / 'manifest.csv') == provenance['BuildManifestSHA256']
        productions[variant] = helper.production_manifest(build)
        for name, digest in productions[variant].items():
            assert sha(build / 'source' / name) == digest
            if variant == 'B':
                assert sha(root / name) == digest
        fixtures[variant] = provenance['Fixture']
        for name, digest in fixtures[variant].items():
            assert sha(build / 'source' / name) == sha(root / name) == digest
        inputs[variant] = dict(BuildSnapshot=str(build), BuildManifestSHA256=sha(build / 'manifest.csv'),
            BehaviorReceipt=str(path), BehaviorSHA256=sha(path), TopologyReceipt=provenance['TopologyReceipt'],
            TopologySHA256=provenance['TopologyReceiptSHA256'])
        binaries[variant], records[variant] = {}, {}
        for project in ['FancyWM.FullAppHarness', 'FancyWM.Perf010Targets']:
            name = 'Release-' + project
            source = build / ('binaries-' + name)
            rows = read(build / 'validation' / (name + '-binary-manifest.json'))
            verifier.verify_files(source, rows)
            archive = base / ('binaries-' + variant + '-' + name)
            shutil.copytree(source, archive)
            verifier.verify_files(archive, rows)
            write(base / (variant + '-' + name + '-manifest.json'), rows)
            binaries[variant].update({str((archive / r['Path']).resolve()): r['SHA256'] for r in rows})
            records[variant][project] = {r['Path']: r['SHA256'] for r in rows}
    assert fixtures['A'] == fixtures['B'] and len(fixtures['A']) == 11
    assert inputs['A']['TopologySHA256'] == inputs['B']['TopologySHA256']
    changes = [dict(Path=n, BaselineSHA256=productions['A'].get(n), CandidateSHA256=productions['B'].get(n))
        for n in sorted(productions['A'].keys() | productions['B'].keys()) if productions['A'].get(n) != productions['B'].get(n)]
    assert [r['Path'] for r in changes] == ['FancyWM/TilingOverlayRenderer.cs',
        'FancyWM/TilingService.Private.cs', 'FancyWM/TilingService.cs']
    gate_path = pathlib.Path(inputs['A']['TopologyReceipt'])
    assert sha(gate_path) == inputs['A']['TopologySHA256']
    gate = read(gate_path)
    capture = root / 'artifacts/performance' / gate['Cases'][0]['Capture']
    witness = next(r for r in gate['Inputs'] if pathlib.Path(r['Path']).resolve() == (capture / 'native-evidence.csv').resolve())
    assert sha(capture / 'native-evidence.csv') == witness['SHA256']
    with (capture / 'native-evidence.csv').open(encoding='utf-8-sig', newline='') as stream:
        witness = next(r for r in csv.DictReader(stream) if r['Path'].replace('\\', '/') == 'run-1/summary.json')
    topology_summary = capture / 'native/run-1/summary.json'
    assert sha(topology_summary) == witness['SHA256']
    areas = [read(topology_summary)['Displays'][0]['WorkArea']]
    differences = {project: [n for n in sorted(records['A'][project].keys() | records['B'][project].keys())
        if records['A'][project].get(n) != records['B'][project].get(n)] for project in records['A']}
    controls = dict(Pairs=5, Iterations=8, Warmups=2, Counts=[1, 10, 50], Configuration='Release',
        Mode='measurement', EnvironmentOverrides={'DOTNET_TieredCompilation': '0', 'COMPlus_TieredCompilation': '0'},
        ExpectedDpi=[96], ExpectedWorkAreas=areas, TopologySummary=str(topology_summary),
        TopologySummarySHA256=sha(topology_summary), InputSent=False, Etw=False)
    write(base / 'provenance.json', dict(Inputs=inputs, Fixture=fixtures['A'], ProductionChanges=changes,
        BinaryDifferences=differences, Controls=controls, RunnerSHA256=sha(pathlib.Path(__file__)),
        LedgerSHA256=sha(root / 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv')))
    def observe(label):
        current = desktop.snapshot()
        failures = desktop.control_failures(current, [96], areas)
        write(native / (label + '-desktop.json'), dict(Current=current, Failures=failures))
        assert not failures, 'Desktop controls changed: ' + '; '.join(failures)
    def wpr_status(label):
        result = subprocess.run(['wpr', '-status'], capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
        (native / (label + '-wpr.stdout')).write_bytes(result.stdout)
        (native / (label + '-wpr.stderr')).write_bytes(result.stderr)
        assert result.returncode == 0 and b'not recording' in result.stdout
    commands, rows, errors = [], [], []
    passed, reference = False, None
    kernel = C.WinDLL('kernel32')
    kernel.SetThreadExecutionState.argtypes = [C.c_uint]
    kernel.SetThreadExecutionState.restype = C.c_uint
    previous = kernel.SetThreadExecutionState(0x80000003)
    assert previous
    try:
        write(native / 'windows-before.json', helper.inventory_windows())
        observe('before')
        wpr_status('before')
        env = os.environ.copy()
        env.update(controls['EnvironmentOverrides'])
        for pair in range(1, 6):
            for variant in ('AB' if pair % 2 else 'BA'):
                label = f'pair-{pair}-{variant}'
                path = native / label
                observe(label + '-before')
                wpr_status(label + '-before')
                host = base / ('binaries-' + variant + '-Release-FancyWM.FullAppHarness/FancyWM.FullAppHarness.exe')
                targets = base / ('binaries-' + variant + '-Release-FancyWM.Perf010Targets/FancyWM.Perf010Targets.exe')
                command = [str(host), str(path), str(targets), '8', 'measurement']
                started = datetime.datetime.now(datetime.timezone.utc).isoformat()
                timed_out = False
                with (native / (label + '.stdout')).open('xb') as stdout, (native / (label + '.stderr')).open('xb') as stderr:
                    process = subprocess.Popen(command, stdout=stdout, stderr=stderr, env=env, creationflags=subprocess.CREATE_NO_WINDOW)
                    try:
                        code = process.wait(timeout=180)
                    except subprocess.TimeoutExpired:
                        timed_out = True
                        killed = subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True,
                            creationflags=subprocess.CREATE_NO_WINDOW)
                        (native / (label + '-timeout.stdout')).write_bytes(killed.stdout)
                        (native / (label + '-timeout.stderr')).write_bytes(killed.stderr)
                        code = process.wait(timeout=30)
                record = dict(Name=label, Pair=pair, Variant=variant, Arguments=command, ProcessId=process.pid,
                    ExitCode=code, TimedOut=timed_out, StartedUtc=started,
                    FinishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(), EnvironmentOverrides=controls['EnvironmentOverrides'])
                commands.append(record)
                write(native / (label + '-command.json'), record)
                assert code == 0 and not timed_out, label + ' failed; evidence retained'
                observe(label + '-after')
                summary, data, owners, geometry = measurement.validate_run(path, record, binaries[variant], 8, False, {96})
                assert summary['FixtureLayoutPreparation'] == 'SettledEvenFlexAndTargetOrder'
                if reference is None:
                    reference = geometry
                assert geometry == reference, 'Native geometry differs across builds/processes'
                rows.extend(dict(Pair=pair, Variant=variant, **r) for r in data)
                print(label, 'PASS: 24 measured transitions, equal geometry, native cleanup', flush=True)
        assert len(rows) == 240
        wpr_status('after')
        measurement.write_csv(base / 'transitions.csv', rows)
        passed = True
    except BaseException as error:
        errors.append(repr(error))
        raise
    finally:
        write(native / 'windows-after.json', helper.inventory_windows())
        restored = kernel.SetThreadExecutionState(previous)
        write(native / 'execution-state.json', dict(Previous=previous, RestoreReturn=restored))
        write(base / 'comparison-run.json', dict(ProcessesPassed=passed, StageAccepted=False, Commands=commands,
            Errors=errors, WprRecordingStarted=False, InputSent=False, LedgerAppend=False, MsixInstalled=False,
            MsixLaunched=False, Published=False))
        for folder in [native, preflight]:
            write(base / (folder.name + '-evidence.json'), [dict(Path=p.relative_to(folder).as_posix(),
                Bytes=p.stat().st_size, SHA256=sha(p)) for p in sorted(folder.rglob('*')) if p.is_file()])
    print('Comparison retained; independent verification is required.', flush=True)


if __name__ == '__main__':
    main()
