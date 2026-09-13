"""Exercise fail-closed checks using retained behavior data and in-memory faults.

The behavior-to-measurement projection is a test fixture, never measurement
evidence. All retained files remain read-only and are rehashed after the tests.
"""
import argparse
import copy
import importlib.util
import json
import pathlib
import unittest.mock


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--behavior-snapshot', type=pathlib.Path, required=True)
    parser.add_argument('--desktop-receipt', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    assert not args.output.exists()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    scripts = pathlib.Path(__file__).resolve().parent
    verifier = module('measurement_verifier', scripts / 'Verify-AnimationFullAppMeasurement.py')
    desktop = module('desktop_controls', scripts / 'FullAppDesktop.py')
    behavior = args.behavior_snapshot.resolve()
    results = []
    original_hashes = {}
    for configuration in ['Debug', 'Release']:
        path = behavior / 'validation' / configuration
        records = {name: verifier.read(path / name) for name in
            ['fullapp-summary.json', 'targets/cleanup.json', 'targets/messages.json', 'targets/interruptions.json']}
        for name in records:
            original_hashes[str(path / name)] = verifier.sha(path / name)
        command = next(r for r in verifier.read(behavior / 'validation-run.json')['Commands'] if r['Name'] == configuration)
        binaries = {str(pathlib.Path(a['Location']).resolve()): a['SHA256'] for a in records['fullapp-summary.json']['Assemblies']}
        # Retain actual native transitions/messages; remove validation-only
        # scenario metadata in memory to exercise the common measurement checks.
        projected = copy.deepcopy(records)
        summary = projected['fullapp-summary.json']
        summary['Validation'] = False
        summary['Observations'] = [r for r in summary['Observations'] if r['Kind'] in ['Created', 'Transition']]
        projected['targets/interruptions.json'] = []
        def first_transition(data):
            return next(r for r in data['fullapp-summary.json']['Observations'] if r['Kind'] == 'Transition')
        def corrupt_geometry(data):
            first_transition(data)['Geometry'][0]['Actual']['Left'] += 1
        def corrupt_owner(data):
            first_transition(data)['Geometry'][0]['OwnerThreadId'] += 1
        def nonfinite_cpu(data):
            first_transition(data)['ProcessCpuMs'] = float('nan')
        def missing_messages(data):
            transition = next(r for r in data['fullapp-summary.json']['Observations'] if r['Kind'] == 'Transition' and r['Measured'])
            data['targets/messages.json'] = [m for m in data['targets/messages.json'] if m['Scenario'] != transition['Scenario']]
        cases = [('recorded-transition-projection', None), ('native-rectangle-mismatch', corrupt_geometry),
            ('wrong-owner-thread', corrupt_owner), ('nonfinite-process-cpu', nonfinite_cpu),
            ('missing-native-messages', missing_messages),
            ('failed-cleanup', lambda d: d['targets/cleanup.json'].update(AllDestroyed=False)),
            ('false-etw-claim', lambda d: d['fullapp-summary.json'].update(EtwProviderEnabled=True)),
            ('failed-process', lambda d: d['fullapp-summary.json'].update(Error='injected failure'))]
        for name, mutate in cases:
            data = copy.deepcopy(projected)
            if mutate:
                mutate(data)
            def read_projected(request):
                return data[pathlib.Path(request).relative_to(path).as_posix()]
            rejected = False
            with unittest.mock.patch.object(verifier, 'read', side_effect=read_projected):
                try:
                    _, rows, owners, _ = verifier.validate_run(path, command, binaries, 2, False, {96})
                    assert len(rows) == 6 and len(owners) == 61
                except AssertionError:
                    if mutate is None:
                        raise
                    rejected = True
            assert rejected == (mutate is not None), (configuration, name, rejected)
            results.append(dict(Configuration=configuration, Case=name, Rejected=rejected, Passed=True))
    recorded = verifier.read(args.desktop_receipt)
    dpis, areas = recorded['ExpectedDpi'], recorded['ExpectedWorkAreas']
    actual_failures = desktop.control_failures(recorded['Current'], dpis, areas)
    assert actual_failures == recorded['Failures'] and len(actual_failures) == 4
    results.append(dict(Case='retained-remote-desktop-rejected', Passed=True, Reasons=actual_failures))
    # These are synthetic contract probes derived from the accepted controls.
    valid = dict(EnumerationSucceeded=True, ProbeFailures=[], RemoteSession=False,
        ThreadDesktop='Default', InputDesktop='Default', InputDesktopError=0,
        Monitors=[dict(Dpi=dpis[0], WorkArea=areas[0], DpiProbeDestroyed=True)])
    assert not desktop.control_failures(valid, dpis, areas)
    for name, mutate in [
        ('missing-monitor', lambda d: d.update(Monitors=[])),
        ('inaccessible-input-desktop', lambda d: d.update(InputDesktop=None)),
        ('remote-session', lambda d: d.update(RemoteSession=True)),
        ('dpi-change', lambda d: d['Monitors'][0].update(Dpi=192)),
        ('failed-probe-cleanup', lambda d: d['Monitors'][0].update(DpiProbeDestroyed=False)),
        ('workarea-change', lambda d: d['Monitors'][0]['WorkArea'].update(Right=1024)),
    ]:
        data = copy.deepcopy(valid)
        mutate(data)
        assert desktop.control_failures(data, dpis, areas), name
        results.append(dict(Case=name, Passed=True))
    assert all(verifier.sha(pathlib.Path(name)) == digest for name, digest in original_hashes.items())
    report = dict(Verdict='PASS', Tests=len(results), Results=results,
        RetainedInputsUnchanged=original_hashes, NativeMeasurement=False,
        Fixture='In-memory projection of V14 transitions plus injected invalid records; no acceptance of a capture',
        Sources=[dict(Path=str(p), SHA256=verifier.sha(p)) for p in [pathlib.Path(__file__).resolve(),
            scripts / 'Verify-AnimationFullAppMeasurement.py', scripts / 'FullAppDesktop.py']])
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    print(json.dumps(dict(Verdict='PASS', Tests=len(results), Output=str(args.output)), indent=2))


if __name__ == '__main__':
    main()
