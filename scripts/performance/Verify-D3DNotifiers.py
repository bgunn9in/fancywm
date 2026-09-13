"""Separate contracts: D3D11 destruction and D3D9 private-data owner release."""
import argparse,copy,importlib.util,shutil
from pathlib import Path
spec=importlib.util.spec_from_file_location('h',Path(__file__).with_name('Verify-HookedD3D9.py'));h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h);s=h.s
one=h.one
def ordered(*rr):assert all(a['Seq']<b['Seq'] and a['Qpc']<b['Qpc'] for a,b in zip(rr,rr[1:]))
def base(rr,pid):
    assert [r['Seq'] for r in rr]==list(range(1,len(rr)+1)) and all(r['Pid']==pid and r['Tid']>0 and r['Qpc']>0 for r in rr)
    assert not any(r['Event'] in ['failure','device-failure','identity-failed','private-guid-collision'] for r in rr)
    assert one(rr,'process-complete')['Value']==0
def check11(rr,pid):
    base(rr,pid);main=one(rr,'device-ready')['Tid'];created=[r for r in rr if r['Event']=='created'];assert len(created)==182
    assert len([r for r in rr if r['Event']=='registered'])==len([r for r in rr if r['Event']=='destroyed'])==182
    for r in created:
        own=[x for x in rr if x['Id']==r['Id']];reg=one(own,'registered');end=one(own,'destroyed');assert r['Value']>0 and reg['Value']==0 and end['Count']==1
        ordered(r,reg,end);assert all(x['Kind']==r['Kind'] and x['Epoch']==r['Epoch'] and x['Cycle']==r['Cycle'] for x in own)
        assert all(x['Count']==(0 if x['Seq']<end['Seq'] else 1) for x in own)
    cases=[r for r in rr if r['Event']=='case-complete'];assert [(r['Epoch'],r['Cycle'],r['Value']) for r in cases]==[(e,c,c%5) for e in range(3) for c in range(50)]
    for case in cases:
        own=[r for r in rr if r['Id']==case['Id']];scenario=one(own,'scenario');end=one(own,'destroyed');assert scenario['Value']==case['Value'] and case['Count']==1;ordered(end,case)
        mapped=[r for r in own if r['Event']=='mapped'];unmapped=[r for r in own if r['Event']=='unmapped']
        assert [r['Extra'] for r in mapped]==[r['Value'] for r in unmapped]==([] if case['Value']==2 else [0,1,2])
        for i,(a,b) in enumerate(zip(mapped,unmapped)):
            assert a['Value']>0;ordered(scenario,a,b,end)
            if i:ordered(unmapped[i-1],a)
        if case['Value'] in [0,4]:ordered(one(own,'release-begin'),end,one(own,'release-end'))
        if case['Value']==1:
            begin=one(own,'worker-release-begin');finish=one(own,'worker-release-end');ordered(one(own,'retainer-acquired'),one(own,'original-released'),begin,end,finish)
            assert begin['Tid']==end['Tid']==finish['Tid']!=main
        if case['Value']==2:
            child=[r for r in rr if r['Epoch']==case['Epoch'] and r['Cycle']==case['Cycle'] and r['Kind']=='view'];holds=one(child,'view-retains-resource');assert holds['Value']==case['Id']
            ordered(holds,one(own,'original-released'),one(child,'view-release-begin'),one(child,'destroyed'),end,one(child,'view-release-end'))
        if case['Value']==3:ordered(one(own,'context-retains-resource'),one(own,'original-released'),one(own,'context-clear-begin'),end,one(own,'context-clear-end'))
        if case['Value']==4:
            canceled=[r for r in rr if r['Epoch']==case['Epoch'] and r['Cycle']==case['Cycle'] and r['Kind']=='unregistered'];assert len(canceled)==3 and all(r['Count']==0 for r in canceled)
            reg=one(canceled,'cancel-register');unreg=one(canceled,'unregister');assert reg['Value']==case['Id'] and unreg['Value']==0 and reg['Extra']==unreg['Extra'];ordered(reg,unreg,end,one(canceled,'canceled-settled'),case)
    epochs=[r for r in rr if r['Event']=='epoch-complete'];assert [(r['Value'],r['Extra']) for r in epochs]==[(e,50) for e in range(3)]
    for e,epoch in enumerate(epochs):
        assert all(r['Seq']<epoch['Seq'] for r in cases if r['Epoch']==e)
        if e<2:assert epoch['Seq']<min(r['Seq'] for r in created if r['Epoch']==e+1)
    device=[r for r in rr if r['Kind']=='device'];context=[r for r in rr if r['Kind']=='context']
    ordered(epochs[-1],one(rr,'shutdown-begin'),one(context,'external-context-released'),one(device,'device-release-begin'),one(context,'destroyed'),one(device,'destroyed'),one(device,'device-release-end'),one(rr,'shutdown-complete'),one(rr,'process-complete'))
    return dict(DestructionCallbacks=182,BufferLifetimes=150,PostWarmupLifetimes=100,MapUnmapPairs=360,UnregisteredCallbacks=30,WorkerFinalReleases=30,PhysicalAllocationReleaseClaim=False)
def check9(rr,pid):
    base(rr,pid);main=one(rr,'device9-create')['Tid'];attached=[r for r in rr if r['Event']=='attach-begin'];assert [r['Id'] for r in attached]==list(range(1,181))
    assert len([r for r in rr if r['Event']=='private-owner-released'])==180
    for r in attached:
        own=[x for x in rr if x['Id']==r['Id']];reg=one(own,'attach-result');end=one(own,'private-owner-released');assert r['Identity']>0 and reg['Value']==0 and end['Closed']==1;ordered(r,reg,end)
        assert all(x['Identity']==r['Identity'] and x['Kind']==r['Kind'] and x['Parent']==r['Parent'] for x in own)
        assert all(x['Closed']==(0 if x['Seq']<end['Seq'] else 1) for x in own)
    cases=[r for r in rr if r['Event']=='case-complete'];assert [(r['Phase'],r['Extra'],r['Value']) for r in cases]==[(e,c,c%5) for e in range(3) for c in range(50)]
    for case in cases:
        own=[r for r in rr if r['Id']==case['Id']];end=one(own,'private-owner-released');scenario=one(own,'scenario');assert scenario['Value']==case['Value'] and scenario['Extra']==case['Extra'];ordered(one(own,'already-observed'),scenario,one(own,'control-lock'),one(own,'control-unlock'),end,case)
        assert one(own,'control-lock')['Value']>0 and one(own,'control-lock')['Extra']>=256
        if case['Value'] in [0,1]:ordered(one(own,'release-begin'),end,one(own,'release-end'))
        if case['Value']==1:ordered(one(own,'retainer-acquired'),one(own,'original-released'),one(own,'release-begin'))
        if case['Value']==2:
            child=[r for r in rr if r['Parent']==case['Id']];ordered(one(own,'parent-external-released'),one(child,'child-release-begin'),end,one(child,'child-release-end'));ordered(one(child,'child-release-begin'),one(child,'private-owner-released'),one(child,'child-release-end'))
        if case['Value']==3:
            begin=one(own,'worker-release-begin');finish=one(own,'worker-release-end');ordered(begin,end,finish);assert begin['Tid']==end['Tid']==finish['Tid']!=main
        if case['Value']==4:
            valid=one(own,'resource-still-valid-after-private-free');assert valid['Value']==valid['Extra']==64;ordered(one(own,'explicit-private-free-begin'),end,one(own,'explicit-private-free-end'),valid,case)
    markers=[r for r in rr if r['Event']=='checkpoint'];assert [(r['Phase'],r['Value'],r['Extra'],r['Third']) for r in markers]==[(0,0,60,0),(1,0,120,0),(2,0,180,0),(3,0,180,0)]
    for cp in markers:assert sum(r['Seq']<cp['Seq'] for r in attached)==cp['Extra'] and sum(r['Seq']<cp['Seq'] for r in rr if r['Event']=='private-owner-released')==cp['Extra']
    assert one(rr,'device9-fixture-cleanup')['Value']==1 and one(rr,'observer-close')['Value']==0
    return dict(PrivateDataGenerations=180,TextureLifetimes=150,PostWarmupLifetimes=100,EarlyPrivateFreeWhileResourceValid=30,WorkerFinalReleases=30,CompleteResourceDestructionClaim=False)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-D3DNotifiers.py','Verify-HookedD3D9.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    result=[];negative=[];raw=[]
    for name,config,api,check,file in [('P4','Debug',11,check11,'raw.stdout'),('P5','Release',11,check11,'raw.stdout'),('P6','Debug',9,check9,'resources.jsonl'),('P7','Release',9,check9,'resources.jsonl')]:
        root=s.ART/('FWM-D3D-LIFETIME-20260913-'+name);created=s.read(root/'created.json');ex=s.read(root/'exited.json');pid=ex['Pid'];assert created['Pid']==pid and created['Configuration']==config and ex['ExitCode']==0 and not ex['ForcedCleanup'];tool=Path(created['Tool']);assert created['ToolProvenanceSHA256']==s.sha(tool/'provenance.json')
        for r in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/r['Path'])=={k:r[k] for k in ['Bytes','SHA256']}
        rr=h.rows(root/file);result.append(dict(Run=root.name,Configuration=config,API=api,Pid=pid,**check(rr,pid)));s.process_state(dict(Pid=pid,EndedUtc=ex['EndedUtc']))
        if config=='Debug':
            for case in ['missing-case','retained-owner','duplicate-callback','missing-unmap','early-release','worker-identity','missing-boundary']:
                bad=copy.deepcopy(rr);end='destroyed' if api==11 else 'private-owner-released'
                if case=='missing-case':bad.remove(next(r for r in bad if r['Event']=='case-complete'))
                if case=='retained-owner':bad.remove(next(r for r in bad if r['Event']==end))
                if case=='duplicate-callback':bad.append(copy.deepcopy(next(r for r in bad if r['Event']==end)))
                if case=='missing-unmap':bad.remove(next(r for r in bad if r['Event']==('unmapped' if api==11 else 'control-unlock')))
                if case=='early-release':next(r for r in bad if r['Event']==end)['Qpc']=1
                if case=='worker-identity':next(r for r in bad if r['Event']=='worker-release-begin')['Tid']=bad[0]['Tid']
                if case=='missing-boundary':bad.remove(next(r for r in bad if r['Event']==('external-context-released' if api==11 else 'resource-still-valid-after-private-free')))
                for i,r in enumerate(bad,1):r['Seq']=i
                try:check(bad,pid)
                except (AssertionError,KeyError):negative.append(dict(API=api,Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
        raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file())
    s.write(dest/'raw-manifest.json',raw);s.write(dest/'verification.json',dict(Verdict='SCOPED_D3D_NATIVE_NOTIFIER_CONTRACTS_PASS',Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,PhysicalAllocationReleaseClaim=False))
    print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
