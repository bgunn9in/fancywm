"""Native DXGI memory control receipts with owned D3D9 resources."""
import argparse,copy,importlib.util,json,shutil
from pathlib import Path
import dxgi_memory_model as model
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
PREFIX='FWM-DXGI-MEMORY-20260913-'
def rows(path):return [json.loads(x) for x in path.read_text(encoding='utf-8').splitlines()]
def check(rr,fixture,pid,drained):
    phases=[e*10000+c*10+step for e in range(3) for c in range(50) for step in range(4)]
    hardware,queries=model.check(rr,pid,phases,2)
    assert all(r['Pid']==pid for r in fixture) and not any(r['Event']=='failure' for r in fixture)
    luid=model.one(fixture,'adapter9')['Value'];budget=model.one(fixture,'budget-admission');assert budget['Third']==luid and budget['Value']>=256*1024*1024
    assert any(a['Luid']==luid for a in hardware)
    assert model.one(fixture,'device9-create')['Value']==0 and model.one(fixture,'device9-create')['Third']==1
    assert model.one(fixture,'device9-fixture-cleanup')['Value']==1 and model.one(fixture,'observer-unloaded')['Value']==1 and model.one(fixture,'process-complete')['Value']==0
    assert [(r['Value'],r['Extra']) for r in fixture if r['Event']=='epoch-complete']==[(e,50) for e in range(3)]
    assert len([r for r in fixture if r['Event']=='fence'])==150*(3 if drained else 2) and all(r['Value']==0 for r in fixture if r['Event']=='fence')
    result=[]
    for e in range(3):
        delta=[];partial=[];empty=[]
        for c in range(50):
            key=e*10000+c*10
            events=[r for r in fixture if r['Phase']==e and ((r['Event'] in ['sample-begin','sample-end'] and key<=r['Value']<key+4) or (r['Event'] in ['owned-surface','one-owner-held'] and r['Extra']==c) or (r['Event'] in ['all-owned-surfaces-released','cycle-complete','partial-release-fence'] and r['Value']==c))]
            expected=['sample-begin','sample-end']+['owned-surface']*4+['sample-begin','sample-end']+(['partial-release-fence'] if drained else [])+['one-owner-held','sample-begin','sample-end','all-owned-surfaces-released','sample-begin','sample-end','cycle-complete']
            assert [r['Event'] for r in events]==expected,'resource/sample order'
            created=[r for r in events if r['Event']=='owned-surface'];assert [r['Third'] for r in created]==list(range(4)) and len({r['Value'] for r in created})==4 and min(r['Value'] for r in created)>0
            assert model.one(events,'one-owner-held')['Value']==created[0]['Value']
            values=[]
            for step in range(4):
                begin=next(r for r in events if r['Event']=='sample-begin' and r['Value']==key+step);end=next(r for r in events if r['Event']=='sample-end' and r['Value']==key+step)
                selected=[r for r in queries if r['Phase']==key+step];assert all(begin['Qpc']<r['QpcBegin']<=r['QpcEnd']<end['Qpc'] for r in selected)
                local={r['CurrentUsage'] for r in selected if r['Luid']==luid and r['Segment']==0};assert len(local)==1,'control samples not stable';values.append(local.pop())
            assert values[1]-values[0]==16777216 and values[0]<=values[2]<=values[1] and values[3]==values[0],'native sensitivity/release accounting'
            assert values[2]-values[0]==(4194304 if drained else 16777216),'partial-release boundary'
            delta.append(values[1]-values[0]);partial.append(values[2]-values[0]);empty.append(values[3]-values[0])
        result.append(dict(Epoch=e,Cycles=50,FullLiveDeltaBytes=sorted(set(delta)),PartialLiveDeltaBytes=sorted(set(partial)),AfterReleaseDeltaBytes=sorted(set(empty))))
    return dict(Adapters=hardware,WorkloadLuid=luid,Queries=len(queries),ResourceLifetimes=600,Epochs=result,PartialReleaseFence=drained)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-DxgiMemoryControl.py','dxgi_memory_model.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    result=[];raw=[];negative=[];own=[]
    for i in range(1,5):
        name=PREFIX+f'P{i}';root=s.ART/name;created=s.read(root/'created.json');ex=s.read(root/'exited.json');pid=created['Pid'];config='Debug' if i%2 else 'Release'
        assert ex['Pid']==pid and ex['ExitCode']==0 and not ex['ForcedCleanup'] and created['Configuration']==config
        tool=Path(created['Tool']);assert s.sha(tool/'provenance.json')==created['ToolProvenanceSHA256']
        for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        dll=tool/'binaries'/config/'DxgiMemory.dll';assert Path(created['Command'][-1])==dll
        rr=rows(root/'dxgi.jsonl');fixture=rows(root/'fixture.jsonl');detail=check(rr,fixture,pid,i>=3)
        result.append(dict(Run=name,Configuration=config,Pid=pid,ToolProvenanceSHA256=s.sha(tool/'provenance.json'),ObserverSHA256=s.sha(dll),**detail));own.append(s.process_state(dict(Pid=pid,EndedUtc=ex['EndedUtc'],Role='native-control')))
        if i==3:
            for case in ['missing-sample','wrong-PID','wrong-LUID','wrong-segment','budget-as-usage','unchanged-usage','missing-owner-release','retained-resource','wrong-query-order']:
                bad=copy.deepcopy(rr);ff=copy.deepcopy(fixture);q=next(r for r in bad if r['Event']=='sample' and r['Phase']==1 and r['Segment']==0)
                if case=='missing-sample':bad.remove(q)
                if case=='wrong-PID':q['Pid']+=1
                if case=='wrong-LUID':q['Luid']+=1
                if case=='wrong-segment':q['Segment']=1
                if case=='budget-as-usage':q['CurrentUsage']=q['Budget']
                if case=='unchanged-usage':
                    for r in bad:
                        if r['Event']=='sample':r['CurrentUsage']=0
                if case=='missing-owner-release':ff.remove(next(r for r in ff if r['Event']=='all-owned-surfaces-released'))
                if case=='retained-resource':
                    for r in bad:
                        if r['Event']=='sample' and r['Phase']==3 and r['Segment']==0:r['CurrentUsage']+=4194304
                if case=='wrong-query-order':q['QpcEnd']=q['QpcBegin']-1
                try:check(bad,ff,pid,True)
                except (AssertionError,KeyError,StopIteration):negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
        raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file())
    assert result[2]['Adapters']==result[3]['Adapters']
    s.write(dest/'raw-manifest.json',raw);s.write(dest/'owned-processes.json',own);s.write(dest/'verification.json',dict(Verdict='SCOPED_NATIVE_DXGI_MEMORY_SOURCE_PASS',Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),PhysicalAllocationIdentityClaim=False,ImmediateReleaseAccountingClaim=False,CompleteGraphicsMemoryClaim=False,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
