"""Known own VirtualAlloc buffers calibrate process private commit accounting."""
import argparse,copy,importlib.util,json,shutil
from pathlib import Path
import process_memory_model as model
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def rows(path):return [json.loads(x) for x in path.read_text().splitlines()]
def check(rr,ff,pid):
    expected=[(-100,i) for i in range(16)]+[(e*10000+c*10+step,i) for e in range(3) for c in range(50) for step in range(4) for i in range(2)]
    qq=model.check(rr,pid,expected);assert all(r['Pid']==pid for r in ff) and not any(r['Event']=='failure' for r in ff)
    assert ff[-2]['Event']=='process-complete' and ff[-2]['Value']==0 and ff[-1]['Event']=='observer-close' and ff[-1]['Value']==0
    assert [r['Value'] for r in ff if r['Event']=='observer-unloaded']==[1]
    result=[]
    for e in range(3):
        deltas=[];parts=[];empty=[]
        for c in range(50):
            begin=next(r for r in ff if r['Event']=='cycle-begin' and r['Phase']==e and r['Value']==c);end=next(r for r in ff if r['Event']=='cycle-complete' and r['Phase']==e and r['Value']==c);events=ff[ff.index(begin)+1:ff.index(end)];key=e*10000+c*10
            names=['sample-begin','sample-end']+['block-created']*4+['sample-begin','sample-end']+['block-free']*3+['one-block-held','sample-begin','sample-end','block-free','all-blocks-released','sample-begin','sample-end']
            assert [r['Event'] for r in events]==names,'allocation/release/sample order'
            created=[r for r in events if r['Event']=='block-created'];freed=[r for r in events if r['Event']=='block-free'];assert len({r['Value'] for r in created})==4 and min(r['Value'] for r in created)>0
            assert [r['Extra'] for r in created]==[8*1024*1024]*4 and [r['Third'] for r in created]==list(range(4)) and all(r['Extra']==1 for r in freed)
            assert [r['Value'] for r in freed]==[r['Value'] for r in created[1:]+created[:1]]
            held=next(r for r in events if r['Event']=='one-block-held');assert held['Value']==created[0]['Value'] and held['Extra']==c
            values=[]
            for step in range(4):
                left=next(r for r in events if r['Event']=='sample-begin' and r['Value']==key+step);right=next(r for r in events if r['Event']=='sample-end' and r['Value']==key+step);selected=[r for r in qq if r['Phase']==key+step]
                assert len(selected)==2 and all(left['Qpc']<r['QpcBegin']<=r['QpcEnd']<right['Qpc'] for r in selected)
                stable={r['PrivateUsage'] for r in selected};assert len(stable)==1;values.append(stable.pop())
            full=values[1]-values[0];partial=values[2]-values[0];assert full==4*partial and partial>=8*1024*1024 and values[3]==values[0]
            deltas.append(full);parts.append(partial);empty.append(values[3]-values[0])
        result.append(dict(Epoch=e,Cycles=50,FullPrivateCommitDeltaBytes=sorted(set(deltas)),OneBlockDeltaBytes=sorted(set(parts)),AfterReleaseDeltaBytes=sorted(set(empty))))
    assert [(r['Value'],r['Extra']) for r in ff if r['Event']=='epoch-complete']==[(e,50) for e in range(3)]
    return dict(Queries=len(qq),AllocationLifetimes=600,RequestedBytesPerBlock=8388608,Epochs=result)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-ProcessMemoryControl.py','process_memory_model.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    result=[];raw=[];negative=[]
    for number,config in [(5,'Debug'),(6,'Release')]:
        root=s.ART/f'FWM-DXGI-MEMORY-20260913-P{number}';created=s.read(root/'created.json');ex=s.read(root/'exited.json');pid=created['Pid'];tool=Path(created['Tool'])
        assert ex['Pid']==pid and ex['ExitCode']==0 and not ex['ForcedCleanup'] and created['Configuration']==config and s.sha(tool/'provenance.json')==created['ToolProvenanceSHA256']
        for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        dll=tool/'binaries'/config/'Memory.dll';assert Path(created['Command'][-1])==dll
        rr=rows(root/'memory.jsonl');ff=rows(root/'fixture.jsonl');details=check(rr,ff,pid)
        result.append(dict(Configuration=config,Pid=pid,Run=root.name,ToolProvenanceSHA256=s.sha(tool/'provenance.json'),ObserverSHA256=s.sha(dll),**details))
        if config=='Debug':
            for case in ['missing-query','foreign-PID','wrong-ABI-size','peak-as-current','missing-free','retained-block','reordered-query']:
                bad=copy.deepcopy(rr);fixture=copy.deepcopy(ff);q=next(r for r in bad if r['Event']=='sample' and r['Phase']==1)
                if case=='missing-query':bad.remove(q)
                if case=='foreign-PID':q['Pid']+=1
                if case=='wrong-ABI-size':q['StructureBytes']=72
                if case=='peak-as-current':
                    for r in bad:
                        if r['Event']=='sample':r['PrivateUsage']=r['PagefileUsage']=r['PeakPagefileUsage']
                if case=='missing-free':fixture.remove(next(r for r in fixture if r['Event']=='block-free'))
                if case=='retained-block':
                    for r in bad:
                        if r['Event']=='sample' and r['Phase']==3:r['PrivateUsage']+=8404992;r['PagefileUsage']=r['PrivateUsage']
                if case=='reordered-query':q['QpcBegin']=q['QpcEnd']+1
                try:check(bad,fixture,pid)
                except (AssertionError,KeyError,StopIteration):negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
        raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file())
    s.write(dest/'raw-manifest.json',raw);s.write(dest/'verification.json',dict(Verdict='SCOPED_NATIVE_PROCESS_COMMIT_SOURCE_PASS',Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),LogicalAllocationSizeEqualsCommitCharge=False,CompleteGraphicsMemoryClaim=False,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
