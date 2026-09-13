"""Own-PID native ETW heap controls with independent raw/XML/CSV comparison."""
import argparse, collections, copy, csv, importlib.util, json, struct, uuid, xml.etree.ElementTree as ET
from pathlib import Path
spec=importlib.util.spec_from_file_location('h',Path(__file__).with_name('Verify-NativeHeap.py'));h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
HEADER=struct.Struct('<16sIIqHBBHH');HEAP=uuid.UUID('222962ab-6180-4b88-a825-346b75f2a24a');STACK=uuid.UUID('def2fe46-7bd6-4b80-bd94-f57fe20d0ce3')
NS={'e':'http://schemas.microsoft.com/win/2004/08/events/event','t':'http://schemas.microsoft.com/win/2004/08/events/trace'}
def raw_events(path):
    raw=path.read_bytes();i=0;rr=[]
    while i<len(raw):
        h.require(i+HEADER.size<=len(raw),'truncated event header');guid,pid,tid,qpc,event,version,opcode,flags,size=HEADER.unpack_from(raw,i);i+=HEADER.size
        h.require(i+size<=len(raw),'truncated event payload');payload=raw[i:i+size];i+=size
        rr.append(dict(Guid=str(uuid.UUID(bytes_le=guid)),Pid=pid,Tid=tid,Qpc=qpc,Version=version,Opcode=opcode,Flags=flags,Payload=payload))
    return rr
def allocation_events(rr,pid):
    out=[];stacks={}
    for r in rr:
        p=r['Payload'];opcode=r['Opcode']
        if r['Guid']==str(STACK):
            h.require(len(p)>=24 and (len(p)-16)%8==0,'invalid native stack payload')
            time,owner,tid=struct.unpack_from('<QII',p);h.require(owner==pid,'foreign native stack')
            key=(owner,tid,time);h.require(key not in stacks,'duplicate stack association');stacks[key]=struct.unpack_from('<'+'Q'*((len(p)-16)//8),p,16)
        elif r['Guid']==str(HEAP):
            if opcode==38:
                h.require(r['Version']==4 and len(p)==72 and struct.unpack_from('<I',p,36)[0]==pid,'foreign/unknown heap rundown')
                continue
            h.require(r['Pid']==pid,'foreign native heap event')
            if opcode not in [33,34,36]:continue
            h.require(r['Version']==2,'unsupported allocation event version')
            if opcode==33:
                h.require(len(p)==28,'alloc payload size');heap,size,address,source=struct.unpack('<QQQI',p)
                data=dict(Heap=heap,Address=address,Bytes=size,Source=source)
            elif opcode==34:
                h.require(len(p)==44,'realloc payload size');heap,new,old,newsize,oldsize,source=struct.unpack('<QQQQQI',p)
                data=dict(Heap=heap,Address=new,OldAddress=old,Bytes=newsize,OldBytes=oldsize,Source=source)
            else:
                h.require(len(p)==20,'free payload size');heap,address,source=struct.unpack('<QQI',p);data=dict(Heap=heap,Address=address,Source=source)
            out.append(dict(Opcode=opcode,Pid=pid,Tid=r['Tid'],Qpc=r['Qpc'],**data))
        else:raise ValueError('unexpected persisted provider')
    for r in out:
        if r['Opcode'] in [33,34]:
            key=(pid,r['Tid'],r['Qpc']);h.require(key in stacks and len(stacks[key])>=2 and min(stacks[key])>0,'missing native allocation stack');r['Stack']=stacks[key]
    h.require(len(stacks)==sum(r['Opcode'] in [33,34] for r in out),'unassociated stack')
    return out,stacks
def controls(events,root,pid):
    pairs=[]
    for kind in ['private','default']:
        phases={s:h.decode((root/'control'/f'{kind}-{s}.bin').read_bytes(),pid) for s in ['baseline','held','resized','released']}
        held=list(struct.unpack('<24Q',(root/'control'/f'{kind}-held-pointers.bin').read_bytes()));final=list(struct.unpack('<24Q',(root/'control'/f'{kind}-pointers.bin').read_bytes()))
        h.witnesses(phases,held,final);heap=phases['held']['Heap']
        for i,(old,new) in enumerate(zip(held,final)):
            found=[]
            for opcode,start,end,address in [(33,'baseline','held',old),(34,'held','resized',new),(36,'resized','released',new)]:
                match=[e for e in events if e['Opcode']==opcode and e['Heap']==heap and e['Address']==address and phases[start]['End']<e['Qpc']<phases[end]['Begin']]
                h.require(len(match)==1,'missing/ambiguous native allocation/realloc/free witness');found.append(match[0])
            a,r,f=found;h.require(a['Bytes']==4096+i*256 and r['OldAddress']==old and r['OldBytes']==a['Bytes'] and r['Bytes']==16384+i*256 and a['Tid']==r['Tid']==f['Tid'],'native allocation control size/thread')
            pairs.append(dict(HeapKind=kind,Index=i,Heap=heap,Original=old,Final=new,AllocQpc=a['Qpc'],ReallocQpc=r['Qpc'],FreeQpc=f['Qpc'],AllocStackFrames=len(a['Stack']),ReallocStackFrames=len(r['Stack'])))
    return pairs
def main():
    p=argparse.ArgumentParser();p.add_argument('--root',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();root=a.root;h.require(not a.output.exists(),'receipt exists')
    admission=h.read(root/'admission-result.json');pid=admission['Pid'];h.require(admission['OwnedProcessExited'] and admission['SessionStopped'] and admission['ExitCode']==0 and admission['Failure'] is None,'admission cleanup')
    for name in ['heap-start','heap-stop','xperf-stats','xperf-dump','tracerpt']:h.require(h.read(root/(name+'.json'))['ExitCode']==0,'native tool failed '+name)
    summary=h.read(root/'native-decoded/summary.json');h.require(summary['ProcessTraceCode']==summary['CloseTraceCode']==summary['EventsLost']==summary['BuffersLost']==0 and summary['WritePassed'],'native decoder/loss')
    stats=(root/'stats.txt').read_text();h.require('Total # Lost Buffers : 0' in stats and 'Total # Lost Events  : 0' in stats,'xperf loss')
    rr=raw_events(root/'native-decoded/raw.bin');h.require(len(rr)==summary['Records'],'native record count');events,stacks=allocation_events(rr,pid);pairs=controls(events,root,pid)
    xml=ET.parse(root/'events.xml').getroot();xml_stacks=[];xml_events=[]
    for element in xml:
        opcode=int(element.findtext('e:System/e:Opcode','',NS));guid=element.findtext('t:ExtendedTracingInfo/t:EventGuid','',NS).strip('{}').lower()
        if guid==str(STACK):xml_stacks.append(bytes.fromhex(element.findtext('e:ProcessingErrorData/e:EventPayload','',NS)))
        if guid!=str(HEAP) or opcode not in [33,34,36]:continue
        data={x.attrib['Name']:int(x.text.strip(),0) for x in element.findall('e:EventData/e:Data',NS)}
        ex=element.find('e:System/e:Execution',NS);h.require(int(ex.attrib['ProcessID'])==pid,'foreign XML heap event')
        fields=['HeapHandle','AllocAddress','AllocSize'] if opcode==33 else ['HeapHandle','NewAllocAddress','OldAllocAddress','NewAllocSize','OldAllocSize'] if opcode==34 else ['HeapHandle','FreeAddress']
        xml_events.append((opcode,int(ex.attrib['ThreadID']),*(data[k] for k in fields)))
    def canonical(e):
        fields=['Heap','Address','Bytes'] if e['Opcode']==33 else ['Heap','Address','OldAddress','Bytes','OldBytes'] if e['Opcode']==34 else ['Heap','Address']
        return (e['Opcode'],e['Tid'],*(e[k] for k in fields))
    wanted=collections.Counter(canonical(e) for e in events)
    h.require(collections.Counter(xml_events)==wanted,'independent XML/native allocation fields')
    h.require(collections.Counter(xml_stacks)==collections.Counter(r['Payload'] for r in rr if r['Guid']==str(STACK)),'independent stack raw payload bytes')
    csv_events=[]
    with (root/'events.csv').open(encoding='utf-8-sig',newline='') as f:
        for row in csv.reader(f,skipinitialspace=True):
            row=[x.strip() for x in row]
            if not row or row[0] not in ['HeapAlloc','HeapRealloc','HeapFree'] or row[1]=='TimeStamp':continue
            opcode={'HeapAlloc':33,'HeapRealloc':34,'HeapFree':36}[row[0]];indices=[4,5,6] if opcode==33 else [4,5,6,7,8] if opcode==34 else [4,5]
            csv_events.append((opcode,int(row[3]),*(int(row[i],0) for i in indices)))
    h.require(collections.Counter(csv_events)==wanted,'independent CSV/native allocation fields')
    negative=[]
    for case in ['missing-allocation','missing-free','missing-stack','foreign-pid']:
        bad=copy.deepcopy(rr)
        if case=='missing-stack':bad.remove(next(r for r in bad if r['Guid']==str(STACK)))
        elif case=='foreign-pid':next(r for r in bad if r['Guid']==str(HEAP) and r['Opcode']==33)['Pid']+=1
        else:
            target=pairs[0]['AllocQpc' if case=='missing-allocation' else 'FreeQpc'];bad.remove(next(r for r in bad if r['Qpc']==target and r['Guid']==str(HEAP)))
        try:bad_events,_=allocation_events(bad,pid);controls(bad_events,root,pid)
        except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    h.write(a.output,dict(Verdict='SCOPED_NATIVE_HEAP_TRACE_ADMISSION_PASS',Pid=pid,NativeControlPairs=pairs,HeapEvents=summary['HeapRecords'],StackEvents=len(stacks),AllocationEvents=len(events),IndependentDecoders=['ProcessTrace/raw','tracerpt/XML','xperf/CSV'],NegativeControls=negative,
        PerformanceClaim=False,StageAccepted=False,ProductionChanged=False,WholeIdStatus='IN_PROGRESS',SessionStopped=True,ProcessExited=True,
        Limits=['Attached after process startup; earlier allocation history excluded','No global kernel/session or registry change','Raw stack identity is verified; symbol attribution is separate','xperf stack aggregation fails without symbols and process metadata; preserved error does not substitute for raw proof'],
        Files=[dict(Path=str(f.relative_to(root)),Bytes=f.stat().st_size,SHA256=h.sha(f)) for f in sorted(root.rglob('*')) if f.is_file()]))
    print(json.dumps(dict(Verdict='SCOPED_NATIVE_HEAP_TRACE_ADMISSION_PASS',Pairs=len(pairs),Stacks=len(stacks),SHA256=h.sha(a.output))))
if __name__=='__main__':main()
