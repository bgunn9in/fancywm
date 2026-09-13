"""Independent payload/range and native control witnesses; immutable receipts."""
import argparse,collections,copy,importlib.util,json,shutil,struct
from pathlib import Path
import clr_heap_model as c
spec=importlib.util.spec_from_file_location('t',Path(__file__).with_name('Verify-HeapTraceAdmission.py'));t=importlib.util.module_from_spec(spec);spec.loader.exec_module(t);h=t.h

def controls(rows,events,timeline,phases):
    pairs=[]
    for epoch in range(2):
        for index in range(24):
            allocation=next((r for r in rows if r.get('Epoch')==epoch and r.get('Index')==index and r['Kind']=='Allocate'),None)
            realloc=next((r for r in rows if r.get('Epoch')==epoch and r.get('Index')==index and r['Kind']=='Reallocate'),None)
            free=next((r for r in rows if r.get('Epoch')==epoch and r.get('Index')==index and r['Kind']=='Free'),None)
            h.require(all([allocation,realloc,free]),'missing native scenario');witness=[]
            for row,op in [(allocation,33),(realloc,34),(free,36)]:
                matched=[e for e in events if e['Opcode']==op and e['Address']==row['Pointer'] and e['Tid']==row['Tid'] and row['Before']<=e['Qpc']<=row['After'] and (op==36 or e['Bytes']==row['Size'])]
                h.require(len(matched)==1,'missing/ambiguous native operation witness');event=matched[0]
                if op!=36:
                    h.require(event['Bytes']==row['Size'],'allocation witness size')
                    ns=('WarmAllocator' if index<8 else 'ColdAllocator' if index<16 else 'OwnedHeapPlugin.Allocator') if op==33 else 'ColdAllocator';name='Allocate' if op==33 else 'Reallocate'
                    matches=[(pc,m) for pc in event['Stack'] for m in timeline.resolve(pc,event['Qpc']) if m['Fields']['MethodNamespace']==ns and m['Fields']['MethodName']==name]
                    h.require(len(matches)==1,'missing/ambiguous temporal managed stack caller');pc,method=matches[0]
                    witness.append(dict(Opcode=op,NativeQpc=event['Qpc'],Tid=event['Tid'],Address=event['Address'],Bytes=event['Bytes'],PC=pc,MethodSequence=method['Sequence'],ModuleSequence=method['ModuleSequence'],Caller=ns+'.'+name,ReJITID=method['Fields']['ReJITID'],Source=method['Source']))
                    if op==34:h.require(event['OldAddress']==allocation['Pointer'] and event['OldBytes']==allocation['Size'],'realloc successor identity')
                else:h.require(row['Success'],'native free failed')
            held=phases[epoch]['held'];resized=phases[epoch]['resized'];released=phases[epoch]['released']
            h.require(held.get(allocation['Pointer'])==allocation['Size'] and resized.get(realloc['Pointer'])==realloc['Size'],'missing held/resized native block')
            h.require(realloc['Pointer'] not in released,'retained native owner block')
            if index>=16:
                method=next(m for m in timeline.methods if m['Sequence']==witness[0]['MethodSequence']);module=next(m for m in timeline.modules if m['Sequence']==method['ModuleSequence'])
                collected=next(r for r in rows if r['Kind']=='CollectibleContext' and r['Epoch']==epoch)
                h.require(collected['Collected'] and method['End'] is not None and module['End'] is not None and witness[0]['NativeQpc']<method['End']<=module['End']<collected['Qpc']<realloc['Before'],'collectible code/module unload while native block retained')
                h.require(not timeline.resolve(witness[0]['PC'],module['End']+1),'stale code PC resolves after module unload')
            pairs.append(dict(Epoch=epoch,Index=index,Allocation=witness[0],Reallocation=witness[1],FreeQpc=matched[0]['Qpc']))
    return pairs

def main():
    p=argparse.ArgumentParser();p.add_argument('--root',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args();root=args.root;h.require(not args.output.exists(),'receipt exists')
    run=h.read(root/'run.json');h.require(run['Failure'] is None and run['OwnProcessesExited'] and run['OwnSessionsInactive'],'run failed/owned resources survive')
    for role in ['target','excluded','collector']:
        r=h.read(root/(role+'-exited.json'));h.require(r['ExitCode']==0 and r['Exited'] and not r['ForcedCleanup'],'own process failed')
    pid=h.read(root/'target-created.json')['Pid'];excluded=h.read(root/'excluded-created.json')['Pid'];summary=h.read(root/'clr/summary.json')
    h.require(summary['OwnPid']==pid and summary['DiscardedForeignEvents']==0 and summary['ProcessCompleted'] and summary['Failure'] is None and not summary['RuntimeEnableReturnedRestartedSession'],'CLR scope/drain failure')
    h.require(all(summary['stopped'][key]==0 for key in ['Code','EventsLost','LogBuffersLost','RealTimeBuffersLost']),'CLR events lost')
    rows,counts=c.decode(root/'clr',pid);h.require(len(rows)==summary['Records'],'CLR count mismatch');timeline=c.Timeline(rows)
    native=h.read(root/'native/summary.json');h.require(native['WritePassed'] and all(native[k]==0 for k in ['ProcessTraceCode','CloseTraceCode','EventsLost','BuffersLost']),'native loss')
    raw=t.raw_events(root/'native/raw.bin');events,stacks=t.allocation_events(raw,pid);h.require(len(raw)==native['Records'],'native count mismatch')
    independent={};data=(root/'stack-reader/stacks.bin').read_bytes();i=0
    while i<len(data):
        q,p,tid,n=struct.unpack_from('<QIII',data,i);i+=20;frames=struct.unpack_from('<'+'Q'*n,data,i);i+=n*8;h.require((p,tid,q) not in independent,'duplicate independent stack');independent[p,tid,q]=frames
    h.require(i==len(data) and stacks==independent,'independent full native stacks differ')
    observed=h.read(root/'target/observations.json');other=h.read(root/'excluded/observations.json')
    h.require(next(r for r in other if r['Kind']=='Process')['Pid']==excluded and sum(r['Kind']=='Allocate' for r in other)==48 and sum(r['Kind']=='Free' and r['Success'] for r in other)==48 and sum(r['Kind']=='CollectibleContext' and r['Collected'] for r in other)==2,'excluded process did not execute actual concurrent workload')
    phases={}
    for epoch in range(2):
        phases[epoch]={}
        for phase,label in [('held','held-after-code-unload'),('resized','resized'),('released','released')]:
            s=h.decode((root/'target'/f'{epoch}-{label}.bin').read_bytes(),pid);phases[epoch][phase]={e['Address']:e['Bytes'] for e in s['Entries'] if e['Flags']&4}
    pairs=controls(observed,events,timeline,phases);negative=[]
    for case in ['missing-scenario','missing-allocation','missing-free','retained-owner','missing-rundown','missing-method','missing-module-unload','stale-code-lifetime','wrong-code-address']:
        rr=copy.deepcopy(rows);ee=copy.deepcopy(events);oo=copy.deepcopy(observed);pp=copy.deepcopy(phases)
        if case=='missing-scenario':oo.remove(next(r for r in oo if r['Kind']=='Allocate'))
        if case in ['missing-allocation','missing-free']:
            q=pairs[0]['Allocation']['NativeQpc'] if case=='missing-allocation' else pairs[0]['FreeQpc'];ee.remove(next(e for e in ee if e['Qpc']==q and e['Opcode']==(33 if case=='missing-allocation' else 36)))
        if case=='retained-owner':pp[0]['released'][pairs[0]['Reallocation']['Address']]=pairs[0]['Reallocation']['Bytes']
        if case=='missing-rundown':rr=[r for r in rr if not(r['Provider']==c.RUNDOWN and r['Id']==146)]
        if case=='missing-method':rr=[r for r in rr if r['Sequence']!=pairs[0]['Allocation']['MethodSequence']]
        if case=='missing-module-unload':rr=[r for r in rr if r['EventName']!='Loader/ModuleUnload']
        if case=='stale-code-lifetime':rr=[r for r in rr if r['EventName'] not in ['Loader/ModuleUnload','Method/UnloadVerbose']]
        if case=='wrong-code-address':next(r for r in rr if r['Sequence']==pairs[0]['Allocation']['MethodSequence'])['Fields']['MethodStartAddress']+=0x100000000
        try:controls(oo,ee,c.Timeline(rr),pp)
        except (ValueError,StopIteration) as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    # Metadata IDs/addresses may be reused: generation is the load sequence.
    plugin=[m for m in timeline.methods if m['Fields']['MethodNamespace']=='OwnedHeapPlugin.Allocator'];h.require(len(plugin)==2 and all(m['End'] for m in plugin),'two plugin generations missing')
    h.write(args.output,dict(Verdict='SCOPED_CLR_NATIVE_CALLER_ADMISSION_PASS',Configuration=run['Configuration'],OwnPid=pid,ExcludedPid=excluded,ClrRecords=len(rows),IndependentPayloads=counts,NativeStacks=len(stacks),ControlPairs=pairs,NegativeControls=negative,PluginGenerations=plugin,ModuleIDReused=plugin[0]['Fields']['ModuleID']==plugin[1]['Fields']['ModuleID'],MethodAddressReused=plugin[0]['Fields']['MethodStartAddress']==plugin[1]['Fields']['MethodStartAddress'],ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LogicalOwnerClaim=False,WholeIdStatus='IN_PROGRESS',SourceSHA256={f.name:h.sha(f) for f in [Path(__file__),Path(c.__file__)]},Files=[dict(Path=str(f.relative_to(root)),Bytes=f.stat().st_size,SHA256=h.sha(f)) for f in sorted(root.rglob('*')) if f.is_file()]))
    print('CLR/native caller admission PASS',run['Configuration'],len(pairs),h.sha(args.output),flush=True)
if __name__=='__main__':main()
