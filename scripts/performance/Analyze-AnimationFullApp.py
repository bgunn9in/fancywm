"""Derive full-app thread scheduling and scoped PresentMon counters offline.

Only an independently verified ETW capture is accepted. Scheduled occupancy
includes interrupt time; shared compositor counters are never app GPU usage.
This analysis does not establish hardware presentation or a speedup.
"""
import argparse
import collections
import csv
import decimal
import hashlib
import importlib.util
import json
import math
import pathlib
import statistics
import sys


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def rows(path):
    with path.open(encoding='utf-8-sig', newline='') as stream:
        yield from csv.DictReader(stream)


def write_csv(path, records):
    assert records
    with path.open('x', encoding='utf-8', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(records[0]))
        writer.writeheader()
        writer.writerows(records)


def process_id(value):
    return int(value.rsplit('(', 1)[1].rstrip(') '))


def scheduled_intervals(events, pids):
    """Require paired switches on the same CPU for every captured process."""
    active, intervals, failures = {}, collections.defaultdict(list), []
    switches = 0
    previous = -1
    for row in events:
        if row[0].strip() != 'CSwitch':
            continue
        time, cpu = int(row[1]), int(row[16])
        assert time >= previous, 'CSwitch export is not chronological'
        previous = time
        switches += 1
        old = (process_id(row[8]), int(row[9]))
        new = (process_id(row[2]), int(row[3]))
        if old[0] in pids:
            start = active.pop(old, None)
            if start is None or start[1] != cpu or start[0] > time:
                failures.append(dict(Kind='unmatched-out-or-CPU-mismatch', Time=time,
                    Thread=old, Start=start, Cpu=cpu))
            else:
                intervals[old].append((start[0], time, cpu))
        if new[0] in pids:
            if new in active:
                failures.append(dict(Kind='duplicate-in', Time=time, Thread=new))
            active[new] = (time, cpu)
    failures.extend(dict(Kind='unmatched-in', Thread=key, Start=value) for key, value in active.items())
    return intervals, failures, switches


def occupancy(intervals, start, end):
    assert start <= end
    return sum(max(0, min(right, end) - max(left, start)) for left, right, _ in intervals) / 1000


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['capture-receipt', 'exports', 'output']:
        parser.add_argument('--' + name, type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    receipt_path = args.capture_receipt.resolve()
    receipt = read(receipt_path)
    assert receipt['Verdict'] == 'CAPTURE_VERIFIED_ETW_ANALYSIS_PENDING'
    assert receipt['Processes'] == 5 and receipt['MeasuredTransitions'] == 120
    base = pathlib.Path(receipt['Snapshot'])
    native = base / 'native'
    for name, key in [('capture-run.json', 'CaptureSHA256'), ('provenance.json', 'ProvenanceSHA256'),
                      ('native-evidence.json', 'RawManifestSHA256')]:
        assert sha(base / name) == receipt[key]
    for record in receipt['Outputs']:
        path = pathlib.Path(record['Path'])
        assert path.parent == receipt_path.parent and path.stat().st_size == record['Bytes']
        assert sha(path) == record['SHA256']
    evidence = read(base / 'native-evidence.json')
    for record in evidence:
        path = native / record['Path']
        assert path.stat().st_size == record['Bytes'] and sha(path) == record['SHA256']
    export = read(args.exports / 'export-command.json')
    assert export['ExitCode'] == 0 and export['ETLSHA256'] == sha(native / 'fullapp.etl')
    for record in export['Exports']:
        path = pathlib.Path(record['Path'])
        assert path.parent == args.exports.resolve()
        assert path.stat().st_size == record['Bytes'] and sha(path) == record['SHA256']
    present_export = read(args.exports / 'presentmon-command.json')
    assert present_export['ExitCode'] == 0
    assert pathlib.Path(present_export['Arguments'][2]).resolve() == native / 'fullapp.etl'
    assert sha(args.exports / 'presents.csv') == present_export['OutputSHA256']
    # Reuse the established export parser; freeze it with this derivation.
    parser_path = base / 'source/scripts/performance/Analyze-AnimationTopology.py'
    spec = importlib.util.spec_from_file_location('native_etw_parser', parser_path)
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    markers = {}
    for row in helper.events(native / 'markers.csv'):
        if row[0] != 'FancyWM-Perf010-FullApp/Boundary/':
            continue
        values = helper.payload(row)
        key = (process_id(row[2]), int(values['scenario']), int(values['phase']))
        assert key not in markers
        markers[key] = (int(row[1]), int(values['qpc']))
    assert len(markers) == receipt['EtwMarkers'] == 750
    summaries = [read(native / f'run-{i}/fullapp-summary.json') for i in range(1, 6)]
    frequency, = {s['Frequency'] for s in summaries}
    assert frequency == 10_000_000
    presents = list(rows(args.exports / 'presents.csv'))
    assert presents
    D = decimal.Decimal
    origins = [D(p['QPCTime']) - D(p['TimeInSeconds']) * frequency for p in presents]
    assert max(origins) - min(origins) < D('0.01'), 'PresentMon clock origins disagree'
    origin = round(statistics.median(origins))
    delays = [stamp * 10 + origin - qpc for stamp, qpc in markers.values()]
    assert -10 <= min(delays) and max(delays) < frequency / 10
    runs, pids = [], set()
    for index, summary in enumerate(summaries, 1):
        target = read(native / f'run-{index}/targets/cleanup.json')['ProcessId']
        app = summary['ProcessId']
        assert app != target and not {app, target} & pids, 'Reused process identity needs an explicit lifetime key'
        pids.update([app, target])
        runs.append((index, summary, app, target))
    intervals, failures, switches = scheduled_intervals(helper.events(args.exports / 'thread-events.csv'), pids)
    with (args.output / 'scheduling-integrity.json').open('x', encoding='utf-8') as stream:
        json.dump(dict(CSwitchEvents=switches, Failures=failures), stream, indent=2)
    assert not failures, 'Incomplete scheduling evidence; retained scheduling-integrity.json'
    thread_rows, transition_rows, gpu_rows = [], [], []
    for index, summary, app, target in runs:
        owner_tids = {w['OwnerThreadId'] for group in summary['Observations'] if group['Kind'] == 'Created'
                      for w in group['Targets']['Windows']}
        dispatcher = summary['DispatcherNativeThreadId']
        assert (app, dispatcher) in intervals and all((target, tid) in intervals for tid in owner_tids)
        for t in (t for t in summary['Observations'] if t['Kind'] == 'Transition' and t['Measured']):
            ident = t['Scenario']
            qpcs = [t[k] for k in ['StartQpc', 'SettingsPublishedQpc', 'LayoutIdleObservedQpc',
                                    'NativeVerifiedQpc', 'DwmFlushedQpc']]
            for phase, qpc in enumerate(qpcs, 1):
                assert markers[(app, ident, phase)][1] == qpc
            # Use the payload QPC boundaries, avoiding provider emission delay.
            start, _, idle, verified, end = [(qpc - origin) / 10 for qpc in qpcs]
            key = dict(Run=index, AppProcessId=app, TargetProcessId=target, Count=t['Count'], Scenario=ident)
            selected = []
            for (pid, tid), spans in sorted(intervals.items()):
                if pid not in [app, target]:
                    continue
                role = ('Dispatcher' if tid == dispatcher else 'AppWorker') if pid == app else (
                    'TargetOwner' if tid in owner_tids else 'TargetWorker')
                row = dict(**key, ProcessId=pid, ThreadId=tid, Role=role,
                    ScheduledMs=occupancy(spans, start, end),
                    ScheduledBeforeLayoutIdleMs=occupancy(spans, start, idle),
                    ScheduledAfterLayoutIdleThroughNativeMs=occupancy(spans, idle, verified),
                    ScheduledAfterNativeThroughFlushMs=occupancy(spans, verified, end))
                assert abs(row['ScheduledMs'] - sum(row[k] for k in [
                    'ScheduledBeforeLayoutIdleMs', 'ScheduledAfterLayoutIdleThroughNativeMs',
                    'ScheduledAfterNativeThroughFlushMs'])) < 1e-7
                selected.append(row)
                thread_rows.append(row)
            transition_rows.append(dict(**key, ProcessCpuMs=t['ProcessCpuMs'],
                AppScheduledMs=sum(r['ScheduledMs'] for r in selected if r['ProcessId'] == app),
                DispatcherScheduledMs=sum(r['ScheduledMs'] for r in selected if r['Role'] == 'Dispatcher'),
                TargetScheduledMs=sum(r['ScheduledMs'] for r in selected if r['ProcessId'] == target),
                TargetOwnerScheduledAfterLayoutIdleMs=sum(r['ScheduledAfterLayoutIdleThroughNativeMs']
                    for r in selected if r['Role'] == 'TargetOwner'),
                NativeAfterLayoutIdleMs=(qpcs[3] - qpcs[2]) * 1000 / frequency))
            for scope, pid in [('AppPresentation', app), ('TargetPresentation', target), ('SharedDwm', None)]:
                ps = [p for p in presents if qpcs[0] <= int(p['QPCTime']) <= qpcs[-1]
                      and (p['Application'].casefold() == 'dwm.exe' if pid is None else int(p['ProcessID']) == pid)]
                values = [float(p['msGPUActive']) for p in ps if p.get('msGPUActive', '').strip()]
                assert all(math.isfinite(value) and value >= 0 for value in values)
                complete = bool(ps) and len(values) == len(ps)
                gpu_rows.append(dict(**key, Scope=scope, Presents=len(ps),
                    Dropped=sum(int(p['Dropped']) for p in ps), GpuSamples=len(values),
                    GpuCounterStatus='MEASURED' if complete else 'NOT_MEASURED',
                    GpuActiveMsSum=sum(values) if complete else None))
    assert len(transition_rows) == 120 and len(gpu_rows) == 360
    write_csv(args.output / 'thread-scheduling.csv', thread_rows)
    write_csv(args.output / 'transition-scheduling.csv', transition_rows)
    write_csv(args.output / 'presentation-gpu-counters.csv', gpu_rows)
    inputs = [receipt_path, base / 'native-evidence.json', native / 'markers.csv', native / 'fullapp.etl',
        args.exports / 'export-command.json', args.exports / 'presentmon-command.json',
        args.exports / 'thread-events.csv', args.exports / 'presents.csv', parser_path]
    inputs.extend(native / f'run-{i}/{name}' for i in range(1, 6)
        for name in ['fullapp-summary.json', 'targets/cleanup.json'])
    frozen_script = args.output / pathlib.Path(__file__).name
    frozen_script.write_bytes(pathlib.Path(__file__).read_bytes())
    inputs.append(frozen_script)
    report = dict(Verdict='DERIVED_FULLAPP_CPU_AND_PRESENTATION_COUNTERS', StageAccepted=False,
        WholeIdStatus='IN_PROGRESS', Snapshot=str(base), Commands=sys.argv,
        Processes=5, MeasuredTransitions=120, ProcessIdentities=len(pids), ThreadIdentities=len(intervals),
        CSwitchEvents=switches, SchedulingFailures=failures, QpcFrequency=frequency,
        PresentMonOriginQpc=origin, EtwMarkerEmissionDelayTicks=dict(Min=min(delays), Max=max(delays)),
        HardwarePresentation='NOT_MEASURED', AttributedWholeAppGpu='NOT_MEASURED',
        Aggregates=[dict(Count=count, Transitions=len(selected),
            Medians={key: statistics.median(r[key] for r in selected) for key in [
                'ProcessCpuMs', 'AppScheduledMs', 'DispatcherScheduledMs', 'TargetScheduledMs',
                'TargetOwnerScheduledAfterLayoutIdleMs', 'NativeAfterLayoutIdleMs']})
            for count in [1, 10, 50] for selected in [[r for r in transition_rows if r['Count'] == count]]],
        Limits=['Scheduled thread occupancy includes interrupt time and is not sampled CPU.',
            'Process CPU is quantized; the captured scope includes settings, layout, overlays and harness observations.',
            'PresentMon GPU counters cover associated presents only; shared DWM activity is not app GPU usage.',
            'No hardware presentation, speedup, multi-DPI or cross-application claim.'],
        Inputs=[dict(Path=str(p.resolve()), Bytes=p.stat().st_size, SHA256=sha(p)) for p in inputs],
        Outputs=[dict(Path=str(p.resolve()), Bytes=p.stat().st_size, SHA256=sha(p))
            for p in sorted(args.output.glob('*.csv'))])
    with (args.output / 'derivation.json').open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    print(json.dumps({k: v for k, v in report.items() if k not in ['Inputs', 'Outputs']}, indent=2))


if __name__ == '__main__':
    main()
