"""Independent normal private/D3D mapped-buffer controls and graph omissions."""
import argparse,copy,importlib.util,json,shutil,struct
from collections import Counter
from pathlib import Path
spec=importlib.util.spec_from_file_location('p',Path(__file__).with_name('Verify-PssHeapControl.py'));p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p);s=p.s
def check(files,rows,pid,epoch,d3d):
    j=lambda name:json.loads(files[name]);ready=j('ready.json');end=j('released.json');clone=ready['ClonePid'];assert ready['Pid']==end['Pid']==pid and end['ClonePid']==clone and pid!=clone
    assert ready['WalkRows']==ready['RegionCount'] and ready['WalkCode']==259 and ready['MarkerFreeCode']==0
    assert end['FreeCode']==0 and end['RetainedHandleClosed'] and end['AfterCloseOpenError']==87 and end['HeldOpenError']==0 and end['CloneWait']==258
    src=p.regions(files['snapshot-va.bin'],pid,clone,259);dst=p.regions(files['clone-va-0.bin'],pid,clone,0);assert dst==p.regions(files['clone-va-1.bin'],pid,clone,0)
    blocks=list(struct.iter_unpack('<QIIII',files['blocks.bin']));expected=3 if d3d else 12;assert len(blocks)==len(rows)==expected and len({b[0] for b in blocks})==expected
    for i,(address,size,protection,index,pattern) in enumerate(blocks):
        assert index==i and size==[4096,65536,1048576][i%3] and pattern==(0x50 if d3d else 0x30)+i
        assert protection==(0x404 if d3d else [4,2,0x404,0x204][i//3]);row=rows[i]
        assert row['Block'] and row['Pid']==pid and row['ClonePid']==clone and row['Epoch']==epoch and row['Index']==i
        assert row['LiveQueryBytes']==48 and row['LiveState']==4096 and row['LiveType']==131072 and row['LiveProtect']==protection
        assert files[f'live-{i}.bin']==bytes([pattern])*size
        sr=[r for r in src if r[0]<=address and address+size<=r[0]+r[2]];dr=[r for r in dst if r[0]<=address and address+size<=r[0]+r[2]]
        assert len(sr)==len(dr)==1 and sr[0][4:]==(4096,protection,131072)
        if d3d:assert dr[0][4]==65536 and dr[0][6]==0 and row['ReadCode']==299 and f'clone-{i}.bin' not in files
        else:assert dr[0][4:]==(4096,protection,131072) and row['ReadCode']==0 and files[f'clone-{i}.bin']==bytes([pattern])*size
    return dict(Pid=pid,ClonePid=clone,Epoch=epoch,Allocations=expected,D3DMappedBuffers=d3d,Cloned=0 if d3d else expected,AbsentFromClone=expected if d3d else 0)
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--id',required=True);args=parser.parse_args();dest=s.ART/args.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in args.id;dest.mkdir()
    for file in [Path(__file__),Path(__file__).with_name('Verify-PssHeapControl.py')]:shutil.copyfile(file,dest/file.name)
    result=[];negative=[];raw=[];processes=[]
    for name,config,d3d in [('P5','Debug',False),('P6','Release',False),('P7','Debug',True),('P8','Release',True)]:
        root=s.ART/('FWM-PSS-HEAP-20260913-'+name);created=s.read(root/'created.json');ex=s.read(root/'exited.json');pid=ex['Pid'];assert pid==created['Pid'] and ex['ExitCode']==0 and not ex['ForcedCleanup'] and created['Configuration']==config
        observer=s.ART/'FWM-PSS-HEAP-20260913-T5/binaries'/config/'FancyWM.NativePss.dll';assert s.sha(observer)==created['ObserverSHA256']
        tool=s.ART/('FWM-PSS-HEAP-20260913-T7' if d3d else 'FWM-PSS-HEAP-20260913-T6');assert s.sha(tool/'provenance.json')==created['ToolProvenanceSHA256']
        for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.sha(tool/row['Path'])==row['SHA256']
        rows=[json.loads(r) for r in (root/'raw.stdout').read_text().splitlines()];assert not (root/'raw.stderr').read_bytes()
        if d3d:
            assert len(rows)==10 and rows[0]['Device'] and rows[0]['Pid']==pid and rows[0]['HResult']==0 and rows[0]['HardwareRequested'] and not rows[0]['SwapchainCreated']
            assert rows[-1]['D3DCleanup'] and rows[-1]['Pid']==pid and rows[-1]['ContextReleaseCount']==rows[-1]['DeviceReleaseCount']==rows[-1]['Result']==0
        else:assert len(rows)==26
        complete=[r for r in rows if r.get('EpochComplete')];assert len(complete)==2
        for epoch in range(2):
            r=complete[epoch];assert r['Pid']==pid and r['Epoch']==epoch and r['CaptureCode']==r['ReleaseCode']==r['Result']==0
            folder=root/'control'/str(epoch);files={f.name:f.read_bytes() for f in folder.iterdir() if f.is_file()};blocks=[r for r in rows if r.get('Block') and r['Epoch']==epoch];out=check(files,blocks,pid,epoch,d3d);assert out['ClonePid']==r['ClonePid'];result.append(dict(Configuration=config,**out))
            processes.append(s.process_state(dict(Pid=out['ClonePid'],EndedUtc=ex['EndedUtc'],Role='clone')))
            if name=='P7' and epoch==0:
                for case in ['missing buffer','false successful clone read','wrong source protection','retained clone','corrupt live content']:
                    ff=copy.deepcopy(files);bad=copy.deepcopy(blocks)
                    if case=='missing buffer':ff['blocks.bin']=ff['blocks.bin'][:-24]
                    if case=='false successful clone read':bad[0]['ReadCode']=0
                    if case=='wrong source protection':bad[0]['LiveProtect']=4
                    if case=='retained clone':ff['released.json']=json.dumps({**json.loads(ff['released.json']),'AfterCloseOpenError':0}).encode()
                    if case=='corrupt live content':ff['live-0.bin']=bytes(4096)
                    try:check(ff,bad,pid,epoch,True)
                    except (AssertionError,KeyError):negative.append(dict(Case=case,Rejected=True))
                    else:raise AssertionError('negative accepted '+case)
        processes.append(s.process_state(dict(Pid=pid,EndedUtc=ex['EndedUtc'],Role='D3D-control' if d3d else 'protection-control')))
        raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file())
    graph=s.read(s.ART/'FWM-PSS-HEAP-20260913-D2/verification.json');root=Path(graph['Snapshot']);gaps=[]
    for config in graph['Configurations']:
        for epoch in config['NativeSnapshots']:
            folder=root/'runs'/(config['Configuration']+'-graceful')/'pss'/epoch['Label'];pid=config['Pid'];clone=epoch['ClonePid'];src=p.regions((folder/'snapshot-va.bin').read_bytes(),pid,clone,259);dst=p.regions((folder/'clone-va-0.bin').read_bytes(),pid,clone,0);missing=[];hist=Counter()
            for sr in src:
                if sr[4]!=4096 or sr[6]!=131072:continue
                for dr in dst:
                    begin=max(sr[0],dr[0]);end=min(sr[0]+sr[2],dr[0]+dr[2])
                    if end<=begin or (dr[4]==4096 and dr[6]==131072):continue
                    hist[(sr[5],dr[4],dr[5],dr[6])]+=end-begin;missing.append(dict(Address=begin,Bytes=end-begin,SourceProtection=sr[5],CloneState=dr[4],CloneProtection=dr[5],CloneType=dr[6]))
            assert sum(r['Bytes'] for r in missing)==epoch['SourceMapPrivateCommittedBytes']-epoch['PrivateCommittedBytes']
            gaps.append(dict(Configuration=config['Configuration'],Label=epoch['Label'],MissingBytes=sum(hist.values()),Histogram=[dict(SourceProtection=k[0],CloneState=k[1],CloneProtection=k[2],CloneType=k[3],Bytes=v) for k,v in sorted(hist.items())],Intervals=missing,SpecificGraphResourceOwnerProven=False))
    s.write(dest/'graph-omissions.json',gaps);s.write(dest/'raw-manifest.json',raw);s.write(dest/'owned-processes.json',processes)
    s.write(dest/'verification.json',dict(Verdict='SCOPED_PSS_COVERAGE_BOUNDARY_VERIFIED',Controls=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),GraphProofSHA256=s.sha(s.ART/'FWM-PSS-HEAP-20260913-D2/verification.json'),GraphOmissionsSHA256=s.sha(dest/'graph-omissions.json'),OrdinaryPrivateAllocationsCloned=48,D3DMappedBuffersAbsent=12,CompleteProcessPrivateMemoryClaim=False,SpecificGraphResourceOwnerProven=False,HeapBlockEnumeration=False,NativeHeapLeakFreedomClaim=False,PerformanceClaim=False,StageAccepted=False,ProductionChanged=False,NewTracingSession=False))
    print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
