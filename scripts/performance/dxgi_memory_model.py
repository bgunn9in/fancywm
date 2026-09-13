"""Identity and finite query coverage; no physical allocation decoder."""
def one(rows,event):
    found=[r for r in rows if r['Event']==event];assert len(found)==1,event;return found[0]
def check(rows,pid,phases,samples):
    assert [r['Seq'] for r in rows]==list(range(1,len(rows)+1))
    assert all(r['Pid']==pid for r in rows)
    assert one(rows,'factory-created')['Code']==0
    adapters=[r for r in rows if r['Event']=='adapter'];hardware=[r for r in adapters if not r['Flags']&2]
    assert adapters and hardware and [r['Ordinal'] for r in adapters]==list(range(len(adapters)))
    assert len({r['Luid'] for r in adapters})==len(adapters) and all(r['Luid']>0 for r in adapters)
    end=one(rows,'enumeration-ended');assert end['Code']==0x887A0002 and end['Count']==len(adapters)
    start=one(rows,'started');stop=one(rows,'stopped');assert start['Code']==stop['Code']==stop['Count']==0 and start['Count']==len(hardware)
    queries=[r for r in rows if r['Event']=='sample'];expected=[(phase,sample,a['Luid'],segment) for phase in phases for sample in range(samples) for a in hardware for segment in [0,1]]
    assert [(r['Phase'],r['Sample'],r['Luid'],r['Segment']) for r in queries]==expected,'sample/adapter/segment coverage'
    assert rows[-1]==stop and len(rows)==len(adapters)+4+len(queries)
    for r in queries:
        a=next(a for a in hardware if a['Luid']==r['Luid'])
        assert r['Node']==0 and r['Ordinal']==a['Ordinal'] and r['Code']==0 and r['CurrentReservation']==0
        assert r['Budget']>=r['CurrentUsage']>=0 and 0<=r['AvailableForReservation']<=r['Budget']
        assert start['Qpc']<r['QpcBegin']<=r['QpcEnd']<stop['Qpc']
    assert all(x['QpcEnd']<y['QpcBegin'] for x,y in zip(queries,queries[1:]))
    return [{k:v for k,v in a.items() if k not in ['Event','Seq','Pid']} for a in hardware],queries
