"""Own native producer and in-map PSS clones, with exact isolated binaries."""
import argparse,importlib.util,os,shutil,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('s',Path(__file__).with_name('Finalize-NativeHeapEvidence.py'));s=importlib.util.module_from_spec(spec);spec.loader.exec_module(s)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--tool',type=Path,required=True);p.add_argument('--observer-tool',type=Path,required=True);p.add_argument('--config',choices=['Debug','Release'],required=True);p.add_argument('--program',default='Methods');a=p.parse_args();dest=s.ART/a.id;assert not dest.exists() and s.datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir();shutil.copyfile(__file__,dest/Path(__file__).name)
    assert s.read(s.ART/'FWM-D3D-MAP-20260913-R0/verification.json')['Verdict']=='D3D_MAPPING_ENTRY_VERIFIED';producer=a.tool.resolve();observer=a.observer_tool.resolve();pss=s.ART/'FWM-PSS-HEAP-20260913-T5'
    for tool in [producer,observer]:
        for row in s.read(tool/'provenance.json')['Sources']+s.read(tool/'provenance.json')['Binaries']:assert s.info(tool/row['Path'])=={k:row[k] for k in ['Bytes','SHA256']}
    for row in s.read(pss/'provenance.json')['Sources']+s.read(pss/'provenance.json')['Binaries']:assert s.sha(pss/row['Path'])==row['SHA256']
    assert s.sha(pss/'provenance.json')==s.read(s.ART/'FWM-PSS-HEAP-20260913-V1/verification.json')['ToolProvenanceSHA256']
    dll=dest/'FancyWM.MapPss.dll';shutil.copyfile(pss/'binaries'/a.config/'FancyWM.NativePss.dll',dll);env=dict(os.environ);env['FWM_D3D9_MAP_PSS_DLL']=str(dll);env['FWM_D3D9_MAP_ROOT']=str(dest/'mapped')
    cmd=[str(producer/'binaries'/a.config/(a.program+'.exe')),str(observer/'binaries'/a.config/'Observer9.dll')];start=s.datetime.now(s.timezone.utc).isoformat()
    with (dest/'raw.stdout').open('xb') as out,(dest/'raw.stderr').open('xb') as err:
        proc=subprocess.Popen(cmd,stdout=out,stderr=err,cwd=dest,env=env,creationflags=0x08000000);s.write(dest/'created.json',dict(Command=cmd,Pid=proc.pid,StartedUtc=start,Configuration=a.config,Tool=str(producer),ToolProvenanceSHA256=s.sha(producer/'provenance.json'),ObserverTool=str(observer),ObserverProvenanceSHA256=s.sha(observer/'provenance.json'),MappedPssSHA256=s.sha(dll),Environment={k:v for k,v in sorted(env.items()) if k.startswith('FWM_')}));forced=False
        try:code=proc.wait(timeout=120)
        except subprocess.TimeoutExpired:forced=True;proc.kill();code=proc.wait(timeout=20)
    s.write(dest/'exited.json',dict(Pid=proc.pid,ExitCode=code,ExitHex=f'0x{code&4294967295:08X}',EndedUtc=s.datetime.now(s.timezone.utc).isoformat(),ForcedCleanup=forced,NewTracingSession=False));assert code==0 and not forced;print(dest,flush=True)
if __name__=='__main__':main()
