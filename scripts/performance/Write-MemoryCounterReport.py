"""Preserve live history while recording native GPU and process memory counters."""
import argparse,hashlib,json
from datetime import datetime,timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];ART=ROOT/'artifacts/performance';PREFIX='FWM-DXGI-MEMORY-20260913-'
def sha(b):return hashlib.sha256(b).hexdigest().upper()
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();dest=ART/a.id;report=ROOT/'docs/performance/DXGI_PROCESS_MEMORY.md'
    assert not dest.exists() and not report.exists() and datetime.now().strftime('%Y%m%d') in a.id
    names=['R0','V1','V2','D0','D2','D3','D4'];receipts={n:ART/(PREFIX+n)/'verification.json' for n in names}
    assert all(f.is_file() for f in receipts.values())
    gpu=read(receipts['D3']);cpu=read(receipts['D4']);assert gpu['Verdict']=='SCOPED_GRAPH_DXGI_USAGE_OBSERVATIONS_VERIFIED' and cpu['Verdict']=='SCOPED_GRAPH_PRIVATE_COMMIT_OBSERVATIONS_VERIFIED'
    assert cpu['GraphProofSHA256']==sha(receipts['D3'].read_bytes())
    entry=read(ART/(PREFIX+'R0')/'entry-review.json');links={'PERFORMANCE_STATUS.md':'docs/performance/','PERFORMANCE_TODO.md':'docs/performance/','docs/performance/AUDIT.md':'','docs/performance/IMPLEMENTATION_RESULTS.md':'','scripts/performance/README.md':'../../docs/performance/'}
    before={name:(ROOT/name).read_bytes() for name in links}
    assert all(sha(b)==entry['Documents'][name]['SHA256'] for name,b in before.items())
    body=(ROOT/'scripts/performance/memory-counter-report.txt').read_text(encoding='utf-8')
    tables=[]
    for graph_name,receipt in [('F2',read(receipts['D2'])),('F3',gpu)]:
        for config in receipt['Configurations']:
            for group in config['MemoryGroups']:
                stages=group['Samples'];tables.append(f"| {graph_name} {config['Configuration']} | {group['Luid']} / {group['Node']} / {group['Segment']} | {' / '.join(str(x['Max']) for x in stages[1:4])} | {stages[-1]['Max']} | {group['ObservedUsageNoGrowth']} |")
    body=body.replace('@@GPU_TABLE@@','\n'.join(tables))
    tables=[]
    for config in cpu['Configurations']:
        stages=config['Samples'];tables.append(f"| {config['Configuration']} | {' / '.join(str(x['Max']) for x in stages[1:4])} | {stages[-1]['Max']} | {config['ObservedPrivateCommitNoGrowth']} |")
    body=body.replace('@@CPU_TABLE@@','\n'.join(tables))
    body=body.replace('@@GUI@@','; '.join(f"{c['Configuration']}: {c['GuiCriterionPassed']} ({c['GuiFailure'] or 'strict gate passed'})" for c in gpu['Configurations']))
    body+='\nReceipt SHA256:\n\n'+'\n'.join(f'- {n}: `{sha(f.read_bytes())}`.' for n,f in receipts.items())+'\n'
    block=(ROOT/'scripts/performance/memory-counter-live.txt').read_text(encoding='utf-8')
    dest.mkdir();changes=[]
    with report.open('x',encoding='utf-8',newline='\n') as f:f.write(body)
    for name,link in links.items():
        old=before[name];split=old.index(b'\n\n')+2;insert=block.format(link=link).encode();new=old[:split]+insert+old[split:]
        for phase,data in [('before',old),('after',new)]:
            target=dest/phase/name;target.parent.mkdir(parents=True,exist_ok=True)
            with target.open('xb') as f:f.write(data)
        assert (ROOT/name).read_bytes()==old
        (ROOT/name).write_bytes(new)
        assert new[:split]+new[split+len(insert):]==old
        changes.append(dict(Path=name,BeforeBytes=len(old),BeforeSHA256=sha(old),AfterBytes=len(new),AfterSHA256=sha(new),PriorBytesPreserved=True))
    with (dest/'verification.json').open('x',encoding='utf-8') as f:json.dump(dict(Verdict='VERIFIED_REPORT_AND_LIVE_DOCUMENT_UPDATE',RecordedUtc=datetime.now(timezone.utc).isoformat(),Changes=changes,Report=dict(Path=str(report.relative_to(ROOT)),Bytes=report.stat().st_size,SHA256=sha(report.read_bytes())),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False),f,indent=2)
    print(sha((dest/'verification.json').read_bytes()))
if __name__=='__main__':main()
