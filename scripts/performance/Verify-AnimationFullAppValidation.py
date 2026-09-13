"""Verify retained full-app behavior evidence; CPU/GPU/presentation remain separate."""
import argparse
import collections
import csv
import datetime
import hashlib
import json
import pathlib


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def verify_files(base, records):
    expected = {row['Path'].replace('\\', '/') for row in records}
    assert len(expected) == len(records), 'Duplicate evidence paths'
    actual = {p.relative_to(base).as_posix() for p in base.rglob('*') if p.is_file()}
    assert actual == expected, str(base)
    for row in records:
        path = base / row['Path']
        assert path.stat().st_size == row['Bytes'] and sha(path) == row['SHA256'], str(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    base = args.snapshot.resolve()
    assert not args.output.exists(), 'Fresh verification output required'
    run = read(base / 'validation-run.json')
    provenance = read(base / 'provenance.json')
    assert run['ProcessesPassed'] and not run['StageAccepted']
    assert not any(run[key] for key in ['LedgerAppend', 'MsixInstalled', 'MsixLaunched', 'WprRecordingStarted'])
    assert run['ProductionChangesFromTopology'] == provenance['ProductionChangesFromTopology']
    assert run['ProductionDelta'] == bool(provenance['ProductionChangesFromTopology'])
    with (base / 'manifest.csv').open(encoding='utf-8-sig', newline='') as stream:
        for row in csv.DictReader(stream):
            assert sha(base / 'source' / row['Path']) == row['SHA256'], row['Path']
    build = pathlib.Path(provenance['BuildSnapshot'])
    assert sha(build / 'manifest.csv') == provenance['BuildManifestSHA256']
    assert sha(build / 'development-validation.json') == provenance['BuildValidationSHA256']
    assert read(build / 'development-validation.json')['BuildsPassed']
    gate_path = pathlib.Path(provenance['TopologyReceipt'])
    assert sha(gate_path) == provenance['TopologyReceiptSHA256']
    gate = read(gate_path)
    assert gate['Verdict'] == 'ACCEPT' and gate['HardwarePresentationTargets'] == 7320
    for name, digest in provenance['Fixture'].items():
        assert sha(base / 'source' / name) == sha(build / 'source' / name) == digest, name
    assert sha(base / 'source/scripts/performance/Run-AnimationFullAppValidation.py') == provenance['RunnerSHA256']
    verify_files(base / 'validation', read(base / 'validation-evidence.json'))
    commands = [row for row in run['Commands'] if row['Name'] in ['Debug', 'Release']]
    assert [row['Name'] for row in commands] == ['Debug', 'Release']
    assert datetime.datetime.fromisoformat(commands[0]['FinishedUtc']) <= datetime.datetime.fromisoformat(commands[1]['StartedUtc'])
    before = read(base / 'validation/windows-before.json')
    after = read(base / 'validation/windows-after.json')
    results = []
    for command in commands:
        config = command['Name']
        assert command['ExitCode'] == 0 and not command['TimedOut']
        binaries = {}
        for project in ['FancyWM.FullAppHarness', 'FancyWM.Perf010Targets']:
            archive = base / ('binaries-' + config + '-' + project)
            records = read(base / (config + '-' + project + '-binary-manifest.json'))
            verify_files(archive, records)
            binaries.update({str((archive / row['Path']).resolve()): row['SHA256'] for row in records})
        path = base / 'validation' / config
        summary = read(path / 'fullapp-summary.json')
        assert summary['Error'] is None and summary['ProcessId'] == command['ProcessId']
        assert all(summary[key] for key in ['Validation', 'PerMonitorV2', 'MainWindowShutdownCompleted',
                                          'AppTerminationCompleted', 'TargetProcessExited'])
        assert summary['Architecture'] == 'X64' and not summary['IsPackaged']
        assert not summary['EtwProviderEnabled'], 'Behavior receipts do not represent an ETW capture'
        assert pathlib.Path(summary['IsolatedSettingsPath']).resolve() == path / 'settings.json'
        for assembly in summary['Assemblies']:
            assert binaries[str(pathlib.Path(assembly['Location']).resolve())] == assembly['SHA256']
        observations = summary['Observations']
        def one(kind):
            matches = [row for row in observations if row['Kind'] == kind]
            assert len(matches) == 1, kind
            return matches[0]
        created = [row for row in observations if row['Kind'] == 'Created']
        assert [row['Count'] for row in created] == [1, 10, 50]
        owned = {row['Hwnd'] for item in created for row in item['Targets']['Windows']}
        def geometry(rows, count):
            assert len(rows) == count and len({row['Hwnd'] for row in rows}) == count
            for row in rows:
                assert row['Hwnd'] in owned and row['OwnerThreadId'] > 0 and row['Dpi'] > 0
                assert row['Visible'] and row['Cloaked'] == 0 and row['NativeEqualsExpected']
                assert all(row['Actual'][key] == row['Expected'][key] for key in ['Left', 'Top', 'Right', 'Bottom'])
        transitions = [row for row in observations if row['Kind'] == 'Transition']
        assert len(transitions) == 3 * (summary['Iterations'] + 2)
        for count in [1, 10, 50]:
            selected = [row for row in transitions if row['Count'] == count]
            assert [row['Iteration'] for row in selected] == list(range(-1, summary['Iterations'] + 1))
            for row in selected:
                stamps = [row[key] for key in ['StartQpc', 'SettingsPublishedQpc', 'LayoutIdleObservedQpc', 'NativeVerifiedQpc', 'DwmFlushedQpc']]
                assert stamps == sorted(stamps) and row['Measured'] == (row['Iteration'] > 0)
                geometry(row['Geometry'], count)
        interactive = one('InteractiveMouseAndDirectHotkey')
        submitted = one('InteractiveCommandSubmitted')
        assert interactive['Passed'] and interactive['HotkeyNotifications'] == 1
        assert interactive['SentMouseInputs'] == 3 and interactive['SentKeyboardInputs'] == 6
        assert interactive['FinalHwnd'] == submitted['ExpectedHwnd'] and interactive['FinalHwnd'] in owned
        assert interactive['DispatcherNativeThreadId'] == summary['DispatcherNativeThreadId']
        concurrent = one('ConcurrentSettings')
        assert concurrent['Passed'] and len(concurrent['Requests']) == 12
        requests = sorted(concurrent['Requests'], key=lambda row: row['StartedQpc'])
        assert requests[1]['StartedQpc'] < requests[0]['CompletedQpc']
        assert all(row['Requested'] == row['Applied'] for row in concurrent['VersionsAfter'])
        geometry(concurrent['Geometry'], 10)
        interruptions = [row for row in observations if row['Kind'] == 'NativeMovementInterruption']
        assert [row['Action'] for row in interruptions] == ['minimize', 'close']
        for row in interruptions:
            native = row['NativeInterruption']
            assert row['Passed'] and native['Hwnd'] in owned
            assert row['StartedQpc'] <= native['NativePositionMessageQpc'] <= native['AppliedQpc'] < row['LayoutIdleObservedQpc'] <= row['NativeVerifiedQpc']
            geometry(row['Geometry'], 9)
        restored = one('NativeRestoreVerified')
        assert restored['Passed'] and restored['Count'] == 10
        geometry(restored['Geometry'], 10)
        pending = one('PendingShutdownRequested')
        assert pending['EnteredNativeOperation'] and len(pending['RestorePositions']) == 50
        assert any(row['ActiveCallbacks'] > 0 and row['FrozenCount'] > 0 for row in pending['Pending'])
        shutdown = one('PendingShutdownVerified')
        assert all(shutdown[key] for key in ['Passed', 'WorkerCompleted', 'WorkerThreadExited', 'TargetsStillAlive'])
        assert shutdown['NativeInterruption']['DirectSignal']
        for check in shutdown['Checks'][-3:]:
            assert len(check['Geometry']) == 50
            assert all(row['Equal'] and row['Expected'] == row['Actual'] for row in check['Geometry'])
        assert len(shutdown['Checks']) >= 3
        cleanup = read(path / 'targets/cleanup.json')
        assert cleanup['AllDestroyed']
        pids = {summary['ProcessId'], cleanup['ProcessId']}
        assert not any(row['ProcessId'] in pids for row in before + after)
        exceptions = collections.Counter(row['Type'] for row in read(path / 'validation-exceptions.json'))
        results.append(dict(Configuration=config,Transitions=len(transitions),RestoreTargets=10,
            ShutdownRestores=50,NativeDpi=sorted({row['Dpi'] for item in transitions for row in item['Geometry']}),
            ObservedExceptions=dict(exceptions)))
    result = dict(Stage='Full-app native / interactive validation', Verdict='PASS', WholeIdStatus='IN_PROGRESS',
        Configurations=results, ProductionDelta=run['ProductionDelta'],
        ProductionChangesFromTopology=provenance['ProductionChangesFromTopology'],
        Snapshot=str(base), RunSHA256=sha(base/'validation-run.json'),
        EvidenceSHA256=sha(base/'validation-evidence.json'), VerifierSHA256=sha(pathlib.Path(__file__)),
        Limits=['Owned targets and actual display DPI only; no multi-DPI coverage claim.',
                'Native minimize/close recovery and pending shutdown are observed; individual canceled animation Task status is not instrumented.',
                'No full-app ETW, attributed CPU/GPU, hardware-presentation or production speedup claim.'])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(result, stream, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
