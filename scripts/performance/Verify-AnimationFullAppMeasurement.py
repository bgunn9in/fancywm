"""Verify full-app capture provenance, process CPU, boundaries and native geometry.

This does not infer hardware presentation from DwmFlush or claim GPU attribution.
Process-only captures are explicitly incomplete for the ETW measurement stage.
"""
import argparse
import collections
import csv
import datetime
import hashlib
import importlib.util
import json
import math
import pathlib
import re
import statistics


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def write_csv(path, rows):
    assert rows
    with path.open('x', encoding='utf-8', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def validate_run(path, command, binaries, iterations, expect_etw, expected_dpi):
    summary = read(path / 'fullapp-summary.json')
    assert summary['Error'] is None and summary['ProcessId'] == command['ProcessId']
    assert not summary['Validation'] and summary['EtwProviderEnabled'] == expect_etw
    assert all(summary[key] for key in ['PerMonitorV2', 'MainWindowShutdownCompleted',
                                      'AppTerminationCompleted', 'TargetProcessExited'])
    assert summary['Architecture'] == 'X64' and not summary['IsPackaged']
    assert summary['Iterations'] == iterations and summary['Frequency'] > 0
    assert summary['ActualAnimationDurationMs'] == 100 and summary['FixtureAutoSplitCount'] == 5
    assert summary['TargetMinimumSize'] == dict(Width=48, Height=48)
    assert pathlib.Path(summary['IsolatedSettingsPath']).resolve() == path / 'settings.json'
    assert pathlib.Path(summary['ProcessPath']).resolve() == pathlib.Path(command['Arguments'][0]).resolve()
    for assembly in summary['Assemblies']:
        assert binaries[str(pathlib.Path(assembly['Location']).resolve())] == assembly['SHA256']
    cleanup = read(path / 'targets/cleanup.json')
    assert cleanup['AllDestroyed'] and cleanup['ProcessId'] != summary['ProcessId']
    assert not read(path / 'targets/interruptions.json')
    created = [r for r in summary['Observations'] if r['Kind'] == 'Created']
    assert [r['Count'] for r in created] == [1, 10, 50]
    assert all(r['Targets']['Success'] and len(r['Targets']['Windows']) == r['Count'] for r in created)
    transitions = [r for r in summary['Observations'] if r['Kind'] == 'Transition']
    assert len(transitions) == 3 * (iterations + 2)
    assert len(summary['Observations']) == len(created) + len(transitions)
    identifiers = [r['Scenario'] for r in transitions]
    assert identifiers == sorted(set(identifiers)) and identifiers[0] > 0
    messages = read(path / 'targets/messages.json')
    assert [m['Qpc'] for m in messages] == sorted(m['Qpc'] for m in messages)
    rows, owners, geometry_keys = [], [], []
    by_scenario = collections.defaultdict(list)
    for message in messages:
        by_scenario[message['Scenario']].append(message)
    for group in created:
        count = group['Count']
        owned = {r['Hwnd']: r for r in group['Targets']['Windows']}
        assert len(owned) == count
        assert sorted(r['Index'] for r in owned.values()) == list(range(count))
        assert len({r['OwnerThreadId'] for r in owned.values()}) == 1
        for handle, target in owned.items():
            assert target['OwnerThreadId'] > 0 and target['Dpi'] in expected_dpi
            owners.append(dict(Count=count, Target=target['Index'], Hwnd=handle,
                OwnerThreadId=target['OwnerThreadId'], ProcessId=cleanup['ProcessId'], Dpi=target['Dpi']))
        selected = [r for r in transitions if r['Count'] == count]
        assert [r['Iteration'] for r in selected] == list(range(-1, iterations + 1))
        for transition in selected:
            stamps = [transition[k] for k in ['StartQpc', 'SettingsPublishedQpc',
                'LayoutIdleObservedQpc', 'NativeVerifiedQpc', 'DwmFlushedQpc']]
            assert stamps == sorted(stamps) and stamps[0] < stamps[-1]
            assert transition['Measured'] == (transition['Iteration'] > 0)
            assert math.isfinite(transition['ProcessCpuMs']) and transition['ProcessCpuMs'] >= 0
            geometry = transition['Geometry']
            assert len(geometry) == count and {r['Hwnd'] for r in geometry} == set(owned)
            mapped = []
            final_native = []
            native_messages = [m for m in by_scenario[transition['Scenario']]
                if stamps[0] <= m['Qpc'] <= stamps[-1] and m['Hwnd'] in owned and m['Flags'] & 3 != 3]
            for row in geometry:
                target = owned[row['Hwnd']]
                assert row['OwnerThreadId'] == target['OwnerThreadId'] and row['Dpi'] == target['Dpi']
                assert row['Visible'] and row['Cloaked'] == 0 and row['NativeEqualsExpected']
                assert row['Expected'] == row['Actual']
                rect = row['Actual']
                assert rect['Right'] - rect['Left'] == rect['Width'] > 0
                assert rect['Bottom'] - rect['Top'] == rect['Height'] > 0
                mapped.append((target['Index'], rect['Left'], rect['Top'], rect['Right'], rect['Bottom']))
                if not transition['Measured']:
                    continue
                window_messages = [m for m in native_messages if m['Hwnd'] == row['Hwnd']]
                assert window_messages and all(m['OwnerThreadId'] == row['OwnerThreadId']
                    and m['Target'] == target['Index'] for m in window_messages)
                moves = [m for m in window_messages if m['Flags'] & 2 == 0]
                sizes = [m for m in window_messages if m['Flags'] & 1 == 0]
                assert moves and sizes, 'The controlled padding change must move and resize every target'
                assert [moves[-1]['X'], moves[-1]['Y']] == [rect['Left'], rect['Top']]
                assert [sizes[-1]['Width'], sizes[-1]['Height']] == [rect['Width'], rect['Height']]
                final_native.append(max(moves[-1]['Qpc'], sizes[-1]['Qpc']))
            geometry_keys.append((count, transition['Iteration'], tuple(sorted(mapped))))
            if not transition['Measured']:
                continue
            scale = 1000 / summary['Frequency']
            rows.append(dict(ProcessId=summary['ProcessId'], TargetProcessId=cleanup['ProcessId'],
                Count=count, Scenario=transition['Scenario'], Iteration=transition['Iteration'],
                ProcessCpuMs=transition['ProcessCpuMs'],
                LayoutIdleObservedMs=(stamps[2] - stamps[0]) * scale,
                NativeVerifiedMs=(stamps[3] - stamps[0]) * scale,
                DwmFlushBoundaryMs=(stamps[4] - stamps[0]) * scale,
                LastNativeMessageObservedMs=(max(final_native) - stamps[0]) * scale,
                NativePositionMessages=len(native_messages), DispatcherNativeThreadId=summary['DispatcherNativeThreadId']))
    return summary, rows, owners, geometry_keys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    base = args.snapshot.resolve()
    assert not args.output.exists(), 'Fresh verification output required'
    args.output.mkdir(parents=True)
    capture = read(base / 'capture-run.json')
    assert capture['CapturePassed'] and capture['Error'] is None, 'Failed capture cannot be accepted: ' + str(capture['Error'])
    native = base / 'native'
    source = base / 'source/scripts/performance'
    helpers = load_module('capture_behavior_verifier', source / 'Verify-AnimationFullAppValidation.py')
    topology = load_module('capture_topology_verifier', source / 'Verify-AnimationTopology.py')
    assert sha(pathlib.Path(__file__)) == sha(source / pathlib.Path(__file__).name)
    topology.verify_manifest(base / 'manifest.csv', base / 'source')
    for folder in ['native', 'preflight']:
        helpers.verify_files(base / folder, read(base / (folder + '-evidence.json')))
    provenance = read(base / 'provenance.json')
    assert capture['CapturePassed'] and capture['Error'] is None and not capture['StageAccepted']
    assert not any(capture[k] for k in ['LedgerAppend', 'ProductionDeltaThisCapture', 'MsixInstalled', 'MsixLaunched', 'Published'])
    expect_etw = not provenance['ProcessCpuOnly']
    assert capture['WprRecordingStarted'] == expect_etw
    assert provenance['Runs'] == 5 and provenance['Iterations'] == 8 and provenance['Warmups'] == 2
    assert provenance['Mode'] == 'measurement' and provenance['Configuration'] == 'Release'
    assert sha(source / 'Capture-AnimationFullApp.py') == provenance['RunnerSHA256']
    behavior_path = pathlib.Path(provenance['BehaviorReceipt'])
    assert sha(behavior_path) == provenance['BehaviorReceiptSHA256']
    behavior = read(behavior_path)
    assert behavior['Verdict'] == 'PASS'
    behavior_snapshot = pathlib.Path(behavior['Snapshot'])
    assert sha(behavior_snapshot / 'validation-evidence.json') == behavior['EvidenceSHA256']
    helpers.verify_files(behavior_snapshot / 'validation', read(behavior_snapshot / 'validation-evidence.json'))
    gate_path = pathlib.Path(provenance['TopologyReceipt'])
    assert sha(gate_path) == provenance['TopologyReceiptSHA256']
    gate = read(gate_path)
    assert gate['Verdict'] == 'ACCEPT'
    desktop = load_module('desktop_controls', source / 'FullAppDesktop.py')
    desktop_receipt = read(base / 'preflight/native-desktop.json')
    assert sha(source / 'FullAppDesktop.py') == desktop_receipt['ProbeSHA256']
    expected_dpi = {dpi for config in behavior['Configurations'] for dpi in config['NativeDpi']}
    assert set(desktop_receipt['ExpectedDpi']) == expected_dpi
    topology_summary_path = pathlib.Path(desktop_receipt['TopologySummaryPath'])
    assert sha(topology_summary_path) == desktop_receipt['TopologySummarySHA256']
    native_manifest = topology_summary_path.parents[2] / 'native-evidence.csv'
    witness = next(r for r in gate['Inputs'] if pathlib.Path(r['Path']).resolve() == native_manifest.resolve())
    assert sha(native_manifest) == witness['SHA256']
    with native_manifest.open(encoding='utf-8-sig', newline='') as stream:
        summary_witness = next(r for r in csv.DictReader(stream) if r['Path'].replace('\\', '/') == 'run-1/summary.json')
    assert summary_witness['SHA256'] == desktop_receipt['TopologySummarySHA256']
    expected_areas = [read(topology_summary_path)['Displays'][0]['WorkArea']]
    assert desktop_receipt['ExpectedWorkAreas'] == expected_areas
    assert not desktop_receipt['Failures'] and not desktop.control_failures(desktop_receipt['Current'], expected_dpi, expected_areas)
    build = pathlib.Path(provenance['BuildSnapshot'])
    assert sha(build / 'manifest.csv') == provenance['BuildManifestSHA256']
    assert sha(build / 'development-validation.json') == provenance['BuildValidationSHA256']
    runner = load_module('behavior_runner', source / 'Run-AnimationFullAppValidation.py')
    assert runner.production_manifest(base) == runner.production_manifest(build)
    for name, digest in provenance['Fixture'].items():
        assert sha(base / 'source' / name) == sha(build / 'source' / name) == digest
    binaries = {}
    for project in ['FancyWM.FullAppHarness', 'FancyWM.Perf010Targets']:
        name = 'Release-' + project
        records = read(base / (name + '-binary-manifest.json'))
        assert records == read(build / 'validation' / (name + '-binary-manifest.json'))
        archive = base / ('binaries-' + name)
        helpers.verify_files(archive, records)
        binaries.update({str((archive / r['Path']).resolve()): r['SHA256'] for r in records})
    commands = capture['Commands']
    assert all(r['ExitCode'] == 0 and not r['TimedOut'] for r in commands)
    processes = [r for r in commands if re.fullmatch(r'run-\d+', r['Name'])]
    assert [r['Name'] for r in processes] == [f'run-{i}' for i in range(1, 6)]
    assert all(datetime.datetime.fromisoformat(a['FinishedUtc']) <= datetime.datetime.fromisoformat(b['StartedUtc'])
        for a, b in zip(processes, processes[1:]))
    before = read(native / 'windows-before.json')
    after = read(native / 'windows-after.json')
    for label in ['before-tracing'] + [f'run-{i}-{boundary}' for i in range(1, 6) for boundary in ['before', 'after']]:
        control = read(native / (label + '-desktop.json'))
        assert not control['Failures'] and not desktop.control_failures(control['Current'], expected_dpi, expected_areas)
    rows, owners, expected_markers, fixed_geometry = [], [], {}, None
    for index, command in enumerate(processes, 1):
        path = native / f'run-{index}'
        host = base / 'binaries-Release-FancyWM.FullAppHarness/FancyWM.FullAppHarness.exe'
        targets = base / 'binaries-Release-FancyWM.Perf010Targets/FancyWM.Perf010Targets.exe'
        assert command['Arguments'] == [str(host), str(path), str(targets), '8', 'measurement']
        summary, run_rows, run_owners, geometry = validate_run(path, command, binaries, 8, expect_etw, expected_dpi)
        assert [r['Scenario'] for r in summary['Observations'] if r['Kind'] == 'Transition'] == list(range(1, 31))
        rows.extend(dict(Run=index, **row) for row in run_rows)
        owners.extend(dict(Run=index, **row) for row in run_owners)
        if fixed_geometry is None:
            fixed_geometry = geometry
        assert geometry == fixed_geometry, 'Controlled final rectangles differ across processes'
        pids = {summary['ProcessId'], run_owners[0]['ProcessId']}
        assert not any(row['ProcessId'] in pids for row in before + after)
        for transition in (row for row in summary['Observations'] if row['Kind'] == 'Transition'):
            for phase, key in enumerate(['StartQpc', 'SettingsPublishedQpc', 'LayoutIdleObservedQpc',
                                        'NativeVerifiedQpc', 'DwmFlushedQpc'], 1):
                expected_markers[(summary['ProcessId'], transition['Scenario'], phase)] = transition[key]
    assert len(rows) == 120 and len(owners) == 305 and len(expected_markers) == 750
    markers = {}
    if expect_etw:
        header = (native / 'trace-header.txt').read_text()
        assert all(re.search(r'^Total # Lost ' + kind + r'\s*:\s*0\s*$', header, re.M) for kind in ['Buffers', 'Events'])
        assert b'not recording' in (native / 'wpr-after.stdout').read_bytes()
        assert sha(native / 'builtin-profiles.wprp') == provenance['BuiltinProfileSHA256']
        assert sha(native / 'AnimationFullApp.wprp') == provenance['MarkerProfileSHA256']
        with (native / 'markers.csv').open(encoding='utf-8-sig', newline='') as stream:
            for row in csv.reader(stream, skipinitialspace=True):
                if row and row[0] == 'FancyWM-Perf010-FullApp/Boundary/' and row[1].isdigit():
                    payload = dict(value.split(' : ', 1) for value in row[9:] if ' : ' in value)
                    key = (int(row[2].rsplit('(', 1)[1].rstrip(') ')), int(payload['scenario']), int(payload['phase']))
                    assert key not in markers
                    markers[key] = int(payload['qpc'])
        assert markers == expected_markers
    else:
        assert not (native / 'fullapp.etl').exists() and not (native / 'markers.csv').exists()
        assert not any(r['Name'] in ['wpr-start', 'wpr-stop'] for r in commands)
    write_csv(args.output / 'transitions.csv', rows)
    write_csv(args.output / 'hwnd-owners.csv', owners)
    aggregates = []
    for count in [1, 10, 50]:
        selected = [r for r in rows if r['Count'] == count]
        assert len(selected) == 40
        aggregates.append(dict(Count=count, Transitions=len(selected),
            Medians={key: statistics.median(r[key] for r in selected) for key in [
                'ProcessCpuMs', 'LayoutIdleObservedMs', 'NativeVerifiedMs', 'DwmFlushBoundaryMs',
                'LastNativeMessageObservedMs', 'NativePositionMessages']}))
    result = dict(Verdict='CAPTURE_VERIFIED_ETW_ANALYSIS_PENDING' if expect_etw else 'PROCESS_CPU_VERIFIED_ETW_PENDING',
        StageAccepted=False, WholeIdStatus='IN_PROGRESS', Snapshot=str(base), Processes=5,
        MeasuredTransitions=120, NativeFinalTargets=2440, EtwMarkers=len(markers),
        ProcessCpu='MEASURED', ThreadScheduling='NOT_MEASURED', AttributedGpu='NOT_MEASURED',
        HardwarePresentation='NOT_MEASURED', NativeGeometryAndCleanup=True, NativeDpi=sorted(expected_dpi),
        Aggregates=aggregates, CaptureSHA256=sha(base / 'capture-run.json'),
        ProvenanceSHA256=sha(base / 'provenance.json'), RawManifestSHA256=sha(base / 'native-evidence.json'),
        VerifierSHA256=sha(pathlib.Path(__file__)),
        Outputs=[dict(Path=str(p.resolve()), Bytes=p.stat().st_size, SHA256=sha(p))
            for p in sorted(args.output.glob('*.csv'))],
        Limits=['Process.TotalProcessorTime is quantized process CPU over submission through DwmFlush observation.',
            'Dispatcher layout-idle observation is not individual animation Task completion.',
            'Native message observations, GetWindowRect geometry and DwmFlush are not hardware presentation.',
            'No CPU speedup, attributed GPU, multi-DPI or cross-application generalization.'])
    with (args.output / 'capture-verification.json').open('x', encoding='utf-8') as stream:
        json.dump(result, stream, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
