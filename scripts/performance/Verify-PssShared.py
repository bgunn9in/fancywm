"""Shared commit propagation with clone threads never explicitly resumed."""
import argparse,copy,importlib.util,json,shutil
from pathlib import Path
import pss_shared_model as shared
spec=importlib.util.spec_from_file_location('p',Path(__file__).with_name('Verify-PssHeapControl.py'));p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p);s=p.s
def check(ff,pid):
    j=lambda name:json.loads(ff[name]);w=j('witness.json');ready=j('ready.json');end=j('released.json');cap=j('capture.json');clone=w['ClonePid'];assert w['Pid']==ready['Pid']==end['Pid']==cap['Pid']==pid and clone==ready['ClonePid']==end['ClonePid']!=pid
    assert all(w[k]==0 for k in ['CaptureCode','BeforeQuery','AfterQuery','BeforeRead','AfterRead','ReleaseCode','Result']) and not w['CloneExplicitlyResumed'] and not w['ExecutableBytesExecuted']
    assert cap['CaptureCode']==0 and cap['Flags']==536877441 and cap['End']<w['CommitBegin']<w['CommitEnd']
    assert ready['RegionCount']==ready['WalkRows'] and ready['WalkCode']==259 and ready['MarkerFreeCode']==0 and not ready['CloneExplicitlyResumed']
    assert end['FreeCode']==0 and end['RetainedHandle'] and end['RetainedHandleClosed'] and end['AfterCloseOpenError']==87 and 1<=end['AbsenceProbes']<=200 and end['HeldOpenError']==0 and end['CloneWait']==258 and not end['CloneExplicitlyResumed']
    first=p.regions(ff['clone-va-0.bin'],pid,clone,0);second=p.regions(ff['clone-va-1.bin'],pid,clone,0);changes=shared.transitions(first,second)
    assert sorted((r['Base'],r['Bytes'],r['Protect']) for r in changes)==sorted([(w['SharedReadWrite']+4096,16384,4),(w['SharedExecuteReadWrite']+4096,16384,64)])
    assert w['Bytes']==65536 and w['CommitOffset']==4096 and w['CommitBytes']==16384
    for i in range(2):
        a=j(f'clone-va-{i}.bin.activity.json');assert a['Pid']==pid and a['ClonePid']==clone and a['GetTimesPassed'] and a['Kernel']==a['User']==0
    assert ff['private-before.bin']==ff['private-after.bin']==bytes([0x41+w['Epoch']])*65536
    return dict(Epoch=w['Epoch'],ClonePid=clone,SharedCommitChanges=changes,PrivateCloneBytesStable=65536,WholeCloneVaStable=False,CloneCpuTimeZero=True,CloneAbsentBeforeSourceCleanup=True)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-PssShared.py','pss_shared_model.py','Verify-PssHeapControl.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    result=[];negative=[];raw=[];own=[]
    for name,config in [('P10','Debug'),('P11','Release')]:
        root=s.ART/('FWM-D3D-MAP-20260913-'+name);created=s.read(root/'created.json');ex=s.read(root/'exited.json');pid=created['Pid'];assert ex['Pid']==pid and ex['ExitCode']==0 and not ex['ForcedCleanup'] and created['Configuration']==config;tool=Path(created['Tool']);assert s.sha(tool/'provenance.json')==created['ToolProvenanceSHA256']
        for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
        pss=s.ART/'FWM-PSS-HEAP-20260913-T5/binaries'/config/'FancyWM.NativePss.dll';assert Path(created['Command'][-1]).resolve()==pss;out=[json.loads(x) for x in (root/'raw.stdout').read_text().splitlines()];assert [(r['Epoch'],r['Result'],r['OwnedViewsUnmapped']) for r in out]==[(0,0,True),(1,0,True)]
        epochs=[]
        for epoch in [0,1]:
            folder=root/'shared'/str(epoch);ff={f.name:f.read_bytes() for f in folder.iterdir() if f.is_file()};record=check(ff,pid);assert record['Epoch']==epoch;epochs.append(record);own.append(s.process_state(dict(Pid=record['ClonePid'],EndedUtc=ex['EndedUtc'],Role='clone')))
            if name=='P10' and epoch==0:
                for case in ['wrong-commit-size','retained-clone','running-clone','changed-private-bytes','wrong-shared-state']:
                    bad=copy.deepcopy(ff)
                    if case=='wrong-commit-size':v=json.loads(bad['witness.json']);v['CommitBytes']+=4096;bad['witness.json']=json.dumps(v).encode()
                    if case=='retained-clone':v=json.loads(bad['released.json']);v['AfterCloseOpenError']=0;bad['released.json']=json.dumps(v).encode()
                    if case=='running-clone':v=json.loads(bad['clone-va-1.bin.activity.json']);v['User']=1;bad['clone-va-1.bin.activity.json']=json.dumps(v).encode()
                    if case=='changed-private-bytes':bad['private-after.bin']=b'X'+bad['private-after.bin'][1:]
                    if case=='wrong-shared-state':bad['clone-va-1.bin']=bad['clone-va-0.bin']
                    try:check(bad,pid)
                    except (AssertionError,KeyError):negative.append(dict(Case=case,Rejected=True))
                    else:raise AssertionError('negative accepted '+case)
        result.append(dict(Run=root.name,Configuration=config,Pid=pid,Epochs=epochs));own.append(s.process_state(dict(Pid=pid,EndedUtc=ex['EndedUtc'],Role='native-control')));raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file())
    s.write(dest/'raw-manifest.json',raw);s.write(dest/'owned-processes.json',own);s.write(dest/'verification.json',dict(Verdict='NATIVE_PSS_SHARED_COMMIT_PROPAGATION_VERIFIED',Configurations=result,NegativeControls=negative,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),WholeCloneVaStable=False,SourcePrivateMutationBytesVerified=False,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
