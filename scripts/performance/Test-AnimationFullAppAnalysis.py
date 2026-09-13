"""Exercise scheduling attribution, phase boundaries and incomplete ETW rejection."""
import argparse
import importlib.util
import json
import pathlib


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    assert not args.output.exists()
    path = pathlib.Path(__file__).with_name('Analyze-AnimationFullApp.py')
    spec = importlib.util.spec_from_file_location('fullapp_analysis', path)
    analysis = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(analysis)
    results = []

    def switch(time, old, new, cpu=0):
        row = [''] * 17
        row[0], row[1], row[16] = 'CSwitch', str(time), str(cpu)
        row[2], row[3] = f'new ({new[0]})', str(new[1])
        row[8], row[9] = f'old ({old[0]})', str(old[1])
        return row

    idle, app, worker, target, foreign = (0, 0), (10, 11), (10, 12), (20, 21), (30, 31)
    trace = [switch(0, idle, app), switch(500, idle, worker, 1),
        switch(1000, app, target), switch(1500, worker, foreign, 1),
        switch(2000, target, app), switch(3000, app, idle)]
    spans, failures, count = analysis.scheduled_intervals(trace, {10, 20})
    assert not failures and count == 6 and set(spans) == {app, worker, target}
    assert analysis.occupancy(spans[app], 0, 3000) == 2
    assert analysis.occupancy(spans[worker], 0, 3000) == 1
    assert analysis.occupancy(spans[target], 0, 3000) == 1
    results.append('Separate app workers, target owner and foreign processes across CPUs')
    assert analysis.occupancy(spans[app], 500, 2500) == 1
    assert analysis.occupancy(spans[app], 1000, 2000) == 0
    assert analysis.occupancy(spans[app], 1000, 1000) == 0
    assert sum(analysis.occupancy(spans[app], a, b) for a, b in
        [(500, 1250), (1250, 2250), (2250, 2500)]) == 1
    results.append('Clip crossing intervals and partition phases without double counting')
    reuse = [switch(0, idle, app), switch(1000, app, idle),
        switch(2000, idle, app, 1), switch(3000, app, idle, 1)]
    spans, failures, _ = analysis.scheduled_intervals(reuse, {10})
    assert not failures and analysis.occupancy(spans[app], 0, 3000) == 2
    results.append('Allow a thread to migrate only between completed intervals')
    for name, broken in [
        ('Missing switch-in', [switch(1000, app, idle)]),
        ('Missing switch-out', [switch(0, idle, app)]),
        ('Mismatched CPU', [switch(0, idle, app), switch(1000, app, idle, 1)]),
        ('Duplicate switch-in', [switch(0, idle, app), switch(500, idle, app, 1), switch(1000, app, idle, 1)]),
    ]:
        _, failures, _ = analysis.scheduled_intervals(broken, {10})
        assert failures, name
        results.append(name + ' rejected')
    rejected = False
    try:
        analysis.scheduled_intervals([switch(1000, idle, app), switch(0, app, idle)], {10})
    except AssertionError:
        rejected = True
    assert rejected
    results.append('Out-of-order raw export rejected')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    receipt = dict(Verdict='PASS', Cases=len(results), Results=results,
        AnalysisSHA256=analysis.sha(path), TestSHA256=analysis.sha(pathlib.Path(__file__)),
        MeasurementEvidence=False)
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(receipt, stream, indent=2)
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
