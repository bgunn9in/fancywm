"""Reconcile the same native control with the exact graph replay model."""
import argparse, copy, importlib.util
from pathlib import Path
from heap_trace_model import Replay
spec=importlib.util.spec_from_file_location('v',Path(__file__).with_name('Verify-HeapGraphTrace.py'));v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v);h=v.h
def replay(rr):
    model=Replay()
    for r in rr:
        if r['Guid']!=str(v.t.HEAP):continue
        if r['Opcode']==35:model.destroy(v.struct.unpack('<Q',r['Payload'])[0])
        if r['Opcode'] in [33,34,36]:model.apply(r['Opcode'],r['Tid'],r['Qpc'],v.allocation(r))
    return model
def main():
    p=argparse.ArgumentParser();p.add_argument('--root',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();h.require(not a.output.exists(),'receipt exists')
    proof=h.read(a.root/'verification.json');h.require(proof['Verdict']=='SCOPED_NATIVE_HEAP_TRACE_ADMISSION_PASS','prior native control')
    rr=list(v.records(a.root/'native-decoded/raw.bin'));model=replay(rr);pairs=proof['NativeControlPairs'];expected={r['ReallocQpc'] for r in pairs if r['Original']!=r['Final']}
    h.require({r['ReallocQpc'] for r in model.aliases}==expected and len(expected)==48,'native nested realloc control coverage')
    h.require(all((p['Heap'],p['Final']) not in model.live for p in pairs),'native control allocation survived')
    negative=[];first=model.aliases[0]
    for case in ['missing-primitive-free','wrong-original-size','wrong-primitive-thread']:
        bad=copy.deepcopy(rr)
        if case=='missing-primitive-free':bad.remove(next(r for r in bad if r['Guid']==str(v.t.HEAP) and r['Qpc']==first['FreeQpc']))
        if case=='wrong-original-size':
            r=next(r for r in bad if r['Guid']==str(v.t.HEAP) and r['Qpc']==first['ReallocQpc']);payload=bytearray(r['Payload']);v.struct.pack_into('<Q',payload,32,123);r['Payload']=bytes(payload)
        if case=='wrong-primitive-thread':next(r for r in bad if r['Guid']==str(v.t.HEAP) and r['Qpc']==first['AllocQpc'])['Tid']+=1
        try:replay(bad)
        except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    h.write(a.output,dict(Verdict='SCOPED_NATIVE_REALLOC_MODEL_PASS',NativePairs=48,NewNativeProcesses=0,AdmissionSHA256=h.sha(a.root/'verification.json'),ModelSHA256=h.sha(Path(__file__).with_name('heap_trace_model.py')),Aliases=model.aliases,NegativeControls=negative,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS'))
    print('REALLOC MODEL PASS',h.sha(a.output))
if __name__=='__main__':main()
