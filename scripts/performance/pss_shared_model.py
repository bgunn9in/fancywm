"""Exact interval validation for commit propagation through shared sections."""
def transitions(first,second):
    assert first and second and first[0][0]==second[0][0] and first[-1][0]+first[-1][2]==second[-1][0]+second[-1][2]
    i=j=0;changes=[]
    while i<len(first) and j<len(second):
        a=first[i];b=second[j];start=max(a[0],b[0]);end=min(a[0]+a[2],b[0]+b[2]);assert end>start
        if (a[1],*a[3:])!=(b[1],*b[3:]):
            assert a[1]==b[1] and a[3]==b[3] and a[6]==b[6]==0x40000,'change outside same shared section'
            assert a[4]==0x2000 and a[5]==0 and b[4]==0x1000 and b[5]==b[3] and b[5] in [4,64],'unexpected shared state/protection transition'
            changes.append(dict(Base=start,Bytes=end-start,AllocationBase=a[1],Protect=b[5],BeforeState=a[4],AfterState=b[4],Type=b[6]))
        if end==a[0]+a[2]:i+=1
        if end==b[0]+b[2]:j+=1
    assert i==len(first) and j==len(second)
    return changes
