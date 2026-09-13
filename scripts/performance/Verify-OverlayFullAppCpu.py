"""Independently derive native process CPU comparisons from the raw paired runs."""
import argparse
import csv
import datetime
import importlib.util
import json
import math
import pathlib
import statistics


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def compare(rows):
    assert len(rows) == 240
    assert {(r['Pair'], r['Variant'], r['Count'], r['Iteration']) for r in rows} == {
        (p, v, c, i) for p in range(1, 6) for v in 'AB' for c in [1, 10, 50] for i in range(1, 9)}
    metrics = ['ProcessCpuMs', 'NativeVerifiedMs', 'LastNativeMessageObservedMs']
    assert all(math.isfinite(r[k]) and r[k] >= 0 for r in rows for k in metrics)
    pairs = []
    for count in [1, 10, 50]:
        for pair in range(1, 6):
            values = {v: {k: statistics.median(r[k] for r in rows
                if r['Count'] == count and r['Pair'] == pair and r['Variant'] == v) for k in metrics} for v in 'AB'}
            pairs.append(dict(Count=count, Pair=pair, Baseline=values['A'], Candidate=values['B']))
    aggregates = []
    for count in [1, 10, 50]:
        group = [p for p in pairs if p['Count'] == count]
        baseline = {k: statistics.median(p['Baseline'][k] for p in group) for k in metrics}
        candidate = {k: statistics.median(p['Candidate'][k] for p in group) for k in metrics}
        aggregates.append(dict(Count=count, Baseline=baseline, Candidate=candidate,
            CpuReductionPercent=100 * (1 - candidate['ProcessCpuMs'] / baseline['ProcessCpuMs']) if baseline['ProcessCpuMs'] else None,
            CpuWins=sum(p['Candidate']['ProcessCpuMs'] < p['Baseline']['ProcessCpuMs'] for p in group),
            CpuTies=sum(p['Candidate']['ProcessCpuMs'] == p['Baseline']['ProcessCpuMs'] for p in group),
            CpuLosses=sum(p['Candidate']['ProcessCpuMs'] > p['Baseline']['ProcessCpuMs'] for p in group)))
    return pairs, aggregates


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--snapshot', type=pathlib.Path, required=True)
    p.add_argument('--output', type=pathlib.Path, required=True)
    a = p.parse_args()
    base = a.snapshot.resolve()
    assert not a.output.exists()
    source = base / 'source/scripts/performance'
    helper = module('runner', source / 'Run-AnimationFullAppValidation.py')
    files = module('files', source / 'Verify-AnimationFullAppValidation.py')
    measurement = module('measurement', source / 'Verify-AnimationFullAppMeasurement.py')
    desktop = module('desktop', source / 'FullAppDesktop.py')
    sha, read = helper.sha, helper.read
    assert sha(pathlib.Path(__file__)) == sha(source / pathlib.Path(__file__).name)
    verified_sources = 0
    def verify_source(snapshot):
        nonlocal verified_sources
        with (snapshot / 'manifest.csv').open(encoding='utf-8-sig', newline='') as stream:
            records = list(csv.DictReader(stream))
        assert len({r['Path'] for r in records}) == len(records)
        for r in records:
            assert sha(snapshot / 'source' / r['Path']) == r['SHA256']
        verified_sources += len(records)
    verify_source(base)
    provenance, run = read(base / 'provenance.json'), read(base / 'comparison-run.json')
    assert sha(source / 'Measure-OverlayFullAppCpu.py') == provenance['RunnerSHA256']
    assert run['ProcessesPassed'] and not run['Errors']
    assert all(not run[k] for k in ['StageAccepted', 'WprRecordingStarted', 'InputSent',
        'LedgerAppend', 'MsixInstalled', 'MsixLaunched', 'Published'])
    controls = provenance['Controls']
    assert {k: controls[k] for k in ['Pairs', 'Iterations', 'Warmups', 'Counts', 'Configuration', 'Mode', 'InputSent', 'Etw']} == dict(
        Pairs=5, Iterations=8, Warmups=2, Counts=[1, 10, 50], Configuration='Release', Mode='measurement', InputSent=False, Etw=False)
    assert controls['EnvironmentOverrides'] == {'DOTNET_TieredCompilation': '0', 'COMPlus_TieredCompilation': '0'}
    productions, binaries, records = {}, {}, {}
    for variant in 'AB':
        item = provenance['Inputs'][variant]
        build = pathlib.Path(item['BuildSnapshot'])
        assert sha(build / 'manifest.csv') == item['BuildManifestSHA256']
        verify_source(build)
        productions[variant] = helper.production_manifest(build)
        path = pathlib.Path(item['BehaviorReceipt'])
        assert sha(path) == sha(base / 'preflight' / (variant + '-behavior.json')) == item['BehaviorSHA256']
        behavior = read(path)
        assert behavior['Verdict'] == 'PASS'
        assert {dpi for c in behavior['Configurations'] for dpi in c['NativeDpi']} == {96}
        validation = pathlib.Path(behavior['Snapshot'])
        assert sha(validation / 'validation-run.json') == behavior['RunSHA256']
        assert sha(validation / 'validation-evidence.json') == behavior['EvidenceSHA256']
        behavior_source = read(validation / 'provenance.json')
        assert pathlib.Path(behavior_source['BuildSnapshot']) == build
        assert behavior_source['Fixture'] == provenance['Fixture']
        assert behavior_source['TopologyReceiptSHA256'] == item['TopologySHA256']
        for name, digest in provenance['Fixture'].items():
            assert sha(base / 'source' / name) == sha(build / 'source' / name) == digest
        binaries[variant], records[variant] = {}, {}
        for project in ['FancyWM.FullAppHarness', 'FancyWM.Perf010Targets']:
            name = 'Release-' + project
            rows = read(base / (variant + '-' + name + '-manifest.json'))
            assert rows == read(build / 'validation' / (name + '-binary-manifest.json'))
            archive = base / ('binaries-' + variant + '-' + name)
            files.verify_files(archive, rows)
            binaries[variant].update({str((archive / r['Path']).resolve()): r['SHA256'] for r in rows})
            records[variant][project] = {r['Path']: r['SHA256'] for r in rows}
    changes = [dict(Path=n, BaselineSHA256=productions['A'].get(n), CandidateSHA256=productions['B'].get(n))
        for n in sorted(productions['A'].keys() | productions['B'].keys()) if productions['A'].get(n) != productions['B'].get(n)]
    assert changes == provenance['ProductionChanges']
    assert [r['Path'] for r in changes] == ['FancyWM/TilingOverlayRenderer.cs', 'FancyWM/TilingService.Private.cs', 'FancyWM/TilingService.cs']
    assert helper.production_manifest(base) == productions['B']
    differences = {p: [n for n in sorted(records['A'][p].keys() | records['B'][p].keys())
        if records['A'][p].get(n) != records['B'][p].get(n)] for p in records['A']}
    assert differences == provenance['BinaryDifferences']
    topology_path = pathlib.Path(provenance['Inputs']['A']['TopologyReceipt'])
    assert sha(topology_path) == provenance['Inputs']['A']['TopologySHA256'] == provenance['Inputs']['B']['TopologySHA256']
    gate = read(topology_path)
    assert gate['Verdict'] == 'ACCEPT' and gate['Processes'] == 15 and gate['HardwarePresentationTargets'] == 7320
    summary_path = pathlib.Path(controls['TopologySummary'])
    manifest = summary_path.parents[2] / 'native-evidence.csv'
    witness = next(r for r in gate['Inputs'] if pathlib.Path(r['Path']).resolve() == manifest.resolve())
    assert sha(manifest) == witness['SHA256']
    with manifest.open(encoding='utf-8-sig', newline='') as stream:
        witness = next(r for r in csv.DictReader(stream) if r['Path'].replace('\\', '/') == 'run-1/summary.json')
    assert sha(summary_path) == controls['TopologySummarySHA256'] == witness['SHA256']
    assert controls['ExpectedDpi'] == [96]
    assert controls['ExpectedWorkAreas'] == [read(summary_path)['Displays'][0]['WorkArea']]
    for folder in ['native', 'preflight']:
        files.verify_files(base / folder, read(base / (folder + '-evidence.json')))
    native = base / 'native'
    commands = run['Commands']
    order = [(p, v) for p in range(1, 6) for v in ('AB' if p % 2 else 'BA')]
    assert [(r['Pair'], r['Variant']) for r in commands] == order
    assert all(datetime.datetime.fromisoformat(x['FinishedUtc']) <= datetime.datetime.fromisoformat(y['StartedUtc'])
        for x, y in zip(commands, commands[1:]))
    rows, geometry_reference, runtimes, pids, drain_waits = [], None, set(), set(), []
    def check_desktop(label):
        value = read(native / (label + '-desktop.json'))
        assert not value['Failures'] and not desktop.control_failures(value['Current'], [96], controls['ExpectedWorkAreas'])
    check_desktop('before')
    for label in ['before', 'after'] + [r['Name'] + '-before' for r in commands]:
        assert b'not recording' in (native / (label + '-wpr.stdout')).read_bytes()
    for command in commands:
        label, variant = command['Name'], command['Variant']
        assert command['ExitCode'] == 0 and not command['TimedOut']
        assert command['EnvironmentOverrides'] == controls['EnvironmentOverrides']
        assert read(native / (label + '-command.json')) == command
        for boundary in ['before', 'after']:
            check_desktop(label + '-' + boundary)
        host = base / ('binaries-' + variant + '-Release-FancyWM.FullAppHarness/FancyWM.FullAppHarness.exe')
        targets = base / ('binaries-' + variant + '-Release-FancyWM.Perf010Targets/FancyWM.Perf010Targets.exe')
        assert command['Arguments'] == [str(host), str(native / label), str(targets), '8', 'measurement']
        summary, data, owners, geometry = measurement.validate_run(native / label, command, binaries[variant], 8, False, {96})
        assert summary['FixtureLayoutPreparation'] == 'SettledEvenFlexAndTargetOrder'
        assert [r['Scenario'] for r in summary['Observations'] if r['Kind'] == 'Transition'] == list(range(1, 31))
        transitions = [r for r in summary['Observations'] if r['Kind'] == 'Transition']
        for wait in summary['OwnershipWaits']:
            assert wait['ExpectedCount'] == 0 and 0 in wait['ProcessIds']
            assert len(wait['Handles']) == len(wait['ProcessIds'])
            assert set(wait['ProcessIds']) <= {0, owners[0]['ProcessId']}
            assert all(not t['StartQpc'] <= wait['Qpc'] <= t['DwmFlushedQpc'] for t in transitions)
        drain_waits.append(dict(Process=label, Count=len(summary['OwnershipWaits'])))
        runtimes.add(summary['Runtime'])
        pids.update([summary['ProcessId'], owners[0]['ProcessId']])
        if geometry_reference is None:
            geometry_reference = geometry
        assert geometry == geometry_reference
        rows.extend(dict(Pair=command['Pair'], Variant=variant, **r) for r in data)
    assert len(runtimes) == 1
    windows = read(native / 'windows-before.json') + read(native / 'windows-after.json')
    assert not any(r['ProcessId'] in pids for r in windows)
    assert read(native / 'execution-state.json')['RestoreReturn']
    assert not list(base.rglob('*.etl'))
    with (base / 'transitions.csv').open(encoding='utf-8-sig', newline='') as stream:
        saved = list(csv.DictReader(stream))
    assert saved == [{k: str(v) for k, v in r.items()} for r in rows], 'Stored counters differ from native raw observations'
    pairs, aggregates = compare(rows)
    a.output.parent.mkdir(parents=True, exist_ok=True)
    report = dict(Verdict='NATIVE_PROCESS_CPU_COMPARISON_VERIFIED', StageAccepted=False, WholeIdStatus='IN_PROGRESS',
        Snapshot=str(base), Pairs=5, Processes=10, MeasuredTransitions=240, WarmupTransitions=60,
        NativeFinalTargets=4880, NativeGeometryAndCleanup=True, NativeDpi=[96], Runtime=next(iter(runtimes)),
        SourceFilesVerified=verified_sources, ProductionChanges=changes, BinaryDifferences=differences,
        PairMedians=pairs, Aggregates=aggregates, ProcessCpu='MEASURED', ThreadScheduling='NOT_MEASURED',
        OwnershipDrainWaits=drain_waits,
        AttributedGpu='NOT_MEASURED', HardwarePresentation='NOT_MEASURED',
        RawCountersSHA256=sha(base / 'transitions.csv'), RunSHA256=sha(base / 'comparison-run.json'),
        ProvenanceSHA256=sha(base / 'provenance.json'), NativeManifestSHA256=sha(base / 'native-evidence.json'),
        VerifierSHA256=sha(pathlib.Path(__file__)), Limits=[
            'Process.TotalProcessorTime is quantized CPU over settings submission through DwmFlush observation, including app and harness work.',
            'Five alternating pairs use the same fixture/dependency source but separately built complete baseline/candidate archives; differing binaries are recorded.',
            'Native geometry and DwmFlush do not prove hardware presentation; no ETW, attributed GPU or multi-DPI result.',
            'The comparison covers padding reuse plus nested-notification correction together; it does not isolate the correction alone.'])
    with a.output.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    print(json.dumps(dict(Verdict=report['Verdict'], Processes=10, MeasuredTransitions=240, Aggregates=aggregates), indent=2))


if __name__ == '__main__':
    main()
