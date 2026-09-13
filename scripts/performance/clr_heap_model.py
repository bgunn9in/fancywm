"""CLR method/module generations, bounded by raw event QPC and completed rundown.

Only names/ranges actually emitted are attributed. No logical ownership,
inlining, pre-attach history, or unique ReJIT-ID claim is inferred.
"""
import collections,json,struct,uuid
RUNTIME='e13c0d23-ccbc-4e12-931b-d9cc2eee27e4';RUNDOWN='a669021c-c450-4609-a035-5af59af4df18'
HEADER=struct.Struct('<16sIIqHBBII')
def require(ok,message):
    if not ok:raise ValueError(message)
def string(data,i):
    end=i
    while end+2<=len(data) and data[end:end+2]!=b'\0\0':end+=2
    require(end+2<=len(data),'unterminated CLR UTF16 string')
    return data[i:end].decode('utf-16-le'),end+2
def decode(folder,pid):
    raw=(folder/'raw.bin').read_bytes();i=0;rows=[];decoded=collections.Counter()
    for seq,line in enumerate((folder/'events.jsonl').read_text(encoding='utf-8').splitlines(),1):
        r=json.loads(line);require(i+44<=len(raw),'CLR raw header truncated');g,p,t,q,e,v,o,ptr,n=HEADER.unpack_from(raw,i);i+=44
        data=raw[i:i+n];i+=n;require(len(data)==n,'CLR raw payload truncated')
        actual=dict(Provider=str(uuid.UUID(bytes_le=g)),Pid=p,Tid=t,Qpc=q,Id=e,Version=v,Opcode=o,PointerSize=ptr,PayloadBytes=n,Sequence=seq)
        require(all(r[k]==x for k,x in actual.items()) and p==pid and ptr==8 and g in [uuid.UUID(RUNTIME).bytes_le,uuid.UUID(RUNDOWN).bytes_le],'CLR raw/JSON header or PID mismatch')
        fields={};method=(r['Provider']==RUNTIME and e in [143,144]) or (r['Provider']==RUNDOWN and e in [143,144])
        module=(r['Provider']==RUNTIME and e in [152,153]) or (r['Provider']==RUNDOWN and e in [153,154])
        if method:
            require(v in [1,2] and n>=38,'unsupported CLR method version')
            values=struct.unpack_from('<QQQIII',data);fields=dict(zip(['MethodID','ModuleID','MethodStartAddress','MethodSize','MethodToken','RawFlags'],values));offset=36
            flags=fields.pop('RawFlags');fields['MethodFlags']=flags&~(0x380|0xf0000000);fields['OptimizationTier']=(flags>>7)&7 if flags&8 else 0
            for key in ['MethodNamespace','MethodName','MethodSignature']:fields[key],offset=string(data,offset)
            fields['ClrInstanceID']=struct.unpack_from('<H',data,offset)[0];offset+=2
            fields['ReJITID']=struct.unpack_from('<Q',data,offset)[0] if v==2 else 0;offset+=8 if v==2 else 0
            require(offset==n and fields['MethodSize']>0,'CLR method payload/range')
            r['RawMethodExtent']=flags>>28
        if module:
            require(v in [1,2] and n>=26,'unsupported CLR module version')
            fields=dict(zip(['ModuleID','AssemblyID','ModuleFlags'],struct.unpack_from('<QQI',data)));offset=24
            for key in ['ModuleILPath','ModuleNativePath']:fields[key],offset=string(data,offset)
            r['RawClrInstanceID']=struct.unpack_from('<H',data,offset)[0];offset+=2
            if v==2:
                for prefix in ['Managed','Native']:
                    fields[prefix+'PdbSignature']=str(uuid.UUID(bytes_le=data[offset:offset+16]));offset+=16
                    fields[prefix+'PdbAge']=struct.unpack_from('<I',data,offset)[0];offset+=4
                    fields[prefix+'PdbBuildPath'],offset=string(data,offset)
            require(offset==n,'CLR module payload length')
        if fields:
            require(all(r['Fields'].get(k)==v for k,v in fields.items()),'independent CLR payload decode differs from SDK')
            decoded['Method' if method else 'Module']+=1
        rows.append(r)
    require(i==len(raw),'unpaired CLR raw events');return rows,dict(decoded)

class Timeline:
    def __init__(self,rows):
        ends=[r['Qpc'] for r in rows if r['Provider']==RUNDOWN and r['Id']==146]
        require(len(ends)==1,'missing/duplicate own CLR rundown completion');self.ready=ends[0]
        self.methods=[];self.modules=[];self.pages=collections.defaultdict(list);self.unknown_unloads=0
        active_modules={};active_methods={}
        def methodkey(r):
            f=r['Fields'];return tuple(f[k] for k in ['ModuleID','MethodID','MethodStartAddress','MethodSize','ReJITID'])+(r.get('RawMethodExtent',0),)
        # Rundown identities become usable only at completion. Ordering methods
        # after module records here supplies the module generation, not an
        # invented earlier lifetime.
        ordered=sorted(rows,key=lambda r:(self.ready if r['Provider']==RUNDOWN else r['Qpc'],0 if 'Loader/Module' in r['EventName'] else 1,r['Sequence']))
        for r in ordered:
            name=r['EventName'];f=r['Fields'];q=self.ready if r['Provider']==RUNDOWN else r['Qpc']
            if name in ['Loader/ModuleLoad','Loader/ModuleDCStop']:
                key=(r.get('RawClrInstanceID',0),f['ModuleID'])
                require(key not in active_modules,'module ID reused while old generation live')
                m=dict(Sequence=r['Sequence'],Begin=q,End=None,Fields=f,ClrInstanceID=key[0]);self.modules.append(m);active_modules[key]=m
            elif name=='Loader/ModuleUnload':
                key=(r.get('RawClrInstanceID',0),f['ModuleID']);m=active_modules.pop(key,None)
                require(m is not None,'module unload without captured generation');m['End']=q;m['UnloadSequence']=r['Sequence']
                for k,v in list(active_methods.items()):
                    if v.get('ModuleSequence')==m['Sequence']:v['End']=min(v['End'] or q,q);del active_methods[k]
            elif name in ['Method/LoadVerbose','Method/DCStopVerbose']:
                key=methodkey(r);m=active_modules.get((f['ClrInstanceID'],f['ModuleID']))
                # A concurrently generated runtime method can precede its
                # rundown module. Keep attribution absent until corroborated.
                if key in active_methods:
                    old=active_methods[key];require(old['Fields']==f,'conflicting repeated method identity');continue
                item=dict(Sequence=r['Sequence'],Begin=q,End=None,Fields=f,ModuleSequence=m['Sequence'] if m else None,Source=name)
                self.methods.append(item);active_methods[key]=item
            elif name=='Method/UnloadVerbose':
                key=methodkey(r);old=active_methods.pop(key,None)
                if old is None:self.unknown_unloads+=1
                else:old['End']=q;old['UnloadSequence']=r['Sequence']
        for m in self.methods:
            f=m['Fields']
            for page in range(f['MethodStartAddress']//4096,(f['MethodStartAddress']+f['MethodSize']-1)//4096+1):self.pages[page].append(m)
    def resolve(self,pc,qpc):
        if qpc<self.ready:return []
        return [m for m in self.pages.get(pc//4096,[]) if m['ModuleSequence'] is not None and m['Begin']<=qpc and (m['End'] is None or qpc<m['End']) and m['Fields']['MethodStartAddress']<=pc<m['Fields']['MethodStartAddress']+m['Fields']['MethodSize']]
