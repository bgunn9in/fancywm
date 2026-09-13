"""Independently check target geometry -> DWM frame -> DXG submission -> hardware flip completion.

This checks raw event payload identifiers, not a nearest-event time heuristic.
The hardware flip log is OS/driver display telemetry, not a photon measurement.
"""
import argparse
import collections
import csv
import hashlib
import json
import pathlib
import re
import struct


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def hash_file(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest().upper()


def event_rows(path, names):
    with path.open(encoding='utf-8-sig',newline='') as stream:
        for line in stream:
            if not line.startswith(names):continue
            row=next(csv.reader([line],skipinitialspace=True))
            if len(row)>8 and row[1].isdigit():
                yield row,{s.split(' : ',1)[0]:s.split(' : ',1)[1] for s in row[9:] if ' : ' in s}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for arg in ['snapshot','derivation','dxg-events','output']:
        parser.add_argument('--'+arg,type=pathlib.Path,required=True)
    a=parser.parse_args();a.output.mkdir(parents=True,exist_ok=False)
    report=read_json(a.derivation/'derivation.json')
    for inp in report['Inputs']:
        path=pathlib.Path(inp['Path'])
        assert path.stat().st_size==inp['Bytes'] and hash_file(path)==inp['SHA256'],path
    for output in report['Outputs']:
        path=pathlib.Path(output['Path'])
        assert path.parent.resolve()==a.derivation.resolve()
        assert path.stat().st_size==output['Bytes'] and hash_file(path)==output['SHA256'],path
    assert not report['SchedulingFailures']
    with (a.derivation/'target-presentation-correlation.csv').open() as stream:targets=list(csv.DictReader(stream))
    assert targets and all(t['CorrelatedDisplayedFrame']=='True' for t in targets)
    wanted={(int(t['DrawEtwUs']),t['Visual'],int(t['ProcessId'])):t for t in targets}
    native=a.snapshot/'native';summaries={r:read_json(native/f'run-{r}/summary.json') for r in range(1,6)}
    dwm_path=next(pathlib.Path(i['Path']) for i in report['Inputs'] if i['Path'].endswith('dwm-events.csv'))
    sprites={};visuals={};current=None;frames={};proofs={};geometry_errors=[]
    initial_stops=[]
    first_native_qpc=min(t['StartQpc'] for s in summaries.values() for t in s['Transitions'])
    dwm_names=tuple('Microsoft-Windows-Dwm-Core/'+name for name in
                    ['WINDOWNODE_GDISPRITE_ASSOCIATION/','BIND_GDISPRITEBITMAP_FIRST_TOKEN/',
                     'SCHEDULE_PROCESS_FRAME/','CurrentFrameId/','ETWGUID_DRAWING_CONTEXT_STATE/',
                     'ETWGUID_VISUAL_RENDERCONTENT/','OVERLAY_PRESENT/'])
    for row,p in event_rows(dwm_path,dwm_names):
        name,time=row[0],int(row[1])
        if '/WINDOWNODE_GDISPRITE_ASSOCIATION/' in name:
            sprites[p['GdiSpritePointer']]=(int(p['windowHandle'],16),time)
        elif '/BIND_GDISPRITEBITMAP_FIRST_TOKEN/' in name:
            if p['logicalSurfaceImagePointer'] in sprites:
                hwnd,associated=sprites[p['logicalSurfaceImagePointer']]
                visuals[p['windowNodePointer']]=(hwnd,associated,time,p['logicalSurfaceImagePointer'])
        elif '/SCHEDULE_PROCESS_FRAME/win:Start' in name:
            assert current is None
            current=dict(pid=int(row[2].rsplit('(',1)[1].rstrip(') ')),tid=int(row[3]),start=time,
                         end=None,ident=None,begin=None,finish=None,content={})
        elif '/CurrentFrameId/' in name and current is not None:
            current['ident']=p['FrameId']
        elif '/ETWGUID_DRAWING_CONTEXT_STATE/' in name:
            key=(time,p['Visual'],int(p['AttributedProcessId']))
            if key not in wanted:continue
            target=wanted[key];s=summaries[int(target['Run'])];count=int(target['Count']);index=int(target['Target'])
            display=next(d for d in s['Displays'] if d['Count']==count)
            transition=next(t for t in s['Transitions'] if t['Transition']==int(target['Transition']))
            rect=(display['Destinations'] if transition['Iteration']%2 else display['Originals'])[index]
            link=visuals[p['Visual']]
            assert link[0]==display['Handles'][index]==int(target['Hwnd'])
            first_window_start=min(t['StartQpc'] for t in s['Transitions'] if t['Count']==count)
            assert link[2]*10+report['PresentMonOriginQpc']<=first_window_start
            assert float(target['PresentationSearchStartEtwUs'])<=time<float(target['PresentationSearchEndEtwUs'])
            later_starts=[t['StartQpc'] for summary in summaries.values() for t in summary['Transitions']
                          if t['StartQpc']>transition['StartQpc']]
            if later_starts:assert time*10+report['PresentMonOriginQpc']<min(later_starts)
            assert current['ident']==target['FrameId']
            transform=struct.unpack('<16f',bytes.fromhex(' '.join(p['TransformMatrix'].split()[1:65])))
            expected=(1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,float(rect['Left']),float(rect['Top']),0.,1.)
            assert transform==expected
            clip=[float(p[k]) for k in ['ClipLeft','ClipTop','ClipRight','ClipBottom']]
            covers=clip[0]<=rect['Left'] and clip[1]<=rect['Top'] and clip[2]>=rect['Right'] and clip[3]>=rect['Bottom']
            proofs[key]=dict(frame=current,rect=rect,link=link,covers=covers)
            if not covers:geometry_errors.append(dict(key=key,clip=clip,rectangle=rect))
        elif '/ETWGUID_VISUAL_RENDERCONTENT/' in name and current is not None:
            current['content'][p['Visual']]=[float(p[k]) for k in ['Left','Top','Right','Bottom']]
        elif '/OVERLAY_PRESENT/win:Start' in name and current is not None:current['begin']=time
        elif '/OVERLAY_PRESENT/win:Stop' in name and current is not None:current['finish']=time
        elif '/SCHEDULE_PROCESS_FRAME/win:Stop' in name:
            if current is None:
                assert not frames and not initial_stops and time*10+report['PresentMonOriginQpc'] < first_native_qpc
                initial_stops.append(dict(TimeUs=time,Process=row[2],ThreadId=int(row[3]),RawFields=row))
                continue
            assert current['pid']==int(row[2].rsplit('(',1)[1].rstrip(') ')) and current['tid']==int(row[3])
            current['end']=time;frames[current['ident']]=current;current=None
    assert current is None
    assert initial_stops==report['InitialIncompleteFrameStops']
    assert set(wanted)==set(proofs),'A reported target draw is missing from raw DWM events.'
    for key,proof in proofs.items():
        rect=proof['rect'];assert proof['frame']['content'][key[1]]==[0.,0.,float(rect['Width']),float(rect['Height'])]

    flip_events=[];pending={};queued={};submitted={};queue_contexts={};sequence_reuse=[]
    dxg_names=tuple('Microsoft-Windows-DxgKrnl/'+name for name in
                    ['FlipMultiPlaneOverlay/','QueuePacket/','MMIOFlipMultiPlaneOverlay/','MMIOFlipMultiPlaneOverlay3/',
                     'VSyncDPC/','VSyncHwFlipQueueLogUpdate/'])
    for row,p in event_rows(a.dxg_events,dxg_names):
        name,time=row[0],int(row[1]);tid=int(row[3]);pid=int(row[2].rsplit('(',1)[1].rstrip(') '))
        thread=(pid,tid)
        if '/FlipMultiPlaneOverlay/win:Info' in name and row[2].lower().startswith('dwm.exe'):
            if p['Enabled']!='1' or p['LayerIndex']!='0':continue
            flip=dict(time=time,pid=pid,tid=tid,source=int(p['VidPnSourceId']),layer=0,
                      rect=[int(p[k]) for k in ['DstRect.left','DstRect.top','DstRect.right','DstRect.bottom']])
            pending[thread]=flip;flip_events.append(flip)
        elif '/QueuePacket/win:Start' in name and p.get('bPresent')=='true' and thread in pending:
            flip=pending.pop(thread);flip['sequence']=int(p['SubmitSequence']);flip['queueTime']=time
            flip.update(context=p['hContext'],queuePacket=p['pQueuePacket'])
            key=(flip['source'],flip['sequence'])
            if key in queued:
                previous=queued[key];vsync=previous.get('vsync')
                # Sequence numbers belong to queue contexts. Reuse is safe to
                # resolve only after the previous queue and displayed flip
                # have independently completed. Overlapping lifetimes remain
                # ambiguous because MMIO payloads do not carry hContext.
                assert previous.get('queueCompleted',time)>=previous['queueTime']
                assert previous.get('queueCompleted',time)<time and vsync is not None and vsync['eventUs']<time
                assert len(previous.get('hardwareCompletions',[]))==1 and previous['hardwareFrames']==[vsync['frame']]
                assert previous['hardwareCompletions'][0]<time*10+report['PresentMonOriginQpc']
                sequence_reuse.append(dict(Source=key[0],SubmitSequence=key[1],PreviousContext=previous['context'],
                    NewContext=flip['context'],PreviousQueueCompletedUs=previous['queueCompleted'],
                    PreviousHardwareCompletedQpc=previous['hardwareCompletions'][0],PreviousVsyncUs=vsync['eventUs'],NewQueueUs=time))
            queued[key]=flip
            queue_contexts[(flip['context'],flip['sequence'],flip['queuePacket'])]=flip
        elif '/QueuePacket/win:Stop' in name:
            key=(p['hContext'],int(p['SubmitSequence']),p['pQueuePacket'])
            if key in queue_contexts:
                flip=queue_contexts[key]
                assert p.get('bPreempted')=='false' and p.get('bTimeouted')=='false'
                assert time>=flip['queueTime']
                flip['queueCompleted']=time
        elif '/MMIOFlipMultiPlaneOverlay3/win:Info' in name:
            key=(int(p['VidPnSourceId']),int(p['FlipSubmitSequence']))
            if key not in queued:continue
            flip=queued[key]
            if int(p['VidPnSourceId'])!=flip['source']:continue
            ids=[int(x,16) for x in re.findall(r'0x[0-9a-fA-F]+',p['PresentId'])]
            layers=[int(x) for x in re.findall(r'\d+',p['LayerIndex'])]
            assert len(ids)==len(layers)==int(p['PlaneCount'])
            selected=[present for layer,present in zip(layers,ids) if layer==flip['layer']]
            if len(selected)!=1:continue
            # The scalar FlipPresentId is in a different namespace. The per-plane
            # array PresentId is the identifier echoed by the hardware flip log.
            assert flip['queueTime']<=time
            flip.update(adapter=p['pDxgAdapter'],presentId=selected[0],mmio3Time=time,mmioPid=pid,mmioTid=tid)
            submitted[(flip['adapter'],flip['source'],flip['layer'],flip['presentId'])]=flip
        elif '/MMIOFlipMultiPlaneOverlay/win:Info' in name:
            seq=int(p['FlipSubmitSequence'],16)>>32
            key=(int(p['VidPnSourceId']),seq)
            if key not in queued:continue
            flip=queued[key]
            # A single QueuePacket can carry several MPO layers. Retain only
            # the source/layer selected by the desktop Flip event, never attach
            # another plane's MMIO or completion to the desktop image.
            if int(p['VidPnSourceId'])!=flip['source'] or int(p['LayerIndex'])!=flip['layer']:continue
            flip.update(mmioTime=time,scalarFlipPresentId=int(p['FlipPresentId']))
        elif '/VSyncDPC/win:Info' in name:
            sequence=int(p['FlipFenceId'])>>32
            if sequence:
                flip=queued.get((int(p['VidPnSourceId']),sequence))
                if flip is not None and flip.get('adapter')==p['pDxgAdapter']:
                    flip.setdefault('vsync',dict(eventUs=time,frame=int(p['FrameNumber']),frameQpc=int(p['FrameQPCTime'])))
        elif '/VSyncHwFlipQueueLogUpdate/win:Info' in name:
            ids=[int(x,16) for x in re.findall(r'0x[0-9a-fA-F]+',p['PresentId'])]
            times=[int(x,16) for x in re.findall(r'0x[0-9a-fA-F]+',p['CompletionTimeStamp'])]
            assert len(ids)==len(times)==int(p['FlipsCompletedCount'])
            for present,qpc in zip(ids,times):
                key=(p['pDxgAdapter'],int(p['VidPnSourceId']),int(p['PlaneId']),present)
                if key in submitted:
                    flip=submitted[key]
                    flip.setdefault('hardwareCompletions',[]).append(qpc)
                    flip.setdefault('hardwareFrames',[]).append(int(p['FrameNumber']))
    origin=report['PresentMonOriginQpc'];frequency=report['QpcFrequency'];witnesses=[];failures=[]
    flips_by_time=collections.defaultdict(list)
    for flip in flip_events:flips_by_time[flip['time']].append(flip)
    for t in targets:
        key=(int(t['DrawEtwUs']),t['Visual'],int(t['ProcessId']));proof=proofs[key];f=proof['frame']
        qpc=int(t['PresentQpc']);us=(qpc-origin)/10
        flips=[flip for time in range(int(us)-1,int(us)+3) for flip in flips_by_time.get(time,[])
               if abs(flip['time']-us)<=1 and flip['pid']==f['pid'] and flip['tid']==f['tid']
               and f['begin']<=flip['time']<=f['finish']]
        if len(flips)!=1:
            failures.append(dict(target=t,reason='DXG flip identity is ambiguous or absent',matches=len(flips)));continue
        flip=flips[0];displayed=float(t['DisplayedQpc']);completion=flip.get('hardwareCompletions',[])
        vsync=flip.get('vsync')
        # PresentMon timestamps the matching VSyncDPC interrupt event. The HW
        # flip log carries an earlier completion QPC. Do not conflate these.
        if (len(completion)!=1 or vsync is None or
            abs(vsync['eventUs']*10+origin-displayed)>10 or
            flip['hardwareFrames']!=[vsync['frame']] or completion[0]>displayed or
            flip['rect']!=[0,0,3440,1440] or not proof['covers']):
            failures.append(dict(target=t,reason='Hardware completion or full visible rectangle mismatch',flip=flip,covers=proof['covers']));continue
        transition=next(tr for tr in summaries[int(t['Run'])]['Transitions'] if tr['Transition']==int(t['Transition']))
        assert transition['StartQpc']<qpc<=completion[0]
        witness=dict(Run=t['Run'],Count=t['Count'],Transition=t['Transition'],Target=t['Target'],Hwnd=t['Hwnd'],
                     GdiSprite=proof['link'][3],Visual=t['Visual'],FrameId=t['FrameId'],
                     DwmThreadId=f['tid'],DrawEtwUs=t['DrawEtwUs'],FlipEtwUs=flip['time'],
                     QueueEtwUs=flip['queueTime'],SubmitSequence=flip['sequence'],MmioEtwUs=flip['mmioTime'],
                     QueueContext=flip['context'],QueuePacket=flip['queuePacket'],QueueCompletedEtwUs=flip.get('queueCompleted'),
                     Adapter=flip['adapter'],VidPnSourceId=flip['source'],PlaneId=flip['layer'],
                     PlanePresentId=flip['presentId'],ScalarFlipPresentId=flip['scalarFlipPresentId'],
                     Mmio3EtwUs=flip['mmio3Time'],MmioExecutingProcessId=flip['mmioPid'],
                     MmioExecutingThreadId=flip['mmioTid'],HardwareDisplayedQpc=completion[0],
                     HardwareFrameNumber=vsync['frame'],VsyncEventUs=vsync['eventUs'],
                     PresentMonVsyncQpc=displayed,VsyncAfterHardwareCompletionMs=(displayed-completion[0])*1000/frequency,
                     SubmitToHardwareDisplayedMs=(completion[0]-transition['StartQpc'])*1000/frequency,
                     DwmFlushBoundaryQpc=transition['DwmFlushedQpc'],
                     HardwareAfterDwmFlushMs=(completion[0]-transition['DwmFlushedQpc'])*1000/frequency,
                     PresentationSearchEndEtwUs=t['PresentationSearchEndEtwUs'],
                     SubmitToPresentMonVsyncMs=t['SubmitToDisplayedMs'])
        witnesses.append(witness)
    if witnesses:
        with (a.output/'physical-display-witnesses.csv').open('x',newline='',encoding='utf-8') as stream:
            writer=csv.DictWriter(stream,fieldnames=list(witnesses[0]));writer.writeheader();writer.writerows(witnesses)
    result=dict(Verdict='PASS' if not failures and len(witnesses)==len(targets) else 'E2E_PENDING',
                StageAccepted=False,SnapshotId=a.snapshot.name,ExpectedTargets=len(targets),VerifiedTargets=len(witnesses),
                Failures=failures,GeometryCoverageFailures=geometry_errors,
                InitialIncompleteFrameStops=initial_stops,FirstNativeStartQpc=first_native_qpc,
                CompletedQueueSequenceReuse=sequence_reuse,
                Method='HWND -> GDI sprite -> original bound DWM visual -> identity transform and full-size render content -> compositor frame and same-thread DXG flip -> QueuePacket context/SubmitSequence -> MMIOFlipMultiPlaneOverlay3 per-plane PresentId -> adapter/source/plane/PresentId hardware completion QPC. A SubmitSequence may be reused only after the previous queue completion, matched hardware completion and VSync; overlapping unresolved lifetimes are rejected. The scalar FlipPresentId is not used as a per-plane identifier. Matching VSyncDPC uses the same submit sequence within its lifetime and hardware FrameNumber; its event time independently agrees with PresentMon within xperf one-microsecond export precision. DwmFlush, hardware completion and VSync interrupt times remain separate.',
                PhysicalMeaning='OS/driver hardware flip completion for the full 3440x1440 desktop plane. Does not measure emitted photons or panel response time.',
                DerivationSHA256=hash_file(a.derivation/'derivation.json'),DxgExportSHA256=hash_file(a.dxg_events),
                ScriptSHA256=hash_file(pathlib.Path(__file__)),
                Outputs=[dict(Path=str(p.resolve()),Bytes=p.stat().st_size,SHA256=hash_file(p))
                         for p in sorted(a.output.glob('*.csv'))])
    with (a.output/'presentation-verification.json').open('x',encoding='utf-8') as stream:json.dump(result,stream,indent=2)
    print(json.dumps({k:v for k,v in result.items() if k not in ['Failures','GeometryCoverageFailures']},indent=2))
    print('Failure count:',len(failures),'geometry coverage failures:',len(geometry_errors))


if __name__=='__main__':main()
