"""Strict capture verification. This receipt alone never accepts the topology/presentation stage."""
import argparse
import collections
import csv
import datetime
import hashlib
import json
import math
import pathlib
import re
import xml.etree.ElementTree as ET


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def rows(path):
    with path.open(encoding='utf-8-sig', newline='') as stream:
        return list(csv.DictReader(stream))


def verify_manifest(manifest, base, exact=False):
    manifest_rows = rows(manifest)
    expected = {r['Path'].replace('\\', '/') for r in manifest_rows}
    require(len(expected) == len(manifest_rows), f'Duplicate path: {manifest}')
    if exact:
        actual = {p.relative_to(base).as_posix() for p in base.rglob('*') if p.is_file()}
        require(actual == expected, f'Path composition differs: {base}: {actual ^ expected}')
    for row in manifest_rows:
        path = base / row['Path']
        require(sha(path) == row['SHA256'], f'Hash mismatch: {path}')
        if 'Length' in row:
            require(path.stat().st_size == int(row['Length']), f'Length mismatch: {path}')
    return len(expected)


def percentile(values, fraction):
    return sorted(values)[max(0, math.ceil(len(values)*fraction)-1)]


def profile_semantics(path):
    # WPR exports provider declarations/references in nondeterministic order.
    # Retain every value, attribute and duplicate; normalize only these sets
    # and the ordering of the human-readable RunningProfile name list.
    def node(element):
        attrs=dict(element.attrib)
        if element.tag=='Profile' and attrs.get('Description','').startswith('RunningProfile:'):
            attrs['Description']='RunningProfile:'+','.join(sorted(s.strip() for s in attrs['Description'][15:].split(',')))
        children=[node(child) for child in element]
        if element.tag in ['Profiles','EventProviders']:
            children.sort()
        return element.tag,tuple(sorted(attrs.items())),(element.text or '').strip(),tuple(children)
    return node(ET.parse(path).getroot())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=pathlib.Path, required=True)
    parser.add_argument('--baseline', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--verifier-snapshot', type=pathlib.Path)
    args = parser.parse_args()
    require(not args.output.exists(), 'Immutable output already exists.')
    args.output.mkdir(parents=True)
    snap, native = args.snapshot.resolve(), args.snapshot.resolve()/'native'
    source_count = verify_manifest(snap/'manifest.csv', snap/'source')
    raw_count = verify_manifest(snap/'native-evidence.csv', native, True)
    binary_count = verify_manifest(native/'binary-manifest.csv', snap/'binaries', True)
    verifier_snap=args.verifier_snapshot.resolve() if args.verifier_snapshot else snap
    if verifier_snap!=snap:verify_manifest(verifier_snap/'manifest.csv',verifier_snap/'source')
    require(sha(pathlib.Path(__file__)) == sha(verifier_snap/'source/scripts/performance/Verify-AnimationTopology.py'),
            'Verifier must match the frozen capture source.')
    for name in ['AnimationThread.cs','TransitionTargetGroup.cs']:
        rel = pathlib.Path('FancyWM/Utilities')/name
        require(sha(snap/'source'/rel) == sha(args.baseline/'source'/rel), 'Production animation changed.')
    require(sha(native/'AnimationNative.wprp') == sha(args.baseline/'native/AnimationNative.wprp'),'Marker profile changed.')
    require(profile_semantics(native/'builtin-profiles.wprp') == profile_semantics(args.baseline/'native/builtin-profiles.wprp'),
            'Built-in profile settings changed.')
    environment = load(native/'environment.json')
    require(environment['Runs'] == 5 and environment['Iterations'] == 8, 'Expected five processes/eight measured iterations.')
    owner_limit = environment['OwnerLimit']
    require(owner_limit in [1,5,50], 'Unexpected topology case.')
    commands = load(native/'commands.json')
    require(all(c['ExitCode'] == 0 and not c['TimedOut'] for c in commands), 'A command failed.')
    if 'ProfileControl' in environment:
        profile_name=ET.parse(native/'builtin-profiles.wprp').getroot().find('Profiles/Profile').attrib['Name']
        start=next(c for c in commands if c['Name']=='wpr-start')
        require(start['Arguments']==['-start',str(native/'builtin-profiles.wprp')+'!'+profile_name,
                '-start',str(native/'AnimationNative.wprp')+'!AnimationNative','-filemode','-instancename',snap.name],
                'WPR did not start the exact archived profile and fixture markers.')
    processes = [c for c in commands if re.fullmatch(r'run-\d+', c['Name'])]
    require(len(processes) == 5, 'Process count mismatch.')
    require('not recording' in (native/'wpr-after.log').read_text(), 'Owned WPR session survived.')
    header = (native/'trace-header.txt').read_text()
    require(all(re.search(r'^Total # Lost '+kind+r'\s*:\s*0\s*$',header,re.M) for kind in ['Buffers','Events']),
            'ETW events or buffers were lost.')
    markers = {}
    with (native/'markers.csv').open() as stream:
        for r in csv.reader(stream,skipinitialspace=True):
            if r and r[0]=='FancyWM-AnimationNativeHarness/Boundary/' and r[1].isdigit():
                pid=int(r[2].rsplit('(',1)[1].rstrip(') '))
                data=dict(x.split(' : ',1) for x in r[9:] if ' : ' in x)
                key=(pid,int(data['transition']),int(data['phase']))
                require(key not in markers,'Duplicate ETW marker.')
                markers[key]=int(data['qpc'])
    require(len(markers)==600,'Expected 600 markers including warmups.')
    baseline=load(args.baseline/'native/run-1/summary.json')
    fixed={d['Count']:{k:d[k] for k in ['Count','Dpi','WorkArea','Originals','Destinations']} for d in baseline['Displays']}
    output_rows=[]
    owner_rows=[]
    for run,command in enumerate(processes,1):
        require(command['Name']==f'run-{run}','Process order differs.')
        if run>1:
            require(datetime.datetime.fromisoformat(command['StartedUtc'])>=datetime.datetime.fromisoformat(processes[run-2]['FinishedUtc']),
                    'Measurement processes overlap.')
        summary=load(native/f'run-{run}/summary.json')
        pid=summary['ProcessId']
        require(pid==command['ProcessId'] and summary['Architecture']=='X64' and summary['EtwProviderEnabled']
                and summary['CleanupPassed'],'Process identity/ETW/cleanup mismatch.')
        require(not summary.get('RecordingCapacityExceeded',False) and not summary.get('RunError'),
                'Recording overflow or native run failure.')
        require(summary['DurationMs']==250 and summary['Warmups']==2 and summary['Iterations']==8
                and summary['NativeTargetOwnerLimit']==owner_limit and summary['TargetFrameRate']==baseline['TargetFrameRate']==144,
                'Fixture parameters changed.')
        require(len(summary['Transitions'])==30,'Transition count differs.')
        require(summary.get('VisibilityProtocol')=='native-topmost-and-five-point-hit-tests','Missing native visibility protocol.')
        for assembly in summary['Assemblies']:
            location=pathlib.Path(assembly['Location']).resolve()
            require(location.is_relative_to(snap/'binaries') and sha(location)==assembly['SHA256'],'Loaded assembly differs.')
        displays={d['Count']:d for d in summary['Displays']}
        for count,display in displays.items():
            require({k:display[k] for k in fixed[count]}==fixed[count],'N3 geometry/display/DPI changed.')
            tids=display['OwnerThreadIds'];owners=min(count,owner_limit)
            require(len(tids)==count and len(set(tids))==owners and all(tid>0 for tid in tids),'Actual owner count differs.')
            require(display['OwnerProcessIds']==[pid]*count,'HWND process ownership differs.')
            require(all(tids[i]==tids[i%owners] for i in range(count)),'HWND owner distribution differs.')
            for target,hwnd in enumerate(display['Handles']):
                owner_rows.append(dict(Run=run,ProcessId=pid,Count=count,Target=target,Hwnd=hwnd,OwnerThreadId=tids[target]))
        by_transition=collections.defaultdict(list)
        for call in rows(native/f'run-{run}/calls.csv'):
            call={k:(v if k=='operation' else int(v)) for k,v in call.items()}
            by_transition[call['transition']].append(call)
        require(sum(map(len,by_transition.values()))<500000,'Sample capacity exceeded.')
        for t in summary['Transitions']:
            for phase,field in enumerate(['StartQpc','CompletedQpc','NativeVerifiedQpc','DwmFlushedQpc'],1):
                require(markers[(pid,t['Transition'],phase)]==t[field],'ETW QPC payload differs.')
            require(t['NativeRectanglesPassed'] and t['StartQpc']<t['CompletedQpc']<=t['NativeVerifiedQpc']<=t['DwmFlushedQpc'],
                    'Native geometry/completion boundaries failed.')
            calls=by_transition[t['Transition']]
            by_op=collections.defaultdict(list)
            for call in calls:by_op[call['operation']].append(call)
            reads,writes,applied,frames=[by_op[op] for op in ['PositionRead','SetPosition','NativeApplied','Frame']]
            require(len(reads)>=len(writes)>=t['Count'] and len(writes)==len(applied) and len(frames)>1,'Native fan-out/count mismatch.')
            display=displays[t['Count']]
            for boundary in ['VisibilityBefore','VisibilityAfter']:
                visibility=t[boundary]
                require(len(visibility)==t['Count'],'Visibility observation count differs.')
                for target,observed in enumerate(visibility):
                    handle=display['Handles'][target]
                    require(observed['Hwnd']==handle and observed['Visible'] and observed['Cloaked']==0
                            and observed['ExtendedStyle']&8 and observed['HitHwnds']==[handle]*5,
                            'An owned target was obscured or lacked native TOPMOST.')
            expected=display['Destinations'] if t['Iteration']%2 else display['Originals']
            latest=0
            for target,rect in enumerate(expected):
                messages=sorted((a for a in applied if a['target']==target),key=lambda a:a['qpc'])
                require(messages and all(m['thread_id']==display['OwnerThreadIds'][target] for m in messages),'Native callback owner differs.')
                final=messages[-1]
                require([final[k] for k in ['x','y','width','height']]==[rect[k] for k in ['Left','Top','Width','Height']],
                        'Final native message rectangle differs.')
                latest=max(latest,final['qpc'])
            if not t['Measured']:continue
            frames.sort(key=lambda f:f['qpc'])
            intervals=[(b['qpc']+b['duration_ticks']-a['qpc']-a['duration_ticks'])*1000/summary['StopwatchFrequency']
                       for a,b in zip(frames,frames[1:])]
            output_rows.append(dict(Run=run,ProcessId=pid,OwnerLimit=owner_limit,OwnerCount=min(t['Count'],owner_limit),
                                    Count=t['Count'],Transition=t['Transition'],Iteration=t['Iteration'],
                                    PositionReads=len(reads),SetPositionCalls=len(writes),NativeApplied=len(applied),
                                    TaskRunDispatchesFromSuccessfulWrites=len(writes),
                                    MaxWritesPerFrame=max(collections.Counter(w['frame'] for w in writes).values()),
                                    WriteThreads=len({w['thread_id'] for w in writes}),Frames=len(frames),
                                    FrameIntervalMedianMs=percentile(intervals,.5),FrameIntervalP95Ms=percentile(intervals,.95),
                                    CompletionMs=t['CompletionMs'],NativeVerifiedMs=t['NativeVerifiedMs'],
                                    LastNativeAppliedMs=(latest-t['StartQpc'])*1000/summary['StopwatchFrequency'],
                                    NativeTailAfterCompletionMs=max(0.,(latest-t['CompletedQpc'])*1000/summary['StopwatchFrequency']),
                                    CompletionProcessCpuMs=t['CompletionProcessCpuMs'],NativeVerifiedProcessCpuMs=t['NativeVerifiedProcessCpuMs'],
                                    ProcessCpuMs=t['ProcessCpuMs'],DwmFlushBoundaryMs=t['DwmFlushBoundaryMs']))
    require(len(output_rows)==120,'Expected 120 measured transitions.')
    for name,data in [('transitions.csv',output_rows),('hwnd-owners.csv',owner_rows)]:
        with (args.output/name).open('x',encoding='utf-8',newline='') as stream:
            writer=csv.DictWriter(stream,fieldnames=list(data[0]));writer.writeheader();writer.writerows(data)
    aggregates=[]
    for count in [1,10,50]:
        sample=[r for r in output_rows if r['Count']==count]
        aggregates.append(dict(Count=count,OwnerCount=min(count,owner_limit),Transitions=len(sample),
                               **{field:percentile([r[field] for r in sample],.5) for field in
                                  ['CompletionMs','LastNativeAppliedMs','NativeVerifiedMs','FrameIntervalMedianMs','ProcessCpuMs']}))
    report=dict(Verdict='CAPTURE_VERIFIED_STAGE_IN_PROGRESS',StageAccepted=False,PerfId='PERF-010',
                SnapshotId=snap.name,OwnerLimit=owner_limit,SourceFiles=source_count,RawFiles=raw_count,Binaries=binary_count,
                Processes=5,MeasuredTransitions=120,EtwMarkers=600,LostEvents=0,LostBuffers=0,NativeGeometryAndCleanup=True,
                Aggregates=aggregates,PhysicalPresentation='E2E_PENDING',OwnerScheduling='SEPARATE_DERIVATION_REQUIRED',
                FullAppBehavior='E2E_PENDING',FullAppCpuGpu='NOT_MEASURED',ProductionChange=False,LedgerAppended=False,
                TaskFanoutMethod='Each successful observed SetPosition call is dispatched by the unchanged production Task.Run(action) site. Dispatch count is source-derived from observed writes, not CLR TaskScheduled ETW.',
                SourceManifestSHA256=sha(snap/'manifest.csv'),RawManifestSHA256=sha(snap/'native-evidence.csv'),
                VerifierSHA256=sha(pathlib.Path(__file__)),EtlBytes=(native/'animation.etl').stat().st_size,EtlSHA256=sha(native/'animation.etl'),
                Outputs=[dict(Path=str(p.resolve()),Bytes=p.stat().st_size,SHA256=sha(p))
                         for p in sorted(args.output.glob('*.csv'))])
    report['VerifierSnapshotId']=verifier_snap.name
    report['ProfileComparison']='Exact marker profile; built-in XML values and multiplicities exact after provider declaration/reference and RunningProfile description order normalization.'
    with (args.output/'capture-verification.json').open('x',encoding='utf-8') as stream:json.dump(report,stream,indent=2)
    print(json.dumps(report,indent=2))


if __name__=='__main__':main()
