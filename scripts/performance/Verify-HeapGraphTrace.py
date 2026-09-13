"""Attribute observed post-attach heap blocks to exact native allocation stacks.

Pre-attach blocks stay unknown. Module/RVA annotations are epoch observations,
not continuous image lifetimes or reconstructed managed allocation owners.
"""
import argparse, collections, copy, csv, importlib.util, json, struct, uuid, xml.etree.ElementTree as ET
from pathlib import Path
from heap_trace_model import Replay
spec=importlib.util.spec_from_file_location('t',Path(__file__).with_name('Verify-HeapTraceAdmission.py'));t=importlib.util.module_from_spec(spec);spec.loader.exec_module(t);h=t.h
def records(path):
    with path.open('rb') as f:
        while header:=f.read(t.HEADER.size):
            h.require(len(header)==t.HEADER.size,'truncated native header')
            guid,pid,tid,qpc,event,version,opcode,flags,size=t.HEADER.unpack(header);payload=f.read(size);h.require(len(payload)==size,'truncated native event')
            yield dict(Guid=str(uuid.UUID(bytes_le=guid)),Pid=pid,Tid=tid,Qpc=qpc,Version=version,Opcode=opcode,Payload=payload)
def allocation(r):
    p=r['Payload'];op=r['Opcode'];h.require(r['Version']==2,'unsupported native allocation version')
    if op==33:
        h.require(len(p)==28,'alloc size');heap,size,address,source=struct.unpack('<QQQI',p);fields=(heap,address,size)
    elif op==34:
        h.require(len(p)==44,'realloc size');heap,address,old,size,oldsize,source=struct.unpack('<QQQQQI',p);fields=(heap,address,old,size,oldsize)
    else:
        h.require(len(p)==20,'free size');heap,address,source=struct.unpack('<QQI',p);fields=(heap,address)
    return fields
def canonical(op,tid,fields):return struct.pack('<II'+'Q'*len(fields),op,tid,*fields)
def decrement(counter,key):
    h.require(counter.get(key,0)>0,'independent decoder field/record mismatch')
    counter[key]-=1
    if not counter[key]:del counter[key]
def validate_snapshot(known,busy,stacks):
    h.require(all(address in busy and busy[address]==v[0] for address,v in known.items()),'traced live allocation does not match locked default-heap snapshot')
    h.require(all(value[1] in stacks for value in known.values()),'surviving allocation stack missing')
def stack_reader(path,stack_payloads,pid):
    remaining=stack_payloads.copy();count=0;frames=0
    with path.open('rb') as f:
        while head:=f.read(20):
            h.require(len(head)==20,'independent stack header');qpc,owner,tid,size=struct.unpack('<qIII',head);h.require(owner==pid and qpc>0 and tid>0 and 2<=size<=8190,'independent stack identity')
            data=f.read(size*8);h.require(len(data)==size*8,'independent stack truncation');decrement(remaining,head[:16]+data);count+=1;frames+=size
    h.require(not remaining,'missing independent full stack');return count,frames
def independent(folder,wanted,stack_payloads,stacks,pid):
    reader=folder.parent/'traceevent-stacks';reader_summary=h.read(reader/'summary.json')
    command=h.read(folder.parent/'traceevent-reader-command.json');tool=Path(command['Command'][0]).parent.parent
    h.require(command['ExitCode']==0 and command['TraceEventPackageVersion']=='3.2.6' and h.sha(tool/'provenance.json')==command['ToolProvenanceSHA256'],'independent reader execution provenance')
    provenance=h.read(tool/'provenance.json')
    for group in ['Sources','Binaries','Packages']:
        for entry in provenance[group]:h.require(h.sha(tool/('packages' if group=='Packages' else '')/entry['Path'])==entry['SHA256'],'independent reader source/package/binary changed')
    h.require(reader_summary['Completed'] and reader_summary['EventsLost']==0 and reader_summary['OwnPid']==pid,'independent managed parser')
    stack_count,frame_count=stack_reader(reader/'stacks.bin',stack_payloads,pid)
    h.require((stack_count,frame_count)==(reader_summary['StackEvents'],reader_summary['StackFrames']),'independent stack summary')
    print('Full TraceEvent/raw stack payloads verified',stack_count,frame_count,flush=True)
    remaining=wanted.copy();remaining_stacks=stack_payloads.copy();count=0
    xml_full=0;xml_prefix=0
    for _,element in ET.iterparse(folder/'events.xml',events=['end']):
        if element.tag!='{'+t.NS['e']+'}Event':continue
        opcode=int(element.findtext('e:System/e:Opcode','0',t.NS));guid=element.findtext('t:ExtendedTracingInfo/t:EventGuid','',t.NS).strip('{}').lower()
        if guid==str(t.STACK):
            raw=element.findtext('e:ProcessingErrorData/e:EventPayload','',t.NS)
            if raw:decrement(remaining_stacks,bytes.fromhex(raw));xml_full+=1
            else:
                fields={x.attrib['Name']:int(x.text.strip(),0) for x in element.findall('e:EventData/e:Data',t.NS)}
                key=(fields['StackThread'],fields['EventTimeStamp']);h.require(fields['StackProcess']==pid and key in stacks,'independent XML stack identity')
                pcs=[fields['Stack'+str(i)] for i in range(1,33)];h.require(set(fields)=={'EventTimeStamp','StackProcess','StackThread'}|{'Stack'+str(i) for i in range(1,33)} and tuple(pcs)==stacks[key][:32],'independent XML fixed-32 prefix')
                expected=struct.pack('<QII'+'Q'*len(stacks[key]),key[1],pid,key[0],*stacks[key]);decrement(remaining_stacks,expected);xml_prefix+=1
        if guid==str(t.HEAP) and opcode in [33,34,36]:
            ex=element.find('e:System/e:Execution',t.NS);h.require(int(ex.attrib['ProcessID'])==pid,'foreign XML allocation')
            data={x.attrib['Name']:int(x.text.strip(),0) for x in element.findall('e:EventData/e:Data',t.NS)}
            names=['HeapHandle','AllocAddress','AllocSize'] if opcode==33 else ['HeapHandle','NewAllocAddress','OldAllocAddress','NewAllocSize','OldAllocSize'] if opcode==34 else ['HeapHandle','FreeAddress']
            decrement(remaining,canonical(opcode,int(ex.attrib['ThreadID']),tuple(data[n] for n in names)));count+=1
        element.clear()
    h.require(not remaining and not remaining_stacks,'missing independent XML events/stacks')
    print('XML allocation/stack fields verified',count,flush=True)
    remaining=wanted.copy();csvcount=0
    with (folder/'events.csv').open(encoding='utf-8-sig',newline='') as f:
        for row in csv.reader(f,skipinitialspace=True):
            row=[x.strip() for x in row]
            if not row or row[0] not in ['HeapAlloc','HeapRealloc','HeapFree'] or row[1]=='TimeStamp':continue
            op={'HeapAlloc':33,'HeapRealloc':34,'HeapFree':36}[row[0]];indices=[4,5,6] if op==33 else [4,5,6,7,8] if op==34 else [4,5]
            decrement(remaining,canonical(op,int(row[3]),tuple(int(row[i],0) for i in indices)));csvcount+=1
    h.require(not remaining and csvcount==count,'missing independent CSV events')
    print('CSV allocation fields verified',csvcount,flush=True)
    return dict(AllocationRows=count,FullStackRecords=stack_count,FullStackFrames=frame_count,XmlRawFullStacks=xml_full,XmlFixed32PrefixStacks=xml_prefix,XmlFullTailClaim=False,CsvStackTimestampClaim=False)
def analyze(root,config):
    run=root/'runs'/(config+'-graceful');folder=run/'heap-decoded';pid=h.read(run/'summary.json')['Pid']
    summary=h.read(folder/'native/summary.json');h.require(summary['ProcessTraceCode']==summary['CloseTraceCode']==summary['EventsLost']==summary['BuffersLost']==0 and summary['WritePassed'],'native decoding/loss')
    stats=(folder/'stats.txt').read_text();h.require('Total # Lost Buffers : 0' in stats and 'Total # Lost Events  : 0' in stats,'independent trace loss')
    decoder=h.read(folder/'decode-receipt.json');h.require(h.sha(run/'heap.etl')==decoder['EtlSHA256'],'ETL provenance')
    snapshots={label:h.decode((run/'heap'/(label+'.bin')).read_bytes(),pid) for label in ['0','1','2','shutdown']}
    # The last native HeapWalk call is still inside the successfully acquired
    # default heap lock. Match only blocks whose allocations are traced.
    cuts={label:[] for label in snapshots};count=0;last=0
    for r in records(folder/'native/raw.bin'):
        count+=1;h.require(r['Qpc']>=last,'native event temporal order');last=r['Qpc']
        if r['Guid']==str(t.HEAP) and r['Opcode']==46:
            for label,s in snapshots.items():
                if r['Pid']==pid and r['Tid']==s['Tid'] and s['Begin']<=r['Qpc']<=s['End'] and struct.unpack('<Q',r['Payload'])[0]==s['Heap']:cuts[label].append(r['Qpc'])
    h.require(count==summary['Records'] and all(cuts.values()),'missing native snapshot HeapWalk witnesses')
    print(config,'native ordering and heap-lock snapshot witnesses',count,flush=True)
    boundaries=sorted((max(values),label) for label,values in cuts.items());nextcut=0;phases={};model=Replay();live=model.live;stacks={};stack_keys=set();stack_payloads=collections.Counter();wanted=collections.Counter();counts=collections.Counter()
    for r in records(folder/'native/raw.bin'):
        while nextcut<len(boundaries) and r['Qpc']>boundaries[nextcut][0]:
            phases[boundaries[nextcut][1]]=live.copy();nextcut+=1
        if r['Guid']==str(t.STACK):
            p=r['Payload'];h.require(len(p)>=24 and (len(p)-16)%8==0,'stack payload');qpc,owner,tid=struct.unpack_from('<QII',p);h.require(owner==pid,'foreign native stack')
            key=(tid,qpc);h.require(key not in stacks,'duplicate native stack association');stacks[key]=struct.unpack_from('<'+'Q'*((len(p)-16)//8),p,16);stack_payloads[p]+=1;continue
        h.require(r['Guid']==str(t.HEAP),'unknown persisted provider');op=r['Opcode'];counts[op]+=1
        if op==38:
            h.require(r['Version']==4 and len(r['Payload'])==72 and struct.unpack_from('<I',r['Payload'],36)[0]==pid,'foreign native heap rundown');continue
        h.require(r['Pid']==pid,'foreign native heap event')
        if op==35:
            model.destroy(struct.unpack('<Q',r['Payload'])[0]);continue
        if op not in [33,34,36]:continue
        fields=allocation(r);wanted[canonical(op,r['Tid'],fields)]+=1
        model.apply(op,r['Tid'],r['Qpc'],fields)
        if op in [33,34]:
            stack_key=(r['Tid'],r['Qpc']);h.require(stack_key not in stack_keys,'duplicate allocation stack identity');stack_keys.add(stack_key)
    while nextcut<len(boundaries):phases[boundaries[nextcut][1]]=live.copy();nextcut+=1
    # Every allocation/reallocation stack is required, not only surviving ones.
    h.require(stacks.keys()==stack_keys and len(stacks)==counts[33]+counts[34] and all(len(v)>=2 and min(v)>0 for v in stacks.values()),'allocation stack coverage')
    print(config,'native allocation/free replay and stack identities',len(stacks),flush=True)
    result=[];negative=[]
    for label,s in snapshots.items():
        busy={e['Address']:e['Bytes'] for e in s['Entries'] if e['Flags']&4};known={address:value for (heap,address),value in phases[label].items() if heap==s['Heap']}
        validate_snapshot(known,busy,stacks)
        if label=='1':
            h.require(bool(known),'no traced live control candidate')
            address=next(iter(known));value=known[address]
            for case in ['surviving-unobserved-block','wrong-live-size','missing-survivor-stack']:
                bad=known.copy();ss=stacks
                if case=='surviving-unobserved-block':bad[max(busy)+4096]=value
                if case=='wrong-live-size':bad[address]=(value[0]+1,*value[1:])
                if case=='missing-survivor-stack':ss=stacks.copy();del ss[value[1]]
                try:validate_snapshot(bad,busy,ss)
                except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
                else:raise ValueError('negative accepted '+case)
        groups=collections.defaultdict(lambda:[0,0]);module_data=h.read(run/('heap-modules-'+label+'.json'));modules=module_data['Modules'];h.require(module_data['Pid']==pid,'module observer PID')
        for address,value in known.items():
            size,key,gen=value[:3]
            h.require(key in stacks,'survivor stack missing');stack=stacks[key];groups[stack][0]+=1;groups[stack][1]+=size
        stackgroups=[]
        for stack,(blocks,size) in sorted(groups.items(),key=lambda p:-p[1][1]):
            annotations=[]
            for pc in stack:
                candidates=[m for m in modules if m['Base']<=pc<m['Base']+m['Bytes']]
                annotations.append(dict(Pc=pc,Module=Path(candidates[0]['Path']).name,Rva=pc-candidates[0]['Base']) if len(candidates)==1 else dict(Pc=pc,Unresolved=True))
            stackgroups.append(dict(Blocks=blocks,Bytes=size,Stack=annotations))
        result.append(dict(Label=label,SnapshotQpc=max(cuts[label]),BusyBlocks=s['BusyBlocks'],BusyBytes=s['BusyBytes'],KnownTracedDefaultHeapBlocks=len(known),KnownTracedDefaultHeapBytes=sum(v[0] for v in known.values()),UnknownPreAttachOrUntracedBusyBlocks=len(busy)-len(known),NativeStackGroups=stackgroups,
            AllKnownBlocksMatchNativeSnapshot=True,PointerGenerationsVerified=True,ModuleAnnotationsAreEpochObservations=True,ManagedOwnerAttribution=False))
    verified=independent(folder,wanted,stack_payloads,stacks,pid)
    return dict(Configuration=config,Pid=pid,NativeRecords=count,IndependentDecoderVerification=verified,NativeStackEvents=len(stacks),HeapOpcodeCounts=dict(counts),UnknownPreAttachFrees=model.unknown_frees,KnownBlocksFreedByHeapDestroy=model.destroyed,NestedReallocSummaries=len(model.aliases),OriginalAddressesAlreadyReused=sum(x['OriginalAddressAlreadyReused'] for x in model.aliases),Epochs=result,NegativeControls=negative)
def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();h.require(not a.output.exists(),'receipt exists');root=a.snapshot
    graph=h.read(root/'verification.json');h.require(graph['Verdict']=='SCOPED_NATIVE_HEAP_OBSERVATIONS_VERIFIED','strict graph receipt required')
    model_proof=root.parent/'FWM-NATIVE-HEAP-20260913-E3/realloc-model-verification.json'
    h.require(h.read(model_proof)['ModelSHA256']==h.sha(Path(__file__).with_name('heap_trace_model.py')),'native realloc model changed after calibration')
    configurations=[]
    for config in ['Debug','Release']:
        configurations.append(analyze(root,config));print(config,'native heap analysis verified',flush=True)
    h.write(a.output,dict(Verdict='SCOPED_POST_ATTACH_NATIVE_HEAP_ATTRIBUTION_PASS',Configurations=configurations,GraphVerificationSHA256=h.sha(root/'verification.json'),ReallocModelSHA256=h.sha(model_proof),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS',NativeHeapLeakFreedomClaim=False,
        Limits=['Traced post-warmup allocations only; untraced/pre-attach blocks remain unknown','Metadata snapshots cover default heap only','Native stack PCs are exact; module/RVA annotations use epoch inventories and do not establish continuous image/managed owner identity','Direct VirtualAlloc, GPU and physical presentation excluded']))
    print('NATIVE HEAP ATTRIBUTION',h.sha(a.output))
if __name__=='__main__':main()
