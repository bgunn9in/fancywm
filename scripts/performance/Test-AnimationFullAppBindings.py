"""Replay real M6 binding churn and reject broken HWND/visual continuity.

The client-space draw remains rejected by the full-frame geometry gate.
These tests never establish hardware presentation.
"""
import argparse
import copy
import csv
import importlib.util
import json
import pathlib
import struct


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    assert not args.output.exists()
    scripts = pathlib.Path(__file__).parent
    analysis = module('fullapp_presentation', scripts / 'Analyze-AnimationFullAppPresentation.py')
    verifier = module('fullapp_presentation_verifier', scripts / 'Verify-AnimationFullAppPresentation.py')
    base = args.snapshot.resolve()
    summary = analysis.read(base / 'native/run-1/fullapp-summary.json')
    cpu = analysis.read(base / 'analysis/cpu/derivation.json')
    created = next(g for g in summary['Observations'] if g['Kind'] == 'Created' and g['Count'] == 1)
    transitions = [g for g in summary['Observations'] if g['Kind'] == 'Transition' and g['Count'] == 1]
    transition = next(g for g in transitions if g['Measured'])
    geometry = transition['Geometry'][0]
    hwnd = geometry['Hwnd']
    origin = cpu['PresentMonOriginQpc']
    start = (transition['StartQpc'] - origin) / 10
    end = min((g['StartQpc'] - origin) / 10 for g in transitions if g['StartQpc'] > transition['StartQpc'])
    window = dict(hwnd=hwnd, pid=analysis.read(base / 'native/run-1/targets/cleanup.json')['ProcessId'],
        alive=(created['Targets']['Qpc'] - origin) / 10,
        first=min((g['StartQpc'] - origin) / 10 for g in transitions), end=end)
    raw = base / 'analysis/exports/dwm-events.csv'
    witness = next(r for r in analysis.read(raw.parent / 'export-command.json')['Exports'] if pathlib.Path(r['Path']) == raw)
    assert analysis.sha(raw) == witness['SHA256']
    sprites, bindings, checked, evidence, candidates = {}, {}, {}, [], []
    with raw.open(encoding='utf-8-sig', newline='') as stream:
        for row in csv.reader(stream, skipinitialspace=True):
            if len(row) < 10 or not row[1].isdigit():
                continue
            stamp = int(row[1])
            if stamp >= end:
                break
            payload = dict(v.split(' : ', 1) for v in row[9:] if ' : ' in v)
            if '/WINDOWNODE_GDISPRITE_ASSOCIATION/' in row[0]:
                sprites[payload['GdiSpritePointer']] = (int(payload['windowHandle'], 16), stamp)
            elif '/BIND_GDISPRITEBITMAP_FIRST_TOKEN/' in row[0]:
                visual, sprite = payload['windowNodePointer'], payload['logicalSurfaceImagePointer']
                analysis.bind_visual(bindings, visual, sprites.get(sprite), stamp, sprite)
                verifier.update_visual_binding(checked, sprites, payload, stamp)
                if sprites.get(sprite, (None,))[0] == hwnd:
                    evidence.append(dict(Time=stamp, Payload=payload, Association=sprites[sprite]))
            elif '/ETWGUID_DRAWING_CONTEXT_STATE/' in row[0] and stamp >= start:
                visual = payload['Visual']
                if visual not in bindings or int(payload['AttributedProcessId']) != window['pid']:
                    continue
                if analysis.bound_window([window], bindings[visual], window['pid'], stamp) is not window:
                    continue
                matrix = struct.unpack('<16f', bytes.fromhex(' '.join(payload['TransformMatrix'].split()[1:65])))
                if list(matrix[12:14]) == [geometry['ClientScreen'][k] for k in ['Left', 'Top']]:
                    candidates.append((stamp, payload, copy.deepcopy(bindings[visual]), copy.deepcopy(checked[visual])))
    assert candidates
    stamp, payload, binding, independent = candidates[-1]
    latest, _ = verifier.verify_binding_lifetime(independent, hwnd, window['alive'], window['first'], stamp)
    assert latest[1] > window['first'], 'The common real trace must exercise post-warmup rebinding'
    # Exact old filtering condition, frozen in M6/source, rejects this draw.
    old_accepts = latest[1] <= window['first'] <= stamp < window['end']
    assert not old_accepts
    results = ['Real M6 post-warmup rebinding: old predicate rejects; both corrected replays retain the owned anchor']

    def rejected(name, candidate, proof, owned=window, time=stamp, process=None):
        assert analysis.bound_window([owned], candidate, owned['pid'] if process is None else process, time) is None, name
        failed = False
        try:
            verifier.verify_binding_lifetime(proof, owned['hwnd'], owned['alive'], owned['first'], time)
        except AssertionError:
            failed = True
        assert failed, name
        results.append(name)

    latest_only = dict(hwnd=hwnd, history=binding['history'][-1:])
    rejected('A new visual first bound after warmup has no anchor', latest_only, (hwnd, independent[1][-1:]))
    rejected('A different HWND cannot inherit this anchor', dict(binding, hwnd=hwnd+1), (hwnd+1, independent[1]))
    rejected('An anchor from before this owned lifetime is rejected', binding, independent,
        owned=dict(window, alive=window['first']-1))
    rejected('Future binding cannot prove an earlier draw', binding, independent, time=latest[1]-1)
    assert analysis.bound_window([window], binding, window['pid']+1, stamp) is None
    results.append('Wrong attributed process rejected')
    assert analysis.bound_window([window], binding, window['pid'], window['end']) is None
    results.append('Draw at next lifetime boundary rejected')
    try:
        analysis.bound_window([window, dict(window)], binding, window['pid'], stamp)
    except AssertionError:
        results.append('Ambiguous owner lifetime rejected')
    else:
        raise AssertionError('Ambiguous owner lifetime accepted')

    for name, middle in [('Unknown sprite breaks continuity', None),
                         ('Changed HWND breaks continuity', (hwnd+1, int(window['first'])+1)),
                         ('Stale association breaks continuity', (hwnd, 0))]:
        visual = payload['Visual']
        a = {visual: copy.deepcopy(binding)}
        b = {visual: copy.deepcopy(independent)}
        # Use a fresh chronological sequence after the last observed binding.
        now = latest[1]+1
        sprite_map = {} if middle is None else {'middle': middle}
        analysis.bind_visual(a, visual, middle, now, 'middle')
        verifier.update_visual_binding(b, sprite_map,
            dict(windowNodePointer=visual, logicalSurfaceImagePointer='middle'), now)
        analysis.bind_visual(a, visual, (hwnd, now+1), now+2, 'last')
        verifier.update_visual_binding(b, {'last': (hwnd, now+1)},
            dict(windowNodePointer=visual, logicalSurfaceImagePointer='last'), now+2)
        rejected(name, a[visual], b[visual], time=max(stamp, now+3))

    try:
        verifier.verify_draw_geometry(payload, geometry['Actual'], geometry['ExtendedFrame'])
    except AssertionError:
        results.append('Real client-space transform is still rejected as full native-frame geometry')
    else:
        raise AssertionError('Client-space draw was relabeled as a full-frame proof')
    clip = [float(payload['Clip'+k]) for k in ['Left', 'Top', 'Right', 'Bottom']]
    assert analysis.covered(clip, geometry['ClientScreen'])
    assert not analysis.covered(clip, geometry['ExtendedFrame'])
    results.append('Actual clip covers client but not the full visible frame; no acceptance relaxation')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = dict(Verdict='PASS', Cases=len(results), Results=results, MeasurementEvidence=False,
        OldBindingPredicateAccepts=old_accepts, RawBindings=evidence,
        RawDraw=dict(Time=stamp, Payload=payload), OwnedWindow=window,
        Native=geometry['Actual'], Visible=geometry['ExtendedFrame'], Client=geometry['ClientScreen'],
        Inputs=[dict(Path=str(p.resolve()), SHA256=analysis.sha(p)) for p in [raw,
            base/'native/run-1/fullapp-summary.json', pathlib.Path(__file__),
            scripts/'Analyze-AnimationFullAppPresentation.py', scripts/'Verify-AnimationFullAppPresentation.py']])
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    print(json.dumps(dict(Verdict=report['Verdict'], Cases=len(results), Output=str(args.output))))


if __name__ == '__main__':
    main()
