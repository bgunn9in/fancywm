"""Retained failure and API boundaries; no native test is rerun here."""
import argparse,importlib.util,shutil,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
PREFIX='FWM-D3D-LIFETIME-20260913-'
def art(name):return s.ART/(PREFIX+name)
def rows(p):return [s.json.loads(x) for x in p.read_text(encoding='utf-8').splitlines()]
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    one=rows(art('P1')/'raw.stdout');two=rows(art('P2')/'raw.stdout');failed=rows(art('P3')/'raw.stdout')
    assert one[0]['HardwareRequested'] and one[0]['Device'] and one[0]['Pid']==s.read(art('P1')/'created.json')['Pid']
    assert [r['Probe'] for r in one if 'Probe' in r]==['device','context','buffer'] and all(r['QueryHResult']==r['RegisterHResult']==0 for r in one if 'Probe' in r) and one[-1]['Cleanup'] and one[-1]['Callbacks']==3
    assert two[0]['CreateDeviceHResult']==0 and two[0]['PrivateDesktop'] and not two[0]['PresentCalled'] and two[0]['Hwnd']>0
    assert [r['Probe'] for r in two if 'Probe' in r]==['direct3d9ex','device9ex','texture9','surface9'] and all(r['QueryHResult']==r['RegisterHResult']==0x80004002 and r['CallbackId']==0 for r in two if 'Probe' in r)
    assert all(two[-1][k] for k in ['Cleanup','HwndDestroyed','ClassUnregistered','OriginalDesktopRestored','OwnedDesktopClosed']) and two[-1]['Callbacks']==0
    assert s.read(art('P3')/'exited.json')['ExitCode']==2 and len([r for r in failed if r['Event']=='case-complete'])==150 and failed[-1]['Value']==2
    failure=next(r for r in failed if r['Event']=='failure');assert failure['Value']==0x80004005 and all(failure['Seq']<r['Seq'] for r in failed if r['Event']=='destroyed' and r['Kind'] in ['device','context'])
    checks=[]
    # The old verifier is used exactly as frozen; the failed scope is not
    # silently reassigned to the updated observer or renamed as a PASS.
    for file,folder,names in [('Verify-HookedD3D9.py','V0',['P8','P9']),('d3d9_owner_model.py','D2',['P10','P11'])]:
        for name in names:
            source=art(folder)/file;spec=importlib.util.spec_from_file_location('frozen_'+folder,source);m=importlib.util.module_from_spec(spec)
            if folder=='V0':
                # Its helper path must resolve to the current read-only helper;
                # use a fresh frozen execution copy with the same source hash.
                execution=dest/(folder+'-'+name);execution.mkdir();shutil.copyfile(source,execution/file);shutil.copyfile(Path(__file__).with_name('Finalize-NativeHeapEvidence.py'),execution/'Finalize-NativeHeapEvidence.py');spec=importlib.util.spec_from_file_location('old',execution/file);m=importlib.util.module_from_spec(spec)
            spec.loader.exec_module(m);root=art(name);pid=s.read(root/'created.json')['Pid'];raw=rows(root/'observer.jsonl')
            try:
                if folder=='V0':m.check(raw,rows(root/'fixture.jsonl'),pid)
                else:m.replay(iter(raw),pid)
            except AssertionError as e:
                text=str(e);assert ('SurfaceLockRect' in text if folder=='V0' else 'incomplete observer lifecycle/maps' in text);checks.append(dict(Run=name,FrozenVerifier=str(source),VerifierSHA256=s.sha(source),ExpectedFailure=text,Preserved=True))
            else:raise AssertionError('old failing source unexpectedly passes')
    assert not (art('V0')/'verification.json').exists() and not (art('D2')/'verification.json').exists()
    s.write(dest/'verification.json',dict(Verdict='D3D_NATIVE_SOURCE_BOUNDARIES_VERIFIED',FrozenFailures=checks,D3D11NotifierSupported=True,D3D9NotifierE_NOINTERFACE=True,FailedContextFixturePreserved=True,NativeReruns=0,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False))
    print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
