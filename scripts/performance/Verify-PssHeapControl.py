"""Independent raw PSS byte/VA/ownership checks; no heap enumeration claim."""
import argparse,copy,importlib.util,json,shutil,struct
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
H=struct.Struct('<8sIIQQQII');R=struct.Struct('<QQQIIII');B=struct.Struct('<QIIII')
def regions(raw,pid,clone,error):
    h=H.unpack_from(raw);assert h[0]==b'FWMPSSV1' and h[1:3]==(pid,clone) and h[3]>0 and h[4]<h[5] and h[7]==error
    assert len(raw)==H.size+h[6]*R.size
    rows=list(R.iter_unpack(raw[H.size:]));assert rows and all(r[2]>0 for r in rows)
    assert all(a[0]+a[2]<=b[0] for a,b in zip(rows,rows[1:]));return rows
def check(files,pid):
    j=lambda name:json.loads(files[name]);cap=j('capture.json');ready=j('ready.json');rel=j('released.json');clone=ready['ClonePid']
    assert cap['Pid']==ready['Pid']==rel['Pid']==pid and clone>0 and clone!=pid and rel['ClonePid']==clone
    assert cap['CaptureCode']==0 and cap['Flags']==536877441 and cap['Begin']<cap['End']
    assert ready['RegionCount']==ready['WalkRows'] and ready['WalkCode']==259 and ready['MarkerFreeCode']==0 and not ready['CloneExplicitlyResumed']
    assert rel['FreeCode']==0 and rel['RetainedHandle'] and rel['RetainedHandleClosed'] and rel['AfterCloseOpenError']==87 and 1<=rel['AbsenceProbes']<=200 and not rel['CloneExplicitlyResumed']
    # Held handle is a real native negative control for clone absence.
    assert rel['HeldOpenError']==0 and rel['CloneWait']==258
    source=regions(files['snapshot-va.bin'],pid,clone,259);assert len(source)==ready['RegionCount']
    clones=[regions(files[f'clone-va-{i}.bin'],pid,clone,0) for i in range(3)];assert clones[0]==clones[1]==clones[2]
    for i in range(3):
        a=j(f'clone-va-{i}.bin.activity.json');assert a['Pid']==pid and a['ClonePid']==clone and a['GetTimesPassed'] and a['Created']>0 and a['Kernel']==a['User']==0
    blocks=list(B.iter_unpack(files['blocks.bin']));assert len(blocks)==52 and len({b[0] for b in blocks})==52
    for i,(address,size,kind,index,pattern) in enumerate(blocks):
        assert index==i and pattern==0x20+i and kind==int(i>=48) and size==(32 if i<16 else 4096 if i<32 else 65536)
        assert files[f'live-after-{i}.bin']==bytes([0xa0+i])*size
        for step in range(3):assert files[f'read-{step}-{i}.bin']==bytes([pattern])*size
        for rows in [source,*clones]:
            selected=[r for r in rows if r[0]<=address and address+size<=r[0]+r[2]]
            assert len(selected)==1 and selected[0][4]==0x1000 and selected[0][6]==0x20000
    return dict(Pid=pid,ClonePid=clone,Blocks=52,HeapBlocks=48,VirtualAllocBlocks=4,PrivateCommittedBytes=sum(r[2] for r in source if r[4]==0x1000 and r[6]==0x20000),CloneAbsenceOnLiveSource=True)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    tool=s.ART/'FWM-PSS-HEAP-20260913-T5';manifest=s.read(tool/'provenance.json')
    for row in manifest['Sources']+manifest['Binaries']:assert s.sha(tool/row['Path'])==row['SHA256']
    results=[];controls=[];raw=[];processes=[]
    for suffix,config in [('P3','Debug'),('P4','Release')]:
        root=s.ART/('FWM-PSS-HEAP-20260913-'+suffix);inv=s.read(root/'invocation.json');run=s.read(root/'run.json');ex=s.read(root/'exited.json');pid=ex['Pid']
        assert inv['Configuration']==config and inv['ToolProvenanceSHA256']==s.sha(tool/'provenance.json') and not run['Failure'] and ex['ExitCode']==0 and not ex['ForcedCleanup']
        lines=[json.loads(l) for l in (root/'control.stdout').read_text().splitlines()];assert len(lines)==4 and not (root/'control.stderr').read_bytes()
        processes.append(s.process_state(dict(Pid=pid,EndedUtc=ex['EndedUtc'],Role='source')))
        for epoch in range(2):
            folder=root/'control'/str(epoch);files={f.name:f.read_bytes() for f in folder.iterdir() if f.is_file()};result=check(files,pid);result.update(Configuration=config,Epoch=epoch);results.append(result)
            assert lines[2*epoch]['Ready'] and lines[2*epoch+1]['Released'] and all(l['Pid']==pid and l['ClonePid']==result['ClonePid'] and l['Epoch']==epoch and l['Result']==0 for l in lines[2*epoch:2*epoch+2])
            inspector=s.read(root/f'inspector-{epoch}.json');out=s.read(root/f'inspector-{epoch}.stdout');assert inspector['ExitCode']==0 and not inspector['TimedOut'] and not (root/f'inspector-{epoch}.stderr').read_bytes()
            assert out['InspectorPid']==inspector['Pid'] and out['TargetPid']==inspector['ClonePid']==result['ClonePid'] and out['SnapshotError']==5 and not out['HeapFound'] and out['Entries']==0 and out['Saved']
            processes.append(s.process_state(dict(Pid=inspector['Pid'],EndedUtc=inspector['EndedUtc'],Role='inspector')));processes.append(s.process_state(dict(Pid=result['ClonePid'],EndedUtc=ex['EndedUtc'],Role='clone')))
            if suffix=='P3' and epoch==0:
                for name,mutate in [
                    ('missing phase',lambda f:f.pop('read-2-0.bin')),
                    ('corrupt clone',lambda f:f.__setitem__('read-1-0.bin',bytes(32))),
                    ('unperformed live mutation',lambda f:f.__setitem__('live-after-0.bin',bytes([32])*32)),
                    ('missing native block',lambda f:f.__setitem__('blocks.bin',f['blocks.bin'][:-24])),
                    ('unstable clone VA',lambda f:f.__setitem__('clone-va-2.bin',f['snapshot-va.bin'])),
                    ('retained clone',lambda f:f.__setitem__('released.json',json.dumps({**json.loads(f['released.json']),'AfterCloseOpenError':0}).encode())),
                    ('foreign owner',lambda f:f.__setitem__('ready.json',json.dumps({**json.loads(f['ready.json']),'Pid':pid+1}).encode()))]:
                    changed=copy.deepcopy(files);mutate(changed)
                    try:check(changed,pid)
                    except (AssertionError,KeyError,struct.error):controls.append(dict(Name=name,Rejected=True))
                    else:raise AssertionError('negative control accepted '+name)
        raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file())
    s.write(dest/'raw-manifest.json',raw);s.write(dest/'owned-processes.json',processes)
    s.write(dest/'verification.json',dict(Verdict='SCOPED_PSS_PRIVATE_MEMORY_SOURCE_PASS',Configurations=results,NegativeControls=controls,ToolProvenanceSHA256=s.sha(tool/'provenance.json'),RawManifestSHA256=s.sha(dest/'raw-manifest.json'),HeapEnumerationAdmitted=False,HeapToolhelpError=5,AtomicAllocatorTransactionClaim=False,WholeAppRetentionClaim=False,NewTracingSession=False,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False))
    print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
