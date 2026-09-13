"""Cross-check private VA occupancy with independent page and interval methods."""
import argparse,copy,importlib.util,shutil
from pathlib import Path
spec=importlib.util.spec_from_file_location('p',Path(__file__).with_name('Verify-PssHeapControl.py'));p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p);s=p.s
def intervals(raw,pid,clone,error=0):
    return [(r[0],r[0]+r[2]) for r in p.regions(raw,pid,clone,error) if r[4]==0x1000 and r[6]==0x20000]
def overlap(a,b):
    i=j=total=0
    while i<len(a) and j<len(b):
        total+=max(0,min(a[i][1],b[j][1])-max(a[i][0],b[j][0]))
        if a[i][1]<=b[j][1]:i+=1
        else:j+=1
    return total
def pages(rows):
    result=set()
    for start,end in rows:
        assert start%4096==end%4096==0;new=set(range(start//4096,end//4096));assert not result&new;result.update(new)
    return result
def compare(a,b):
    left=pages(a);right=pages(b);common=overlap(a,b)
    assert common==len(left&right)*4096
    old=sum(end-start for start,end in a);new=sum(end-start for start,end in b)
    assert old==len(left)*4096 and new==len(right)*4096
    assert new-common==len(right-left)*4096 and old-common==len(left-right)*4096
    return dict(BaselineBytes=old,ObservedBytes=new,SharedAddressBytes=common,NewlyCommittedAddressBytes=new-common,AbsentBaselineAddressBytes=old-common,DeltaBytes=new-old,AllocationGenerationIdentity=False,LogicalOwnerIdentity=False)
def main():
    a=argparse.ArgumentParser();a.add_argument('--id',required=True);a.add_argument('--graph-proof',type=Path,required=True);args=a.parse_args();dest=s.ART/args.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in args.id;dest.mkdir()
    for file in [Path(__file__),Path(__file__).with_name('Verify-PssHeapControl.py')]:shutil.copyfile(file,dest/file.name)
    proof=s.read(args.graph_proof);assert proof['Verdict']=='SCOPED_PSS_GRAPH_VA_OBSERVATIONS_VERIFIED';root=Path(proof['Snapshot']);manifest=args.graph_proof.parent/'raw-manifest.json';assert s.sha(manifest)==proof['RawManifestSHA256']
    for row in s.read(manifest):assert s.info(root/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    output=[]
    for config in proof['Configurations']:
        folder=root/'runs'/(config['Configuration']+'-graceful')/'pss';maps={};source={}
        for e in config['NativeSnapshots']:
            label=e['Label'];maps[label]=intervals((folder/label/'clone-va-0.bin').read_bytes(),config['Pid'],e['ClonePid']);source[label]=intervals((folder/label/'snapshot-va.bin').read_bytes(),config['Pid'],e['ClonePid'],259)
            assert sum(y-x for x,y in maps[label])==e['PrivateCommittedBytes']
        output.append(dict(Configuration=config['Configuration'],Epochs=[dict(Label=l,**compare(maps['0'],maps[l])) for l in ['0','1','2','shutdown']],SourceMapVersusClone=[dict(Label=l,**compare(source[l],maps[l])) for l in maps],TwoIndependentOccupancyMethodsAgree=True))
    s.write(dest/'verification.json',dict(Verdict='SCOPED_PSS_PRIVATE_VA_OCCUPANCY_VERIFIED',GraphProofSHA256=s.sha(args.graph_proof),Configurations=output,NewNativeProcesses=0,NewTracingSession=False,HeapBlockEnumeration=False,NativeHeapLeakFreedomClaim=False,LogicalOwnerClaim=False,PerformanceClaim=False,StageAccepted=False,ProductionChanged=False,LedgerAppended=False))
    print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
