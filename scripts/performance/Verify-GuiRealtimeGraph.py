"""Attribute live USER counts using native raw events and QPC-bracketed counters."""
import argparse,copy,importlib.util,json
from pathlib import Path
from collections import Counter

def module(name,file):
    s=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
i=module('inputs','Verify-InputContexts.py');e=i.e;a=i.a

def snapshots(events,marks,pid):
    a.require(all(x['Timestamp']<=y['Timestamp'] for x,y in zip(events,events[1:])),'out-of-order native event stream')
    result=[];live={};transferred={};cursor=0
    def apply(r):
        id=r['EventId'];h=r['HandleValue']
        if id in [452,454] and r['OwnerProcessId']==pid:
            if id==452:a.require(h not in live,'duplicate live USER handle')
            live[h]=r
        if id==454 and r['OwnerProcessId']!=pid:
            a.require(h in live,'owner transfer without currently owned USER anchor')
            transferred[h]=live[h];live.pop(h)
        if id==453:live.pop(h,None)
    for mark in marks:
        begin,end=mark['CounterQpcBegin'],mark['CounterQpcEnd'];a.require(begin<=end,'invalid QPC bracket')
        while cursor<len(events) and events[cursor]['Timestamp']<begin:apply(events[cursor]);cursor+=1
        atBegin=dict(live);counts=[len(live)]
        while cursor<len(events) and events[cursor]['Timestamp']<=end:apply(events[cursor]);cursor+=1;counts.append(len(live))
        matched=mark['Result'] in counts
        if mark['Phase'] in [9,19,29]:a.require(set(live)==set(atBegin),'unstable epoch USER identity within counter bracket')
        countsByType=Counter(r['HandleType'] for r in atBegin.values())
        contexts=[dict(Handle=h,Tid=r['HeaderTid'],CreatedQpc=r['Timestamp']) for h,r in sorted(atBegin.items()) if r['HandleType']==17]
        result.append(dict(Phase=mark['Phase'],USER=mark['Result'],GDI=mark['Argument'],CountsByType=dict(countsByType),InputContexts=contexts,CounterIntervalCounts=sorted(set(counts)),CounterMatched=matched,AbsoluteCounterResidual=mark['Result']-len(atBegin),AbsoluteCounterClaim=False,PreviouslyOwnedTransfers=[dict(Handle=h,Type=r['HandleType']) for h,r in sorted(transferred.items())],NonContextObjects=[dict(Handle=h,Type=r['HandleType']) for h,r in sorted(atBegin.items()) if r['HandleType']!=17]))
    a.require([x['Phase'] for x in result]==[9,19,29,999],'missing epoch/shutdown native marker')
    for x in result[:3]:a.require(len({r['Tid'] for r in x['InputContexts']})==len(x['InputContexts']),'multiple live HIMCs per thread')
    baselineCounts=Counter(r['Type'] for r in result[0]['NonContextObjects'])
    a.require(all(Counter(r['Type'] for r in x['NonContextObjects'])<=baselineCounts for x in result[1:3]),'non-context USER count accumulation')
    for index in [1,2]:
        previous={r['Handle']:r['Type'] for r in result[index-1]['NonContextObjects']};current={r['Handle']:r['Type'] for r in result[index]['NonContextObjects']};begin=marks[index-1]['CounterQpcEnd'];end=marks[index]['CounterQpcBegin'];transitions=[]
        for h in previous.keys()-current.keys():
            rr=[r for r in events if r['EventId']==453 and r['HandleValue']==h and begin<r['Timestamp']<end]
            a.require(len(rr)==1 and previous[h] in [1,5,16],'non-context replacement missing original native destroy');transitions.append(dict(Handle=h,Type=previous[h],Action='destroy',Qpc=rr[0]['Timestamp']))
        for h in current.keys()-previous.keys():
            rr=[r for r in events if r['EventId']==452 and r['HandleValue']==h and begin<r['Timestamp']<end]
            a.require(len(rr)==1 and current[h] in [5,16],'non-context replacement missing new native create');transitions.append(dict(Handle=h,Type=current[h],Action='create',Qpc=rr[0]['Timestamp']))
        result[index]['NativeReplacements']=transitions
    a.require(all(x['USER']-result[0]['USER']==len(x['InputContexts'])-len(result[0]['InputContexts'])+len(x['NonContextObjects'])-len(result[0]['NonContextObjects']) for x in result[:3]),'unattributed USER variation')
    a.require(all(x['AbsoluteCounterResidual']==2 and x['PreviouslyOwnedTransfers']==result[0]['PreviouslyOwnedTransfers'] and len(x['PreviouslyOwnedTransfers'])==2 and all(t['Type']==2 for t in x['PreviouslyOwnedTransfers']) for x in result[:3]),'unexplained/changing absolute USER residual or owner transfers')
    return result

def stability(rows):
    for epoch in range(3):
        raw=[r for r in rows if r['Kind']=='GuiStabilitySample' and r['Epoch']==epoch];admitted=[r for r in rows if r['Kind']=='GuiStabilityAdmitted' and r['Epoch']==epoch]
        a.require(len(admitted)==1 and 20<=len(raw)<=300 and [r['Sample'] for r in raw]==list(range(len(raw))),'bounded stability scenario coverage')
        plateau=[];first=None
        for index,r in enumerate(raw):
            a.require(r['DispatcherAlive'] and (index==0 or r['Qpc']>raw[index-1]['Qpc']),'stability dispatcher/time')
            if plateau and (plateau[0]['USER'],plateau[0]['GDI'])!=(r['USER'],r['GDI']):plateau=[]
            plateau.append(r)
            if len(plateau)==20:first=index;break
        a.require(first==len(raw)-1 and len(plateau)==20,'first stable plateau was skipped or never reached')
        admission=admitted[0];a.require(admission['Observations']==len(raw) and admission['MaxObservations']==300 and admission['Consecutive']==20 and admission['FirstStablePlateau'] and not admission['MinimumSelection'],'stability policy changed')
        selected=[r for r in rows if r['Kind']=='GuiSample' and r['Epoch']==epoch]
        a.require(len(selected)==20 and all((x['USER'],x['GDI'],x['Qpc'])==(y['USER'],y['GDI'],y['Qpc']) for x,y in zip(selected,plateau)),'selected samples differ from native observations')

def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--threads',action='store_true');p.add_argument('--quiescence',type=int,choices=[0,45],default=0);p.add_argument('--stable-window',type=int,choices=[0,30],default=0);p.add_argument('--compact-targets',action='store_true');args=p.parse_args();root=args.snapshot;a.require(not args.output.exists(),'receipt exists')
    graph=a.read(root/'gui-verification.json');a.require(graph['VerificationPassed'] and graph['Processes']==2,'prior strict graph verification')
    for f in graph['RawFiles']:a.require(a.sha(root/f['Path'])==f['SHA256'],'graph raw receipt changed')
    native=a.read(root.parent/'FWM-GUI-ATTRIBUTION-20260913-I1/verification.json');a.require(native['Verdict']=='SCOPED_NATIVE_INPUT_CONTEXT_PASS' and native['NativeHimcPairs']==48,'native type calibration')
    observed=[];negative=[]
    for c in ['Debug','Release']:
        folder=root/'runs'/(c+'-graceful');pid=a.read(folder/'summary.json')['Pid'];events,capture=i.capture(folder,pid);api=a.rows(folder/'gui-api.jsonl');e.controls(events,api,pid)
        cleanup=a.read(root/'validation'/(c+'-owned-etw-cleanup.json'));a.require(cleanup['Inactive'] and cleanup['OwnPid']==pid,'owned session cleanup')
        provenance=a.read(root/'realtime-provenance.json');a.require(a.sha(root/'binaries'/c/'FancyWM.OwnedUserTrace.dll')==provenance['SHA256'],'realtime binary provenance')
        marks=[r for r in api if r['Action']=='mark' and r['Phase'] in [9,19,29,999]];phases=snapshots(events,marks,pid)
        if args.threads:
            for phase in phases[:3]:
                threads=a.read(folder/('gui-threads-'+str(phase['Phase']//10)+'.json'));tids={r['Id'] for r in threads['Threads']};a.require(all(r['Tid'] in tids for r in phase['InputContexts']),'HIMC whose creating thread no longer lives')
                phase['OwnNativeThreads']=len(tids);phase['ManagedPoolThreads']=threads['ManagedPoolThreads'];phase['PendingWork']=threads['PendingWork'];phase['EveryContextCreatorThreadAlive']=True
        rows=a.rows(folder/'observations.jsonl');quiescence=[r for r in rows if r['Kind']=='GuiQuiescence'];a.require(len(quiescence)==3*args.quiescence,'quiescence scenario coverage')
        if args.compact_targets:
            contract=next(r for r in rows if r['Kind']=='GuiTargetLayoutContract');a.require(contract['CompactNativeTargets'] and contract['AutoSplitCount']==50 and contract['NativeTargetMinimum']==8 and not contract['GlobalDisplayChanged'],'compact diagnostic target contract')
            protocol=a.rows(folder/'target-protocol.jsonl');ready=protocol[0];a.require(ready['CompactNativeTargets'] and ready['Dpi']==192,'current native target environment')
            creates=[r for r in protocol if r.get('Operation')=='create' and r.get('Success')];a.require(len(creates)==150 and all(w['CompactNativeTargets'] and w['NativeMinimumReplies']>0 and w['Dpi']==192 for r in creates for w in r['Windows']),'real native compact target scenario coverage')
            display=next(r for r in rows if r['Kind']=='GraphDisplayContract');a.require(len(display['Displays'])==1 and display['Displays'][0]['Device']=='NoMonitor' and display['Displays'][0]['WorkArea']['Width']==1024 and display['Displays'][0]['WorkArea']['Height']==768,'observed fallback display boundary changed')
        if args.quiescence:
            for epoch in range(3):a.require([r['Second'] for r in quiescence if r['Epoch']==epoch]==list(range(1,46)) and all(r['DispatcherAlive'] for r in quiescence),'quiescence coverage/dispatcher')
        if args.stable_window:
            stability(rows)
            if c=='Debug':
                for case in ['missing-stability-sample','selected-counter-changed','minimum-selection']:
                    bad=copy.deepcopy(rows)
                    if case=='missing-stability-sample':bad.remove(next(r for r in bad if r['Kind']=='GuiStabilitySample'))
                    if case=='selected-counter-changed':next(r for r in bad if r['Kind']=='GuiSample')['USER']+=1
                    if case=='minimum-selection':next(r for r in bad if r['Kind']=='GuiStabilityAdmitted')['MinimumSelection']=True
                    try:stability(bad)
                    except ValueError as x:negative.append(dict(Case=case,Rejected=True,Reason=str(x)))
                    else:raise ValueError('negative accepted '+case)
        observed.append(dict(Configuration=c,Pid=pid,Capture=capture,NativePairs=72,Phases=phases,Quiescence=quiescence,CompactTargetContract=contract if args.compact_targets else None,GraphDisplay=display if args.compact_targets else None,NativeTargetEnvironment=ready if args.compact_targets else None))
        if c=='Debug':
            for case in ['missing-scenario','retained-native-owner','wrong-counter','surviving-HWND']:
                bad=copy.deepcopy(events);mm=copy.deepcopy(marks)
                if case=='missing-scenario':mm.pop(1)
                if case=='wrong-counter':mm[1]['Result']+=1
                if case in ['retained-native-owner','surviving-HWND']:
                    cutoff=mm[2]['CounterQpcBegin'];r=next(r for r in bad if r['EventId']==453 and r['HandleType']==(17 if case=='retained-native-owner' else 1) and mm[0]['CounterQpcEnd']<r['Timestamp']<cutoff);bad.remove(r)
                try:snapshots(bad,mm,pid)
                except ValueError as x:negative.append(dict(Case=case,Rejected=True,Reason=str(x)))
                else:raise ValueError('negative accepted '+case)
    a.write(args.output,dict(Verdict='SCOPED_NATIVE_USER_ATTRIBUTION_PASS',RawGuiNoGrowthPassed=graph['GuiCriterionPassed'],StableWindowVerified=bool(args.stable_window),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS',GraphVerificationSHA256=a.sha(root/'gui-verification.json'),NativeInputCalibrationSHA256=a.sha(root.parent/'FWM-GUI-ATTRIBUTION-20260913-I1/verification.json'),NativeInputContextCreatorThreadsVerified=args.threads,Configurations=observed,NegativeControls=negative))
    print(json.dumps(dict(Verdict='SCOPED_NATIVE_USER_ATTRIBUTION_PASS',RawGuiNoGrowthPassed=graph['GuiCriterionPassed'],SHA256=a.sha(args.output))))
if __name__=='__main__':main()
