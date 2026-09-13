"""PSS VA membership at a native D3D lock point with live owner identity."""
import argparse,copy,importlib.util,json,shutil
from pathlib import Path
import d3d9_owner_model as model
spec=importlib.util.spec_from_file_location('p',Path(__file__).with_name('Verify-PssHeapControl.py'));p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p);s=p.s
def rows(path):return [json.loads(x) for x in path.read_text(encoding='utf-8').splitlines()]
def one(rr,event):
    found=[r for r in rr if r['Event']==event];assert len(found)==1,(event,len(found));return found[0]
def files(root):return {int(d.name):{f.name:f.read_bytes() for f in d.iterdir() if f.is_file()} for d in sorted(root.iterdir(),key=lambda x:int(x.name)) if d.is_dir()}
def region(rr,address):
    found=[r for r in rr if r[0]<=address<r[0]+r[2]];assert len(found)==1;return found[0]
def check(rr,ff,pid):
    replay=model.replay(iter(rr),pid);assert one(rr,'mapped-snapshot-configured')['Value']>0
    begins=[r for r in rr if r['Event']=='mapped-snapshot-begin'];ends=[r for r in rr if r['Event']=='mapped-snapshot-end'];assert [r['Value'] for r in begins]==[r['Value'] for r in ends]==list(ff)==list(range(1,len(ff)+1)) and ff,'snapshot sequence/coverage'
    eligible={}
    for call in replay['MapCalls'].values():
        b=call['Begin'];r=call['Result'];phase=r['Phase'];owner=call['Owner']
        if call['API'] in model.LOCKS and r['Value']==0 and b['Third']==1 and owner and (phase==-1 or phase>=0 and phase%10==0):eligible.setdefault((phase,replay['Generations'][owner]['Attach']['Kind']),b['Value'])
    records=[];actual={}
    for a,e in zip(begins,ends):
        data=ff[a['Value']];j=lambda name:json.loads(data[name]);w=j('mapped-owner.json');cap=j('capture.json');ready=j('ready.json');end=j('released.json');clone=ready['ClonePid'];owner=w['OwnerId'];call=replay['MapCalls'][w['Call']];g=replay['Generations'][owner];address=w['Pointer'];actual[(w['Phase'],w['OwnerKind'])]=w['Call']
        assert w['Snapshot']==a['Value']==e['Value'] and w['Pid']==cap['Pid']==ready['Pid']==end['Pid']==a['Pid']==e['Pid']==pid and clone>0 and clone!=pid and clone==w['ClonePid']==end['ClonePid']==e['Extra']
        assert call['Owner']==owner==a['Id']==e['Id'] and w['OwnerIdentity']==g['Attach']['Identity']==a['Identity'] and w['OwnerKind']==g['Attach']['Kind']
        assert call['API']==w['API'] and call['Begin']['Third']==1 and call['Begin']['Tid']==w['Tid']==a['Tid']==e['Tid']
        assert w['Phase']==a['Phase']==e['Phase'] and address==a['Extra']==call['Result']['Extra']>0 and a['Third']==w['Call']
        assert call['Result']['Qpc']<w['Begin']<=a['Qpc']<cap['Begin']<cap['End']<w['End']<e['Qpc']<call['End']['Qpc'],'capture not inside native map call'
        assert g['Registration']['Qpc']<w['Begin'] and (not g['End'] or g['End']['Qpc']>w['End']),'owner no longer registered'
        assert w['CaptureCode']==w['FirstQuery']==w['SecondQuery']==w['ReleaseCode']==cap['CaptureCode']==0 and cap['Flags']==536877441 and w['ProbeBytes']==1 and not w['LiveBytesReadOrWritten']
        assert w['SourceQueryBytes']==w['AfterQueryBytes']==48 and all(w[k]==w['After'+k] for k in ['Base','AllocationBase','State','Type','Protect'])
        # The probe belongs to an allocation, not to every adjacent page that
        # VirtualQuery groups with it. Allow only the observed upward extension
        # of that same region, while retaining the point and all its metadata.
        assert w['AfterBytes']>=w['Bytes'] and w['AfterBase']<=address<w['AfterBase']+w['AfterBytes']
        assert w['State']==0x1000 and w['Base']<=address<w['Base']+w['Bytes'] and w['ExtentOrPitch']==call['Result']['Third']
        assert ready['RegionCount']==ready['WalkRows'] and ready['WalkCode']==259 and ready['MarkerFreeCode']==0 and not ready['CloneExplicitlyResumed']
        assert end['FreeCode']==0 and end['RetainedHandle'] and end['RetainedHandleClosed'] and end['AfterCloseOpenError']==87 and 1<=end['AbsenceProbes']<=200 and end['HeldOpenError']==0 and end['CloneWait']==258 and not end['CloneExplicitlyResumed']
        source=p.regions(data['snapshot-va.bin'],pid,clone,259);first=p.regions(data['clone-va-0.bin'],pid,clone,0);second=p.regions(data['clone-va-1.bin'],pid,clone,0);assert first==second and len(source)==ready['RegionCount']
        for step in range(2):
            activity=j(f'clone-va-{step}.bin.activity.json');assert activity['Pid']==pid and activity['ClonePid']==clone and activity['GetTimesPassed'] and activity['Kernel']==activity['User']==0
        src=region(source,address);copied=region(first,address);assert src[1]==w['AllocationBase'] and src[4]==w['State'] and src[5]==w['Protect'] and src[6]==w['Type']
        present=copied[4]==0x1000
        if present:assert w['CloneReadCode']==e['Third']==0 and len(data['clone-probe.bin'])==1
        else:assert copied[4]==0x10000 and w['CloneReadCode']==e['Third']==299 and 'clone-probe.bin' not in data
        records.append(dict(Snapshot=w['Snapshot'],Phase=w['Phase'],OwnerId=owner,OwnerKind=w['OwnerKind'],Call=w['Call'],Pointer=address,SourceAllocationBase=src[1],SourceType=src[6],SourceProtect=src[5],SourceRegionBytesBefore=w['Bytes'],SourceRegionBytesAfter=w['AfterBytes'],CloneState=copied[4],ClonePointPresent=present,CloneReadCode=w['CloneReadCode'],ClonePid=clone,PrivateDataOwnerHeldDuringSnapshot=True,CloneAbsentBeforeMapReturns=True,EntireRegionOwnershipClaim=False))
    assert actual==eligible,'missing or unselected map snapshot'
    return records
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--id',required=True);a=parser.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-MappedPss.py','Verify-PssHeapControl.py','d3d9_owner_model.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    cal=[s.ART/'FWM-D3D-MAP-20260913-V2/verification.json',s.ART/'FWM-D3D-MAP-20260913-V3/verification.json'];assert all(s.read(c)['Configurations'] for c in cal)
    result=[];negative=[];raw=[];own=[]
    for name,config in [('P6','Debug'),('P7','Release'),('P8','Debug'),('P9','Release')]:
        root=s.ART/('FWM-D3D-MAP-20260913-'+name);c=s.read(root/'created.json');ex=s.read(root/'exited.json');pid=c['Pid'];assert pid==ex['Pid'] and c['Configuration']==config and ex['ExitCode']==0 and not ex['ForcedCleanup']
        expected=s.ART/'FWM-PSS-HEAP-20260913-T5/binaries'/config/'FancyWM.NativePss.dll';assert s.sha(root/'FancyWM.MapPss.dll')==c['MappedPssSHA256']==s.sha(expected)
        rr=rows(root/'observer.jsonl');ff=files(root/'mapped');records=check(rr,ff,pid);assert len(records)==12
        if name in ['P6','P7']:assert all(r['ClonePointPresent']==(r['Phase']<100) for r in records)
        result.append(dict(Run=root.name,Configuration=config,Pid=pid,Snapshots=records))
        for role,ident in [('producer',pid),*[('clone',r['ClonePid']) for r in records]]:own.append(s.process_state(dict(Pid=ident,EndedUtc=ex['EndedUtc'],Role=role)))
        if name=='P6':
            for case in ['missing-snapshot','wrong-owner','stale-pointer-interval','retained-clone','foreign-clone','source-not-committed','missing-clone-probe','wrong-call','live-buffer-read','changed-allocation','point-outside-after-region']:
                bad=copy.deepcopy(ff);which=1;j=lambda file:json.loads(bad[which][file]);put=lambda file,v:bad[which].update({file:json.dumps(v).encode()})
                if case=='missing-snapshot':del bad[1]
                if case=='wrong-owner':w=j('mapped-owner.json');w['OwnerIdentity']+=8;put('mapped-owner.json',w)
                if case=='stale-pointer-interval':w=j('mapped-owner.json');w['Begin']=1;put('mapped-owner.json',w)
                if case=='retained-clone':w=j('released.json');w['AfterCloseOpenError']=0;put('released.json',w)
                if case=='foreign-clone':w=j('ready.json');w['ClonePid']=pid;put('ready.json',w)
                if case=='source-not-committed':w=j('mapped-owner.json');w['State']=w['AfterState']=0x10000;put('mapped-owner.json',w)
                if case=='missing-clone-probe':del bad[1]['clone-probe.bin']
                if case=='wrong-call':w=j('mapped-owner.json');w['Call']+=1;put('mapped-owner.json',w)
                if case=='live-buffer-read':w=j('mapped-owner.json');w['LiveBytesReadOrWritten']=True;put('mapped-owner.json',w)
                if case=='changed-allocation':w=j('mapped-owner.json');w['AfterAllocationBase']+=65536;put('mapped-owner.json',w)
                if case=='point-outside-after-region':w=j('mapped-owner.json');w['AfterBytes']=w['Pointer']-w['AfterBase'];put('mapped-owner.json',w)
                try:check(rr,bad,pid)
                except (AssertionError,KeyError):negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
        raw.extend(dict(Path=str(f.relative_to(s.ART)),**s.info(f)) for f in sorted(root.rglob('*')) if f.is_file())
    s.write(dest/'owned-processes.json',own);s.write(dest/'raw-manifest.json',raw);s.write(dest/'verification.json',dict(Verdict='SCOPED_NATIVE_D3D_MAPPED_PSS_SOURCE_PASS',Configurations=result,NegativeControls=negative,CalibrationSHA256=[s.sha(c) for c in cal],RawManifestSHA256=s.sha(dest/'raw-manifest.json'),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,CompleteResourceLifetimeClaim=False,PhysicalAllocationReleaseClaim=False,LiveBufferBytesReadOrWritten=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
