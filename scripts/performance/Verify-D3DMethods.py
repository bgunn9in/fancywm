"""Independent software/hardware VP producer versus intercepted native methods."""
import argparse,copy,importlib.util,shutil
from pathlib import Path
import d3d9_owner_model as model
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def rows(p):return [s.json.loads(x) for x in p.read_text(encoding='utf-8').splitlines()]
def one(rr,event):
    found=[r for r in rr if r['Event']==event];assert len(found)==1,(event,len(found));return found[0]
def check(native,observer,pid,incomplete=False):
    assert [r['Seq'] for r in native]==list(range(1,len(native)+1)) and all(r['Pid']==pid for r in native) and not any(r['Event']=='failure' for r in native)
    assert one(native,'process-complete')['Value']==one(native,'observer-start-result')['Value']==one(native,'observer-stop-result')['Value']==0 and one(native,'hardware-device-created')['Value']==0
    assert one(native,'device9-fixture-cleanup')['Value']==1 and one(native,'observer-close')['Value']==0
    replay=model.replay(iter(observer),pid);generations=replay['Generations'];assert len(generations)==900 and not replay['FinalActiveIds'] and not replay['ApiFailures'] and not replay['UnownedMapEvents']
    producer=[r for r in native if r['Event']=='producer-resource'];assert [(r['Phase']//100,r['Phase']%100,r['Third']&0xffffffff,r['Extra']) for r in producer]==[(d,e,c,k) for d in range(2) for e in range(3) for c in range(50) for k in [1,2,3]]
    assert [(r['Phase'],r['Value'],r['Extra'],r['Third']) for r in native if r['Event']=='producer-cycle-complete']==[(d*100+e,c,d,e) for d in range(2) for e in range(3) for c in range(50)]
    coverage=[];matched=set();lower=one(native,'observer-start-result')['Qpc'];total=0;missing=0;method_sets={0:set(),1:set()}
    for witness in producer:
        found=[g for g in generations.values() if g['Attach']['Identity']==witness['Value'] and lower<g['Registration']['Qpc']<witness['Qpc']];assert len(found)==1;g=found[0];ident=g['Attach']['Id'];assert ident not in matched;matched.add(ident)
        end=g['End'];assert end;close=next(r for r in native if r['Event']=='producer-release-end' and r['Value']==witness['Value'] and r['Qpc']>witness['Qpc']);scope=[r for r in native if witness['Qpc']<=r['Qpc']<=close['Qpc']]
        assert one(scope,'producer-release-begin')['Qpc']<end['Qpc']<close['Qpc'];method=one(scope,'producer-methods');assert method['Value']==witness['Value'];d=witness['Phase']//100;kind=witness['Extra'];method_sets[d].add((method['Extra'],method['Third']))
        assert g['Attach']['Kind']==('index-buffer9' if kind==2 else 'vertex-buffer9')
        calls=[r for r in scope if r['Event']=='producer-map'];unmaps=[r for r in scope if r['Event']=='producer-unmap'];assert [r['Third'] for r in calls]==[r['Extra'] for r in unmaps]==[0,1,2]
        own=[r for r in replay['MapPairs'] if r['Id']==ident];expected=0 if incomplete and d==1 else 3;assert len(own)==expected,('native method coverage',d,kind,len(own),expected)
        for i,(a,b) in enumerate(zip(calls,unmaps)):
            assert a['Value']==b['Value']==witness['Value'] and a['Extra']>0 and a['Qpc']<b['Qpc'];total+=1
            if own:assert own[i]['API']==('IndexLock' if kind==2 else 'VertexLock') and own[i]['Pointer']==a['Extra'] and own[i]['ExtentOrPitch']==128 and own[i]['Begin']<a['Qpc']<own[i]['End']<b['Qpc']
            else:missing+=1
        lower=close['Qpc']
    assert method_sets[0].isdisjoint(method_sets[1]) and len(method_sets[0])==len(method_sets[1])==2 and total==2700 and missing==(1350 if incomplete else 0)
    marks=replay['Checkpoints'];expected=[]
    for d in range(2):
        for e in range(3):expected.extend([(d*100+e*10,0,d*450+e*150),(d*100+e*10+1,0,d*450+(e+1)*150)])
    expected.append((999,0,900));assert [(r['Phase'],r['Active'],r['Total']) for r in marks]==expected
    return dict(PrivateOwnerGenerations=900,ProducerMapUnmapPairs=total,ObservedMapUnmapPairs=total-missing,MissingHardwareMapPairs=missing,MethodSets={str(k):sorted(v) for k,v in method_sets.items()},CompleteMappingCoverage=not incomplete)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--runs',nargs=2,required=True);p.add_argument('--observer-tool',type=Path,required=True);p.add_argument('--expect-incomplete',action='store_true');a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-D3DMethods.py','d3d9_owner_model.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    observer=a.observer_tool.resolve();manifest=s.read(observer/'provenance.json')
    for row in manifest['Sources']+manifest['Binaries']:assert s.info(observer/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    result=[];negative=[];raw=[];tools=[]
    for name,config in zip(a.runs,['Debug','Release']):
        root=s.ART/name;created=s.read(root/'created.json');ex=s.read(root/'exited.json');pid=created['Pid'];tool=Path(created['Tool']);tools.append(str(tool));assert pid==ex['Pid'] and ex['ExitCode']==0 and not ex['ForcedCleanup'] and created['Configuration']==config and created['ToolProvenanceSHA256']==s.sha(tool/'provenance.json')
        for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        assert Path(created['Command'][-1]).resolve()==observer/'binaries'/config/'Observer9.dll';native=rows(root/'fixture.jsonl');rr=rows(root/'observer.jsonl');stats=check(native,rr,pid,a.expect_incomplete);result.append(dict(Run=name,Configuration=config,Pid=pid,ObserverSHA256=s.sha(observer/'binaries'/config/'Observer9.dll'),**stats))
        if config=='Debug':
            if a.expect_incomplete:
                try:check(native,rr,pid,False)
                except AssertionError as e:assert 'native method coverage' in str(e);negative.append(dict(Case='complete-coverage-on-old-observer',Rejected=True,Reason=str(e)))
                else:raise AssertionError('old observer accepted')
            else:
                for case in ['missing-resource','wrong-pointer','missing-unmap','retained-owner','wrong-hardware-method','missing-call-boundary']:
                    bad=copy.deepcopy(rr);producer=copy.deepcopy(native)
                    if case=='missing-resource':producer.remove(next(r for r in producer if r['Event']=='producer-resource'))
                    if case=='wrong-pointer':next(r for r in producer if r['Event']=='producer-map')['Extra']+=8
                    if case=='missing-unmap':bad.remove(next(r for r in bad if r['Event']=='VertexUnlock' and r['Id']))
                    if case=='retained-owner':bad.remove(next(r for r in bad if r['Event']=='private-owner-released'))
                    if case=='wrong-hardware-method':
                        old=next(r for r in producer if r['Event']=='producer-methods' and r['Phase']==0);new=next(r for r in producer if r['Event']=='producer-methods' and r['Phase']==100);new['Extra']=old['Extra'];new['Third']=old['Third']
                    if case=='missing-call-boundary':bad.remove(next(r for r in bad if r['Event'].startswith('map-call-exit-')))
                    for i,r in enumerate(bad,1):r['Seq']=i
                    for i,r in enumerate(producer,1):r['Seq']=i
                    try:check(producer,bad,pid)
                    except (AssertionError,KeyError):negative.append(dict(Case=case,Rejected=True))
                    else:raise AssertionError('negative accepted '+case)
        s.process_state(dict(Pid=pid,EndedUtc=ex['EndedUtc']));raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file())
    assert len(set(tools))==1;s.write(dest/'raw-manifest.json',raw);s.write(dest/'verification.json',dict(Verdict='D3D9_METHOD_COVERAGE_GAP_REPRODUCED' if a.expect_incomplete else 'SCOPED_D3D9_SOFTWARE_HARDWARE_METHOD_COVERAGE_PASS',ProducerTool=tools[0],ObserverTool=str(observer),ObserverProvenanceSHA256=s.sha(observer/'provenance.json'),Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,CompleteResourceLifetimeClaim=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
