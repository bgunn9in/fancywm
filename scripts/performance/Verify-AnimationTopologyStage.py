"""Close the existing owner topology / presentation stage only with all three complete cases."""
import argparse
import collections
import csv
import datetime
import hashlib
import importlib.util
import json
import math
import pathlib


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def rows(path):
    with path.open(encoding='utf-8-sig',newline='') as stream:return list(csv.DictReader(stream))


def sha(path):
    path=path.resolve();s=path.stat();key=(str(path),s.st_size,s.st_mtime_ns)
    if key not in HASHES:
        with path.open('rb') as stream:HASHES[key]=hashlib.file_digest(stream,'sha256').hexdigest().upper()
    return HASHES[key]


HASHES={}


def p50(values):
    values=sorted(values)
    return values[(len(values)-1)//2]


def main():
    p=argparse.ArgumentParser(description=__doc__)
    for name in ['output','snapshot']:
        p.add_argument('--'+name,type=pathlib.Path,required=True)
    p.add_argument('--case',type=pathlib.Path,action='append',required=True,
                   help='JSON containing capture, captureVerification, derivation, presentation paths.')
    a=p.parse_args();a.output.mkdir(parents=True,exist_ok=False)
    root=pathlib.Path.cwd();inputs=set();cases=[];processes=[];fixture=None
    def add(path):
        path=path.resolve();assert path.is_file(),path;inputs.add(path);return path
    def document(path):return read(add(path))
    def verified_files(records,base=None):
        paths=set()
        for r in records:
            path=add(pathlib.Path(r['Path']))
            if base is not None:assert path.parent==base.resolve()
            assert path not in paths;paths.add(path)
            assert path.stat().st_size==r['Bytes'] and sha(path)==r['SHA256'],path
        return paths
    verifier=a.snapshot/'source/scripts/performance/Verify-AnimationTopologyStage.py'
    assert sha(pathlib.Path(__file__))==sha(verifier)
    add(verifier);add(a.snapshot/'manifest.csv')
    accepted=root/'artifacts/performance/FWM-DISPLAY-PROVIDER-SNAPSHOT-20260909-C2'
    production_roots={'FancyWM','FancyWM.GUI','FancyWM.Layouts','FancyWM.ThemeEngine','FancyWM.DllImports',
                      'FancyWM.Package','ModernWpf','winman','winman-windows'}
    def production(path):return pathlib.PurePosixPath(path).parts[0] in production_roots or path in ['Directory.Build.props','FancyWM.sln','version.json']
    expected={r['Path'].replace('\\','/'):r['SHA256'] for r in rows(add(accepted/'manifest.csv')) if production(r['Path'].replace('\\','/'))}
    assert len(expected)==2155
    for config in a.case:
        c=document(config);cap=pathlib.Path(c['capture']);cv=pathlib.Path(c['captureVerification']);dd=pathlib.Path(c['derivation']);pp=pathlib.Path(c['presentation'])
        analysis_snapshot=pathlib.Path(c.get('analysisSnapshot',c['capture']))
        presentation_snapshot=pathlib.Path(c.get('presentationSnapshot',str(analysis_snapshot)))
        v=document(cv/'capture-verification.json');d=document(dd/'derivation.json');pr=document(pp/'presentation-verification.json')
        capture_script=add(cap/'source/scripts/performance/Verify-AnimationTopology.py')
        assert v['VerifierSHA256']==sha(capture_script)
        assert pr['ScriptSHA256']==sha(add(presentation_snapshot/'source/scripts/performance/Verify-AnimationPresentation.py'))
        spec=importlib.util.spec_from_file_location('capture_verifier',capture_script)
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        module.verify_manifest(cap/'manifest.csv',cap/'source')
        module.verify_manifest(cap/'native-evidence.csv',cap/'native',True)
        module.verify_manifest(cap/'native/binary-manifest.csv',cap/'binaries',True)
        if analysis_snapshot.resolve()!=cap.resolve():
            add(analysis_snapshot/'manifest.csv');module.verify_manifest(analysis_snapshot/'manifest.csv',analysis_snapshot/'source')
        if presentation_snapshot.resolve() not in [cap.resolve(),analysis_snapshot.resolve()]:
            add(presentation_snapshot/'manifest.csv');module.verify_manifest(presentation_snapshot/'manifest.csv',presentation_snapshot/'source')
        analyzer=add(analysis_snapshot/'source/scripts/performance/Analyze-AnimationTopology.py')
        assert any(pathlib.Path(r['Path']).resolve()==analyzer and r['SHA256']==sha(analyzer) for r in d['Inputs'])
        assert pr['InitialIncompleteFrameStops']==d['InitialIncompleteFrameStops']
        assert all(r['TimeUs']<d['FirstNativeBoundaryEtwUs'] for r in d['InitialIncompleteFrameStops'])
        assert verified_files(v['Outputs'],cv)=={(cv/name).resolve() for name in ['transitions.csv','hwnd-owners.csv']}
        derived=verified_files(d['Outputs'],dd)
        assert (dd/'owner-scheduling.csv').resolve() in derived and (dd/'shared-compositor-counters.csv').resolve() in derived
        assert verified_files(pr['Outputs'],pp)=={(pp/'physical-display-witnesses.csv').resolve()}
        assert v['Verdict']=='CAPTURE_VERIFIED_STAGE_IN_PROGRESS' and v['MeasuredTransitions']==120
        assert v['NativeGeometryAndCleanup'] and v['EtwMarkers']==600 and v['LostEvents']==v['LostBuffers']==0
        assert v['SnapshotId']==cap.name==pr['SnapshotId'] and pathlib.Path(d['Snapshot']).resolve()==cap.resolve()
        assert not d['SchedulingFailures'] and d['TargetsWithDisplayedFrameCorrelation']==2440
        assert pr['Verdict']=='PASS' and pr['ExpectedTargets']==pr['VerifiedTargets']==2440 and not pr['Failures'] and not pr['GeometryCoverageFailures']
        assert pr['DerivationSHA256']==sha(dd/'derivation.json')
        assert sha(add(cap/'manifest.csv'))==v['SourceManifestSHA256']
        assert sha(add(cap/'native-evidence.csv'))==v['RawManifestSHA256']
        etl=add(cap/'native/animation.etl');assert etl.stat().st_size==v['EtlBytes'] and sha(etl)==v['EtlSHA256']
        current={r['Path'].replace('\\','/'):r['SHA256'] for r in rows(cap/'manifest.csv') if production(r['Path'].replace('\\','/'))}
        assert current==expected,'A capture contains a production delta.'
        native_fixture={r['Path']:r['SHA256'] for r in rows(cap/'manifest.csv') if r['Path'].replace('\\','/').startswith('scripts/performance/native/')}
        if fixture is None:fixture=native_fixture
        assert native_fixture==fixture,'Topology captures do not use the same final native fixture.'
        for i in d['Inputs']:
            path=add(pathlib.Path(i['Path']));assert path.stat().st_size==i['Bytes'] and sha(path)==i['SHA256']
        export=pathlib.Path(c['exportDirectory'])
        export_receipt=document(export/'export-command.json');pm=document(export/'presentmon-command.json')
        assert export_receipt['ExitCode']==pm['ExitCode']==0 and export_receipt['ETLSHA256']==v['EtlSHA256']
        assert export_receipt['ExporterSHA256']==sha(add(cap/'source/scripts/performance/Export-AnimationTopology.py'))
        verified_files(export_receipt['Exports'],export)
        assert pm['OutputSHA256']==sha(add(export/'presents.csv'))
        assert pm['ToolSHA256']==sha(add(pathlib.Path(pm['Arguments'][0])))
        assert export_receipt['XperfSHA256']==sha(add(pathlib.Path(export_receipt['Arguments'][0])))
        dxg=next(pathlib.Path(i['Path']) for i in export_receipt['Exports'] if i['Path'].endswith('dxg-events.csv'))
        assert sha(add(dxg))==pr['DxgExportSHA256']
        commands=document(cap/'native/commands.json')
        processes.extend(dict(Capture=cap.name,**r) for r in commands if r['Name'] in [f'run-{i}' for i in range(1,6)])
        tr=rows(add(cv/'transitions.csv'));ow=rows(add(cv/'hwnd-owners.csv'));sc=rows(add(dd/'owner-scheduling.csv'))
        witnesses=rows(add(pp/'physical-display-witnesses.csv'));gpu=rows(add(dd/'shared-compositor-counters.csv'))
        key=lambda row:tuple(int(row[k]) for k in ['Run','Count','Transition'])
        expected_targets={(int(r['Run']),int(r['Count']),int(r['Transition']),i) for r in tr for i in range(int(r['Count']))}
        actual_targets={(*key(r),int(r['Target'])) for r in witnesses}
        assert len(witnesses)==len(actual_targets)==2440 and actual_targets==expected_targets
        assert len(tr)==len(sc)==len(gpu)==120 and {key(r) for r in tr}=={key(r) for r in gpu}
        assert {(int(r['run']),int(r['count']),int(r['transition'])) for r in sc}=={key(r) for r in tr}
        assert len(ow)==305 and d['Owners']==len({(r['ProcessId'],r['OwnerThreadId']) for r in ow})
        presentation_by_transition=collections.defaultdict(list)
        for r in witnesses:presentation_by_transition[key(r)].append(float(r['SubmitToHardwareDisplayedMs']))
        aggregates=[]
        for count in [1,10,50]:
            rr=[r for r in tr if int(r['Count'])==count];ss=[r for r in sc if int(r['count'])==count];gg=[r for r in gpu if int(r['Count'])==count]
            assert len(rr)==len(ss)==len(gg)==40
            assert all(int(r['OwnerCount'])==min(count,v['OwnerLimit']) for r in rr)
            metrics={k:p50([float(r[k]) for r in rr]) for k in ['PositionReads','SetPositionCalls','TaskRunDispatchesFromSuccessfulWrites',
                     'FrameIntervalMedianMs','CompletionMs','LastNativeAppliedMs','NativeVerifiedMs','ProcessCpuMs']}
            metrics.update(LastTargetHardwareDisplayedMs=p50([max(presentation_by_transition[key(r)]) for r in rr]),
                           OwnerScheduledAfterTaskMs=p50([float(r['OwnerScheduledAfterTaskMs']) for r in ss]),
                           SharedDwmDroppedPresents=sum(int(r['DwmDropped']) for r in gg))
            assert all(math.isfinite(value) and value>=0 for value in metrics.values())
            gpu_available=all(r['DwmGpuStatus']=='MEASURED' and int(r['DwmGpuSamples'])==int(r['DwmPresents'])>0 for r in gg)
            gpu_metric=p50([float(r['DwmGpuActiveMsSum']) for r in gg]) if gpu_available else None
            if gpu_metric is not None:assert math.isfinite(gpu_metric) and gpu_metric>=0
            aggregates.append(dict(Count=count,ActualOwners=min(count,v['OwnerLimit']),P50NearestRank=metrics,
                                   SharedDwmGpuStatus='MEASURED' if gpu_available else 'NOT_MEASURED',
                                   SharedDwmGpuActiveMsP50=gpu_metric))
        cases.append(dict(Capture=cap.name,OwnerLimit=v['OwnerLimit'],Aggregates=aggregates))
    assert sorted(c['OwnerLimit'] for c in cases)==[1,5,50]
    assert len(processes)==15
    processes.sort(key=lambda r:r['StartedUtc'])
    assert all(datetime.datetime.fromisoformat(x['FinishedUtc'])<=datetime.datetime.fromisoformat(y['StartedUtc']) for x,y in zip(processes,processes[1:]))
    ledger=add(root/'docs/performance/OPTIMIZATION_MEASUREMENTS.csv')
    assert ledger.stat().st_size==41782945 and sha(ledger)=='84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11'
    result=dict(PerfId='PERF-010',Stage='owner topology / presentation',Verdict='ACCEPT',WholeIdStatus='IN_PROGRESS',
                Processes=15,MeasuredTransitions=360,EtwMarkers=1800,HardwarePresentationTargets=7320,
                Cases=cases,ProcessOrder=processes,ProductionDelta=False,LedgerAppend=False,
                Criteria=dict(OwnerScheduling='PASS',FinalFixtureTopologyControls='PASS',NativeGeometry='PASS',
                              HardwarePresentationCorrelation='PASS',ProcessCpu='MEASURED',
                              SharedDwmGpu='MEASURED' if all(g['SharedDwmGpuStatus']=='MEASURED' for c in cases for g in c['Aggregates']) else 'NOT_MEASURED',
                              AttributedFancyWmGpu='NOT_MEASURED',OwnedCleanup='PASS'),
                RemainingExistingStages=['Full-app native / interactive validation','Full-app CPU/GPU/presentation measurements'],
                Limits=['Controlled same-process owner topologies; full-app separate-process targets remain for the next stage.',
                        'Physical telemetry means OS/driver hardware flip completion; photons and panel response are NOT_MEASURED.',
                        'DWM GPU counters are shared compositor observations, not attributed FancyWM GPU consumption.',
                        'Process CPU and shared GPU observations end at the DwmFlush boundary; independently correlated hardware display completion can follow that boundary.',
                        'Task.Run fan-out is source-derived from observed successful writes, not TPL task ETW.',
                        'Observational cases are not A/B candidates and establish no production speedup.'],
                Inputs=[dict(Path=str(path),Bytes=path.stat().st_size,SHA256=sha(path)) for path in sorted(inputs)])
    with (a.output/'stage-verification.json').open('x',encoding='utf-8') as f:json.dump(result,f,indent=2)
    print(json.dumps({k:v for k,v in result.items() if k not in ['Inputs','ProcessOrder']},indent=2))


if __name__=='__main__':main()
