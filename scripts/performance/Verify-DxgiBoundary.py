"""Reproduce missing graph checkpoints in the first DXGI-only fixture."""
import argparse,importlib.util,shutil
from pathlib import Path
spec=importlib.util.spec_from_file_location('v',Path(__file__).with_name('Verify-DxgiMemoryGraph.py'));v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v);s=v.s;g=v.g
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-DxgiBoundary.py','Verify-DxgiMemoryGraph.py','Verify-FullGraphLifetime.py','dxgi_memory_model.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    root=s.ART/'FWM-DXGI-MEMORY-20260913-F1';failed=s.ART/'FWM-DXGI-MEMORY-20260913-D1';assert not failed.exists()
    cmd=['python','scripts/performance/Verify-DxgiMemoryGraph.py','--graph',str(root),'--id',failed.name];run=s.run(cmd);s.write(dest/'strict-verifier-command.json',run)
    assert run['ExitCode']!=0 and 'graph generation checkpoint coverage' in run['Stderr'] and not (failed/'verification.json').exists()
    records=[]
    for config in ['Debug','Release']:
        folder=root/'runs'/(config+'-graceful');rr=g.rows(folder/'observations.jsonl');summary=s.read(folder/'summary.json');assert summary['Verdict']=='PASS'
        try:g.check(rr,'graceful',True,50,False,True)
        except ValueError as e:assert str(e)=='graph generation checkpoint coverage'
        else:raise AssertionError('first fixture failure disappeared')
        assert sum(r['Kind']=='GraphWindowAcquired' for r in rr)==sum(r['Kind']=='GraphWindowClosed' for r in rr)==4
        assert not any(r['Kind']=='GraphWindowCheckpoint' for r in rr)
        records.append(dict(Configuration=config,Pid=summary['Pid'],GraphAcquired=4,GraphClosed=4,ApplicationWindowCheckpoints=0,FullGraphLifetimeVerified=False,RawSHA256=s.sha(folder/'observations.jsonl')))
    original=root/'source/scripts/performance/fullgraph-lifetime/Program.GraphWindows.cs';assert original.read_text().count('if (NativeHeapSnapshot == null && PssBeginCall == null) return;')==1
    s.write(dest/'verification.json',dict(Verdict='DXGI_GRAPH_FIXTURE_CHECKPOINT_GAP_REPRODUCED',Configurations=records,OriginalFixtureSHA256=s.sha(original),FailedVerifierDirectory=failed.name,StrictVerifierChanged=False,NativeRuns=0,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
