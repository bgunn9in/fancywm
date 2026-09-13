"""Verify explicit and per-thread default HIMC identities against native events."""
import argparse,copy,importlib.util,json
from pathlib import Path
spec=importlib.util.spec_from_file_location('events',Path(__file__).with_name('Verify-UserHandleEvents.py'));e=importlib.util.module_from_spec(spec);spec.loader.exec_module(e);a=e.a
ERRORS=['StartCode','EnableCode','DisableCode','StopCode','ConsumeCode','DecodeErrors','WriteErrors','EventsLost','BuffersLost','RealTimeBuffersLost']

def capture(folder,pid):
    summary=a.read(folder/'realtime/summary.json');events=a.rows(folder/'realtime/owned-events.jsonl')
    a.require(all(summary[k]==0 for k in ERRORS),'native capture/loss/cleanup failure')
    a.require(e.raw_rows(folder/'realtime/owned-events.raw')==[{k:r[k] for k in e.FIELDS} for r in events],'independent raw/JSON mismatch')
    a.require(summary['WrittenOwnedEvents']==len(events) and summary['SeenHandleEvents']==len(events)+summary['DiscardedOtherResourceEvents'],'filter accounting')
    e.owned_scope(events,pid);return events,summary

def check(rows,events,pid):
    def one(kind):
        rr=[r for r in rows if r['Kind']==kind];a.require(len(rr)==1,'missing/duplicate '+kind);return rr[0]
    def pair(r):
        rr=[x for x in events if x['HandleValue']==r['Handle'] and x['EventId'] in [452,453]]
        a.require(len(rr)==2 and [x['EventId'] for x in rr]==[452,453] and all(x['HandleType']==17 and x['OwnerProcessId']==pid and x['HeaderTid']==r['Tid'] for x in rr),'missing/retained/wrong HIMC pair')
        a.require(rr[0]['Timestamp']<=r['Qpc']<rr[1]['Timestamp'],'HIMC lifetime order');return rr
    explicit=[r for r in rows if r['Kind']=='ExplicitCreate'];workers=[r for r in rows if r['Kind']=='WorkerDefaultContext']
    a.require(len(explicit)==len(workers)==24 and len({r['Tid'] for r in workers})==24,'scenario/thread coverage')
    a.require(all(r['Result'] for r in rows if 'Result' in r),'native API failed')
    held=one('WorkerWindowsClosedThreadsAlive');joined=one('WorkerThreadsJoined');pairs=[]
    for r in explicit:
        create,destroy=pair(r);release=[x for x in rows if x['Kind']=='ExplicitDestroy' and x['Handle']==r['Handle']]
        a.require(len(release)==1 and destroy['Timestamp']<=release[0]['Qpc'],'explicit release witness');pairs.append(dict(Kind='Explicit',Handle=r['Handle'],Tid=r['Tid']))
    for r in workers:
        create,destroy=pair(r);closing=[x for x in rows if x['Kind']=='WorkerWindowDestroyed' and x['Tid']==r['Tid']];leaving=[x for x in rows if x['Kind']=='ThreadLeaving' and x['Tid']==r['Tid']]
        a.require(len(closing)==len(leaving)==1 and closing[0]['Result'] and closing[0]['Qpc']<held['QpcBegin']<=held['QpcEnd']<leaving[0]['Qpc']<destroy['Timestamp']<joined['QpcBegin'],'default HIMC must survive window close then release at thread exit')
        pairs.append(dict(Kind='ThreadDefault',Handle=r['Handle'],Tid=r['Tid'],WindowClosed=closing[0]['Qpc'],ThreadLeaving=leaving[0]['Qpc'],Destroyed=destroy['Timestamp']))
    marks=[one(k) for k in ['Baseline','ExplicitHeld','ExplicitReleased','WorkerWindowsHeld','WorkerWindowsClosedThreadsAlive','WorkerThreadsJoined']]
    baseline=marks[0]['USER'];a.require([r['USER']-baseline for r in marks]==[0,24,0,72,24,0],'USER calibration delta/retention')
    a.require(len({r['GDI'] for r in marks})==1,'GDI calibration changed')
    return dict(Pairs=pairs,Markers=marks)

def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args();root=args.snapshot;a.require(not args.output.exists(),'receipt exists')
    folder=root/'runs/Native-graceful';summary=a.read(folder/'summary.json');run=a.read(root/'runs.json')['Processes'][0];pid=run['ProcessId'];a.require(summary['Pid']==pid and summary['Verdict']=='PASS' and run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run['InputSent'] and not run['DesktopSwitched'] and a.read(root/'cleanup.json')['OwnedSessionStopped'],'owned process/cleanup')
    invocation=a.read(root/'invocation.json');tool=Path(invocation['Tool']);a.require(a.sha(tool/'InputContextProbe.exe')==invocation['ProbeSHA256'] and a.sha(tool/'source/InputContextProbe.cpp')==invocation['SourceSHA256'],'native source/binary provenance')
    events,captured=capture(folder,pid);rr=a.rows(folder/'input-contexts.jsonl');result=check(rr,events,pid);negative=[]
    for case in ['missing-context','retained-context','wrong-type','premature-destruction','surviving-HWND']:
        bad=copy.deepcopy(events);rows=copy.deepcopy(rr);worker=next(r for r in rows if r['Kind']=='WorkerDefaultContext');destroy=next(r for r in bad if r['HandleValue']==worker['Handle'] and r['EventId']==453)
        if case=='missing-context':rows.remove(worker)
        if case=='retained-context':bad.remove(destroy)
        if case=='wrong-type':destroy['HandleType']=999
        if case=='premature-destruction':destroy['Timestamp']=worker['Qpc']
        if case=='surviving-HWND':next(r for r in rows if r['Kind']=='WorkerWindowDestroyed')['Result']=0
        try:check(rows,bad,pid)
        except ValueError as x:negative.append(dict(Case=case,Rejected=True,Reason=str(x)))
        else:raise ValueError('negative accepted '+case)
    a.write(args.output,dict(Verdict='SCOPED_NATIVE_INPUT_CONTEXT_PASS',ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS',Pid=pid,NativeHimcPairs=48,NegativeControls=negative,Capture=captured,**result,RawFiles=[dict(Path=str(f.relative_to(root)),Bytes=f.stat().st_size,SHA256=a.sha(f)) for f in sorted((root/'runs').rglob('*')) if f.is_file()]))
    print(json.dumps(dict(Verdict='SCOPED_NATIVE_INPUT_CONTEXT_PASS',Pairs=48,SHA256=a.sha(args.output))))
if __name__=='__main__':main()
