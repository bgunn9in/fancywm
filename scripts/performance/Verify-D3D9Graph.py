"""Full graph native evidence with calibrated D3D9 private-data observations."""
import argparse,copy,importlib.util,json,shutil
from pathlib import Path
def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
h=module('hook','Verify-HookedD3D9.py');s=h.s;g=module('graph','Verify-FullGraphLifetime.py');m=module('d3d','d3d9_owner_model.py')
PHASES=[-1,0,1,10,11,20,21,99,999]
def check(rows,managed,pid,mutation=None):
    model=m.replay(rows,pid,mutation);marks=model['Checkpoints'];assert [r['Phase'] for r in marks]==PHASES,'checkpoint coverage'
    start=g.single(managed,'D3D9Started');assert start['Pid']==pid and start['Code']==0 and start['PrivateDataOwnersOnly'] and managed.index(start)<managed.index(g.single(managed,'Process'))
    own=[r for r in managed if r['Kind']=='D3D9Mark'];assert [r['Phase'] for r in own]==PHASES[:-1]
    for a,b in zip(marks,own):assert b['Pid']==pid and b['Code']==0 and b['QpcBegin']<=a['Qpc']<=b['QpcEnd'] and b['DispatcherAlive']==(a['Phase']!=99)
    assert marks[0]['Total']==0 and model['Generations'] and model['MapPairs'],'native source did not observe graph'
    return model
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--graph',type=Path,required=True);p.add_argument('--pss-proof',type=Path,required=True);p.add_argument('--calibration',type=Path);a=p.parse_args();dest=s.ART/a.id;assert not dest.exists(),'receipt exists';assert s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    for name in ['Verify-D3D9Graph.py','d3d9_owner_model.py','Verify-HookedD3D9.py','Verify-FullGraphLifetime.py','Finalize-NativeHeapEvidence.py']:shutil.copyfile(Path(__file__).with_name(name),dest/name)
    root=a.graph.resolve();pss=s.read(a.pss_proof);assert Path(pss['Snapshot'])==root and pss['Verdict'] in ['SCOPED_PSS_GRAPH_VA_OBSERVATIONS_VERIFIED','SCOPED_PSS_GRAPH_PRIVATE_VA_WITH_SHARED_COMMITS_VERIFIED']
    if pss['Verdict']=='SCOPED_PSS_GRAPH_PRIVATE_VA_WITH_SHARED_COMMITS_VERIFIED':
        assert pss['SharedControlSHA256']==s.sha(s.ART/'FWM-D3D-MAP-20260913-V5/verification.json') and pss['SharedChangesAreOnlyReservedToCommitted']
    for r in s.read(a.pss_proof.parent/'raw-manifest.json'):assert s.info(root/r['Path'])=={k:r[k] for k in ['Bytes','SHA256']}
    sources=g.provenance(root,True);proof=s.read(root/'d3d9-provenance.json');cal=a.calibration.resolve() if a.calibration else s.ART/'FWM-D3D-LIFETIME-20260913-V3/verification.json';tool=Path(proof['Tool']);assert s.sha(cal)==proof['CalibrationSHA256'] and s.sha(tool/'provenance.json')==proof['ToolProvenanceSHA256']
    for r in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/r['Path'])=={k:r[k] for k in ['Bytes','SHA256']}
    # The linear replay is independently checked against the original calibrated
    # generation journal, without starting any new native process.
    calibration=[]
    for cr in s.read(cal)['Configurations']:
        folder=s.ART/cr['Run'];pid=cr['Pid'];rr=h.rows(folder/'observer.jsonl');linear=m.replay(iter(rr),pid);original=h.journal(rr,pid)
        assert set(linear['Generations'])==set(original)
        for ident,o in original.items():
            actual=linear['Generations'][ident]
            for k in ['Attach','Registration','End']:assert actual[k]=={key:value for key,value in o[k].items() if key!='Stack'}
        assert len(linear['MapPairs'])==600 and not linear['FinalActiveIds'];calibration.append(dict(Run=cr['Run'],Generations=1050,Equivalent=True))
    results=[];negative=[];details=[]
    for run in s.read(root/'runs.json')['Processes']:
        config=run['Configuration'];pid=run['ProcessId'];folder=root/'runs'/(config+'-graceful');managed=g.rows(folder/'observations.jsonl');g.check(managed,'graceful',True,50,False,True)
        observer=s.read(root/f'{config}-d3d9-binary.json');assert observer['SHA256']==s.sha(root/observer['Path'])==s.sha(tool/'binaries'/config/'Observer9.dll')
        model=check(m.read(folder/'d3d9.jsonl'),managed,pid);stop=s.read(folder/'d3d9-stopped.json');assert stop['Pid']==pid and stop['Code']==0 and not stop['ModuleUnloaded'] and not stop['CompleteResourceDestructionClaim']
        assert stop['QpcBegin']<model['Checkpoints'][-1]['Qpc']<stop['QpcEnd']
        checkpoints=model['Checkpoints'];warm=next(c for c in checkpoints if c['Phase']==1);live=[c for c in checkpoints if c['Phase'] in [1,11,21]];shutdown=next(c for c in checkpoints if c['Phase']==99)
        generations=model['Generations'];cohorts=[]
        for cp in live+[shutdown,checkpoints[-1]]:
            alive=set(cp['ActiveIds']);old=set(warm['ActiveIds']);cohorts.append(dict(Phase=cp['Phase'],Active=cp['Active'],WarmOwnersStillActive=len(alive&old),NewOwnersActive=len(alive-old),ActiveKinds=cp['Kinds']))
        # A successful map pointer only describes memory during its Lock/Unlock
        # interval. Snapshot correlation is permitted only inside that interval.
        mapped=[]
        for label in ['startup','0','1','2','shutdown']:
            capture=s.read(folder/'pss'/label/'capture.json');active=[x for x in model['MapPairs'] if x['Begin']<capture['Begin'] and capture['End']<x['End']]
            mapped.append(dict(Label=label,MapsCoveringEntireCapture=active))
        stats=dict(Configuration=config,Pid=pid,Rows=model['Rows'],RegisteredPrivateOwners=len(generations),PrivateOwnerReleases=sum(v['End'] is not None for v in generations.values()),FinalActivePrivateOwners=len(model['FinalActiveIds']),PrivateOwnerCountNoGrowth=all(c['Active']<=warm['Active'] for c in live),Cohorts=cohorts,MapUnmapPairs=len(model['MapPairs']),UnownedMapEvents=model['UnownedMapEvents'],ObserverClosed=model['ObserverClosed'],DeferredClose=model['DeferredClose'] is not None,SnapshotMapCorrelation=mapped)
        stats.update(NestedMapCalls=len(model['NestedMaps']),NativeMapCallBoundaries=len(model['MapCalls']),ApiFailures=model['ApiFailures'],UntrackedTextureParentCount=len(model['UntrackedTextureParents']))
        results.append(stats);details.append(dict(Configuration=config,Checkpoints=checkpoints,FinalActiveOwners=[generations[i] for i in model['FinalActiveIds']],Events=model['Events']))
        print(config,json.dumps({k:v for k,v in stats.items() if k not in ['SnapshotMapCorrelation']}),flush=True)
        if config=='Debug':
            for case in ['retained-owner','missing-map-end','wrong-pid','wrong-owner','missing-checkpoint','bad-checkpoint','early-release','missing-call-boundary']:
                try:check(m.read(folder/'d3d9.jsonl'),managed,pid,case)
                except (AssertionError,KeyError):negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
            for case in ['missing-lifetime','surviving-HWND','retained-managed-owner']:
                bad=copy.deepcopy(managed)
                if case=='missing-lifetime':bad.remove(next(r for r in bad if r['Kind']=='Lifetime'))
                if case=='surviving-HWND':next(r for r in bad if r['Kind']=='Lifetime')['SurvivingHwnds']=[123]
                if case=='retained-managed-owner':next(r for r in bad if r['Kind']=='Epoch')['Retained']={'SettingsWindow':1}
                try:g.check(bad,'graceful',True,50,False,True)
                except ValueError:negative.append(dict(Case=case,Rejected=True))
                else:raise AssertionError('negative accepted '+case)
    s.write(dest/'private-owner-details.json',details);s.write(dest/'verification.json',dict(Verdict='SCOPED_D3D9_GRAPH_PRIVATE_OWNER_OBSERVATIONS_VERIFIED',Graph=str(root),PssProofSHA256=s.sha(a.pss_proof),CalibrationSHA256=s.sha(cal),ReplayCalibration=calibration,Configurations=results,NegativeControls=negative,DetailsSHA256=s.sha(dest/'private-owner-details.json'),SourceFilesVerified=sources,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,CompleteResourceDestructionClaim=False,CompleteD3DResourceCoverage=False,NativeHeapLeakFreedomClaim=False,PhysicalAllocationReleaseClaim=False,WholeIdStatus='IN_PROGRESS'))
    print(s.sha(dest/'verification.json'),flush=True)
if __name__=='__main__':main()
