"""Preserve strict all-VA failure and independently validate shared transitions."""
import argparse,copy,importlib.util,json,shutil
from pathlib import Path
import pss_shared_model as shared
spec=importlib.util.spec_from_file_location('g',Path(__file__).with_name('Verify-PssGraph.py'));g=importlib.util.module_from_spec(spec);spec.loader.exec_module(g);s=g.s
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-PssSharedBoundary.py','pss_shared_model.py','Verify-PssGraph.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    root=s.ART/'FWM-D3D-MAP-20260913-F1';folder=root/'runs/Release-graceful';rr=g.g.rows(folder/'observations.jsonl');pid=g.g.single(rr,'Process')['Pid'];ff={label:{f.name:f.read_bytes() for f in (folder/'pss'/label).iterdir() if f.is_file()} for label in g.LABELS}
    assert not (s.ART/'FWM-D3D-MAP-20260913-D1/verification.json').exists() and not (s.ART/'FWM-D3D-MAP-20260913-D2/verification.json').exists()
    assert s.sha(Path(__file__).with_name('Verify-PssGraph.py'))==s.sha(s.ART/'FWM-D3D-MAP-20260913-D1/Verify-PssGraph.py')
    try:g.observations(rr,ff,pid)
    except AssertionError:strict=False
    else:raise AssertionError('strict full VA failure disappeared')
    clone=json.loads(ff['0']['ready.json'])['ClonePid'];a0=g.p.regions(ff['0']['clone-va-0.bin'],pid,clone,0);b0=g.p.regions(ff['0']['clone-va-1.bin'],pid,clone,0);changes=shared.transitions(a0,b0);assert len(changes)==3 and sum(r['Bytes'] for r in changes)==57344
    # Independent page oracle only expands rows that differ, never enormous VA
    # reservations or a guessed heap address space.
    old=set(a0)-set(b0);new=set(b0)-set(a0);pages=set()
    for r in old|new:
        assert r[2]<=16*1024*1024;pages.update(range(r[0],r[0]+r[2],4096))
    changed=set()
    for page in pages:
        x=next(r for r in a0 if r[0]<=page<r[0]+r[2]);y=next(r for r in b0 if r[0]<=page<r[0]+r[2])
        if (x[1],*x[3:])!=(y[1],*y[3:]):changed.add(page)
    emitted={page for r in changes for page in range(r['Base'],r['Base']+r['Bytes'],4096)};assert changed==emitted and len(changed)==14
    negative=[]
    for case in ['private-region-change','shared-decommit','wrong-section','wrong-type','wrong-protection']:
        first=list(a0);second=list(b0)
        if case=='shared-decommit':first,second=second,first
        else:
            address=changes[0]['Base'];index=next(i for i,r in enumerate(second) if r[0]<=address<r[0]+r[2])
            if case=='private-region-change':index=next(i for i,r in enumerate(second) if r[6]==0x20000 and r[4]==0x1000)
            row=list(second[index])
            if case=='private-region-change':row[5]^=16
            if case=='wrong-section':row[1]+=65536
            if case=='wrong-type':row[6]=0x20000
            if case=='wrong-protection':row[5]=1
            second[index]=tuple(row)
        try:shared.transitions(first,second)
        except AssertionError:negative.append(dict(Case=case,Rejected=True))
        else:raise AssertionError('negative accepted '+case)
    s.write(dest/'verification.json',dict(Verdict='PSS_SHARED_GRAPH_BOUNDARY_VERIFIED',Graph=str(root),FailedStrictVerifierSHA256=s.sha(Path(__file__).with_name('Verify-PssGraph.py')),WholeCloneVaStable=strict,SharedChanges=changes,IndependentChangedPages=len(changed),NegativeControls=negative,NativeRuns=0,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
