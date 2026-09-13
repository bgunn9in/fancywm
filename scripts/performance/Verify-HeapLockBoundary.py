"""Compare native allocation ETW QPC with independently bracketed lock/call times."""
import argparse,copy,importlib.util,struct
from pathlib import Path
spec=importlib.util.spec_from_file_location('t',Path(__file__).with_name('Verify-HeapTraceAdmission.py'));t=importlib.util.module_from_spec(spec);spec.loader.exec_module(t);h=t.h
PAIR=struct.Struct('<6Q6I')
def check(pairs,events,pid):
    h.require(len(pairs)==24,'missing lock scenario');result=[]
    for i,row in enumerate(pairs):
        before,after,locked,unlock_begin,unlock_end,pointer,tid,size,completed,lockok,unlockok,freed=row
        h.require(pointer>0 and tid>0 and size==(32 if i<12 else 65536) and lockok==unlockok==freed==1,'native lock/free/identity')
        h.require(locked<before<unlock_begin<unlock_end and after>before,'native lock call intervals')
        matches=[e for e in events if e['Opcode']==33 and e['Pid']==pid and e['Tid']==tid and e['Address']==pointer and e['Bytes']==size and before<=e['Qpc']<=after]
        h.require(len(matches)==1,'missing/ambiguous lock allocation witness');event=matches[0]
        h.require((after<unlock_begin) if completed else (after>unlock_begin),'completion/lock boundary mismatch')
        result.append(dict(Index=i,Bytes=size,Pointer=pointer,Tid=tid,CallBegin=before,CallEnd=after,LockedQpc=locked,UnlockBegin=unlock_begin,UnlockEnd=unlock_end,AllocationEventQpc=event['Qpc'],CompletedWhileLocked=bool(completed),EventBeforeUnlock=event['Qpc']<unlock_begin,EventAfterUnlock=event['Qpc']>unlock_end,StackFrames=len(event['Stack'])))
    return result
def main():
    p=argparse.ArgumentParser();p.add_argument('--root',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();root=a.root;h.require(not a.output.exists(),'receipt exists')
    run=h.read(root/'run.json');exit=h.read(root/'exited.json');pid=exit['Pid'];h.require(run['Failure'] is None and run['OwnSessionInactive'] and exit['ExitCode']==0 and exit['Exited'] and not exit['ForcedCleanup'],'native run failed')
    native=h.read(root/'native/summary.json');h.require(native['WritePassed'] and all(native[k]==0 for k in ['ProcessTraceCode','CloseTraceCode','EventsLost','BuffersLost']),'native loss')
    events,_=t.allocation_events(t.raw_events(root/'native/raw.bin'),pid);pairs=list(PAIR.iter_unpack((root/'control/pairs.bin').read_bytes()));result=check(pairs,events,pid);negative=[]
    for case in ['missing-pair','wrong-pid','missing-allocation','wrong-lock-boundary','false-completed-before-unlock']:
        pp=copy.deepcopy(pairs);ee=copy.deepcopy(events);owner=pid
        if case=='missing-pair':pp.pop()
        if case=='wrong-pid':owner+=1
        if case=='missing-allocation':ee.remove(next(e for e in ee if e['Opcode']==33 and e['Qpc']==result[0]['AllocationEventQpc']))
        if case=='wrong-lock-boundary':r=list(pp[0]);r[3]=r[0]-1;pp[0]=tuple(r)
        if case=='false-completed-before-unlock':r=list(pp[0]);r[8]=1-r[8];pp[0]=tuple(r)
        try:check(pp,ee,owner)
        except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    blocked=[r for r in result if not r['CompletedWhileLocked']];early=[r for r in blocked if r['EventBeforeUnlock']]
    h.write(a.output,dict(Verdict='SCOPED_HEAP_LOCK_TIMESTAMP_CONTROL_VERIFIED',Pid=pid,Pairs=result,BlockedCalls=len(blocked),BlockedCallsWithEventBeforeUnlock=len(early),TimestampAtCompletionClaim=len(early)==0,NegativeControls=negative,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS',SourceSHA256=h.sha(Path(__file__)),Files=[dict(Path=str(f.relative_to(root)),Bytes=f.stat().st_size,SHA256=h.sha(f)) for f in sorted(root.rglob('*')) if f.is_file()]))
    print('Native lock control',len(result),'blocked',len(blocked),'ETW before unlock',len(early),h.sha(a.output))
if __name__=='__main__':main()
