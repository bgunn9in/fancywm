"""Test frozen fixture geometry in five sequential processes without sending input.

No ETW or performance-stage acceptance. This development gate catches unequal
workloads before another costly trace and preserves every failed process.
"""
import argparse
import datetime
import importlib.util
import json
import pathlib
import subprocess
import sys


def module(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    value=importlib.util.module_from_spec(spec);spec.loader.exec_module(value);return value


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-snapshot',type=pathlib.Path,required=True)
    parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args()
    root=pathlib.Path.cwd();scripts=root/'scripts/performance';base=args.output.resolve()
    assert not base.exists()
    helper=module('behavior_helpers',scripts/'Run-AnimationFullAppValidation.py')
    verifier=module('capture_verifier',scripts/'Verify-AnimationFullAppMeasurement.py')
    files=module('file_verifier',scripts/'Verify-AnimationFullAppValidation.py')
    desktop=module('desktop',scripts/'FullAppDesktop.py')
    build=args.build_snapshot.resolve()
    assert helper.read(build/'development-validation.json')['BuildsPassed']
    for name,digest in helper.production_manifest(build).items():
        assert helper.sha(root/name)==helper.sha(build/'source'/name)==digest
    for path in (scripts/'fullapp').rglob('*'):
        if path.is_file() and not any(p in ['bin','obj'] for p in path.relative_to(scripts/'fullapp').parts):
            assert helper.sha(path)==helper.sha(build/'source'/path.relative_to(root))
    state=desktop.snapshot()
    assert not desktop.control_failures(state,{96},[dict(Left=0,Top=0,Right=3440,Bottom=1392)])
    status=subprocess.run(['wpr','-status'],capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
    assert status.returncode==0 and b'not recording' in status.stdout
    binaries={}
    for project in ['FancyWM.FullAppHarness','FancyWM.Perf010Targets']:
        folder=build/('binaries-Release-'+project)
        manifest=helper.read(build/'validation'/('Release-'+project+'-binary-manifest.json'))
        files.verify_files(folder,manifest)
        binaries.update({str((folder/r['Path']).resolve()):r['SHA256'] for r in manifest})
    host=build/'binaries-Release-FancyWM.FullAppHarness/FancyWM.FullAppHarness.exe'
    targets=build/'binaries-Release-FancyWM.Perf010Targets/FancyWM.Perf010Targets.exe'
    native=base/'native';native.mkdir(parents=True)
    commands=[];errors=[];reference=None;passed=False
    helper.write(base/'preflight.json',state)
    (base/pathlib.Path(__file__).name).write_bytes(pathlib.Path(__file__).read_bytes())
    try:
        for i in range(1,6):
            path=native/f'run-{i}';command=[str(host),str(path),str(targets),'2','measurement']
            started=datetime.datetime.now(datetime.timezone.utc).isoformat()
            with (native/f'run-{i}.stdout').open('xb') as stdout,(native/f'run-{i}.stderr').open('xb') as stderr:
                process=subprocess.Popen(command,stdout=stdout,stderr=stderr,creationflags=subprocess.CREATE_NO_WINDOW)
                try:code=process.wait(timeout=180)
                except subprocess.TimeoutExpired:
                    subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
                    process.wait(timeout=30)
                    raise
            record=dict(Name=f'run-{i}',Arguments=command,ProcessId=process.pid,ExitCode=code,
                StartedUtc=started,FinishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
            commands.append(record);helper.write(native/f'run-{i}-command.json',record)
            assert code==0,f'Run {i} failed'
            summary,rows,owners,geometry=verifier.validate_run(path,record,binaries,2,False,{96})
            assert summary['FixtureLayoutPreparation']=='SettledEvenFlexAndTargetOrder'
            if reference is None:reference=geometry
            if geometry!=reference:errors.append(dict(Run=i,Reason='Exact final geometry differs from run 1'))
            print(f'run-{i}: native checks pass; geometry equals reference: {geometry==reference}',flush=True)
        passed=not errors
    finally:
        helper.write(base/'native-evidence.json',[dict(Path=p.relative_to(native).as_posix(),Bytes=p.stat().st_size,SHA256=helper.sha(p))
            for p in sorted(native.rglob('*')) if p.is_file()])
        helper.write(base/'repeatability-run.json',dict(Verdict='PASS' if passed else 'REJECT',StageAccepted=False,
            BuildSnapshot=str(build),BuildManifestSHA256=helper.sha(build/'manifest.csv'),Commands=commands,Errors=errors,
            RunnerSHA256=helper.sha(pathlib.Path(__file__)),WprRecordingStarted=False,InputSent=False,
            PerformanceMeasurementAccepted=False))
    assert passed,'Fixture repeatability failed; raw processes retained'


if __name__=='__main__':main()
