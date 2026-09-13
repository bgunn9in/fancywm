"""Read-only ETL-export derivation; outputs never constitute stage acceptance.

Inputs: xperf dumper Thread and Dwm-Core exports with -add_fieldnames,
PresentMon --etl_file --v1_metrics --qpc_time output, and native capture.
Every output directory must be fresh. No recording or production code runs.
"""
import argparse
import bisect
import collections
import csv
import decimal
import hashlib
import json
import pathlib
import statistics
import struct
import sys


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def csv_rows(path):
    with path.open(encoding='utf-8-sig', newline='') as stream:
        yield from csv.DictReader(stream)


def events(path):
    with path.open(encoding='utf-8-sig', newline='') as stream:
        for row in csv.reader(stream, skipinitialspace=True):
            if len(row) > 1 and row[1].strip().isdigit():
                yield row


def payload(row):
    return dict(field.split(' : ', 1) for field in row[9:] if ' : ' in field)


def write_csv(path, rows, fields=None):
    with path.open('x', encoding='utf-8', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=fields or list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['snapshot', 'thread-events', 'dwm-events', 'presents', 'output']:
        parser.add_argument('--' + name, type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    native = args.snapshot / 'native'
    summaries = [load(p) for p in sorted(native.glob('run-*/summary.json'))]
    frequency, = {s['StopwatchFrequency'] for s in summaries}
    assert frequency == 10_000_000, 'This export protocol requires the captured 10 MHz QPC.'
    markers = {}
    for row in events(native / 'markers.csv'):
        if row[0] == 'FancyWM-AnimationNativeHarness/Boundary/':
            data = payload(row)
            pid = int(row[2].rsplit('(', 1)[1].rstrip(') '))
            key = (pid, int(data['transition']), int(data['phase']))
            assert key not in markers
            markers[key] = (int(row[1]), int(data['qpc']))

    windows = []
    transitions = []
    owners = set()
    native_starts=sorted(stamp for (pid,ident,phase),(stamp,qpc) in markers.items() if phase==1)
    def next_native_start(stamp):
        index=bisect.bisect_right(native_starts,stamp)
        return native_starts[index] if index<len(native_starts) else float('inf')
    for run, summary in enumerate(summaries, 1):
        pid = summary['ProcessId']
        calls = list(csv_rows(native / f'run-{run}' / 'calls.csv'))
        tids_by_count = {}
        for display in summary['Displays']:
            count = display['Count']
            ids = {t['Transition'] for t in summary['Transitions'] if t['Count'] == count}
            first = min(markers[(pid, i, 1)][0] for i in ids)
            last = max(markers[(pid, i, 4)][0] for i in ids)
            tids = []
            for target, hwnd in enumerate(display['Handles']):
                observed = {int(c['thread_id']) for c in calls if c['operation'] == 'NativeApplied'
                            and int(c['transition']) in ids and int(c['target']) == target}
                assert len(observed) == 1, (run, count, target, observed)
                tid = observed.pop()
                if 'OwnerThreadIds' in display:
                    assert display['OwnerThreadIds'][target] == tid
                    assert display['OwnerProcessIds'][target] == pid
                tids.append(tid)
                owners.add((pid, tid))
                windows.append(dict(run=run, pid=pid, count=count, target=target, hwnd=hwnd,
                                    tid=tid, first=first, last=last,presentation_end=next_native_start(last)))
            tids_by_count[count] = sorted(set(tids))
        for transition in summary['Transitions']:
            if not transition['Measured']:
                continue
            ident, count = transition['Transition'], transition['Count']
            display = next(d for d in summary['Displays'] if d['Count'] == count)
            expected = display['Destinations'] if transition['Iteration'] % 2 else display['Originals']
            transitions.append(dict(run=run, pid=pid, transition=ident, count=count,
                                    ownerCount=len(tids_by_count[count]), tids=tids_by_count[count],
                                    start=markers[(pid, ident, 1)][0],
                                    completed=markers[(pid, ident, 2)][0],
                                    verified=markers[(pid, ident, 3)][0],
                                    end=markers[(pid, ident, 4)][0], expected=expected,
                                    presentation_end=next_native_start(markers[(pid,ident,1)][0]),
                                    StartQpc=transition['StartQpc'],
                                    calls=[c for c in calls if int(c['transition']) == ident]))

    active = {}
    intervals = collections.defaultdict(list)
    switches = 0
    matched = 0
    failures = []
    switch_fields = ['timestamp_us', 'pid', 'tid', 'direction', 'cpu', 'old_state', 'wait_reason']
    with (args.output / 'owner-switches.csv').open('x', encoding='utf-8', newline='') as stream:
        writer = csv.writer(stream)
        writer.writerow(switch_fields)
        for row in events(args.thread_events):
            if row[0].strip() != 'CSwitch':
                continue
            switches += 1
            time, newtid, oldtid, cpu = int(row[1]), int(row[3]), int(row[9]), int(row[16])
            newpid = int(row[2].rsplit('(', 1)[1].rstrip(') '))
            oldpid = int(row[8].rsplit('(', 1)[1].rstrip(') '))
            old, new = (oldpid, oldtid), (newpid, newtid)
            if old in owners:
                writer.writerow([time, *old, 'out', cpu, row[12], row[13]])
                start = active.pop(old, None)
                if start is None or start[1] != cpu or start[0] > time:
                    failures.append(dict(kind='unmatched-or-CPU-mismatch', time=time, owner=old, start=start, cpu=cpu))
                else:
                    intervals[old].append((start[0], time, cpu))
                    matched += 1
            if new in owners:
                writer.writerow([time, *new, 'in', cpu, row[12], row[13]])
                if new in active:
                    failures.append(dict(kind='duplicate-in', time=time, owner=new))
                active[new] = (time, cpu)
    for owner in active:
        failures.append(dict(kind='unmatched-in', owner=owner))
    schedule_rows = []
    for t in transitions:
        row = {k: t[k] for k in ['run', 'pid', 'transition', 'count', 'ownerCount']}
        row.update(EtwTaskToGeometryMs=(t['verified'] - t['completed']) / 1000,
                   OwnerScheduledThroughGeometryMs=0.0, OwnerScheduledAfterTaskMs=0.0)
        for tid in t['tids']:
            for start, end, cpu in intervals[(t['pid'], tid)]:
                row['OwnerScheduledThroughGeometryMs'] += max(0, min(end, t['verified']) - max(start, t['start'])) / 1000
                row['OwnerScheduledAfterTaskMs'] += max(0, min(end, t['verified']) - max(start, t['completed'])) / 1000
        schedule_rows.append(row)
    write_csv(args.output / 'owner-scheduling.csv', schedule_rows)

    presents = list(csv_rows(args.presents))
    D = decimal.Decimal
    origins = {D(p['QPCTime']) - D(p['TimeInSeconds']) * frequency for p in presents}
    assert max(origins) - min(origins) < D('0.01'), 'PresentMon QPC/time origin differs across rows.'
    origin = round(statistics.median(origins))
    marker_delays = [stamp * 10 + origin - qpc for stamp, qpc in markers.values()]
    assert min(marker_delays) >= -10 and max(marker_delays) < frequency / 10, 'ETL export and PresentMon clocks disagree.'
    dwm_presents = []
    for p in presents:
        if p['Application'].casefold() == 'dwm.exe':
            dwm_presents.append(dict(qpc=int(p['QPCTime']),
                                     shown=int(p['QPCTime']) + float(p['msUntilDisplayed']) * frequency / 1000,
                                     dropped=int(p['Dropped']), pid=int(p['ProcessID']),
                                     mode=p['PresentMode'], gpu_ms=float(p['msGPUActive']) if p.get('msGPUActive','').strip() else None))
    dwm_presents.sort(key=lambda p: p['qpc'])
    present_qpcs = [p['qpc'] for p in dwm_presents]

    handles = {w['hwnd'] for w in windows}
    associations = {}
    visuals = {}
    frame = None
    frames = []
    draws = []
    links = []
    counts = collections.Counter()
    initial_frame_stops = []
    first_native_boundary = min(w['first'] for w in windows)
    last_dwm_stamp=0
    for row in events(args.dwm_events):
        name, stamp = row[0], int(row[1])
        if not name.startswith('Microsoft-Windows-Dwm-Core/'):
            continue
        counts[name] += 1
        last_dwm_stamp=max(last_dwm_stamp,stamp)
        data = payload(row)
        if '/SCHEDULE_PROCESS_FRAME/win:Start' in name:
            assert frame is None
            frame = dict(start=stamp, end=None, id=None, drawCount=0, presentStart=None, presentEnd=None,
                         rendered={}, pid=int(row[2].rsplit('(',1)[1].rstrip(') ')), tid=int(row[3]))
        elif '/CurrentFrameId/' in name and frame is not None:
            frame['id'] = data['FrameId']
        elif '/WINDOWNODE_GDISPRITE_ASSOCIATION/' in name:
            hwnd = int(data['windowHandle'], 16)
            sprite = data['GdiSpritePointer']
            # Pointer reuse replaces the previous association at its event time.
            associations[sprite] = (hwnd, stamp)
        elif '/BIND_GDISPRITEBITMAP_FIRST_TOKEN/' in name:
            sprite = data['logicalSurfaceImagePointer']
            if sprite in associations:
                hwnd, associated = associations[sprite]
                visual = data['windowNodePointer']
                visuals[visual] = (hwnd, stamp)
                if hwnd in handles:
                    links.append(dict(hwnd=hwnd, sprite=sprite, visual=visual,
                                      association_us=associated, binding_us=stamp))
        elif '/OVERLAY_PRESENT/win:Start' in name and frame is not None:
            frame['presentStart'] = stamp
        elif '/OVERLAY_PRESENT/win:Stop' in name and frame is not None:
            frame['presentEnd'] = stamp
        elif '/ETWGUID_VISUAL_RENDERCONTENT/' in name and frame is not None:
            frame['rendered'][data['Visual']] = [float(data[k]) for k in ['Left','Top','Right','Bottom']]
        elif '/ETWGUID_DRAWING_CONTEXT_STATE/' in name:
            if frame is None or data['Visual'] not in visuals:
                continue
            hwnd, bound = visuals[data['Visual']]
            if hwnd not in handles:
                continue
            pid = int(data['AttributedProcessId'])
            window = next((w for w in windows if w['hwnd'] == hwnd and w['pid'] == pid
                           and bound<=w['first']<=stamp<w['presentation_end']), None)
            if window is None:
                continue
            encoded = data['TransformMatrix'].split()
            matrix = struct.unpack('<16f', bytes.fromhex(' '.join(encoded[1:65])))
            assert matrix[:12] == (1., 0., 0., 0., 0., 1., 0., 0., 0., 0., 1., 0.)
            assert matrix[14:] == (0., 1.)
            frame['drawCount'] += 1
            draws.append(dict(frame=frame, pid=pid, hwnd=hwnd, target=window['target'],
                              count=window['count'], time=stamp, x=matrix[12], y=matrix[13],
                              visual=data['Visual'], bound=bound,
                              clip=[float(data[k]) for k in ['ClipLeft','ClipTop','ClipRight','ClipBottom']]))
        elif '/SCHEDULE_PROCESS_FRAME/win:Stop' in name:
            if frame is None:
                # Enabling a provider can begin inside an already active DWM
                # frame. Retain that leading stop, but never use it for a
                # target correlation or allow a gap inside the native runs.
                assert not frames and not initial_frame_stops and stamp < first_native_boundary
                initial_frame_stops.append(dict(TimeUs=stamp,Process=row[2],ThreadId=int(row[3]),RawFields=row))
                continue
            assert frame['pid']==int(row[2].rsplit('(',1)[1].rstrip(') ')) and frame['tid']==int(row[3])
            frame['end'] = stamp
            frames.append(frame)
            frame = None
    assert frame is None
    write_csv(args.output / 'hwnd-visual-links.csv', links)
    frame_rows = []
    for f in frames:
        first, last = f['presentStart'], f['presentEnd']
        if first is None or last is None:
            f['presents'] = []
        else:
            # Membership in the emitting API interval, never nearest-frame attribution.
            low = bisect.bisect_left(present_qpcs, first * 10 + origin - 10)
            high = bisect.bisect_right(present_qpcs, last * 10 + origin + 10)
            f['presents'] = [p for p in dwm_presents[low:high] if p['pid'] == f['pid']]
        frame_rows.append(dict(FrameId=f['id'], StartUs=f['start'], EndUs=f['end'],
                               PresentStartUs=first, PresentEndUs=last, TargetDraws=f['drawCount'],
                               PresentMonRows=len(f['presents']),
                               DisplayedRows=sum(p['dropped'] == 0 for p in f['presents'])))
    write_csv(args.output / 'compositor-frames.csv', frame_rows)
    draws_by_target = collections.defaultdict(list)
    for draw in draws:
        draws_by_target[(draw['pid'],draw['count'],draw['target'])].append(draw)
    target_rows = []
    for t in transitions:
        for target, rect in enumerate(t['expected']):
            deadline=min(t['presentation_end'],last_dwm_stamp)
            matching = [d for d in draws_by_target[(t['pid'],t['count'],target)] if t['start'] <= d['time'] < deadline
                        and d['x'] == rect['Left'] and d['y'] == rect['Top']]
            rendered = [d for d in matching if d['frame']['rendered'].get(d['visual']) ==
                        [0., 0., float(rect['Width']), float(rect['Height'])]]
            # The same visual can have several damage-clipped draws in one
            # frame. Select a full covering draw, not the first partial draw
            # tied at the same presentation timestamp.
            covering = [d for d in rendered if d['clip'][0]<=rect['Left'] and d['clip'][1]<=rect['Top']
                        and d['clip'][2]>=rect['Right'] and d['clip'][3]>=rect['Bottom']]
            candidates = [(d, d['frame']['presents'][0]) for d in covering
                          if len(d['frame']['presents']) == 1 and d['frame']['presents'][0]['dropped'] == 0
                          and d['frame']['presents'][0]['mode'] == 'Hardware: Legacy Flip']
            candidate = min(candidates, key=lambda pair: pair[1]['shown']) if candidates else None
            row = dict(Run=t['run'], ProcessId=t['pid'], Transition=t['transition'], Count=t['count'],
                       Target=target, ExpectedX=rect['Left'], ExpectedY=rect['Top'],
                       PresentationSearchStartEtwUs=t['start'],PresentationSearchEndEtwUs=deadline,
                       MatchingDraws=len(matching), MatchingRenderedContent=len(rendered),
                       MatchingCoveringContent=len(covering), CorrelatedDisplayedFrame=candidate is not None,
                       Hwnd=None, Visual=None, FrameId=None, DrawEtwUs=None, PresentQpc=None,
                       DisplayedQpc=None, SubmitToDisplayedMs=None)
            if candidate:
                d, p = candidate
                row.update(Hwnd=d['hwnd'], Visual=d['visual'], FrameId=d['frame']['id'],
                           DrawEtwUs=d['time'], PresentQpc=p['qpc'], DisplayedQpc=p['shown'],
                           SubmitToDisplayedMs=(p['shown']-t['StartQpc'])*1000/frequency)
            target_rows.append(row)
    write_csv(args.output / 'target-presentation-correlation.csv', target_rows)
    compositor_rows=[]
    for t in transitions:
        begin,end=t['start']*10+origin,t['end']*10+origin
        ps=dwm_presents[bisect.bisect_left(present_qpcs,begin):bisect.bisect_right(present_qpcs,end)]
        gpu_values=[p['gpu_ms'] for p in ps if p['gpu_ms'] is not None]
        compositor_rows.append(dict(Run=t['run'],ProcessId=t['pid'],Count=t['count'],Transition=t['transition'],
                                   DwmPresents=len(ps),DwmDropped=sum(p['dropped'] for p in ps),
                                   DwmGpuSamples=len(gpu_values),
                                   DwmGpuStatus='MEASURED' if ps and len(gpu_values)==len(ps) else 'NOT_MEASURED',
                                   DwmGpuActiveMsSum=sum(gpu_values) if ps and len(gpu_values)==len(ps) else None,
                                   DwmGpuActiveMsMean=statistics.mean(gpu_values) if gpu_values else None))
    write_csv(args.output/'shared-compositor-counters.csv',compositor_rows)
    inputs = [args.thread_events, args.dwm_events, args.presents, native/'animation.etl',
              native/'markers.csv', pathlib.Path(__file__).resolve()]
    report = dict(Verdict='DERIVATION_ONLY_STAGE_IN_PROGRESS', Snapshot=str(args.snapshot),
                  Commands=sys.argv, CSwitchEvents=switches, Owners=len(owners),
                  MatchedOwnerIntervals=matched, SchedulingFailures=failures,
                  QpcFrequency=frequency, PresentMonOriginQpc=origin,
                  EtwMarkerEmissionDelayTicks=dict(min=min(marker_delays), max=max(marker_delays)),
                  SchedulingAggregates=[dict(Count=c, Transitions=len(rr),
                       MedianEtwTaskToGeometryMs=statistics.median(r['EtwTaskToGeometryMs'] for r in rr),
                       MedianOwnerScheduledAfterTaskMs=statistics.median(r['OwnerScheduledAfterTaskMs'] for r in rr),
                       P50NearestRankEtwTaskToGeometryMs=sorted(r['EtwTaskToGeometryMs'] for r in rr)[(len(rr)-1)//2],
                       P50NearestRankOwnerScheduledAfterTaskMs=sorted(r['OwnerScheduledAfterTaskMs'] for r in rr)[(len(rr)-1)//2])
                       for c in [1,10,50] for rr in [[r for r in schedule_rows if r['count']==c]]],
                  DwmEventCounts=counts, DwmPresentMonRows=len(dwm_presents), HwndVisualLinks=len(links),
                  InitialIncompleteFrameStops=initial_frame_stops,FirstNativeBoundaryEtwUs=first_native_boundary,
                  PresentationSearch='Exact final draws between this submission and the next native submission (or retained DWM trace end); original visual binding must predate the first transition for the same HWND/scenario. DwmFlush does not bound physical presentation.',
                  TargetFinalPositions=len(target_rows),
                  TargetsWithDisplayedFrameCorrelation=sum(r['CorrelatedDisplayedFrame'] for r in target_rows),
                  PhysicalPresentationClaim=False,
                  Limitations=['Exploratory correlation requires independent validation before a physical-presentation claim.',
                               'Scheduled owner occupancy includes interrupts; not sampled CPU or process CPU.',
                               'DWM GPU time describes the shared desktop compositor, not attributable whole-app GPU usage.'],
                  Inputs=[dict(Path=str(p.resolve()), Bytes=p.stat().st_size, SHA256=digest(p)) for p in inputs],
                  Outputs=[dict(Path=str(p.resolve()), Bytes=p.stat().st_size, SHA256=digest(p))
                           for p in sorted(args.output.glob('*.csv'))])
    with (args.output/'derivation.json').open('x',encoding='utf-8') as stream:
        json.dump(report,stream,indent=2)
    print(json.dumps({k:v for k,v in report.items() if k not in ['Inputs','DwmEventCounts']},indent=2))


if __name__ == '__main__':
    main()
