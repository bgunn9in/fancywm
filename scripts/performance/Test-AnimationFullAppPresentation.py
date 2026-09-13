"""Verify native/visible/client geometry guards using immutable V14 observations.

Generated DWM payloads are test inputs only, never hardware-display evidence.
"""
import argparse
import copy
import importlib.util
import json
import pathlib
import struct


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--behavior-snapshot',type=pathlib.Path,required=True)
    parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args()
    assert not args.output.exists()
    verifier_path=pathlib.Path(__file__).with_name('Verify-AnimationFullAppPresentation.py')
    spec=importlib.util.spec_from_file_location('presentation_verifier',verifier_path)
    verifier=importlib.util.module_from_spec(spec);spec.loader.exec_module(verifier)
    results=[];inputs=[]
    for configuration in ['Debug','Release']:
        path=args.behavior_snapshot/'validation'/configuration/'fullapp-summary.json'
        before=verifier.hash_file(path);summary=verifier.read_json(path)
        transition=next(t for t in summary['Observations'] if t['Kind']=='Transition' and t['Measured'])
        geometry=transition['Geometry'][0]
        created=next(t for t in summary['Observations'] if t['Kind']=='Created' and t['Count']==transition['Count'])
        window=next(w for w in created['Targets']['Windows'] if w['Hwnd']==geometry['Hwnd'])
        rect=geometry['Actual'];visible=geometry['ExtendedFrame']
        reported=dict(Hwnd=geometry['Hwnd'],ExpectedX=rect['Left'],ExpectedY=rect['Top'],
            ExpectedWidth=rect['Width'],ExpectedHeight=rect['Height'])
        reported.update({label+k:g[k] for label,g in [('Visible',visible),('Client',geometry['ClientScreen'])]
            for k in ['Left','Top','Right','Bottom']})
        verifier.verify_window_geometry(geometry,window,reported)
        results.append(configuration+': recorded geometry passes')
        for name,mutation in [
            ('wrong HWND',lambda g:g.update(Hwnd=g['Hwnd']+1)),
            ('wrong owner',lambda g:g.update(OwnerThreadId=g['OwnerThreadId']+1)),
            ('wrong DPI',lambda g:g.update(Dpi=192)),
            ('hidden HWND',lambda g:g.update(Visible=False)),
            ('cloaked HWND',lambda g:g.update(Cloaked=1)),
            ('wrong native rectangle',lambda g:g['Actual'].update(Left=g['Actual']['Left']+1)),
            ('wrong visible frame',lambda g:g['ExtendedFrame'].update(Right=g['ExtendedFrame']['Right']-1)),
            ('wrong client rectangle',lambda g:g['ClientScreen'].update(Left=g['ClientScreen']['Left']+1)),
        ]:
            altered=copy.deepcopy(geometry);mutation(altered);rejected=False
            try:verifier.verify_window_geometry(altered,window,reported)
            except AssertionError:rejected=True
            assert rejected,name
            results.append(configuration+': '+name+' rejected')
        matrix=[1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,float(rect['Left']),float(rect['Top']),0.,1.]
        def encode(values):return '64 '+struct.pack('<16f',*values).hex(' ')
        payload=dict(TransformMatrix=encode(matrix),**{'Clip'+k:str(visible[k]) for k in ['Left','Top','Right','Bottom']})
        _,covers=verifier.verify_draw_geometry(payload,rect,visible)
        assert covers and (rect['Left']<visible['Left'] or rect['Bottom']>visible['Bottom'])
        results.append(configuration+': visible coverage excludes recorded invisible resize margins')
        clipped=dict(payload,ClipRight=str(visible['Right']-1))
        assert not verifier.verify_draw_geometry(clipped,rect,visible)[1]
        results.append(configuration+': one visible pixel missing cannot establish full coverage')
        for name,index in [('wrong translation',12),('nonidentity scale',0)]:
            values=list(matrix);values[index]+=1;rejected=False
            try:verifier.verify_draw_geometry(dict(payload,TransformMatrix=encode(values)),rect,visible)
            except AssertionError:rejected=True
            assert rejected,name
            results.append(configuration+': '+name+' rejected')
        assert verifier.hash_file(path)==before
        inputs.append(dict(Path=str(path.resolve()),SHA256=before))
    args.output.parent.mkdir(parents=True,exist_ok=True)
    receipt=dict(Verdict='PASS',Cases=len(results),Results=results,MeasurementEvidence=False,Inputs=inputs,
        TestSHA256=verifier.hash_file(pathlib.Path(__file__)),VerifierSHA256=verifier.hash_file(verifier_path))
    with args.output.open('x',encoding='utf-8') as stream:json.dump(receipt,stream,indent=2)
    print(json.dumps(dict(Verdict='PASS',Cases=len(results),Output=str(args.output))))


if __name__=='__main__':main()
