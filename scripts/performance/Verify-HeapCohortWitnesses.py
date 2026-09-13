"""Check native cohort identities, birth/free witnesses and realloc successors.

This complements the calibrated replay with independent lookups in raw native
events and exact HeapWalk snapshot sets. It does not claim logical ownership.
"""
import argparse,contextlib,copy,importlib.util,json,os,shutil,struct,sys,traceback
from collections import Counter
from datetime import datetime,timezone
from pathlib import Path
spec=importlib.util.spec_from_file_location('cohort',Path(__file__).with_name('Verify-HeapCohorts.py'));c=importlib.util.module_from_spec(spec);spec.loader.exec_module(c);v=c.v;h=c.h

def decode_generations(path):
    raw=path.read_bytes();h.require(len(raw)%64==0,'truncated generation witness')
    rr=[dict(zip(['Address','Size','Generation','BirthQpc','BirthTid','StackQpc','Seeded','Heap'],row)) for row in struct.iter_unpack('<8Q',raw)]
    h.require(len({r['Address'] for r in rr})==len(rr) and len({r['Generation'] for r in rr})==len(rr),'duplicate generation/address witness')
    return rr
def birthkey(b,pid,heap):return (heap,pid,b['tid'],b['qpc'],b['address'],b['size'])
def deathkey(d):return (36,d['qpc'],d['address'],0) if d['reason']=='HeapFree' else (34,d['qpc'],d['address'],d['size']) if d['reason']=='HeapRealloc' else (35,d['qpc'],0,0)
def edgekey(parent,child,qpc,births,deaths):
    h.require(parent in births and child in births and parent in deaths,'realloc generation metadata missing')
    return (qpc,deaths[parent]['address'],births[child]['address'],deaths[parent]['size'],births[child]['size'],births[child]['tid'])

def check(phases,snapshots,cuts,births,deaths,edges,native,pid,heap):
    h.require(list(phases)==list(snapshots)==list(cuts)==['0','1','2','shutdown'],'missing native epoch')
    initial={r['Generation']:r for r in phases['0']};seen={}
    for label,rr in phases.items():
        h.require(len({r['Generation'] for r in rr})==len(rr) and len({r['Address'] for r in rr})==len(rr),'duplicate exported identity')
        expected={e['Address']:e['Bytes'] for e in snapshots[label]['Entries'] if e['Flags']&4}
        h.require({r['Address']:r['Size'] for r in rr}==expected,'exported inventory differs from full locked native snapshot')
        for r in rr:
            g=r['Generation'];h.require(g in births,'generation birth metadata missing');b=births[g]
            h.require(r['Heap']==heap and (r['Address'],r['Size'],r['BirthQpc'],r['BirthTid'],bool(r['Seeded']))==(b['address'],b['size'],b['qpc'],b['tid'],b['seeded']),'exported generation metadata mismatch')
            h.require(b['qpc']<=cuts[label] and (g not in deaths or deaths[g]['qpc']>cuts[label]),'generation born later or already retired')
            if b['seeded']:
                h.require(g in initial and b['qpc']==cuts['0'] and b['tid']==0 and r['StackQpc']==0,'seeded baseline falsely has allocation provenance')
            else:h.require(native['births'].get(birthkey(b,pid,heap),0)==1,'missing/ambiguous native allocation birth')
            if r['StackQpc']:
                k=(heap,pid,b['tid'],r['StackQpc'],r['Address'],r['Size']);h.require(native['births'].get(k,0)==1,'missing native current allocation-stack association')
            seen[g]=r
    for g,b in births.items():
        if not b['seeded']:h.require(native['births'].get(birthkey(b,pid,heap),0)==1,'realloc-related birth lacks native witness')
    for g,d in deaths.items():
        h.require(g in births and (d['address'],d['size'])==(births[g]['address'],births[g]['size']),'death generation identity')
        h.require(native['deaths'].get(deathkey(d),0)==1,'missing/ambiguous native retirement witness')
    for parent,(child,qpc) in edges.items():
        k=edgekey(parent,child,qpc,births,deaths);h.require(native['edges'].get(k,0)==1,'realloc successor lacks native address/size/thread identity')
        h.require(births[child]['qpc']<=qpc and deaths[parent]['qpc']<=qpc,'realloc temporal identity')
    counts=[]
    for first,last in zip(list(phases),list(phases)[1:]):
        left={r['Generation'] for r in phases[first]};right={r['Generation'] for r in phases[last]}
        h.require(all(g in deaths and cuts[first]<deaths[g]['qpc']<=cuts[last] for g in left-right),'retired phase generation missing native free')
        counts.append(dict(From=first,To=last,SameGenerationSurvivors=len(left&right),RetiredGenerations=len(left-right),NewLiveGenerations=len(right-left)))
    return counts

def configuration(native_root,derived,config,dest):
    name=config['Configuration'];run=native_root/'runs'/(name+'-graceful');folder=dest/name;folder.mkdir()
    prior=h.read(native_root/'trace-verification.json');tc=next(x for x in prior['Configurations'] if x['Configuration']==name)
    snapshots={k:h.decode((run/'heap'/(k+'.bin')).read_bytes(),config['Pid']) for k in ['0','1','2','shutdown']};cuts={r['Label']:r['SnapshotQpc'] for r in tc['Epochs']}
    model,states,summary=c.replay(v.records(run/'heap-decoded/native/raw.bin'),snapshots,cuts)
    h.require(summary=={k:value for k,value in config.items() if k!='Configuration'},'replayed native summary differs from D1')
    phases={label:decode_generations(derived/name/(label+'-generations.bin')) for label in snapshots}
    for label,rr in phases.items():
        h.require({r['Address']:r['Generation'] for r in rr}=={a:b.generation for a,b in states[label].items()},'replayed generation differs from frozen exported witness')
    selected={r['Generation'] for rr in phases.values() for r in rr}|set(model.successors)|{child for child,qpc in model.successors.values()}
    births={g:model.births[g]._asdict() for g in selected};deaths={g:d._asdict() for g,d in model.deaths.items() if g in selected};edges=model.successors.copy()
    pid=summary['Pid'];heap=summary['Heap'];wanted_births={birthkey(b,pid,heap) for b in births.values() if not b['seeded']}
    wanted_births.update((heap,pid,r['BirthTid'],r['StackQpc'],r['Address'],r['Size']) for rr in phases.values() for r in rr if r['StackQpc'])
    wanted_deaths={deathkey(d) for d in deaths.values()};wanted_edges={edgekey(parent,child,qpc,births,deaths) for parent,(child,qpc) in edges.items()}
    native={'births':Counter(),'deaths':Counter(),'edges':Counter()};del model,states
    for r in v.records(run/'heap-decoded/native/raw.bin'):
        if r['Guid']!=str(v.t.HEAP) or r['Pid']!=pid:continue
        op=r['Opcode'];qpc=r['Qpc']
        if op not in [33,34,35,36]:continue
        if op==35:
            if struct.unpack('<Q',r['Payload'])[0]!=heap:continue
            d=(35,qpc,0,0)
            if d in wanted_deaths:native['deaths'][d]+=1
            continue
        f=v.allocation(r)
        if f[0]!=heap:continue
        if op in [33,34]:
            b=(heap,pid,r['Tid'],qpc,f[1],f[2] if op==33 else f[3])
            if b in wanted_births:native['births'][b]+=1
        if op==36:
            d=(36,qpc,f[1],0)
            if d in wanted_deaths:native['deaths'][d]+=1
        if op==34:
            d=(34,qpc,f[2],f[4]);e=(qpc,f[2],f[1],f[4],f[3],r['Tid'])
            if d in wanted_deaths:native['deaths'][d]+=1
            if e in wanted_edges:native['edges'][e]+=1
    counts=check(phases,snapshots,cuts,births,deaths,edges,native,pid,heap)
    h.require(all(all(row[k]==expected[k] for k in ['SameGenerationSurvivors','RetiredGenerations','NewLiveGenerations']) for row,expected in zip(counts,config['Intervals'])),'independent cohort counts')
    negative=[]
    for case in ['missing-epoch','missing-baseline-block','missing-retirement','wrong-birth-qpc','wrong-realloc-successor','surviving-retired-generation']:
        pp={k:list(rr) for k,rr in phases.items()};bb=births.copy();dd=deaths.copy();ee=edges.copy()
        if case=='missing-epoch':del pp['1']
        if case=='missing-baseline-block':pp['0']=pp['0'][1:]
        if case=='missing-retirement':
            g=next(g for g in {r['Generation'] for r in phases['0']}-{r['Generation'] for r in phases['1']});del dd[g]
        if case=='wrong-birth-qpc':
            g=next(g for g,b in bb.items() if not b['seeded']);bb[g]=dict(bb[g],qpc=bb[g]['qpc']+1)
        if case=='wrong-realloc-successor':
            parent=next(iter(ee));child,qpc=ee[parent];other=next(g for g in bb if g!=child and g!=parent);ee[parent]=(other,qpc)
        if case=='surviving-retired-generation':
            old=next(r for r in pp['0'] if r['Generation'] in dd and dd[r['Generation']]['qpc']<=cuts['1']);pp['1'].append(old)
        try:check(pp,snapshots,cuts,bb,dd,ee,native,pid,heap)
        except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    h.write(folder/'native-generation-witnesses.json',dict(Births=[dict(Generation=g,**b) for g,b in sorted(births.items())],Deaths=[dict(Generation=g,**d) for g,d in sorted(deaths.items())],ReallocSuccessors=[dict(Parent=g,Child=child,Qpc=qpc) for g,(child,qpc) in sorted(edges.items())]))
    return dict(Configuration=name,Pid=pid,Heap=heap,ExportedPhaseRows=sum(map(len,phases.values())),NativeBirthOrStackWitnesses=len(native['births']),NativeRetirementWitnesses=len(native['deaths']),NativeReallocSuccessorWitnesses=len(native['edges']),IndependentCohortCounts=counts,NegativeControls=negative,EveryExportedBusyAddressAndSizeVerified=True,AllReallocSuccessorsHaveNativeWitnesses=True)

def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--derived',type=Path,required=True);p.add_argument('--native',type=Path,required=True);a=p.parse_args();dest=c.ART/a.id
    h.require(not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id,'output exists/wrong date');dest.mkdir();(dest/'source').mkdir()
    for name in c.SOURCES+['Verify-HeapCohortWitnesses.py']:shutil.copyfile(Path(__file__).parent/name,dest/'source'/name)
    started=datetime.now(timezone.utc).isoformat();code=0
    with (dest/'raw.log').open('x',encoding='utf-8') as log,contextlib.redirect_stdout(log),contextlib.redirect_stderr(log):
        try:
            d=h.read(a.derived/'verification.json');h.require(d['Verdict']=='SCOPED_COMPLETE_DEFAULT_HEAP_BASELINE_REPLAY_PASS','derived receipt')
            for s in d['Sources']:h.require(h.sha(Path(__file__).parent/s['Name'])==s['SHA256'],'calibrated/replayed source changed')
            h.require(h.sha(a.native/'trace-verification.json')==d['NativeTraceVerificationSHA256']=='DECF9134FED51292D6187BE400438C7257EAB2ED803F6C4DF62035007B8B5C20','native trace receipt changed')
            rr=[configuration(a.native,a.derived,config,dest) for config in d['Configurations']]
            h.write(dest/'verification.json',dict(Verdict='SCOPED_NATIVE_COHORT_WITNESSES_PASS',Configurations=rr,DerivedReceiptSHA256=h.sha(a.derived/'verification.json'),NativeTraceReceiptSHA256=d['NativeTraceVerificationSHA256'],NewNativeProcesses=0,NewEtls=0,NewTrxRuns=0,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LedgerAppended=False,WholeIdStatus='IN_PROGRESS',LogicalOwnerClaim=False,NativeHeapLeakFreedomClaim=False))
        except Exception as e:traceback.print_exc();h.write(dest/'failure.json',dict(Error=str(e),Type=type(e).__name__,ClaimedAsPass=False));code=1
    h.write(dest/'command.json',dict(Command=[sys.executable,*sys.argv],Pid=os.getpid(),StartedUtc=started,EndedUtc=datetime.now(timezone.utc).isoformat(),ExitCode=code,RawLogSHA256=h.sha(dest/'raw.log'),OfflineOnly=True))
    print(json.dumps(dict(Output=str(dest),ExitCode=code,ReceiptSHA256=h.sha(dest/'verification.json') if code==0 else None)));sys.exit(code)
if __name__=='__main__':main()
