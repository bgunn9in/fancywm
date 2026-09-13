"""Derive bounded comparisons from verified receipts, without new native runs."""
import argparse, collections, hashlib, json
from pathlib import Path

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()

def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    assert not a.output.exists(),'receipt exists'
    trace=read(a.snapshot/'trace-verification.json');assert trace['Verdict']=='SCOPED_POST_ATTACH_NATIVE_HEAP_ATTRIBUTION_PASS'
    graph=read(a.snapshot/'verification.json');assert sha(a.snapshot/'verification.json')==trace['GraphVerificationSHA256']
    configurations=[]
    for c in trace['Configurations']:
        epochs=[]
        for e in c['Epochs']:
            groups=e['NativeStackGroups'];assert sum(g['Blocks'] for g in groups)==e['KnownTracedDefaultHeapBlocks']
            assert sum(g['Bytes'] for g in groups)==e['KnownTracedDefaultHeapBytes']
            buckets=collections.defaultdict(lambda:[0,0]);unresolved=0
            for g in groups:
                # A first non-allocator module observed at the epoch is only a
                # descriptive bucket. It is not a logical allocation owner.
                name=next((f.get('Module','unresolved') for f in g['Stack'] if f.get('Module','').lower() not in ['ntdll.dll','kernelbase.dll','kernel32.dll','ucrtbase.dll']),'unresolved')
                buckets[name][0]+=g['Blocks'];buckets[name][1]+=g['Bytes']
                if any(f.get('Unresolved') for f in g['Stack']):unresolved+=g['Blocks']
            epochs.append(dict(Label=e['Label'],BusyBlocks=e['BusyBlocks'],BusyBytes=e['BusyBytes'],KnownBlocks=e['KnownTracedDefaultHeapBlocks'],KnownBytes=e['KnownTracedDefaultHeapBytes'],
                UnknownBlocks=e['UnknownPreAttachOrUntracedBusyBlocks'],UnknownBytes=e['BusyBytes']-e['KnownTracedDefaultHeapBytes'],KnownBlocksWithUnresolvedFrames=unresolved,
                FirstNonAllocatorModuleAtEpoch=[dict(Module=k,Blocks=v[0],Bytes=v[1]) for k,v in sorted(buckets.items(),key=lambda x:-x[1][1])]))
        rows=[json.loads(l) for l in (a.snapshot/'runs'/(c['Configuration']+'-graceful')/'observations.jsonl').read_text(encoding='utf-8').splitlines()]
        acquired=[r for r in rows if r['Kind']=='GraphWindowAcquired'];closed=[r for r in rows if r['Kind']=='GraphWindowClosed']
        assert len(acquired)==len(closed) and len({r['Id'] for r in acquired})==len(acquired)
        for label in ['exit','shutdown']:
            checkpoint=next(r for r in rows if r['Kind']=='GraphWindowCheckpoint' and r['Label']==label)
            assert not checkpoint['SurvivingNativeHwnds'] and not checkpoint['Active']
        g=next(x for x in graph['Configurations'] if x['Configuration']==c['Configuration'])
        configurations.append(dict(Configuration=c['Configuration'],Pid=c['Pid'],GraphWindowGenerations=len(acquired),GraphWindowGenerationsClosed=len(closed),
            GuiCriterionPassed=g['GuiCriterionPassed'],DefaultHeapSnapshotNoGrowth=g['DefaultHeapSnapshotNoGrowth'],Epochs=epochs,IndependentDecoders=c['IndependentDecoderVerification']))
    result=dict(Verdict='VERIFIED_RECEIPT_DERIVATION',TraceVerificationSHA256=sha(a.snapshot/'trace-verification.json'),GraphVerificationSHA256=sha(a.snapshot/'verification.json'),
        Configurations=configurations,NewNativeRuns=0,ModuleBucketsAreEpochObservations=True,LogicalOwnerClaim=False,NativeHeapLeakFreedomClaim=False,
        ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS')
    with a.output.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2)
    print(json.dumps(dict(Receipt=str(a.output),SHA256=sha(a.output))))
if __name__=='__main__':main()
