"""Full graph resource/mapping/PSS observations at native lock boundaries."""
import argparse,importlib.util,json,shutil
from pathlib import Path
spec=importlib.util.spec_from_file_location('v',Path(__file__).with_name('Verify-MappedPss.py'));v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v);s=v.s
class Rows:
    def __init__(self,path):self.path=path
    def __iter__(self):
        with self.path.open(encoding='utf-8') as f:
            for line in f:yield json.loads(line)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--graph',type=Path,required=True);p.add_argument('--graph-proof',type=Path,required=True);p.add_argument('--point-calibration',type=Path);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-MappedPssGraph.py','Verify-MappedPss.py','Verify-PssHeapControl.py','d3d9_owner_model.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    root=a.graph.resolve();graph=s.read(a.graph_proof);assert Path(graph['Graph'])==root and graph['Verdict']=='SCOPED_D3D9_GRAPH_PRIVATE_OWNER_OBSERVATIONS_VERIFIED';cal=s.ART/'FWM-D3D-MAP-20260913-V4/verification.json';method=s.ART/'FWM-D3D-MAP-20260913-V2/verification.json';proof=s.read(root/'mapped-pss-provenance.json');assert proof['CalibrationSHA256']==s.sha(cal) and proof['MethodCalibrationSHA256']==s.sha(method) and proof['SeparatePssModule'] and not proof['LiveBufferBytesReadOrWritten']
    point=a.point_calibration.resolve() if a.point_calibration else cal;calibration=s.read(point);assert calibration['Verdict']=='SCOPED_NATIVE_D3D_MAPPED_PSS_SOURCE_PASS' and len(calibration['NegativeControls'])>=(11 if a.point_calibration else 9) and all(r['Rejected'] for r in calibration['NegativeControls'])
    assert calibration['RawManifestSHA256']==s.read(cal)['RawManifestSHA256'] and calibration['CalibrationSHA256']==s.read(cal)['CalibrationSHA256']
    for row in s.read(cal.parent/'raw-manifest.json'):assert s.info(s.ART/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    result=[];raw=[];own=[]
    for run in s.read(root/'runs.json')['Processes']:
        config=run['Configuration'];pid=run['ProcessId'];folder=root/'runs'/(config+'-graceful');rr=Rows(folder/'d3d9.jsonl');managed=v.rows(folder/'observations.jsonl')
        pss=s.read(root/f'{config}-mapped-pss-binary.json');normal=s.read(root/f'{config}-pss-binary.json');assert s.sha(root/pss['Path'])==pss['SHA256']==normal['SHA256']==s.sha(root/normal['Path'])
        loaded=[r for r in managed if r['Kind']=='PssModuleLoaded'];assert len(loaded)==1 and loaded[0]['Pid']==pid and loaded[0]['Module']>0 and loaded[0]['Module']!=v.one(rr,'mapped-snapshot-configured')['Value'],'separate snapshot module identity'
        records=v.check(rr,v.files(folder/'d3d9-mapped-pss'),pid);assert len(records)>=4
        live=[r for r in records if r['Phase']>=0];assert all(r['Phase'] in [0,10,20] for r in live) and {r['Phase'] for r in live}=={0,10,20}
        for epoch in [0,1,2]:
            mark=next(r for r in managed if r['Kind']=='D3D9Mark' and r['Phase']==epoch*10+1);assert mark['DispatcherAlive']
            for snap in [r for r in records if r['Phase']==epoch*10]:assert s.read(folder/'d3d9-mapped-pss'/str(snap['Snapshot'])/'mapped-owner.json')['End']<mark['QpcEnd']
        assert any(r['OwnerKind']=='vertex-buffer9' and not r['ClonePointPresent'] for r in records),'no actual hardware vertex mapping witness'
        assert any(r['OwnerKind']=='plain-surface9' and r['ClonePointPresent'] for r in records),'no actual ordinary surface mapping witness'
        for r in records:own.append(s.process_state(dict(Pid=r['ClonePid'],EndedUtc=run['EndedUtc'],Role='mapped-PSS-clone')))
        result.append(dict(Configuration=config,Pid=pid,Snapshots=records,PresentPoints=sum(r['ClonePointPresent'] for r in records),AbsentPoints=sum(not r['ClonePointPresent'] for r in records),CaptureWhileDispatcherAliveAtEpochBoundaries=True))
        raw.extend(dict(Path=str(f.relative_to(root)),**s.info(f)) for f in sorted((folder/'d3d9-mapped-pss').rglob('*')) if f.is_file());print(config,'mapped snapshots',len(records),'absent',sum(not r['ClonePointPresent'] for r in records),flush=True)
    s.write(dest/'owned-clones.json',own);s.write(dest/'raw-manifest.json',raw);s.write(dest/'verification.json',dict(Verdict='SCOPED_GRAPH_D3D_MAPPING_PSS_OMISSIONS_VERIFIED',Graph=str(root),GraphProofSHA256=s.sha(a.graph_proof),CalibrationSHA256=s.sha(cal),PointVerifierCalibrationSHA256=s.sha(point),MethodCalibrationSHA256=s.sha(method),Configurations=result,RawManifestSHA256=s.sha(dest/'raw-manifest.json'),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS',CompleteProcessMemoryClaim=False,PhysicalAllocationReleaseClaim=False,EntireVaRegionOwnershipClaim=False,LiveMappedBytesReadOrWritten=False));print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
