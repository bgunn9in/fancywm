"""Strict offline baseline-seeded heap replay and native generation cohorts."""
import argparse,contextlib,copy,hashlib,importlib.util,json,os,shutil,struct,sys,traceback
from datetime import datetime,timezone
from pathlib import Path
from heap_cohort_model import CohortReplay
spec=importlib.util.spec_from_file_location('trace',Path(__file__).with_name('Verify-HeapGraphTrace.py'));v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v);h=v.h
ROOT=Path(__file__).resolve().parents[2];ART=ROOT/'artifacts/performance'
SOURCES=['Verify-HeapCohorts.py','heap_cohort_model.py','heap_trace_model.py','Verify-HeapGraphTrace.py','Verify-HeapTraceAdmission.py','Verify-NativeHeap.py']
WITNESS=struct.Struct('<8Q')
def busy(s):return {e['Address']:e['Bytes'] for e in s['Entries'] if e['Flags']&4}
def cuts_from_control(rr,snapshots):
    out={}
    for label,s in snapshots.items():
        matches=[r['Qpc'] for r in rr if r['Guid']==str(v.t.HEAP) and r['Opcode']==46 and r['Pid']==s['Pid'] and r['Tid']==s['Tid'] and struct.unpack('<Q',r['Payload'])[0]==s['Heap'] and s['Begin']<=r['Qpc']<=s['End']]
        h.require(bool(matches),'missing locked baseline witness');out[label]=max(matches)
    return out

def replay(rr,snapshots,cuts):
    h.require(len(snapshots)==4 and list(snapshots)==list(cuts),'missing/duplicate heap epoch')
    order=list(cuts);boundaries=[cuts[k] for k in order];h.require(boundaries==sorted(set(boundaries)),'snapshot order')
    first=snapshots[order[0]];heap=first['Heap'];pid=first['Pid'];model=CohortReplay(heap);phases={};witnesses=set();index=0;last=0;count=0
    h.require(all(s['Heap']==heap and s['Pid']==pid and s['Begin']<=cuts[k]<=s['End'] for k,s in snapshots.items()),'snapshot identity/time')
    def checkpoint(label):
        h.require(label in witnesses,'missing exact HeapWalk witness')
        if not model.seeded:model.seed(busy(snapshots[label]),cuts[label])
        else:model.validate(busy(snapshots[label]))
        phases[label]=model.current();print('Full snapshot matches',label,len(phases[label]),flush=True)
    for r in rr:
        count+=1;h.require(r['Qpc']>=last,'native temporal order');last=r['Qpc']
        while index<len(order) and r['Qpc']>boundaries[index]:checkpoint(order[index]);index+=1
        if index==len(order):break
        if r['Guid']==str(v.t.STACK):continue
        h.require(r['Guid']==str(v.t.HEAP),'unexpected provider')
        op=r['Opcode']
        if op==38:
            h.require(r['Version']==4 and len(r['Payload'])==72 and struct.unpack_from('<I',r['Payload'],36)[0]==pid,'foreign heap rundown');continue
        h.require(r['Pid']==pid,'foreign heap PID')
        if op==46:
            for label,qpc in cuts.items():
                if r['Qpc']==qpc:
                    h.require(r['Tid']==snapshots[label]['Tid'] and struct.unpack('<Q',r['Payload'])[0]==heap,'wrong native snapshot witness');witnesses.add(label)
        if op==35:model.destroy(struct.unpack('<Q',r['Payload'])[0],r['Qpc'])
        if op in [33,34,36]:model.apply(op,r['Tid'],r['Qpc'],v.allocation(r))
    while index<len(order):checkpoint(order[index]);index+=1
    h.require(list(phases)==order,'phase coverage')
    intervals=[dict(From=left,To=right,**model.compare(phases[left],phases[right],cuts[left],cuts[right])) for left,right in zip(order,order[1:])]
    baseline=[dict(To=right,**model.compare(phases[order[0]],phases[right],cuts[order[0]],cuts[right])) for right in order[1:]]
    return model,phases,dict(Pid=pid,Heap=heap,NativeRecordsThroughFinalSnapshot=count,Intervals=intervals,BaselineCohort=baseline,EveryBusyAddressAndSizeMatches=True,LogicalOwnerClaim=False)

def control(root,dest):
    proof=h.read(root/'verification.json');h.require(proof['Verdict']=='SCOPED_NATIVE_HEAP_TRACE_ADMISSION_PASS','native admission required');rr=list(v.records(root/'native-decoded/raw.bin'));pid=proof['Pid'];results=[];negative=[]
    for kind in ['private','default']:
        snapshots={k:h.decode((root/'control'/f'{kind}-{k}.bin').read_bytes(),pid) for k in ['baseline','held','resized','released']};cuts=cuts_from_control(rr,snapshots)
        model,phases,result=replay(iter(rr),snapshots,cuts)
        held=list(struct.unpack('<24Q',(root/'control'/f'{kind}-held-pointers.bin').read_bytes()));final=list(struct.unpack('<24Q',(root/'control'/f'{kind}-pointers.bin').read_bytes()))
        for old,new in zip(held,final):
            parent=phases['held'][old].generation;child=phases['resized'][new].generation
            h.require(model.successors[parent][0]==child and model.deaths[parent].reason=='HeapFree' and model.deaths[child].reason=='HeapFree','native moved-realloc lifetime control')
        result['NativeMovedReallocPairs']=24;results.append(dict(HeapKind=kind,**result))
        pair=next(x for x in proof['NativeControlPairs'] if x['HeapKind']==kind)
        for case in ['missing-allocation','missing-final-free','wrong-pid','wrong-size','missing-epoch','missing-baseline-block','extra-live-block']:
            bad=copy.deepcopy(rr);ss=copy.deepcopy(snapshots);cc=cuts.copy()
            if case in ['missing-allocation','missing-final-free']:
                qpc=pair['AllocQpc' if case=='missing-allocation' else 'FreeQpc'];bad.remove(next(r for r in bad if r['Guid']==str(v.t.HEAP) and r['Qpc']==qpc))
            if case=='wrong-pid':ss['held']['Pid']+=1
            if case=='wrong-size':next(e for e in ss['resized']['Entries'] if e['Address']==final[0])['Bytes']+=1
            if case=='missing-epoch':del ss['resized']
            if case=='missing-baseline-block':
                candidates=[e for e in ss['baseline']['Entries'] if e['Flags']&4]
                if not candidates:
                    # The exclusively owned empty private heap has no seeded
                    # busy block; this scenario applies to the default heap.
                    continue
                ss['baseline']['Entries'].remove(candidates[0])
            if case=='extra-live-block':ss['released']['Entries'].append(dict(Address=max(e['Address'] for e in ss['released']['Entries'])+4096,Bytes=123,Flags=4))
            try:replay(iter(bad),ss,cc)
            except ValueError as e:negative.append(dict(HeapKind=kind,Case=case,Rejected=True,Reason=str(e)))
            else:raise ValueError('negative accepted '+kind+'/'+case)
    return dict(Verdict='SCOPED_NATIVE_BASELINE_REPLAY_CONTROL_PASS',NativeMovedReallocPairs=48,Heaps=results,NegativeControls=negative,NativeAdmissionSHA256=h.sha(root/'verification.json'))

def graph(root,calibration,dest):
    cp=h.read(calibration/'verification.json');h.require(cp['Verdict']=='SCOPED_NATIVE_BASELINE_REPLAY_CONTROL_PASS','baseline replay calibration required')
    for item in cp['Sources']:h.require(h.sha(Path(__file__).parent/item['Name'])==item['SHA256'],'calibrated model/source changed')
    prior=h.read(root/'trace-verification.json');h.require(prior['Verdict']=='SCOPED_POST_ATTACH_NATIVE_HEAP_ATTRIBUTION_PASS','strict native trace required')
    result=[]
    for c in prior['Configurations']:
        label=c['Configuration'];run=root/'runs'/(label+'-graceful');folder=dest/label;folder.mkdir()
        snapshots={x:h.decode((run/'heap'/(x+'.bin')).read_bytes(),c['Pid']) for x in ['0','1','2','shutdown']};cuts={e['Label']:e['SnapshotQpc'] for e in c['Epochs']}
        model,phases,summary=replay(v.records(run/'heap-decoded/native/raw.bin'),snapshots,cuts)
        # Export exact identities for independent receipt inspection. Seeded
        # baseline blocks explicitly have no allocation stack/TID witness.
        for phase,state in phases.items():
            with (folder/(phase+'-generations.bin')).open('xb') as f:
                for address,b in sorted(state.items()):
                    birth=model.births[b.generation];f.write(WITNESS.pack(address,b.size,b.generation,birth.qpc,birth.tid,b.stack[1] if b.stack else 0,int(birth.seeded),summary['Heap']))
        observed_generations={b.generation for state in phases.values() for b in state.values()}
        h.write(folder/'generation-dispositions.json',dict(Deaths=[dict(Generation=g,**d._asdict()) for g,d in sorted(model.deaths.items()) if g in observed_generations],
            ReallocSuccessors=[dict(Parent=g,Child=child,Qpc=qpc) for g,(child,qpc) in sorted(model.successors.items())]))
        result.append(dict(Configuration=label,**summary));print(label,'full baseline replay passed',flush=True)
    return dict(Verdict='SCOPED_COMPLETE_DEFAULT_HEAP_BASELINE_REPLAY_PASS',Configurations=result,CalibrationSHA256=h.sha(calibration/'verification.json'),NativeTraceVerificationSHA256=h.sha(root/'trace-verification.json'),
        LeakFreedomClaim=False,OriginalAllocationStackForSeededBlocks=False,Limits=['Complete default-heap busy inventory at existing native snapshots only','Seeded blocks have unknown pre-baseline allocation stacks/owners','Native generation retirement and reallocation successors do not prove logical owner cleanup','Other heaps/direct VirtualAlloc/GPU and physical presentation excluded'])

def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--mode',choices=['control','graph'],required=True);p.add_argument('--input',type=Path,required=True);p.add_argument('--calibration',type=Path);a=p.parse_args();dest=ART/a.id
    h.require(not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id,'output exists/wrong date');dest.mkdir();(dest/'source').mkdir()
    started=datetime.now(timezone.utc).isoformat();sources=[]
    for name in SOURCES:
        path=Path(__file__).parent/name;shutil.copyfile(path,dest/'source'/name);sources.append(dict(Name=name,SHA256=h.sha(path)))
    code=0
    with (dest/'raw.log').open('x',encoding='utf-8') as log,contextlib.redirect_stdout(log),contextlib.redirect_stderr(log):
        try:
            result=control(a.input.resolve(),dest) if a.mode=='control' else graph(a.input.resolve(),a.calibration.resolve(),dest)
            result.update(Sources=sources,NewNativeProcesses=0,NewEtls=0,NewTrxRuns=0,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS')
            h.write(dest/'verification.json',result)
        except Exception as e:
            traceback.print_exc();h.write(dest/'failure.json',dict(Error=str(e),Type=type(e).__name__,ClaimedAsPass=False));code=1
    h.write(dest/'command.json',dict(Command=[sys.executable,*sys.argv],Pid=os.getpid(),StartedUtc=started,EndedUtc=datetime.now(timezone.utc).isoformat(),ExitCode=code,RawLogSHA256=h.sha(dest/'raw.log'),OfflineOnly=True))
    print(json.dumps(dict(Output=str(dest),ExitCode=code,ReceiptSHA256=h.sha(dest/'verification.json') if code==0 else None)));sys.exit(code)
if __name__=='__main__':main()
