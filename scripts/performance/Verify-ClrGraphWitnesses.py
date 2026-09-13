"""Reopen caller exports and match each selected witness to raw native/CLR data."""
import argparse,collections,contextlib,copy,importlib.util,json,os,shutil,struct,sys,traceback
from datetime import datetime,timezone
from pathlib import Path
import clr_heap_model as c
spec=importlib.util.spec_from_file_location('v',Path(__file__).with_name('Verify-HeapGraphTrace.py'));v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v);h=v.h
def lines(p):
    with p.open(encoding='utf-8') as f:
        for line in f:yield json.loads(line)
def caller(w,m,events,modules):
    r=events[m['MethodSequence']];f=r['Fields'];module=modules[m['ModuleSequence']];q=w['Qpc'];pc=m['PC']
    h.require(m['Frame']<len(w['Frames']) and w['Frames'][m['Frame']]==pc,'caller PC differs from native stack')
    h.require(all(m[k]==f[key] for k,key in [('MethodID','MethodID'),('ModuleID','ModuleID'),('ReJITID','ReJITID'),('CodeStart','MethodStartAddress'),('CodeSize','MethodSize')]),'caller method identity/version differs')
    h.require(f['MethodStartAddress']<=pc<f['MethodStartAddress']+f['MethodSize'] and m['Begin']<=q and (m['End'] is None or q<m['End']),'stale/outside native code range')
    h.require(m['Name']==f['MethodNamespace']+'.'+f['MethodName'] and m['Signature']==f['MethodSignature'],'caller name/signature mismatch')
    h.require(module['Fields']['ModuleID']==f['ModuleID'] and module['ClrInstanceID']==f['ClrInstanceID'] and module['Begin']<=q and (module['End'] is None or q<module['End']),'caller module generation stale')
    h.require(m['ModulePath']==module['Fields']['ModuleILPath'],'caller module path mismatch')
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--analysis',type=Path,required=True);a=p.parse_args();art=Path.cwd()/'artifacts/performance';dest=art/a.id;h.require(not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id,'output exists/wrong date');dest.mkdir();started=datetime.now(timezone.utc).isoformat();code=0
    for f in [Path(__file__),Path(c.__file__)]:shutil.copyfile(f,dest/f.name)
    analysis=a.analysis.resolve();proof=h.read(analysis/'verification.json');root=Path(proof['Graph']);results=[]
    with (dest/'raw.log').open('x',encoding='utf-8') as log,contextlib.redirect_stdout(log),contextlib.redirect_stderr(log):
        try:
            h.require(proof['Verdict']=='SCOPED_FULLGRAPH_CLR_NATIVE_CALLERS_VERIFIED' and not proof['LogicalOwnerClaim'] and not proof['NativeHeapLeakFreedomClaim'],'invalid graph attribution claim')
            for config in proof['Configurations']:
                label=config['Configuration'];folder=analysis/label;run=root/'runs'/(label+'-graceful');pid=config['Pid'];rr,_=c.decode(run/'clr',pid);events={r['Sequence']:r for r in rr};timeline=c.Timeline(rr);modules={m['Sequence']:m for m in timeline.modules};methods={m['Sequence']:m for m in timeline.methods}
                witnesses={(w['Tid'],w['Qpc']):w for w in lines(folder/'stack-callers.jsonl')};h.require(len(witnesses)==config['SelectedLiveStackWitnesses'],'selected witness count');wanted=set(witnesses);operations={};matched=set();decoded=Path(h.read(folder/'decode-reuse.json')['Original']) if (folder/'decode-reuse.json').exists() else folder
                for r in v.records(decoded/'native/raw.bin'):
                    if r['Guid']==str(v.t.STACK):
                        q,owner,tid=struct.unpack_from('<QII',r['Payload']);key=(tid,q)
                        if key in wanted:
                            h.require(owner==pid and key not in matched and struct.pack('<'+'Q'*len(witnesses[key]['Frames']),*witnesses[key]['Frames'])==r['Payload'][16:],'exported native stack differs');matched.add(key)
                    elif r['Opcode'] in [33,34] and (r['Tid'],r['Qpc']) in wanted:
                        key=r['Tid'],r['Qpc'];h.require(key not in operations and r['Pid']==pid,'ambiguous/foreign native allocation');operations[key]=(r['Opcode'],v.allocation(r))
                h.require(matched==wanted==operations.keys(),'missing selected native stack/allocation')
                caller_count=0
                for w in witnesses.values():
                    for m in w['ManagedCallers']:
                        caller(w,m,events,modules);method=methods[m['MethodSequence']];h.require((m['Begin'],m['End'],m['ModuleSequence'])==(method['Begin'],method['End'],method['ModuleSequence']),'exported generation boundary differs');caller_count+=1
                phase_counts=collections.Counter();phase_bytes=collections.Counter();phase_categories=collections.defaultdict(collections.Counter);rows=0
                for r in lines(folder/'phase-callers.jsonl'):
                    h.require(r['EventStreamOnly'] and not r['LogicalOwnerClaim'] and not r['Seeded'],'invalid atomic/owner claim');key=r['StackTid'],r['StackQpc'];h.require(key in witnesses,'missing phase stack');op,fields=operations[key];size=fields[2] if op==33 else fields[3]
                    h.require(fields[1]==r['Address'] and size==r['Bytes'],'phase native address/size mismatch');w=witnesses[key];nearest=w['ManagedCallers'][0]['Name'] if w['ManagedCallers'] else None
                    h.require(nearest==r['NearestManagedCaller'] and r['Category']==('TracedManagedCaller' if nearest else 'TracedNoClrCaller'),'phase caller/category mismatch')
                    phase_counts[r['Phase']]+=1;phase_bytes[r['Phase']]+=r['Bytes'];phase_categories[r['Phase']][r['Category']]+=1;rows+=1
                h.require(set(phase_counts)=={'0','1','2','shutdown'},'missing phase scenario')
                for phase in config['Phases']:h.require(phase_counts[phase['Phase']]==phase['Blocks'] and phase_bytes[phase['Phase']]==phase['Bytes'] and dict(phase_categories[phase['Phase']])==phase['Categories'],'phase receipt aggregation differs')
                example=next(w for w in witnesses.values() if w['ManagedCallers']);negative=[]
                for case in ['wrong-pc','wrong-rejit-id','stale-method-lifetime','wrong-module-generation','wrong-stack-tail']:
                    w=copy.deepcopy(example);m=w['ManagedCallers'][0]
                    if case=='wrong-pc':m['PC']+=1
                    if case=='wrong-rejit-id':m['ReJITID']+=1
                    if case=='stale-method-lifetime':m['End']=w['Qpc']
                    if case=='wrong-module-generation':m['ModuleSequence']=-1
                    if case=='wrong-stack-tail':w['Frames'][-1]^=1
                    try:
                        if case=='wrong-stack-tail':h.require(w['Frames']==example['Frames'],'raw native tail differs')
                        caller(w,m,events,modules)
                    except (ValueError,KeyError) as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
                    else:raise ValueError('negative accepted '+case)
                results.append(dict(Configuration=label,Pid=pid,SelectedNativeStackWitnesses=len(witnesses),ManagedCallerWitnesses=caller_count,PhaseRows=rows,NegativeControls=negative,EverySelectedNativePayloadAndAllocationMatches=True,EveryCallerVersionAndLifetimeMatches=True));print(label,results[-1],flush=True)
            h.write(dest/'verification.json',dict(Verdict='SCOPED_CLR_NATIVE_CALLER_WITNESSES_PASS',AnalysisSHA256=h.sha(analysis/'verification.json'),Configurations=results,NewNativeProcesses=0,NewEtls=0,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,LogicalOwnerClaim=False,WholeIdStatus='IN_PROGRESS',SourceSHA256={f.name:h.sha(f) for f in [Path(__file__),Path(c.__file__)]}))
        except Exception as e:traceback.print_exc();h.write(dest/'failure.json',dict(Error=str(e),ClaimedAsPass=False));code=1
    h.write(dest/'command.json',dict(Command=[sys.executable,*sys.argv],Pid=os.getpid(),StartedUtc=started,EndedUtc=datetime.now(timezone.utc).isoformat(),ExitCode=code,OfflineOnly=True,RawLogSHA256=h.sha(dest/'raw.log')));print(dest,'ExitCode',code,flush=True);sys.exit(code)
if __name__=='__main__':main()
