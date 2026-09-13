"""Current-process private commit observations, separate from GPU accounting."""
def check(rows,pid,expected):
    assert [r['Seq'] for r in rows]==list(range(1,len(rows)+1)) and all(r['Pid']==pid for r in rows)
    assert rows[0]['Event']=='started' and rows[-1]['Event']=='stopped' and rows[0]['Errors']==rows[-1]['Errors']==0
    queries=rows[1:-1];assert all(r['Event']=='sample' for r in queries)
    assert [(r['Phase'],r['Sample']) for r in queries]==expected,'process sample coverage'
    for r in queries:
        assert r['Code']==0 and r['StructureBytes']==80 and r['PrivateUsage']==r['PagefileUsage']>0
        assert r['PeakPagefileUsage']>=r['PrivateUsage'] and r['PeakWorkingSetSize']>=r['WorkingSetSize']>0
        assert rows[0]['Qpc']<r['QpcBegin']<=r['QpcEnd']<rows[-1]['Qpc']
    assert all(x['QpcEnd']<y['QpcBegin'] and x['PeakPagefileUsage']<=y['PeakPagefileUsage'] and x['PeakWorkingSetSize']<=y['PeakWorkingSetSize'] for x,y in zip(queries,queries[1:]))
    return queries
