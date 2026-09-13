"""Full native graph replay plus temporally valid CLR callers for live cohorts."""
import argparse,collections,contextlib,copy,importlib.util,json,os,shutil,struct,subprocess,sys,traceback
from datetime import datetime,timezone
from pathlib import Path
import clr_heap_model as c
spec=importlib.util.spec_from_file_location('co',Path(__file__).with_name('Verify-HeapCohorts.py'));co=importlib.util.module_from_spec(spec);spec.loader.exec_module(co);h=co.h;ROOT=Path.cwd();ART=ROOT/'artifacts/performance'
def now():return datetime.now(timezone.utc).isoformat()
def command(dest,name,cmd):
    cmd=list(map(str,cmd));started=now()
    with (dest/(name+'.stdout')).open('xb') as out,(dest/(name+'.stderr')).open('xb') as err:
        proc=subprocess.Popen(cmd,stdout=out,stderr=err,creationflags=0x08000000);code=proc.wait(timeout=600)
    h.write(dest/(name+'-command.json'),dict(Command=cmd,Pid=proc.pid,StartedUtc=started,EndedUtc=now(),ExitCode=code,Exited=True));h.require(code==0,'offline command failed '+name)
def analyze(root,dest,config,event_callers_only=False,reuse=None):
    folder=dest/config;folder.mkdir();run=root/'runs'/(config+'-graceful');pid=h.read(run/'summary.json')['Pid']
    decoder=ART/'FWM-NATIVE-HEAP-20260913-T3/HeapDecode.exe';reader=ART/'FWM-NATIVE-HEAP-20260913-T5/binaries/HeapStackReader.exe'
    decoded=folder
    if reuse is not None and (reuse/config/'stack-reader/summary.json').exists():
        decoded=reuse/config
        h.require(h.read(decoded/'native-command.json')['Command'][1]==str(run/'heap.etl'),'reused decoder ETL identity')
        h.write(folder/'decode-reuse.json',dict(Original=str(decoded),NewDecode=False,Files=[dict(Path=str(f),Bytes=f.stat().st_size,SHA256=h.sha(f)) for name in ['native','stack-reader'] for f in sorted((decoded/name).iterdir()) if f.is_file()]))
    else:
        command(folder,'native',[decoder,run/'heap.etl',folder/'native']);command(folder,'stack',[reader,run/'heap.etl',folder/'stack-reader',pid])
    metadata=h.read(decoded/'native/summary.json');h.require(metadata['WritePassed'] and all(metadata[k]==0 for k in ['ProcessTraceCode','CloseTraceCode','EventsLost','BuffersLost']),'native loss')
    reader_summary=h.read(decoded/'stack-reader/summary.json');h.require(reader_summary['Completed'] and reader_summary['EventsLost']==0 and reader_summary['OwnPid']==pid,'independent stack decoder loss')
    summary=h.read(run/'clr/summary.json');h.require(summary['OwnPid']==pid and summary['DiscardedForeignEvents']==0 and summary['ProcessCompleted'] and summary['Failure'] is None and not summary['RuntimeEnableReturnedRestartedSession'],'CLR collection failed')
    h.require(all(summary['stopped'][k]==0 for k in ['Code','EventsLost','LogBuffersLost','RealTimeBuffersLost']),'CLR events lost')
    created=h.read(run/'clr-created.json');stopped=h.read(run/'clr-stopped.json');h.require(created['Pid']==pid and created['CollectorPid']==stopped['CollectorPid'] and stopped['Exited'] and stopped['ExitCode']==0 and created['StartedBeforeProductionStartup'],'CLR process boundary')
    h.require(h.read(root/'validation'/(config+'-clr-cleanup.json'))['Inactive'] and h.read(root/'validation'/(config+'-heap-cleanup.json'))['Inactive'],'own sessions active')
    rows,independent=c.decode(run/'clr',pid);h.require(len(rows)==summary['Records'],'CLR count');timeline=c.Timeline(rows)
    snapshots={label:h.decode((run/'heap'/(label+'.bin')).read_bytes(),pid) for label in ['0','1','2','shutdown']};cuts={};raw=decoded/'native/raw.bin';total=0;opcodes=collections.Counter()
    for r in co.v.records(raw):
        total+=1;opcodes[r['Guid'],r['Opcode']]+=1
        if r['Guid']==str(co.v.t.HEAP) and r['Opcode']==46:
            for label,s in snapshots.items():
                if r['Pid']==pid and r['Tid']==s['Tid'] and s['Begin']<=r['Qpc']<=s['End'] and struct.unpack('<Q',r['Payload'])[0]==s['Heap']:cuts[label]=r['Qpc']
    h.require(total==metadata['Records'] and list(cuts)==list(snapshots),'native count/locked snapshot witnesses');h.require(timeline.ready<min(cuts.values()),'CLR rundown completed after heap baseline')
    if event_callers_only:
        # HeapLock calibration demonstrates concurrent 32-byte allocation on
        # this OS. Do not seed a purported atomic snapshot or weaken replay's
        # equality gate. Attribute only the independently recorded event stream.
        model=co.CohortReplay(snapshots['0']['Heap']);phases={};index=0;bounds=list(cuts.items());last=0
        for r in co.v.records(raw):
            h.require(r['Qpc']>=last,'native temporal order');last=r['Qpc']
            while index<len(bounds) and r['Qpc']>bounds[index][1]:phases[bounds[index][0]]=model.current();index+=1
            if r['Guid']==str(co.v.t.STACK):continue
            h.require(r['Guid']==str(co.v.t.HEAP),'unexpected native provider');op=r['Opcode']
            if op==38:
                h.require(r['Version']==4 and len(r['Payload'])==72 and struct.unpack_from('<I',r['Payload'],36)[0]==pid,'foreign heap rundown');continue
            h.require(r['Pid']==pid,'foreign native allocation')
            if op==35:model.destroy(struct.unpack('<Q',r['Payload'])[0],r['Qpc'])
            if op in [33,34,36]:model.apply(op,r['Tid'],r['Qpc'],co.v.allocation(r))
        while index<len(bounds):phases[bounds[index][0]]=model.current();index+=1
        comparisons=[]
        for label,state in phases.items():
            busy=co.busy(snapshots[label]);missing=[dict(Address=address,Bytes=b.size,NativeSnapshotBytes=busy.get(address)) for address,b in state.items() if busy.get(address)!=b.size]
            comparisons.append(dict(Phase=label,StreamLiveBlocks=len(state),SnapshotBusyBlocks=len(busy),TracedSubsetMatches=not missing,Conflicts=missing,CompleteAtomicSnapshotClaim=False))
        replayed=dict(EventStreamOnly=True,SnapshotComparisons=comparisons,UnknownPreAttachFrees=model.model.unknown_frees,CompleteDefaultHeapSnapshotsMatch=False,LogicalOwnerClaim=False)
    else:model,phases,replayed=co.replay(co.v.records(raw),snapshots,cuts)
    h.write(folder/'cohort-replay.json',replayed)
    wanted={b.stack for phase in phases.values() for b in phase.values() if b.stack};found={};stack_count=0;frame_count=0;operation_keys=set();stack_keys=set();module_by_seq={m['Sequence']:m for m in timeline.modules}
    with (decoded/'stack-reader/stacks.bin').open('rb') as other:
        for r in co.v.records(raw):
            if r['Guid']!=str(co.v.t.STACK):
                if r['Opcode'] in [33,34]:
                    key=(r['Tid'],r['Qpc']);h.require(key not in operation_keys,'duplicate native stack operation');operation_keys.add(key)
                continue
            p=r['Payload'];q,owner,tid=struct.unpack_from('<QII',p);h.require(owner==pid and len(p)>=32 and (len(p)-16)%8==0,'foreign/invalid native stack')
            n=(len(p)-16)//8;header=other.read(20);h.require(header==struct.pack('<QIII',q,owner,tid,n) and other.read(n*8)==p[16:],'independent full native stack payload/order differs')
            key=(tid,q);h.require(key not in stack_keys,'duplicate stack key');stack_keys.add(key);stack_count+=1;frame_count+=n
            if key in wanted:
                frames=struct.unpack_from('<'+'Q'*n,p,16);mapped=[];ambiguous=[]
                for index,pc in enumerate(frames):
                    matches=timeline.resolve(pc,q)
                    if len(matches)==1:
                        m=matches[0];f=m['Fields'];module=module_by_seq[m['ModuleSequence']]
                        mapped.append(dict(Frame=index,PC=pc,MethodSequence=m['Sequence'],ModuleSequence=m['ModuleSequence'],MethodID=f['MethodID'],ModuleID=f['ModuleID'],ReJITID=f['ReJITID'],CodeStart=f['MethodStartAddress'],CodeSize=f['MethodSize'],Begin=m['Begin'],End=m['End'],Name=f['MethodNamespace']+'.'+f['MethodName'],Signature=f['MethodSignature'],ModulePath=module['Fields']['ModuleILPath']))
                    elif len(matches)>1:ambiguous.append(dict(Frame=index,PC=pc,MethodSequences=[m['Sequence'] for m in matches]))
                found[key]=dict(Qpc=q,Tid=tid,Frames=list(frames),ManagedCallers=mapped,Ambiguous=ambiguous)
        h.require(not other.read(1),'extra independent stack data')
    h.require(operation_keys==stack_keys and set(found)==wanted,'missing/unassociated native stack');h.require(stack_count==reader_summary['StackEvents']==metadata['StackRecords'] and frame_count==reader_summary['StackFrames'],'full native stack totals')
    with (folder/'stack-callers.jsonl').open('x',encoding='utf-8') as out:
        for key,value in sorted(found.items()):out.write(json.dumps(value)+'\n')
    h.write(folder/'clr-timeline.json',dict(ReadyQpc=timeline.ready,Methods=timeline.methods,Modules=timeline.modules,UnknownMethodUnloads=timeline.unknown_unloads))
    phase_results=[]
    with (folder/'phase-callers.jsonl').open('x',encoding='utf-8') as out:
        for label,state in phases.items():
            categories=collections.Counter();sizes=collections.Counter();callers=collections.Counter();caller_bytes=collections.Counter();fancy=0
            for address,b in sorted(state.items()):
                birth=model.births[b.generation];w=found[b.stack] if b.stack else None
                category='SeededUnknown' if w is None else 'TracedManagedCaller' if w['ManagedCallers'] else 'TracedNoClrCaller';categories[category]+=1;sizes[category]+=b.size
                nearest=w['ManagedCallers'][0]['Name'] if w and w['ManagedCallers'] else None
                if nearest:callers[nearest]+=1;caller_bytes[nearest]+=b.size
                if w and any(x['Name'].startswith(('FancyWM.','WinMan.')) for x in w['ManagedCallers']):fancy+=1
                out.write(json.dumps(dict(Phase=label,Address=address,Bytes=b.size,Generation=b.generation,BirthQpc=birth.qpc,BirthTid=birth.tid,Seeded=birth.seeded,StackQpc=b.stack[1] if b.stack else None,StackTid=b.stack[0] if b.stack else None,Category=category,NearestManagedCaller=nearest,EventStreamOnly=event_callers_only,LogicalOwnerClaim=False))+'\n')
            phase_results.append(dict(Phase=label,SnapshotQpc=cuts[label],Blocks=len(state),Bytes=sum(b.size for b in state.values()),Categories=dict(categories),CategoryBytes=dict(sizes),BlocksWithFancyWmOrWinManFrame=fancy,TopManagedCallers=[dict(Name=name,Blocks=count,Bytes=caller_bytes[name]) for name,count in callers.most_common(30)]))
    h.require(any(p['BlocksWithFancyWmOrWinManFrame']>0 for p in phase_results),'no observed application managed caller')
    return dict(Configuration=config,Pid=pid,ClrRecords=len(rows),IndependentClrPayloads=independent,MethodGenerations=len(timeline.methods),ModuleGenerations=len(timeline.modules),NativeRecords=total,NativeStacks=stack_count,NativeStackFrames=frame_count,SelectedLiveStackWitnesses=len(found),Phases=phase_results,CompleteDefaultHeapSnapshotsMatch=not event_callers_only,EventStreamOnly=event_callers_only,DecoderSHA256=h.sha(decoder),StackReaderSHA256=h.sha(reader),EtlSHA256=h.sha(run/'heap.etl'),LogicalOwnerClaim=False,PerformanceClaim=False)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--graph',type=Path,required=True);p.add_argument('--event-callers-only',action='store_true');p.add_argument('--reuse-decodes',type=Path);a=p.parse_args();dest=ART/a.id;h.require(not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id,'output exists/wrong date');dest.mkdir();(dest/'source').mkdir();root=a.graph.resolve();started=now();code=0
    files=['Analyze-ClrHeapGraph.py','clr_heap_model.py','Verify-NativeHeapGraph.py','Verify-HeapCohorts.py','heap_cohort_model.py','heap_trace_model.py','Verify-HeapGraphTrace.py','Verify-HeapTraceAdmission.py','Verify-NativeHeap.py','Verify-FullGraphLifetime.py']
    for name in files:shutil.copyfile(Path(__file__).with_name(name),dest/'source'/name)
    with (dest/'raw.log').open('x',encoding='utf-8') as log,contextlib.redirect_stdout(log),contextlib.redirect_stderr(log):
        try:
            command(dest,'graph-verifier',[sys.executable,'scripts/performance/Verify-NativeHeapGraph.py','--snapshot',root,'--output',root/(a.id+'-graph-verification.json'),'--quiescence','0','--stable-window','0'])
            calibration=ART/'FWM-CLR-HEAP-20260913-V1/verification.json';h.require(h.read(calibration)['Verdict']=='SCOPED_CLR_NATIVE_CALLER_SOURCE_PASS','source calibration absent')
            for r in h.read(calibration)['Runs']:
                proof=Path(r['Command'][-1]);h.require(h.sha(proof)==r['ReceiptSHA256'] and h.read(proof)['SourceSHA256']['clr_heap_model.py']==h.sha(Path(c.__file__)),'calibrated CLR model changed')
            if a.event_callers_only:
                proof=ART/'FWM-CLR-HEAP-20260913-L1/verification.json';boundary=h.read(proof);h.require(boundary['Verdict']=='SCOPED_HEAP_LOCK_TIMESTAMP_CONTROL_VERIFIED' and any(r['CompletedWhileLocked'] for r in boundary['Pairs']),'native boundary evidence absent')
                h.write(dest/'boundary-scope.json',dict(NativeControlSHA256=h.sha(proof),AtomicHeapSnapshotClaim=False,OriginalStrictReplayGateUnchanged=True,NewNativeGraphRuns=0))
            results=[analyze(root,dest,config,a.event_callers_only,a.reuse_decodes.resolve() if a.reuse_decodes else None) for config in ['Debug','Release']]
            h.write(dest/'verification.json',dict(Verdict='SCOPED_FULLGRAPH_CLR_NATIVE_CALLERS_VERIFIED',Configurations=results,Graph=str(root),CalibrationSHA256=h.sha(calibration),WholeIdStatus='IN_PROGRESS',ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,LogicalOwnerClaim=False,NativeHeapLeakFreedomClaim=False,NewNativeRunsInThisAnalysis=0,Sources={f.name:h.sha(f) for f in (dest/'source').iterdir()}))
        except Exception as e:traceback.print_exc();h.write(dest/'failure.json',dict(Error=str(e),Type=type(e).__name__,ClaimedAsPass=False));code=1
    h.write(dest/'command.json',dict(Command=[sys.executable,*sys.argv],Pid=os.getpid(),StartedUtc=started,EndedUtc=now(),ExitCode=code,OfflineOnly=True,RawLogSHA256=h.sha(dest/'raw.log')))
    print(dest,'ExitCode',code,flush=True);sys.exit(code)
if __name__=='__main__':main()
