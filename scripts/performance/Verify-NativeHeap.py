"""Independent binary decoding and strict native allocation control validation."""
import argparse, copy, hashlib, json, struct
from pathlib import Path
H=struct.Struct('<8sIIIIQQQIIII'); E=struct.Struct('<QI HBB')
def require(ok, message):
    if not ok: raise ValueError(message)
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p): return json.loads(p.read_text(encoding='utf-8'))
def write(p, v):
    with p.open('x',encoding='utf-8') as f: json.dump(v,f,indent=2)
def decode(raw, pid=None):
    require(len(raw)>=H.size,'truncated native header')
    magic,schema,owner,tid,count,heap,begin,end,error,locked,unlocked,overflow=H.unpack_from(raw)
    require(magic==b'FWMHEAP1' and schema==1,'native schema')
    require(owner>0 and tid>0 and heap>0 and (pid is None or pid==owner),'native PID/heap identity')
    require(error==259 and locked==unlocked==1 and overflow==0,'incomplete/unsafe heap enumeration')
    require(begin>0 and end>=begin and len(raw)==H.size+count*E.size,'native count/time/length')
    entries=[dict(Address=a,Bytes=b,Flags=f,Overhead=o,Region=r) for a,b,f,o,r in E.iter_unpack(raw[H.size:])]
    busy=[e for e in entries if e['Flags']&4]
    require(len({e['Address'] for e in busy})==len(busy),'duplicate busy block')
    return dict(Pid=owner,Tid=tid,Heap=heap,Begin=begin,End=end,Entries=entries,BusyBlocks=len(busy),BusyBytes=sum(e['Bytes'] for e in busy),OverheadBytes=sum(e['Overhead'] for e in busy))
def witnesses(phases,held,final):
    require(set(phases)=={'baseline','held','resized','released'},'missing native phase')
    require(len(held)==len(final)==24 and len(set(held))==len(set(final))==24 and min(held+final)>0,'missing native allocation witness')
    blocks={k:{e['Address']:e for e in s['Entries'] if e['Flags']&4} for k,s in phases.items()}
    require(len({s['Heap'] for s in phases.values()})==1,'heap identity changed')
    for i,(old,new) in enumerate(zip(held,final)):
        require(old not in blocks['baseline'],'allocation predates control')
        require(old in blocks['held'] and blocks['held'][old]['Bytes']==4096+i*256,'missing allocated block/size')
        require(new in blocks['resized'] and blocks['resized'][new]['Bytes']==16384+i*256,'missing resized block/size')
        require(new not in blocks['released'],'retained native block')
    return dict(Allocated=24,Resized=24,Freed=24,Moved=sum(a!=b for a,b in zip(held,final)))
def main():
    p=argparse.ArgumentParser();p.add_argument('--build',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    require(not a.output.exists(),'receipt exists');root=a.build
    require(read(root/'build-receipt.json')['ExitCode']==0,'build failed')
    run=read(root/'control-process.json');require(run['ExitCode']==0 and run['Exited'],'owned native control failed')
    require(sha(root/'control.log')==run['LogSHA256'],'raw process log')
    log=read(root/'control.log');require(log['Pid']==run['Pid'] and log['Result']==0 and log['PrivateAndDefaultHeap'],'native control result')
    for manifest,base in [('source-manifest.json',root/'source'),('binaries.json',root)]:
        for r in read(root/manifest):require((base/r['Path']).stat().st_size==r['Bytes'] and sha(base/r['Path'])==r['SHA256'],'source/binary provenance')
    result=[];controls=[]
    for kind in ['private','default']:
        rr={phase:decode((root/'control'/f'{kind}-{phase}.bin').read_bytes(),run['Pid']) for phase in ['baseline','held','resized','released']}
        held=list(struct.unpack('<24Q',(root/'control'/f'{kind}-held-pointers.bin').read_bytes()))
        final=list(struct.unpack('<24Q',(root/'control'/f'{kind}-pointers.bin').read_bytes()))
        result.append(dict(Heap=kind,Control=witnesses(rr,held,final),Phases={k:{f:s[f] for f in ['Heap','Begin','End','BusyBlocks','BusyBytes']} for k,s in rr.items()}))
        for case in ['missing-phase','missing-allocation','wrong-size','retained-block']:
            bad=copy.deepcopy(rr)
            if case=='missing-phase':del bad['resized']
            if case=='missing-allocation':bad['held']['Entries']=[e for e in bad['held']['Entries'] if e['Address']!=held[0]]
            if case=='wrong-size':next(e for e in bad['resized']['Entries'] if e['Address']==final[0])['Bytes']+=1
            if case=='retained-block':bad['released']['Entries'].append(next(e for e in bad['resized']['Entries'] if e['Address']==final[0]))
            try:witnesses(bad,held,final)
            except ValueError as e:controls.append(dict(Heap=kind,Case=case,Rejected=True,Reason=str(e)))
            else:raise ValueError('negative accepted '+case)
    raw=(root/'control/default-held.bin').read_bytes()
    for case,offset,value in [('wrong-pid',12,0),('walk-error',48,5),('lock-failure',52,0),('unlock-failure',56,0),('overflow',60,1)]:
        bad=bytearray(raw);struct.pack_into('<I',bad,offset,value)
        try:decode(bad,run['Pid'])
        except ValueError as e:controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    write(a.output,dict(Verdict='SCOPED_NATIVE_CONTROL_PASS',Pid=run['Pid'],Heaps=result,NegativeControls=controls,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS',
        RawFiles=[dict(Path=str(f.relative_to(root)),Bytes=f.stat().st_size,SHA256=sha(f)) for f in sorted(root.glob('control/*'))],
        ObserverSHA256=sha(root/'FancyWM.NativeHeap.dll'),Limits=['HeapWalk metadata only; no block contents','Default process heap plus exclusively owned private calibration heap','No allocation stack, pointer-generation, VirtualAlloc, GPU or whole-native-heap claim']))
    print(json.dumps(dict(Verdict='SCOPED_NATIVE_CONTROL_PASS',Allocations=48,Reallocations=48,Frees=48,NegativeControls=len(controls),SHA256=sha(a.output))))
if __name__=='__main__':main()
