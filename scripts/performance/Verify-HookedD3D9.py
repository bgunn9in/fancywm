"""Native COM/private-data generations, independently matched to producer calls."""
import argparse,copy,importlib.util,json,shutil
from pathlib import Path
import d3d9_owner_model as model
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def rows(path):return [json.loads(r) for r in path.read_text(encoding='utf-8').splitlines()]
def one(rr,event):
    found=[r for r in rr if r['Event']==event];assert len(found)==1,(event,len(found));return found[0]
def journal(rr,pid):
    assert rr and [r['Seq'] for r in rr]==list(range(1,len(rr)+1)) and all(r['Pid']==pid and r['Tid']>0 and r['Qpc']>0 and r['Stack'] for r in rr)
    assert not any(r['Event'] in ['failure','identity-failed','private-guid-collision','observer-start-failure'] for r in rr)
    installed=one(rr,'hooks-installed');assert installed['Value']==0 and installed['Extra']>=18
    attached=[r for r in rr if r['Event']=='attach-begin'];assert [r['Id'] for r in attached]==list(range(1,len(attached)+1))
    result={}
    for r in attached:
        own=[x for x in rr if x['Id']==r['Id']];end=[x for x in own if x['Event']=='private-owner-released'];registration=one(own,'attach-result')
        assert r['Identity']>0 and r['Closed']==0 and registration['Value']==0 and r['Seq']<registration['Seq'] and len(end)<=1
        assert all(x['Identity']==r['Identity'] and x['Kind']==r['Kind'] and x['Parent']==r['Parent'] for x in own)
        if end:assert end[0]['Closed']==1 and registration['Seq']<end[0]['Seq']
        result[r['Id']]=dict(Attach=r,Registration=registration,End=end[0] if end else None,Rows=own)
    for checkpoint in [r for r in rr if r['Event']=='checkpoint']:
        prior=[g for g in result.values() if g['Registration']['Seq']<checkpoint['Seq']]
        active=[g for g in prior if not g['End'] or g['End']['Seq']>checkpoint['Seq']]
        assert checkpoint['Value']==len(active) and checkpoint['Extra']==len(prior) and checkpoint['Third']==0
    return result
def check(rr,producer,pid):
    generations=journal(rr,pid);assert len(generations)==1050
    assert all(r['Pid']==pid for r in producer) and not any(r['Event']=='failure' for r in producer)
    assert one(producer,'observer-start-result')['Value']==one(producer,'observer-stop-result')['Value']==one(producer,'process-complete')['Value']==0
    assert all(r['Value']==1 for r in producer if r['Event']=='device9-fixture-cleanup')
    completed=[r for r in producer if r['Event']=='native-case-complete'];assert [(r['Phase'],r['Value']) for r in completed]==[(e,c) for e in range(3) for c in range(50)]
    lower=one(producer,'observer-start-result')['Qpc'];assigned=set();maps=0;reuse=[]
    for case in completed:
        scope=[r for r in producer if lower<r['Qpc']<=case['Qpc']];created=[r for r in scope if r['Event']=='native-resource-created'];assert len(created)==7 and [r['Third'] for r in created]==list(range(7)) and all(r['Extra']==case['Value'] and r['Phase']==case['Phase'] for r in created)
        own=[]
        for witness in created:
            matches=[g for g in generations.values() if g['Attach']['Identity']==witness['Value'] and lower<g['Registration']['Qpc']<witness['Qpc']]
            assert len(matches)==1;g=matches[0];assert g['Attach']['Id'] not in assigned;assigned.add(g['Attach']['Id']);own.append(g)
        assert [g['Attach']['Kind'] for g in own]==['texture9','render-target9','depth9','plain-surface9','vertex-buffer9','index-buffer9','texture-surface9']
        assert own[6]['Attach']['Parent']==own[0]['Attach']['Id']
        close=one(scope,'native-close-begin');held=one(scope,'native-parent-held-by-surface')
        for g in own:assert g['End'] and close['Qpc']<g['End']['Qpc']<case['Qpc']
        assert held['Qpc']<own[0]['End']['Qpc'] and held['Qpc']<own[6]['End']['Qpc']
        if case['Value']%5==1:assert one(scope,'native-retainer-release')['Qpc']<own[0]['End']['Qpc']
        names=[('native-texture-map','TextureLockRect','TextureUnlockRect',0),('native-surface-map','SurfaceLockRect','SurfaceUnlockRect',3),('native-vertex-map','VertexLock','VertexUnlock',4),('native-index-map','IndexLock','IndexUnlock',5)]
        for i,(name,lock,unlock,index) in enumerate(names):
            witness=one(scope,name);g=own[index];locked=one(g['Rows'],lock);unlocked=one(g['Rows'],unlock);nextq=one(scope,names[i+1][0])['Qpc'] if i<3 else close['Qpc']
            assert locked['Value']==unlocked['Value']==0 and locked['Extra']==witness['Value']>0 and locked['Third']==witness['Third']>0
            assert g['Registration']['Qpc']<locked['Qpc']<witness['Qpc']<unlocked['Qpc']<nextq;maps+=1
        lower=case['Qpc']
    assert len(assigned)==1050 and maps==600
    checkpoints=[r for r in rr if r['Event']=='checkpoint'];assert [(r['Phase'],r['Value'],r['Extra']) for r in checkpoints]==[(0,0,0),(1,0,350),(10,0,350),(11,0,700),(20,0,700),(21,0,1050),(999,0,1050)]
    assert one(rr,'hooks-detached')['Value']==one(rr,'observer-close')['Value']==0 and not any(r['Event']=='close-deferred-for-live-private-owners' for r in rr)
    linear=model.replay(iter(rr),pid);assert len(linear['MapPairs'])==600 and len(linear['NestedMaps'])==150 and not linear['UnownedMapEvents']
    for nested in linear['NestedMaps']:
        assert nested['API']=='SurfaceLockRect' and nested['ParentAPI']=='TextureLockRect' and generations[nested['Id']]['Attach']['Parent']==nested['ParentOwner']
        parent=linear['MapCalls'][nested['ParentCall']];child=linear['MapCalls'][nested['Call']]
        assert nested['Pointer']==parent['Result']['Extra'] and parent['Begin']['Seq']<child['Begin']['Seq']<child['End']['Seq']<parent['End']['Seq']
    return dict(PrivateDataGenerations=1050,ClosedPrivateOwners=1050,NativeMapUnmapPairs=600,NestedSurfaceLocks=150,NativeMapCallBoundaries=len(linear['MapCalls']),ResourceLifetimeClaim=False,ReusedIdentityAddresses=1050-len({g['Attach']['Identity'] for g in generations.values()}))
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--runs',nargs=2,required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name);result=[];negative=[];raw=[]
    shutil.copyfile(Path(__file__).with_name('d3d9_owner_model.py'),dest/'d3d9_owner_model.py')
    for name,config in zip(a.runs,['Debug','Release']):
        root=s.ART/name;created=s.read(root/'created.json');ex=s.read(root/'exited.json');pid=ex['Pid'];assert pid==created['Pid'] and ex['ExitCode']==0 and not ex['ForcedCleanup'] and created['Configuration']==config
        tool=Path(created['Tool']);assert created['ToolProvenanceSHA256']==s.sha(tool/'provenance.json')
        for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        assert Path(created['Command'][-1]).resolve()==(tool/'binaries'/config/'Observer9.dll').resolve()
        rr=rows(root/'observer.jsonl');producer=rows(root/'fixture.jsonl');stats=check(rr,producer,pid);result.append(dict(Run=name,Configuration=config,Pid=pid,ObserverSHA256=s.sha(tool/'binaries'/config/'Observer9.dll'),ToolProvenanceSHA256=s.sha(tool/'provenance.json'),**stats))
        if config=='Debug':
            for case in ['missing-resource','retained-owner','missing-unmap','wrong-pointer','early-parent-release','wrong-pid','duplicate-callback','missing-call-boundary','wrong-nested-parent']:
                bad=copy.deepcopy(rr);calls=copy.deepcopy(producer)
                if case=='missing-resource':calls.remove(next(r for r in calls if r['Event']=='native-resource-created'))
                if case=='retained-owner':bad.remove(next(r for r in bad if r['Event']=='private-owner-released'))
                if case=='missing-unmap':bad.remove(next(r for r in bad if r['Event']=='SurfaceUnlockRect' and r['Id']>0))
                if case=='wrong-pointer':next(r for r in calls if r['Event']=='native-texture-map')['Value']+=16
                if case=='early-parent-release':next(r for r in bad if r['Event']=='private-owner-released' and r['Kind']=='texture9')['Qpc']=1
                if case=='wrong-pid':bad[0]['Pid']+=1
                if case=='duplicate-callback':bad.append(copy.deepcopy(next(r for r in bad if r['Event']=='private-owner-released')))
                if case=='missing-call-boundary':bad.remove(next(r for r in bad if r['Event'].startswith('map-call-exit-')))
                if case=='wrong-nested-parent':next(r for r in bad if r['Event'].startswith('map-call-enter-') and r['Extra'])['Extra']+=1
                # Preserve sequence validity so omissions exercise semantic gates.
                for i,r in enumerate(bad,1):r['Seq']=i
                try:check(bad,calls,pid)
                except (AssertionError,KeyError):negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
        raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file());s.process_state(dict(Pid=pid,EndedUtc=ex['EndedUtc']))
    s.write(dest/'raw-manifest.json',raw);s.write(dest/'verification.json',dict(Verdict='SCOPED_HOOKED_D3D9_PRIVATE_OWNERS_PASS',Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,NewTracingSession=False,CompleteResourceDestructionClaim=False,PhysicalAllocationReleaseClaim=False))
    print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
