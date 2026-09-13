"""Export selected raw ETW rows in one xperf pass; retain complete-stream hash.

The full immutable ETL remains authoritative. Thread CSV retains every CSwitch,
DWM CSV every Dwm-Core event, and DXG CSV all events used by the independent
flip verifier. Omitted DXG profiler/stack rows remain available in the ETL.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import subprocess


def digest(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest().upper()


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--etl',type=pathlib.Path,required=True)
    p.add_argument('--output',type=pathlib.Path,required=True)
    p.add_argument('--presentmon',type=pathlib.Path,required=True)
    p.add_argument('--xperf',type=pathlib.Path,default=pathlib.Path(r'C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe'))
    a=p.parse_args();a.output.mkdir(parents=True,exist_ok=False)
    args=[str(a.xperf),'-i',str(a.etl.resolve()),'-a','dumper','-provider',
          '{3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c}','{9e9bba3c-2e38-40cb-99f4-9e8281425164}',
          '{802ec45a-1e99-4b83-9920-87c98277ba9d}','-add_fieldnames']
    prefixes=tuple(('Microsoft-Windows-DxgKrnl/'+s).encode() for s in
                   ['FlipMultiPlaneOverlay/','QueuePacket/','MMIOFlipMultiPlaneOverlay/',
                    'MMIOFlipMultiPlaneOverlay3/','VSyncDPC/','VSyncDPCMultiPlane/','VSyncHwFlipQueueLogUpdate/'])
    combined=hashlib.sha256();total=0;counts=dict(thread=0,dwm=0,dxg=0)
    began=datetime.datetime.now(datetime.timezone.utc).isoformat()
    paths={key:a.output/f'{key}-events.csv' for key in counts}
    streams={key:path.open('xb') for key,path in paths.items()}
    try:
        with (a.output/'xperf.stderr').open('xb') as stderr:
            process=subprocess.Popen(args,stdout=subprocess.PIPE,stderr=stderr)
            for line in process.stdout:
                combined.update(line);total+=len(line)
                key=None
                if line.lstrip().startswith(b'CSwitch,'):key='thread'
                elif line.startswith(b'Microsoft-Windows-Dwm-Core/'):key='dwm'
                elif line.startswith(prefixes):key='dxg'
                if key:
                    streams[key].write(line);counts[key]+=1
            process.wait()
    finally:
        for stream in streams.values():stream.close()
    result=dict(Arguments=args,ExitCode=process.returncode,StartedUtc=began,
                FinishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                CompleteStdoutBytes=total,CompleteStdoutSHA256=combined.hexdigest().upper(),
                SelectedLinesIncludingSchema=counts,ETLSHA256=digest(a.etl),XperfSHA256=digest(a.xperf),
                ExporterSHA256=digest(pathlib.Path(__file__)),
                Exports=[dict(Path=str(path.resolve()),Bytes=path.stat().st_size,SHA256=digest(path)) for path in paths.values()])
    with (a.output/'export-command.json').open('x',encoding='utf-8') as stream:json.dump(result,stream,indent=2)
    assert process.returncode==0,'xperf export failed; retained raw output and error.'
    print(json.dumps(result,indent=2),flush=True)
    args=[str(a.presentmon.resolve()),'--etl_file',str(a.etl.resolve()),'--output_file',str((a.output/'presents.csv').resolve()),
          '--qpc_time','--v1_metrics','--no_console_stats']
    began=datetime.datetime.now(datetime.timezone.utc).isoformat()
    with (a.output/'presentmon.stdout').open('xb') as stdout,(a.output/'presentmon.stderr').open('xb') as stderr:
        process=subprocess.run(args,stdout=stdout,stderr=stderr)
    result=dict(Arguments=args,ExitCode=process.returncode,StartedUtc=began,
                FinishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                ToolSHA256=digest(a.presentmon),OutputSHA256=digest(a.output/'presents.csv') if (a.output/'presents.csv').exists() else None)
    with (a.output/'presentmon-command.json').open('x',encoding='utf-8') as stream:json.dump(result,stream,indent=2)
    assert process.returncode==0,'PresentMon offline export failed.'
    print(json.dumps(result,indent=2),flush=True)


if __name__=='__main__':main()
