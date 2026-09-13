"""Independent raw decoding, native controls and ETW ownership/loss checks."""
import argparse,copy,importlib.util,json,struct,xml.etree.ElementTree as E
from collections import Counter
from pathlib import Path
spec=importlib.util.spec_from_file_location('gui',Path(__file__).with_name('Verify-GuiAttribution.py'));a=importlib.util.module_from_spec(spec);spec.loader.exec_module(a)
FIELDS=['EventId','Version','Opcode','HeaderPid','HeaderTid','Timestamp','HeaderFlags','HandleValue','NewHandleValue','HandleType','SessionId','OwnerProcessId','UserDataLength']
def raw_rows(p):
    data=p.read_bytes();offset=0;rr=[]
    while offset<len(data):
        a.require(offset+24<=len(data),'truncated raw header')
        id,version,opcode,pid,tid,qpc,length,flags=struct.unpack_from('<HBBIIqHH',data,offset);offset+=24
        a.require(length==(28 if id==458 else 20) and offset+length<=len(data),'raw payload size')
        values=struct.unpack_from('<QQIII' if id==458 else '<QIII',data,offset);offset+=length
        if id!=458:values=(values[0],0,*values[1:])
        rr.append(dict(zip(FIELDS,[id,version,opcode,pid,tid,qpc,flags,*values,length])))
    return rr
def controls(events,api,pid):
    matches=[]
    for name,phase,create,delete,type in [('CreateBitmap',-100,455,456,5),('CreateMenu',-101,452,453,2),('CreateIconIndirect',-102,452,453,3)]:
        known=[r for r in api if r['Api']==name and r['Action']=='acquire' and r['Phase']==phase and r['Depth']==0]
        a.require(len(known)==24,'known native object count')
        for r in known:
            rr=[e for e in events if e['HandleValue']==r['Handle'] and e['EventId'] in [create,delete]]
            a.require(len(rr)==2 and [e['EventId'] for e in rr]==[create,delete] and all(e['OwnerProcessId']==pid and e['HandleType']==type for e in rr),'missing/wrong create-destroy pair: '+name)
            a.require(rr[0]['Timestamp']<=rr[1]['Timestamp'],'native lifetime order')
            matches.append(dict(Api=name,Handle=r['Handle'],Type=type,Create=rr[0]['Timestamp'],Destroy=rr[1]['Timestamp']))
    return matches
def owned_scope(rr,pid):
    owned=set()
    for r in rr:
        key=(r['EventId']<=454,r['HandleValue']);prior=key in owned
        a.require(r['OwnerProcessId']==pid or prior,'foreign resource persisted without owned anchor')
        id=r['EventId']
        if r['OwnerProcessId']==pid and id in [452,454,455,457]:owned.add(key)
        if id in [453,456] or (id in [454,457] and r['OwnerProcessId']!=pid):owned.discard(key)
        if id==458:
            owned.discard(key)
            if r['OwnerProcessId']==pid:owned.add((False,r['NewHandleValue']))
def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--realtime',action='store_true');args=p.parse_args();root=args.snapshot;a.require(not args.output.exists(),'receipt exists')
    folder=root/'runs/Native-graceful';native=a.read(folder/'summary.json');pid=native['Pid'];api=a.rows(folder/'gui-api.jsonl');a.calibration(api,installed=46)
    if args.realtime:
        summary=a.read(folder/'realtime/summary.json');events=a.rows(folder/'realtime/owned-events.jsonl');raw=raw_rows(folder/'realtime/owned-events.raw')
        a.require(raw==[{k:r[k] for k in FIELDS} for r in events],'independent binary/JSON decode mismatch')
        a.require(all(summary[k]==0 for k in ['StartCode','EnableCode','DisableCode','StopCode','ConsumeCode','DecodeErrors','WriteErrors','EventsLost','BuffersLost','RealTimeBuffersLost']),'capture/loss/cleanup failure')
        a.require(summary['WrittenOwnedEvents']==len(events) and summary['SeenHandleEvents']==len(events)+summary['DiscardedOtherResourceEvents'],'filter accounting')
        owned_scope(events,pid);scope=True
    else:
        summary=a.read(folder/'decoded/summary.json');events=a.rows(folder/'decoded/events.jsonl');a.require(summary['HandleEvents']==len(events) and summary['PayloadErrors']==summary['EventsLost']==0,'native decoder/loss')
        ns={'e':'http://schemas.microsoft.com/win/2004/08/events/event'};xml=[]
        for ev in E.parse(folder/'tracerpt.xml').getroot():
            id=ev.find('e:System/e:EventID',ns)
            if id is None or not 452<=int(id.text)<=458:continue
            payload={x.attrib['Name']:int(x.text.strip(),0) if x.text.strip().startswith('0x') else int(x.text) for x in ev.findall('e:EventData/e:Data',ns)}
            execution=ev.find('e:System/e:Execution',ns)
            xml.append(dict(EventId=int(id.text),HeaderPid=int(execution.attrib['ProcessID']),HeaderTid=int(execution.attrib['ThreadID']),HandleValue=payload.get('HandleValue',payload.get('PreviousHandleValue')),NewHandleValue=payload.get('NewHandleValue',0),HandleType=payload['HandleType'],SessionId=payload['SessionId'],OwnerProcessId=payload['OwnerProcessId']))
        a.require(xml==[{k:r[k] for k in xml[0]} for r in events],'independent tracerpt payload mismatch')
        scope=all(r['OwnerProcessId']==pid for r in events) if native.get('OwnerPayloadFilter') else all(r['HeaderPid']==pid for r in events)
        a.require(not scope,'expected observed provider filter gap changed')
    matched=controls(events,api,pid);negative=[]
    for case in ['missing-create','missing-destroy','wrong-owner','wrong-type','foreign-persisted']:
        bad=copy.deepcopy(events);known=matched[24]['Handle'];target=next(r for r in bad if r['HandleValue']==known and r['EventId']==452)
        if case=='missing-create':bad.remove(target)
        if case=='missing-destroy':bad.remove(next(r for r in bad if r['HandleValue']==known and r['EventId']==453))
        if case=='wrong-owner':target['OwnerProcessId']=pid+1
        if case=='wrong-type':target['HandleType']=999
        if case=='foreign-persisted':bad.insert(0,dict(target,HandleValue=0x12345678,OwnerProcessId=pid+1))
        try:
            if case=='foreign-persisted':owned_scope(bad,pid)
            else:controls(bad,api,pid)
        except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    a.write(args.output,dict(Verdict='SCOPED_NATIVE_SOURCE_PASS' if scope else 'PROVIDER_FILTER_UNMET',NativePairsVerified=len(matched),NativePairs=matched,NativeEvents=len(events),OwnPid=pid,ScopeFilterPassed=scope,RealtimeConsumer=args.realtime,
        NegativeControls=negative,Summary=summary,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS',LedgerAppended=False,ProductionChanged=False,
        RawFiles=[dict(Path=str(f.relative_to(root)),Bytes=f.stat().st_size,SHA256=a.sha(f)) for f in sorted(folder.rglob('*')) if f.is_file()]))
    print(json.dumps(dict(Verdict='SCOPED_NATIVE_SOURCE_PASS' if scope else 'PROVIDER_FILTER_UNMET',Pairs=len(matched),SHA256=a.sha(args.output))))
if __name__=='__main__':main()
