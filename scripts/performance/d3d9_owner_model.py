"""Linear replay of private-data generations; no physical-memory inference."""
from collections import Counter
import json
LOCKS={'TextureLockRect':'TextureUnlockRect','SurfaceLockRect':'SurfaceUnlockRect','VertexLock':'VertexUnlock','IndexLock':'IndexUnlock'}
CREATORS={'CreateTexture','CreateVertexBuffer','CreateIndexBuffer','CreateRenderTarget','CreateDepthStencilSurface','CreateOffscreenPlainSurface','CreateRenderTargetEx','CreateOffscreenPlainSurfaceEx','CreateDepthStencilSurfaceEx','GetSurfaceLevel'}
def read(path):
    with path.open(encoding='utf-8') as f:
        for line in f:yield json.loads(line)
def replay(rows,pid,mutate=None):
    generations={};active=set();events=Counter();unowned=Counter();unknown=[];failures=Counter();unknownparents=[];checkpoints=[];locks={};maps=[];nested=[];calls={};callstack={};rowcount=0;installed=False;detached=False;closed=False;deferred=None;changed=False
    for original in rows:
        r=dict(original);event=r['Event'];ident=r['Id']
        if mutate and not changed:
            if mutate=='retained-owner' and event=='private-owner-released':changed=True;continue
            if mutate=='missing-map-end' and event in LOCKS.values() and ident:changed=True;continue
            if mutate=='wrong-pid':r['Pid']+=1;changed=True
            if mutate=='wrong-owner' and event=='attach-result':r['Identity']+=8;changed=True
            if mutate=='missing-checkpoint' and event=='checkpoint' and r['Phase']==11:changed=True;continue
            if mutate=='bad-checkpoint' and event=='checkpoint' and r['Phase']==11:r['Value']+=1;changed=True
            if mutate=='early-release' and event=='private-owner-released':r['Qpc']=1;changed=True
            if mutate=='missing-call-boundary' and event.startswith('map-call-exit-'):changed=True;continue
            if mutate=='wrong-nested-parent' and event.startswith('map-call-enter-') and r['Extra']:r['Extra']+=1;changed=True
        rowcount+=1
        if mutate:r['Seq']=rowcount
        assert r['Seq']==rowcount and r['Pid']==pid and r['Tid']>0 and r['Qpc']>0 and r['Stack'],'stream identity/order'
        r.pop('Stack');events[event]+=1
        assert event not in ['failure','identity-failed','private-guid-collision','observer-start-failure'],'native observer error'
        if event=='hooks-installed':assert events[event]==1 and r['Value']==0 and r['Extra']>=18;installed=True
        if event=='hooks-detached':assert installed and not detached and r['Value']==0;detached=True
        if event.startswith('map-call-enter-'):
            api=event[len('map-call-enter-'):];stack=callstack.setdefault(r['Tid'],[]);call=r['Value']
            assert installed and not detached and call not in calls and r['Extra']==(stack[-1] if stack else 0) and r['Third']==len(stack)+1,'native call nesting'
            calls[call]=dict(API=api,Owner=ident,Begin=r,End=None,Result=None);stack.append(call)
        if event.startswith('map-call-exit-'):
            stack=callstack[r['Tid']];assert stack and stack[-1]==r['Value'],'native call exit order';call=calls[stack.pop()]
            assert call['API']==event[len('map-call-exit-'):] and call['Owner']==ident and call['Begin']['Extra']==r['Extra'] and call['Begin']['Third']==r['Third'] and call['End'] is None and call['Result'] is not None,'native call boundary'
            assert call['Begin']['Qpc']<call['Result']['Qpc']<r['Qpc'];call['End']=r
        if event=='attach-begin':
            assert installed and not detached and ident==len(generations)+1 and r['Identity']>0 and r['Closed']==0
            if r['Parent']:assert r['Parent'] in active,'inactive/unknown texture parent'
            elif r['Kind']=='texture-surface9':unknownparents.append(ident)
            generations[ident]=dict(Attach=r,Registration=None,End=None);active.add(ident)
        if ident:
            assert ident in generations,'unknown generation';g=generations[ident];a=g['Attach']
            assert all(r[k]==a[k] for k in ['Identity','Kind','Parent']),'owner identity'
            if event=='attach-result':assert g['Registration'] is None and r['Value']==0 and a['Seq']<r['Seq'];g['Registration']=r
            if event=='private-owner-released':
                assert g['Registration'] and g['End'] is None and r['Closed']==1 and g['Registration']['Qpc']<r['Qpc'],'private-data release ordering'
                g['End']=r;active.remove(ident)
        if event=='checkpoint':
            assert all(g['Registration'] for g in generations.values()) and r['Value']==len(active) and r['Extra']==len(generations) and r['Third']==0,'checkpoint counters'
            checkpoints.append(dict(Phase=r['Phase'],Seq=r['Seq'],Qpc=r['Qpc'],Active=len(active),Total=len(generations),Kinds=dict(Counter(generations[i]['Attach']['Kind'] for i in active)),ActiveIds=sorted(active)))
        map_api=event in LOCKS or event in LOCKS.values()
        if installed and (map_api or event in CREATORS) and r['Value']&0x80000000:failures[event+':'+hex(r['Value'])]+=1
        depth=0
        if map_api and installed:
            stack=callstack.get(r['Tid'],[]);assert stack,'missing native map call entry';call=calls[stack[-1]]
            assert call['API']==event and call['Owner']==ident and call['Result'] is None,'native map result';call['Result']=r;depth=len(stack)
            if depth>1 and event in LOCKS and r['Value']==0:
                parent=calls[stack[-2]];nested.append(dict(Id=ident,API=event,Pointer=r['Extra'],Call=stack[-1],ParentCall=stack[-2],ParentOwner=parent['Owner'],ParentAPI=parent['API']))
        if event in LOCKS and r['Value']==0 and installed and depth==1:
            if not ident:unowned[event]+=1;unknown.append(r);continue
            key=(ident,event);assert key not in locks,'nested/overlapping map';assert generations[ident]['End'] is None and r['Extra']>0
            locks[key]=r
        if event in LOCKS.values() and r['Value']==0 and installed and depth==1:
            if not ident:unowned[event]+=1;unknown.append(r);continue
            lockevent=next(k for k,v in LOCKS.items() if v==event);key=(ident,lockevent);assert key in locks,'unmatched map end';begin=locks.pop(key)
            assert begin['Seq']<r['Seq'] and begin['Qpc']<r['Qpc'],'map ordering';maps.append(dict(Id=ident,API=lockevent,Pointer=begin['Extra'],ExtentOrPitch=begin['Third'],Begin=begin['Qpc'],End=r['Qpc']))
        if event in CREATORS and installed and r['Value']==0:assert ident>0,'successful unowned resource creation'
        if event=='observer-close':assert not active and r['Value']==0 and detached;closed=True
        if event=='close-deferred-for-live-private-owners':assert active and r['Value']==len(active) and detached;deferred=r
    assert installed and detached and not locks and all(g['Registration'] for g in generations.values()),'incomplete observer lifecycle/maps'
    assert all(not stack for stack in callstack.values()) and all(c['End'] for c in calls.values()) and set(calls)==set(range(1,len(calls)+1)),'incomplete native map call boundaries'
    assert closed or deferred,'missing observer completion'
    if mutate:assert changed,'negative mutation not exercised'
    return dict(Rows=rowcount,Generations=generations,Checkpoints=checkpoints,MapPairs=maps,NestedMaps=nested,MapCalls=calls,UnownedMapEvents=dict(unowned),UnknownMapRows=unknown,UntrackedTextureParents=unknownparents,ApiFailures=dict(failures),Events=dict(events),FinalActiveIds=sorted(active),ObserverClosed=closed,DeferredClose=deferred)
