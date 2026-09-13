"""Fault injection into retained raw bytes; never alters the native evidence."""
import argparse,copy,importlib.util,json,shutil,struct
from pathlib import Path
import clr_heap_model as c
spec=importlib.util.spec_from_file_location('h',Path(__file__).with_name('Verify-NativeHeap.py'));h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
class Virtual:
    def __init__(self,files,key=None):self.files=files;self.key=key
    def __truediv__(self,key):return Virtual(self.files,key)
    def read_bytes(self):return self.files[self.key]
    def read_text(self,**kwargs):return self.files[self.key].decode(kwargs.get('encoding','utf-8'))
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);a=p.parse_args();root=Path.cwd()/'artifacts/performance'/a.id;h.require(not root.exists(),'negative evidence exists');root.mkdir()
    for f in [Path(__file__),Path(c.__file__)]:shutil.copyfile(f,root/f.name)
    source=Path.cwd()/'artifacts/performance/FWM-CLR-HEAP-20260913-P1';pid=h.read(source/'target-created.json')['Pid']
    original={name:(source/'clr'/name).read_bytes() for name in ['raw.bin','events.jsonl']};rows=[json.loads(x) for x in original['events.jsonl'].decode().splitlines()]
    offset=0;offsets=[]
    for r in rows:offsets.append(offset);offset+=44+r['PayloadBytes']
    method=next(i for i,r in enumerate(rows) if r['EventName']=='Method/DCStopVerbose' and r['Fields']['MethodNamespace']=='WarmAllocator');base=offsets[method];negative=[]
    for case in ['wrong-pid','wrong-qpc','unsupported-method-version','wrong-code-address','wrong-module-id','wrong-rejit-id','truncated-payload','missing-raw-record','missing-json-record']:
        raw=bytearray(original['raw.bin']);rr=copy.deepcopy(rows)
        if case=='wrong-pid':struct.pack_into('<I',raw,base+16,pid+1);rr[method]['Pid']=pid+1
        if case=='wrong-qpc':struct.pack_into('<q',raw,base+24,rr[method]['Qpc']+1)
        if case=='unsupported-method-version':raw[base+34]=9;rr[method]['Version']=9
        if case=='wrong-code-address':raw[base+44+16]^=1
        if case=='wrong-module-id':raw[base+44+8]^=1
        if case=='wrong-rejit-id':
            j=next(i for i,r in enumerate(rows) if r['EventName']=='Method/LoadVerbose' and r['Version']==2);raw[offsets[j]+44+rows[j]['PayloadBytes']-8]^=1
        if case=='truncated-payload':raw=raw[:-1]
        if case=='missing-raw-record':del raw[base:base+44+rows[method]['PayloadBytes']]
        if case=='missing-json-record':rr.pop(method)
        files={'raw.bin':bytes(raw),'events.jsonl':('\n'.join(json.dumps(x) for x in rr)+'\n').encode()}
        try:c.decode(Virtual(files),pid)
        except (ValueError,struct.error) as e:negative.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative accepted '+case)
    for name,data in original.items():h.require((source/'clr'/name).read_bytes()==data,'original evidence changed')
    h.write(root/'verification.json',dict(Verdict='SCOPED_CLR_RAW_NEGATIVE_CONTROLS_PASS',NegativeControls=negative,NewNativeProcesses=0,OriginalEvidenceUnchanged=True,SourceSHA256=h.sha(Path(c.__file__)),ProductionChanged=False,PerformanceClaim=False,StageAccepted=False))
    print('CLR raw negative controls PASS',len(negative),h.sha(root/'verification.json'))
if __name__=='__main__':main()
