"""Private commit in the calibrated DXGI full graph, without heap/PSS observers."""
import argparse,copy,importlib.util,json,shutil,sys
from pathlib import Path
import process_memory_model as model
def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
s=module('s','Finalize-NativeHeapEvidence.py');g=module('g','Verify-FullGraphLifetime.py')
PHASES=[-1,0,1,2,99]
def check(native,managed,pid):
    queries=model.check(native,pid,[(p,i) for p in PHASES for i in range(5)])
    start=g.single(managed,'ProcessMemoryStarted');dxgi=g.single(managed,'DxgiMemoryStarted');assert start['Code']==0 and start['Pid']==pid and start['Module']>0 and start['Module']!=dxgi['Module']
    assert start['QpcBegin']<native[0]['Qpc']<start['QpcEnd']<dxgi['QpcBegin']
    calls=[r for r in managed if r['Kind']=='ProcessMemorySample'];assert [(r['Phase'],r['Sample']) for r in calls]==[(p,i) for p in PHASES for i in range(5)]
    for r,q in zip(calls,queries):
        assert r['Pid']==pid and r['Code']==0 and r['DispatcherAlive']==(r['Phase']!=99) and not r['AtomicWithDxgi']
        assert r['QpcBegin']<q['QpcBegin']<=q['QpcEnd']<r['QpcEnd']
        dx=next(x for x in managed if x['Kind']=='DxgiMemorySample' and (x['Phase'],x['Sample'])==(r['Phase'],r['Sample']))
        assert dx['QpcEnd']<r['QpcBegin'] and managed.index(dx)+1==managed.index(r)
    values=[]
    for phase in PHASES:
        qq=[r for r in queries if r['Phase']==phase];pp=[r['PrivateUsage'] for r in qq]
        values.append(dict(Phase=phase,PrivateUsageBytes=pp,WorkingSetBytes=[r['WorkingSetSize'] for r in qq],PeakCommitBytes=[r['PeakPagefileUsage'] for r in qq],Min=min(pp),Max=max(pp),Stable=len(set(pp))==1))
    gate=all(r['Stable'] for r in values[1:4]) and all(r['Max']<=values[1]['Max'] for r in values[2:4])
    return dict(Samples=values,ObservedPrivateCommitNoGrowth=gate,ShutdownAtOrBelowWarmup=values[-1]['Max']<=values[1]['Max'])
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--graph',required=True,type=Path);p.add_argument('--graph-proof',required=True,type=Path);a=p.parse_args();root=a.graph.resolve();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-ProcessMemoryGraph.py','process_memory_model.py','Verify-FullGraphLifetime.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    s.write(dest/'invocation.json',dict(Command=[sys.executable,*sys.argv],RecordedUtc=s.datetime.now(s.timezone.utc).isoformat()))
    graph=s.read(a.graph_proof);assert graph['Verdict']=='SCOPED_GRAPH_DXGI_USAGE_OBSERVATIONS_VERIFIED' and Path(graph['Graph'])==root
    assert all(r['Rejected'] for r in graph['NegativeControls']) and len(graph['NegativeControls'])==12
    sources=g.provenance(root,True);proof=s.read(root/'memory-provenance.json');tool=Path(proof['Tool']);cal=s.ART/'FWM-DXGI-MEMORY-20260913-V2/verification.json';calibration=s.read(cal)
    assert s.sha(cal)==proof['CalibrationSHA256']=='973D2574CAE4871BE0341B80EBB2589E1B409E4FA952B9E35933321CBFBAEEAA'
    assert proof['ToolProvenanceSHA256']==s.sha(tool/'provenance.json') and proof['CurrentProcessOnly'] and proof['NoTracingSession'] and not proof['HookInterception'] and not proof['AtomicWithDxgi']
    for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    for row in s.read(cal.parent/'raw-manifest.json'):assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    for row in s.read(a.graph_proof.parent/'raw-manifest.json'):assert s.info(root/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    assert s.sha(tool/'source/Memory.cpp')==s.sha(root/'source/scripts/performance/process-memory/Memory.cpp')
    assert not any((root/name).exists() for name in ['pss-provenance.json','d3d9-provenance.json','heap-observer-provenance.json','heap-trace-provenance.json','clr-source-provenance.json'])
    result=[];negative=[]
    for run in s.read(root/'runs.json')['Processes']:
        config=run['Configuration'];pid=run['ProcessId'];folder=root/'runs'/(config+'-graceful');rr=g.rows(folder/'observations.jsonl');native=g.rows(folder/'memory.jsonl');g.check(rr,'graceful',True,50,False,True)
        observer=s.read(root/f'{config}-memory-binary.json');control=next(c for c in calibration['Configurations'] if c['Configuration']==config)
        assert s.sha(root/observer['Path'])==observer['SHA256']==control['ObserverSHA256']==s.sha(tool/'binaries'/config/'Memory.dll')
        details=check(native,rr,pid);stop=s.read(folder/'memory-stopped.json');assert stop['Pid']==pid and stop['Code']==0 and stop['ModuleUnloaded'] and stop['QpcBegin']<native[-1]['Qpc']<stop['QpcEnd']
        result.append(dict(Configuration=config,Pid=pid,Queries=25,ObserverModuleUnloaded=True,**details))
        if config=='Debug':
            for case in ['missing-sample','wrong-PID','wrong-ABI-size','stopped-dispatcher','wrong-query-order','atomic-total-claim','private-exceeds-peak']:
                nn=copy.deepcopy(native);bad=copy.deepcopy(rr);q=next(r for r in nn if r['Event']=='sample' and r['Phase']==1)
                if case=='missing-sample':nn.remove(q)
                if case=='wrong-PID':q['Pid']+=1
                if case=='wrong-ABI-size':q['StructureBytes']=72
                if case=='stopped-dispatcher':next(r for r in bad if r['Kind']=='ProcessMemorySample' and r['Phase']==1)['DispatcherAlive']=False
                if case=='wrong-query-order':q['QpcBegin']=q['QpcEnd']+1
                if case=='atomic-total-claim':next(r for r in bad if r['Kind']=='ProcessMemorySample')['AtomicWithDxgi']=True
                if case=='private-exceeds-peak':q['PrivateUsage']=q['PeakPagefileUsage']+4096
                try:check(nn,bad,pid)
                except (AssertionError,KeyError,StopIteration):negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
            for case in ['injected-private-commit-growth','unstable-private-samples']:
                nn=copy.deepcopy(native);baseline=max(r['PrivateUsage'] for r in nn if r['Event']=='sample' and r['Phase']==0)
                peak=max(r['PeakPagefileUsage'] for r in nn if r['Event']=='sample')
                for r in nn:
                    if r['Event']=='sample':
                        if r['Phase']==1:r['PrivateUsage']=r['PagefileUsage']=baseline+(4096 if case=='injected-private-commit-growth' or r['Sample']==0 else 0)
                        r['PeakPagefileUsage']=max(peak,baseline+4096)
                assert not check(nn,rr,pid)['ObservedPrivateCommitNoGrowth'];negative.append(dict(Case=case,Rejected=True,QueryCoverageStillValid=True,NoGrowthClaimRejected=True))
        print(config,'private commit no growth',details['ObservedPrivateCommitNoGrowth'],flush=True)
    raw=[dict(Path=str(f.relative_to(root)),**s.info(f)) for f in sorted((root/'runs').rglob('*')) if f.is_file()];s.write(dest/'raw-manifest.json',raw)
    s.write(dest/'verification.json',dict(Verdict='SCOPED_GRAPH_PRIVATE_COMMIT_OBSERVATIONS_VERIFIED',Graph=str(root),GraphProofSHA256=s.sha(a.graph_proof),SourceFilesVerified=sources,CalibrationSHA256=s.sha(cal),Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,AtomicCpuGpuTotal=False,NativeHeapLeakFreedomClaim=False,LogicalOwnerClaim=False,CompleteGraphicsMemoryClaim=False,NewTracingSession=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
