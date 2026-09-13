"""Native common-fixture worker retention correctness, separate from GUI counts."""
import argparse, copy, csv, hashlib, importlib.util, json, subprocess, xml.etree.ElementTree as ET
from pathlib import Path
spec=importlib.util.spec_from_file_location('graph',Path(__file__).with_name('Verify-FullGraphLifetime.py')); g=importlib.util.module_from_spec(spec); spec.loader.exec_module(g)
req=g.require; sha=g.sha; read=g.read
def source(root):
    with (root/'manifest.csv').open(encoding='utf-8-sig',newline='') as f: data=list(csv.DictReader(f))
    for r in data: req(sha(root/'source'/r['Path'])==r['SHA256'],'source provenance')
    return {r['Path'].replace('\\','/'):r['SHA256'] for r in data}
def main():
    p=argparse.ArgumentParser(); p.add_argument('--baseline',type=Path,required=True);p.add_argument('--candidate',type=Path,required=True);p.add_argument('--tests',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();req(not a.output.exists(),'receipt exists')
    before=source(a.baseline);after=source(a.candidate)
    common=['scripts/performance/Run-FullGraphLifetime.py','scripts/performance/fullgraph-lifetime/Program.cs','scripts/performance/fullgraph-lifetime/Program.Retention.cs','scripts/performance/fullgraph-lifetime/FancyWM.FullGraphLifetime.csproj']
    req(all(before[r]==after[r] for r in common),'common fixture changed')
    production=[r for r in before if r.startswith(('FancyWM/','winman-windows/')) and before[r]!=after.get(r)]
    req(sorted(production)==['FancyWM/Utilities/AnimationThread.cs','winman-windows/src/WinMan.Windows/Utilities/EventLoop.cs'],'unexpected production delta')
    br=g.rows(a.baseline/'runs/Debug-graceful/observations.jsonl');bep=g.single(br,'Epoch')
    req(bep['Epoch']==0 and bep['Captured']==592 and bep['Retained']=={'WindowReference':1,'SettingsWorkspaceReference':1} and bep['DispatcherAlive'],'native common baseline red')
    req(any(r['Kind']=='Failure' and 'Retained workload owner' in r['Error'] for r in br),'missing baseline failure')
    req(not (a.baseline/'runs/Debug-graceful/summary.json').exists(),'baseline promoted')
    g.provenance(a.candidate,True)
    raw=[];gui=[];runs=read(a.candidate/'runs.json')['Processes'];req([(r['Configuration'],r['Mode']) for r in runs]==[('Debug','graceful'),('Release','graceful')],'native configuration coverage')
    for run in runs:
        c=run['Configuration'];root=a.candidate/'runs'/f'{c}-graceful';rr=g.rows(root/'observations.jsonl');g.check(rr,'graceful',True,10,False);raw.append(rr)
        s=read(root/'summary.json');req(run['ExitCode']==0 and s['Verdict']=='PASS' and s['SurvivingHwnds']==[] and all(s[k] for k in ['DispatcherStopped','MainCompleted','WorkspaceWorkersStopped','MicaStopped','HooksCompleted']),'native worker/shutdown failure')
        gui.append(dict(Configuration=c,Epochs=[r for r in rr if r['Kind']=='Epoch'],Verdict='OBSERVATION_ONLY',NoGrowthClaim=False))
    tests=[];source(a.tests);ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    for run in read(a.tests/'runs.json'):
        c=run['Configuration'];req(run['ExitCode']==0,'affected failure');trx=a.tests/'validation'/c/f'{c}.trx';leaves=[x for x in ET.parse(trx).getroot().findall('.//t:UnitTestResult',ns) if x.find('t:InnerResults',ns) is None]
        req(len(leaves)==(411 if c=='Debug' else 423) and all(x.get('outcome')=='Passed' for x in leaves),'affected leaf coverage')
        req({'IdleLoopReleasesCompletedCallbackOwnerBeforeShutdown','LoopPreservesFifoReentrancyOriginalFailureAndAcceptedShutdownWork'}<= {x.get('testName') for x in leaves},'new EventLoop coverage')
        for x in run['Binaries']:req((a.tests/x['Path']).stat().st_size==x['Bytes'] and sha(a.tests/x['Path'])==x['SHA256'],'test binaries')
        tests.append(dict(Configuration=c,Leaves=len(leaves),TRXSHA256=sha(trx)))
    req([r['Configuration'] for r in tests]==['Debug','Release'],'affected configurations')
    controls=[]
    for case in ['missing-lifetime','retained-animation-owner','retained-settings-workspace-owner','surviving-HWND','missing-native-membership','missing-epoch']:
        rr=copy.deepcopy(raw[0])
        if case=='missing-lifetime':rr.remove(next(r for r in rr if r['Kind']=='Lifetime'))
        if case.startswith('retained-'):next(r for r in rr if r['Kind']=='Epoch')['Retained']={'WindowReference' if case=='retained-animation-owner' else 'SettingsWorkspaceReference':1}
        if case=='surviving-HWND':next(r for r in rr if r['Kind']=='Lifetime')['SurvivingHwnds']=[123]
        if case=='missing-native-membership':rr.remove(g.single(rr,'MembershipAdapter'))
        if case=='missing-epoch':rr.remove(next(r for r in rr if r['Kind']=='Epoch'))
        try:g.check(rr,'graceful',True,10,False)
        except ValueError as e:controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative control accepted')
    patch='patches/winman-windows/PERF-001-recent-timer.patch';oldpatch=(a.baseline/'source'/patch).read_bytes();newpatch=(a.candidate/'source'/patch).read_bytes();req(newpatch.startswith(oldpatch),'inherited patch prefix changed')
    rollback=[]
    for rel in production:
        path=a.output.parent/'rollback-originals'/rel;req(not path.exists(),'rollback copy exists');path.parent.mkdir(parents=True,exist_ok=True)
        with path.open('xb') as f:f.write((a.baseline/'source'/rel).read_bytes())
        req(sha(path)==before[rel],'rollback byte exact');rollback.append(dict(Path=rel,OriginalSHA256=before[rel],CandidateSHA256=after[rel],RestorableBytes=path.stat().st_size))
    receipt=dict(Verdict='PASS',Scope='Native completed animation/callback payload retention and graceful shutdown',NativeConfigurations=2,PostWarmupCyclesPerConfiguration=100,WeakReferencesPerConfiguration=6914,Retained=0,ProductionDelta=production,CommonFixture=common,Tests=tests,NegativeControls=controls,Rollback=rollback,InheritedPatchPrefixSHA256=hashlib.sha256(oldpatch).hexdigest().upper(),GuiObservations=gui,
        Protocol=['Native B1 red and owned dump gcroot identify two idle stack roots','Blocking admission and nonblocking channel drain isolated in non-inlined scopes; completed payload slots leave before idle wait','FIFO, reentrant enqueue, original callback failure and accepted shutdown work tested','Existing frame/transition/cancellation/clock-boost/failure tests retained','No reconciliation cadence, batching policy, clock design or performance claim','New dependency hunk preserves old patch bytes and pinned HEAD; complete C2/replay required'],
        WholeIdStatus='IN_PROGRESS',ProductionChanged=True,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,UserGdiNoGrowth=False,UnmodifiedStartup=False,NativeVirtualDesktopMembership=False,DeliveryRequired=True)
    with a.output.open('x',encoding='utf-8') as f:json.dump(receipt,f,indent=2)
    print(json.dumps(dict(Verdict='PASS',NegativeControls=len(controls),SHA256=sha(a.output))))
if __name__=='__main__':main()
