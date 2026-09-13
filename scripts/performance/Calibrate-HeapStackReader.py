"""Validate full-stack decoding and reject corrupted tails, including >32 frames."""
import argparse, collections, importlib.util, io, subprocess, struct
from pathlib import Path
spec=importlib.util.spec_from_file_location('v',Path(__file__).with_name('Verify-HeapGraphTrace.py'));v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v);h=v.h
class Memory:
    def __init__(self,data):self.data=data
    def open(self,mode):assert mode=='rb';return io.BytesIO(self.data)
def main():
    p=argparse.ArgumentParser();p.add_argument('--tool',type=Path,required=True);p.add_argument('--control',type=Path,required=True);p.add_argument('--graph',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();h.require(not a.output.exists(),'receipt exists')
    pid=h.read(a.control/'admission-result.json')['Pid'];out=a.control/'traceevent-stacks-v2';h.require(not out.exists(),'offline reader output exists')
    cmd=[str(a.tool.resolve()/'binaries/HeapStackReader.exe'),str((a.control/'heap.etl').resolve()),str(out.resolve()),str(pid)]
    with (a.control/'traceevent-v2.stdout').open('xb') as stdout,(a.control/'traceevent-v2.stderr').open('xb') as stderr:
        proc=subprocess.Popen(cmd,stdout=stdout,stderr=stderr);code=proc.wait(timeout=60)
    h.write(a.control/'traceevent-v2-command.json',dict(Command=cmd,ProcessId=proc.pid,ExitCode=code,Exited=proc.poll() is not None,NewNativeApplicationRun=False,ToolProvenanceSHA256=h.sha(a.tool/'provenance.json')));h.require(code==0,'offline reader failed')
    payloads=collections.Counter(r['Payload'] for r in v.records(a.control/'native-decoded/raw.bin') if r['Guid']==str(v.t.STACK));count,frames=v.stack_reader(out/'stacks.bin',payloads,pid);h.require(count==166,'native stack control count')
    graph=a.graph/'runs/Debug-graceful';long_record=next(r for r in v.records(graph/'heap-decoded/native/raw.bin') if r['Guid']==str(v.t.STACK) and len(r['Payload'])>16+32*8)
    payload=long_record['Payload'];qpc,owner,tid=struct.unpack_from('<QII',payload);length=(len(payload)-16)//8;data=payload[:16]+struct.pack('<I',length)+payload[16:]
    v.stack_reader(Memory(data),collections.Counter({payload:1}),owner)
    negative=[]
    for case in ['missing-stack','wrong-qpc','wrong-pid','changed-tail-after-32','duplicate-stack']:
        bad=bytearray(data)
        if case=='missing-stack':bad=bytearray()
        if case=='wrong-qpc':struct.pack_into('<Q',bad,0,qpc+1)
        if case=='wrong-pid':struct.pack_into('<I',bad,8,owner+1)
        if case=='changed-tail-after-32':bad[-1]^=1
        if case=='duplicate-stack':bad+=data
        try:v.stack_reader(Memory(bad),collections.Counter({payload:1}),owner)
        except ValueError as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    h.write(a.output,dict(Verdict='SCOPED_INDEPENDENT_FULL_STACK_READER_PASS',ControlStackEvents=count,ControlFrames=frames,LongNativeStackFrames=length,NativeLongStackPid=owner,NativeLongStackQpc=qpc,NegativeControls=negative,ToolProvenanceSHA256=h.sha(a.tool/'provenance.json'),NewNativeApplicationRuns=0,ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS'))
    print('FULL STACK READER PASS',h.sha(a.output))
if __name__=='__main__':main()
