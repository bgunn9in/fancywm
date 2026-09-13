"""Check equal final native rectangles across retained full-app processes.

This is a fixture-comparability check, not acceptance of a performance capture.
Behavior scenarios are aligned by target count and iteration, not scenario ID.
"""
import argparse
import csv
import hashlib
import json
import pathlib


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest().upper()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot',type=pathlib.Path,required=True)
    parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args()
    assert not args.output.exists()
    base=args.snapshot.resolve()
    if (base/'native-evidence.json').exists():
        folder=base/'native';manifest=base/'native-evidence.json'
        paths=sorted(folder.glob('run-*/fullapp-summary.json'))
    else:
        folder=base/'validation';manifest=base/'validation-evidence.json'
        paths=[folder/config/'fullapp-summary.json' for config in ['Debug','Release']]
    expected={r['Path']:r for r in read(manifest)}
    assert len(paths)>=2
    reference=None;differences=[];processes=[];inputs=[manifest]
    for path in paths:
        cleanup=path.parent/'targets/cleanup.json'
        for file in [path,cleanup]:
            witness=expected[file.relative_to(folder).as_posix()]
            assert file.stat().st_size==witness['Bytes'] and sha(file)==witness['SHA256']
            inputs.append(file)
        summary=read(path);assert summary['Error'] is None and read(cleanup)['AllDestroyed']
        observations=summary['Observations']
        owners={g['Count']:{w['Hwnd']:w['Index'] for w in g['Targets']['Windows']}
            for g in observations if g['Kind']=='Created'}
        signature={}
        for t in (t for t in observations if t['Kind']=='Transition'):
            key=(t['Count'],t['Iteration'])
            assert key not in signature and len(t['Geometry'])==t['Count']
            assert all(g['Actual']==g['Expected'] and g['NativeEqualsExpected'] for g in t['Geometry'])
            signature[key]=sorted((owners[t['Count']][g['Hwnd']],*(g['Actual'][k] for k in ['Left','Top','Right','Bottom']))
                for g in t['Geometry'])
        assert set(owners)=={1,10,50}
        if reference is None:reference=signature
        assert signature.keys()==reference.keys()
        changed=[k for k in reference if signature[k]!=reference[k]]
        for count in [1,10,50]:
            selected=[k for k in changed if k[0]==count]
            if not selected:continue
            key=next((k for k in selected if k[1]>0),selected[0])
            mismatch=[dict(Reference=a,Actual=b) for a,b in zip(reference[key],signature[key]) if a!=b]
            differences.append(dict(Process=path.parent.name,Count=count,ChangedTransitions=len(selected),
                ExampleIteration=key[1],ChangedTargets=len(mismatch),FirstDifference=mismatch[0]))
        processes.append(dict(Process=path.parent.name,ProcessId=summary['ProcessId'],Transitions=len(signature),
            ChangedTransitions=len(changed),Preparation=summary.get('FixtureLayoutPreparation','UncontrolledInheritedFlex')))
    args.output.parent.mkdir(parents=True,exist_ok=True)
    result=dict(Verdict='PASS' if not differences else 'REJECT',StageAccepted=False,
        Snapshot=str(base),Processes=processes,Differences=differences,
        Rule='Exact target-index/native-rectangle mapping for every count and warmup/measured iteration across processes.',
        PerformanceMeasurementAccepted=False,ScriptSHA256=sha(pathlib.Path(__file__)),
        Inputs=[dict(Path=str(p.resolve()),Bytes=p.stat().st_size,SHA256=sha(p)) for p in inputs])
    with args.output.open('x',encoding='utf-8') as stream:json.dump(result,stream,indent=2)
    print(json.dumps({k:v for k,v in result.items() if k not in ['Inputs','Differences']},indent=2))
    print('Mismatched process/count groups:',len(differences))


if __name__=='__main__':main()
