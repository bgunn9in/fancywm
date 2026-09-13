"""Correlate verified full-app final geometry with DWM draws and PresentMon.

Native, extended-frame and client rectangles are distinct. This derivation
requires an independent raw DWM/Dxg verifier before claiming hardware display.
"""
import argparse
import bisect
import collections
import csv
import hashlib
import importlib.util
import json
import pathlib
import struct
import sys


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def write_csv(path, rows):
    assert rows
    with path.open('x', encoding='utf-8', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def covered(clip, rect):
    return (clip[0] <= rect['Left'] and clip[1] <= rect['Top']
        and clip[2] >= rect['Right'] and clip[3] >= rect['Bottom'])


def bind_visual(visuals, visual, association, stamp, sprite):
    """Keep an uninterrupted HWND binding history across resized GDI surfaces."""
    if association is None:
        visuals.pop(visual, None)
        return
    hwnd, associated = association
    assert associated <= stamp
    previous = visuals.get(visual)
    history = []
    if (previous is not None and previous['hwnd'] == hwnd
            and previous['history'][-1][0] <= associated
            and previous['history'][-1][1] < stamp):
        history = previous['history']
    visuals[visual] = dict(hwnd=hwnd, history=history + [(associated, stamp, sprite)])


def bound_window(windows, binding, pid, stamp):
    matches = []
    for window in windows:
        if not (window['pid'] == pid and window['hwnd'] == binding['hwnd']
                and window['first'] <= stamp < window['end']):
            continue
        history = binding['history']
        # Anchor the visual after the owned HWND was observed alive and before
        # warmups. Later same-HWND rebindings must preserve this chain. The
        # latest binding alone need not predate the first transition.
        anchored = any(window['alive'] <= associated <= bound <= window['first']
                       for associated, bound, _ in history)
        if anchored and history[-1][0] <= history[-1][1] <= stamp:
            matches.append(window)
    assert len(matches) <= 1, 'Ambiguous HWND/owner lifetime'
    return matches[0] if matches else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['cpu-derivation', 'exports', 'output']:
        parser.add_argument('--' + name, type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    cpu_path = args.cpu_derivation.resolve() / 'derivation.json'
    cpu = read(cpu_path)
    assert cpu['Verdict'] == 'DERIVED_FULLAPP_CPU_AND_PRESENTATION_COUNTERS'
    assert cpu['Processes'] == 5 and cpu['MeasuredTransitions'] == 120 and not cpu['SchedulingFailures']
    for item in cpu['Inputs'] + cpu['Outputs']:
        path = pathlib.Path(item['Path'])
        assert path.stat().st_size == item['Bytes'] and sha(path) == item['SHA256']
    base = pathlib.Path(cpu['Snapshot'])
    native = base / 'native'
    source = base / 'source/scripts/performance/Analyze-AnimationTopology.py'
    spec = importlib.util.spec_from_file_location('native_etw', source)
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    export_path = args.exports / 'export-command.json'
    export = read(export_path)
    assert export['ExitCode'] == 0 and export['ETLSHA256'] == sha(native / 'fullapp.etl')
    for item in export['Exports']:
        path = pathlib.Path(item['Path'])
        assert path.parent == args.exports.resolve()
        assert path.stat().st_size == item['Bytes'] and sha(path) == item['SHA256']
    pm_path = args.exports / 'presentmon-command.json'
    pm = read(pm_path)
    assert pm['ExitCode'] == 0 and pm['OutputSHA256'] == sha(args.exports / 'presents.csv')
    assert pathlib.Path(pm['Arguments'][2]).resolve() == native / 'fullapp.etl'
    origin, frequency = cpu['PresentMonOriginQpc'], cpu['QpcFrequency']
    assert frequency == 10_000_000
    summaries = [read(native / f'run-{i}/fullapp-summary.json') for i in range(1, 6)]
    starts = sorted(t['StartQpc'] for s in summaries for t in s['Observations'] if t['Kind'] == 'Transition')

    def next_start(qpc):
        index = bisect.bisect_right(starts, qpc)
        return (starts[index] - origin) / 10 if index < len(starts) else float('inf')

    windows, transitions = [], []
    for run, summary in enumerate(summaries, 1):
        app = summary['ProcessId']
        target_pid = read(native / f'run-{run}/targets/cleanup.json')['ProcessId']
        observations = [t for t in summary['Observations'] if t['Kind'] == 'Transition']
        for group in (g for g in summary['Observations'] if g['Kind'] == 'Created'):
            count = group['Count']
            selected = [t for t in observations if t['Count'] == count]
            first = min(t['StartQpc'] for t in selected)
            last = max(t['StartQpc'] for t in selected)
            owned = {w['Hwnd']: w for w in group['Targets']['Windows']}
            for hwnd, window in owned.items():
                windows.append(dict(run=run, app=app, pid=target_pid, count=count, target=window['Index'],
                    hwnd=hwnd, alive=(group['Targets']['Qpc'] - origin) / 10,
                    first=(first - origin) / 10, end=next_start(last)))
            for t in selected:
                if not t['Measured']:
                    continue
                geometry = {owned[g['Hwnd']]['Index']: g for g in t['Geometry']}
                assert sorted(geometry) == list(range(count))
                transitions.append(dict(run=run, app=app, pid=target_pid, count=count,
                    transition=t['Scenario'], start=(t['StartQpc'] - origin) / 10,
                    StartQpc=t['StartQpc'], end=next_start(t['StartQpc']), geometry=geometry))
    assert len(windows) == 305 and len(transitions) == 120
    first_boundary = (starts[0] - origin) / 10
    presents = []
    with (args.exports / 'presents.csv').open(encoding='utf-8-sig', newline='') as stream:
        for p in csv.DictReader(stream):
            if p['Application'].casefold() == 'dwm.exe':
                presents.append(dict(qpc=int(p['QPCTime']), pid=int(p['ProcessID']),
                    shown=int(p['QPCTime']) + float(p['msUntilDisplayed']) * frequency / 1000,
                    dropped=int(p['Dropped']), mode=p['PresentMode']))
    presents.sort(key=lambda p: p['qpc'])
    present_qpcs = [p['qpc'] for p in presents]
    handles = {w['hwnd'] for w in windows}
    associations, visuals, frames, draws, links = {}, {}, [], [], []
    current, initial_stops = None, []
    counts = collections.Counter()
    last_stamp = 0
    for row in helper.events(args.exports / 'dwm-events.csv'):
        name, stamp = row[0], int(row[1])
        if not name.startswith('Microsoft-Windows-Dwm-Core/'):
            continue
        counts[name] += 1
        last_stamp = max(last_stamp, stamp)
        data = helper.payload(row)
        if '/WINDOWNODE_GDISPRITE_ASSOCIATION/' in name:
            associations[data['GdiSpritePointer']] = (int(data['windowHandle'], 16), stamp)
        elif '/BIND_GDISPRITEBITMAP_FIRST_TOKEN/' in name:
            sprite = data['logicalSurfaceImagePointer']
            visual = data['windowNodePointer']
            bind_visual(visuals, visual, associations.get(sprite), stamp, sprite)
            if sprite in associations:
                hwnd, associated = associations[sprite]
                if hwnd in handles:
                    links.append(dict(Hwnd=hwnd, Sprite=sprite, Visual=visual,
                        AssociationUs=associated, BindingUs=stamp))
        elif '/SCHEDULE_PROCESS_FRAME/win:Start' in name:
            assert current is None
            current = dict(start=stamp, end=None, ident=None, begin=None, finish=None, content={},
                pid=int(row[2].rsplit('(', 1)[1].rstrip(') ')), tid=int(row[3]))
        elif '/CurrentFrameId/' in name and current is not None:
            current['ident'] = data['FrameId']
        elif '/OVERLAY_PRESENT/win:Start' in name and current is not None:
            current['begin'] = stamp
        elif '/OVERLAY_PRESENT/win:Stop' in name and current is not None:
            current['finish'] = stamp
        elif '/ETWGUID_VISUAL_RENDERCONTENT/' in name and current is not None:
            current['content'][data['Visual']] = [float(data[k]) for k in ['Left', 'Top', 'Right', 'Bottom']]
        elif '/ETWGUID_DRAWING_CONTEXT_STATE/' in name and current is not None:
            if data['Visual'] not in visuals:
                continue
            binding = visuals[data['Visual']]
            hwnd = binding['hwnd']
            if hwnd not in handles:
                continue
            pid = int(data['AttributedProcessId'])
            window = bound_window(windows, binding, pid, stamp)
            if window is None:
                continue
            matrix = struct.unpack('<16f', bytes.fromhex(' '.join(data['TransformMatrix'].split()[1:65])))
            identity = (matrix[:12] == (1., 0., 0., 0., 0., 1., 0., 0., 0., 0., 1., 0.)
                and matrix[14:] == (0., 1.))
            draws.append(dict(frame=current, window=window, time=stamp, identity=identity,
                x=matrix[12], y=matrix[13], visual=data['Visual'],
                clip=[float(data[k]) for k in ['ClipLeft', 'ClipTop', 'ClipRight', 'ClipBottom']]))
        elif '/SCHEDULE_PROCESS_FRAME/win:Stop' in name:
            if current is None:
                assert not frames and not initial_stops and stamp < first_boundary
                initial_stops.append(dict(TimeUs=stamp, Process=row[2], ThreadId=int(row[3]), RawFields=row))
                continue
            assert current['pid'] == int(row[2].rsplit('(', 1)[1].rstrip(') ')) and current['tid'] == int(row[3])
            current['end'] = stamp
            frames.append(current)
            current = None
    assert current is None, 'The capture ends with an incomplete DWM frame'
    frame_rows = []
    for frame in frames:
        first, last = frame['begin'], frame['finish']
        if first is None or last is None:
            frame['presents'] = []
        else:
            low = bisect.bisect_left(present_qpcs, first * 10 + origin - 10)
            high = bisect.bisect_right(present_qpcs, last * 10 + origin + 10)
            frame['presents'] = [p for p in presents[low:high] if p['pid'] == frame['pid']]
        frame_rows.append(dict(FrameId=frame['ident'], StartUs=frame['start'], EndUs=frame['end'],
            PresentStartUs=first, PresentEndUs=last, PresentMonRows=len(frame['presents']),
            DisplayedRows=sum(p['dropped'] == 0 for p in frame['presents'])))
    indexed = collections.defaultdict(list)
    for draw in draws:
        w = draw['window']
        indexed[(w['pid'], w['count'], w['target'])].append(draw)
    target_rows = []
    for t in transitions:
        for target, g in t['geometry'].items():
            rect, visible, client = g['Actual'], g['ExtendedFrame'], g['ClientScreen']
            assert g['Expected'] == rect and g['Visible'] and not g['Cloaked']
            assert covered([rect[k] for k in ['Left', 'Top', 'Right', 'Bottom']], visible)
            assert covered([visible[k] for k in ['Left', 'Top', 'Right', 'Bottom']], client)
            deadline = min(t['end'], last_stamp)
            matching = [d for d in indexed[(t['pid'], t['count'], target)]
                if t['start'] <= d['time'] < deadline and d['identity']
                and d['x'] == rect['Left'] and d['y'] == rect['Top']]
            client_draws = [d for d in indexed[(t['pid'], t['count'], target)]
                if t['start'] <= d['time'] < deadline and d['identity']
                and d['x'] == client['Left'] and d['y'] == client['Top']]
            rendered = [d for d in matching if d['frame']['content'].get(d['visual']) ==
                [0., 0., float(rect['Width']), float(rect['Height'])]]
            covering = [d for d in rendered if covered(d['clip'], visible)]
            candidates = [(d, d['frame']['presents'][0]) for d in covering
                if len(d['frame']['presents']) == 1 and d['frame']['presents'][0]['dropped'] == 0
                and d['frame']['presents'][0]['mode'] == 'Hardware: Legacy Flip']
            candidate = min(candidates, key=lambda pair: pair[1]['shown']) if candidates else None
            row = dict(Run=t['run'], AppProcessId=t['app'], ProcessId=t['pid'], Transition=t['transition'],
                Count=t['count'], Target=target, ExpectedX=rect['Left'], ExpectedY=rect['Top'],
                ExpectedWidth=rect['Width'], ExpectedHeight=rect['Height'],
                VisibleLeft=visible['Left'], VisibleTop=visible['Top'], VisibleRight=visible['Right'], VisibleBottom=visible['Bottom'],
                ClientLeft=client['Left'], ClientTop=client['Top'], ClientRight=client['Right'], ClientBottom=client['Bottom'],
                PresentationSearchStartEtwUs=t['start'], PresentationSearchEndEtwUs=deadline,
                MatchingDraws=len(matching), MatchingRenderedContent=len(rendered), MatchingCoveringContent=len(covering),
                DiagnosticClientTranslatedDraws=len(client_draws),
                DiagnosticClientClipCoveringDraws=sum(covered(d['clip'], client) for d in client_draws),
                CorrelatedDisplayedFrame=candidate is not None, Hwnd=g['Hwnd'], Visual=None,
                FrameId=None, DrawEtwUs=None, PresentQpc=None, DisplayedQpc=None, SubmitToDisplayedMs=None)
            if candidate:
                draw, present = candidate
                row.update(Visual=draw['visual'], FrameId=draw['frame']['ident'], DrawEtwUs=draw['time'],
                    PresentQpc=present['qpc'], DisplayedQpc=present['shown'],
                    SubmitToDisplayedMs=(present['shown'] - t['StartQpc']) * 1000 / frequency)
            target_rows.append(row)
    assert len(target_rows) == 2440
    write_csv(args.output / 'target-presentation-correlation.csv', target_rows)
    write_csv(args.output / 'compositor-frames.csv', frame_rows)
    if links:
        write_csv(args.output / 'hwnd-visual-links.csv', links)
    frozen = args.output / pathlib.Path(__file__).name
    frozen.write_bytes(pathlib.Path(__file__).read_bytes())
    inputs = [cpu_path, export_path, pm_path, native / 'fullapp.etl', native / 'before-tracing-desktop.json', args.exports / 'dwm-events.csv',
        args.exports / 'presents.csv', source, frozen]
    inputs.extend(native / f'run-{i}/{name}' for i in range(1, 6)
        for name in ['fullapp-summary.json', 'targets/cleanup.json'])
    report = dict(Verdict='FULLAPP_PRESENTATION_DERIVATION_ONLY', StageAccepted=False,
        Snapshot=str(base), Commands=sys.argv, QpcFrequency=frequency, PresentMonOriginQpc=origin,
        SchedulingFailures=cpu['SchedulingFailures'], InitialIncompleteFrameStops=initial_stops,
        FirstNativeBoundaryEtwUs=first_boundary, DwmEventCounts=counts, HwndVisualLinks=len(links),
        TargetFinalPositions=len(target_rows), TargetsWithDisplayedFrameCorrelation=sum(r['CorrelatedDisplayedFrame'] for r in target_rows),
        BindingValidatedDraws=len(draws),
        DiagnosticTargetsWithClientTranslation=sum(r['DiagnosticClientTranslatedDraws'] > 0 for r in target_rows),
        DiagnosticTargetsWithClientClipCoverage=sum(r['DiagnosticClientClipCoveringDraws'] > 0 for r in target_rows),
        HardwarePresentation='INDEPENDENT_VERIFICATION_PENDING',
        GeometryRule='Exact native transform and render-content size; clip covers the separately recorded extended visible frame, including its client rectangle.',
        Limits=['Visible extended frame excludes invisible native resize margins.',
            'The HWND/target-process/visual chain is anchored between the owned alive observation and first warmup; later surface rebindings must remain continuous.',
            'Client-coordinate and clip counters are diagnostics only: they do not prove full visible-frame rendering or hardware presentation.',
            'PresentMon displayed timestamps require independent DXG hardware completion verification; no photons or speedup claim.'],
        Inputs=[dict(Path=str(p.resolve()), Bytes=p.stat().st_size, SHA256=sha(p)) for p in inputs],
        Outputs=[dict(Path=str(p.resolve()), Bytes=p.stat().st_size, SHA256=sha(p)) for p in sorted(args.output.glob('*.csv'))])
    with (args.output / 'derivation.json').open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    print(json.dumps({k: v for k, v in report.items() if k not in ['Inputs', 'Outputs', 'DwmEventCounts']}, indent=2))


if __name__ == '__main__':
    main()
