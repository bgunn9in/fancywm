"""Verify the native red/common-fixture green and affected regressions."""
import argparse, copy, csv, hashlib, json, xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def req(v,m):
    if not v: raise ValueError(m)
def sources(root):
    with (root/'manifest.csv').open(encoding='utf-8-sig',newline='') as f: rr=list(csv.DictReader(f))
    for r in rr: req(sha(root/'source'/r['Path'])==r['SHA256'],'source provenance')
    return {r['Path'].replace('\\','/'):r['SHA256'] for r in rr}
def validate(red,green,delta):
    req(red['ExitHex']=='0xE0434352' and red['OriginalNullReference'] and red['NativeGraph'] and red['AfterApplicationExit'] and red['MissingSummary'],'native baseline red')
    req(delta==['FancyWM/Utilities/FauxMicaProvider.cs'],'unexpected candidate delta')
    req(len(green)==2 and [g['Configuration'] for g in green]==['Debug','Release'],'candidate coverage')
    for g in green:
        req(g['ExitCode']==0 and g['ShutdownProbe'] and g['NativeGraph'] and g['Summary']['Verdict']=='PASS','candidate native green')
        s=g['Summary']; req(s['MicaStopped'] and s['WorkspaceWorkersStopped'] and s['HooksCompleted'] and s['MainCompleted'] and s['SurvivingHwnds']==[] and s['DispatcherStopped'],'surviving worker/HWND')
        req(s['CrashCleanups']==0 and s['ProgramExits']==1 and s['OriginalArranging']==s['FinalArranging'],'failure/global parameter')
def main():
    p=argparse.ArgumentParser(); p.add_argument('--baseline',type=Path,required=True); p.add_argument('--candidate',type=Path,required=True); p.add_argument('--tests',type=Path,required=True); p.add_argument('--output',type=Path,required=True); a=p.parse_args();req(not a.output.exists(),'receipt exists')
    old=sources(a.baseline); new=sources(a.candidate); req(old.keys()==new.keys(),'source set changed')
    delta=[r for r in old if old[r]!=new[r]]
    req((a.baseline/'adapters.json').read_bytes()==(a.candidate/'adapters.json').read_bytes(),'common adapters changed')
    b=a.baseline/'runs/Debug-graceful'; log=(b/'profile/FancyWM/fancywm.log').read_text(encoding='utf-8-sig'); br=read(a.baseline/'validation/Debug-graceful-receipt.json'); observations=[json.loads(s) for s in (b/'observations.jsonl').read_text().splitlines()]
    red=dict(ExitHex=br['ExitHex'],OriginalNullReference='System.NullReferenceException' in log and 'FauxMicaProvider.<>c.<.ctor>' in log,NativeGraph=any(r['Kind']=='GraphReady' and r['MicaRunning'] for r in observations),AfterApplicationExit=any(r['Kind']=='ApplicationExit' for r in observations),MissingSummary=not (b/'summary.json').exists())
    green=[]
    for config in ['Debug','Release']:
        root=a.candidate/'runs'/f'{config}-graceful'; rr=[json.loads(s) for s in (root/'observations.jsonl').read_text().splitlines()]; receipt=read(a.candidate/'validation'/f'{config}-graceful-receipt.json')
        req(not any(r['Kind']=='Failure' for r in rr),'candidate failure')
        req(receipt['OwnedDesktopHandleClosed'] and not receipt.get('TimedOut') and sha(a.candidate/'validation'/f'{config}-graceful.log')==receipt['LogSHA256'],'candidate native/process logs')
        for entry in read(a.candidate/f'{config}-binary-manifest.json'): req((a.candidate/entry['Path']).stat().st_size==entry['Bytes'] and sha(a.candidate/entry['Path'])==entry['SHA256'],'candidate binaries')
        green.append(dict(Configuration=config,ExitCode=receipt['ExitCode'],ShutdownProbe=any(r['Kind']=='ShutdownProbe' and r['MicaWorker'] for r in rr),NativeGraph=any(r['Kind']=='GraphReady' and r['MicaRunning'] for r in rr),Summary=read(root/'summary.json')))
    validate(red,green,delta)
    tests=[]; ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    sources(a.tests)
    for r in read(a.tests/'runs.json'):
        c=r['Configuration']; req(r['ExitCode']==0 and sha(a.tests/f'{c}.log')==r['LogSHA256'],'affected test failure/log')
        trx=a.tests/'validation'/c/f'{c}.trx'; result=ET.parse(trx).getroot(); leaves=[x for x in result.findall('.//t:UnitTestResult',ns) if x.find('t:InnerResults',ns) is None]
        req(len(leaves)==317 and all(x.get('outcome')=='Passed' for x in leaves),'affected leaf test coverage')
        for x in r['Binaries']: req((a.tests/x['Path']).stat().st_size==x['Bytes'] and sha(a.tests/x['Path'])==x['SHA256'],'test binaries')
        tests.append(dict(Configuration=c,PassedLeaves=len(leaves),TRXSHA256=sha(trx)))
    req([r['Configuration'] for r in tests]==['Debug','Release'],'affected configuration coverage')
    controls=[]
    for case in ['missing-baseline-red','surviving-worker','surviving-HWND','extra-production-delta','missing-Release','global-setting-changed']:
        r=copy.deepcopy(red); g=copy.deepcopy(green); d=delta[:]
        if case=='missing-baseline-red': r['OriginalNullReference']=False
        if case=='surviving-worker': g[0]['Summary']['MicaStopped']=False
        if case=='surviving-HWND': g[0]['Summary']['SurvivingHwnds']=[123]
        if case=='extra-production-delta': d.append('FancyWM/Startup.cs')
        if case=='missing-Release': g.pop()
        if case=='global-setting-changed': g[0]['Summary']['FinalArranging']=not g[0]['Summary']['OriginalArranging']
        try: validate(r,g,d)
        except ValueError as e: controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else: raise ValueError('negative control accepted')
    rel=delta[0]; oldbytes=(a.baseline/'source'/rel).read_bytes(); candidatebytes=(a.candidate/'source'/rel).read_bytes()
    rollback=a.output.parent/'isolated-rollback'; req(not rollback.exists(),'rollback exists'); rollback.mkdir()
    # Reversible rehearsal only in a fresh scratch file; immutable snapshots are inputs.
    scratch=rollback/'FauxMicaProvider.cs'; scratch.write_bytes(candidatebytes); before=sha(scratch); scratch.write_bytes(oldbytes); after=sha(scratch); req(before==new[rel] and after==old[rel],'byte-exact rollback')
    result=dict(Verdict='PASS',Scope='Native FauxMicaProvider logger lifetime correctness candidate',Baseline=red,Candidate=green,Tests=tests,ProductionDelta=delta,NegativeControls=controls,
        CommonFixture=True,Rollback=dict(Path=str(scratch),CandidateSHA256=before,RestoredSHA256=after,Bytes=len(oldbytes)),ProductionChanged=True,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS',DeliveryRequired=True,
        Protocol=['Logger captured while App exists; original warning and exception preserved','Public invalid interval check preserved; affected 317/317 leaves','No batching/worker/animation/lock/cancellation delta','Native post-App.Run failure red; native graph and provider disposal green','1500ms boundary delay is a common test-copy adapter, not a production change'])
    with a.output.open('x',encoding='utf-8') as f: json.dump(result,f,indent=2)
    print(json.dumps(dict(Verdict='PASS',NegativeControls=len(controls),SHA256=sha(a.output))))
if __name__=='__main__': main()
