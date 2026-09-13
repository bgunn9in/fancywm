"""Decode each completed owned heap ETL into fresh raw/XML/CSV evidence."""
import argparse, importlib.util, subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('h',Path(__file__).with_name('Verify-NativeHeap.py'));h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
def main():
    p=argparse.ArgumentParser();p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--decoder',type=Path,required=True);p.add_argument('--configs',nargs='+',default=['Debug','Release']);a=p.parse_args();root=a.snapshot.resolve();decoder=a.decoder.resolve();tool=decoder.parent
    h.require(h.read(tool/'build-receipt.json')['ExitCode']==0 and h.sha(tool/'source/Decode.cpp')==h.sha(root/'source/scripts/performance/native-heap/Decode.cpp'),'decoder source/build')
    expected=next(x for x in h.read(tool/'binary-manifest.json') if x['Path']==decoder.name);h.require(h.sha(decoder)==expected['SHA256'],'decoder binary')
    xperf=h.read(root/'heap-trace-provenance.json')['XperfPath']
    for config in a.configs:
        folder=root/'runs'/(config+'-graceful');etl=folder/'heap.etl'
        h.require(h.read(root/'validation'/(config+'-heap-cleanup.json'))['Inactive'],'own trace still active')
        target=folder/'heap-decoded';h.require(not target.exists(),'decoded evidence already exists');target.mkdir()
        commands=[('native',[str(decoder),str(etl),str(target/'native')]),('stats',[xperf,'-i',str(etl),'-o',str(target/'stats.txt'),'-a','tracestats']),('csv',[xperf,'-i',str(etl),'-o',str(target/'events.csv'),'-a','dumper']),('xml',['tracerpt',str(etl),'-o',str(target/'events.xml'),'-of','XML','-summary',str(target/'summary.txt')])]
        records=[]
        for name,cmd in commands:
            with (target/(name+'.stdout')).open('xb') as out,(target/(name+'.stderr')).open('xb') as err:r=subprocess.run(cmd,stdout=out,stderr=err,timeout=600)
            h.write(target/(name+'-command.json'),dict(Command=cmd,ExitCode=r.returncode));h.require(r.returncode==0,'native decode failed '+name);records.append(dict(Name=name,ExitCode=r.returncode))
        h.write(target/'decode-receipt.json',dict(Configuration=config,DecoderPath=str(decoder),DecoderSHA256=h.sha(decoder),DecoderSourceSHA256=h.sha(tool/'source/Decode.cpp'),EtlSHA256=h.sha(etl),Commands=records))
        print(config,'decoded',flush=True)
if __name__=='__main__':main()
