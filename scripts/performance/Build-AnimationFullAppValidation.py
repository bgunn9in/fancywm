"""Freeze and build the existing full-app behavior harness without launching it."""
import argparse
import csv
import datetime
import hashlib
import json
import pathlib
import shutil
import subprocess


def write(path, value):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(value, stream, indent=2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot-id', required=True)
    parser.add_argument('--baseline-production', type=pathlib.Path,
        help='Build the common current fixture with exactly the three archived overlay baseline production files')
    args = parser.parse_args()
    root = pathlib.Path.cwd()
    base = root / 'artifacts/performance' / args.snapshot_id
    assert not base.exists(), 'Fresh immutable ID required'
    snapshot = subprocess.run(['pwsh', '-NoProfile', '-File',
        'scripts/performance/Save-ImplementationSnapshot.ps1', '-SnapshotId', args.snapshot_id],
        capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    assert base.is_dir()
    (base / 'snapshot.stdout').write_bytes(snapshot.stdout)
    (base / 'snapshot.stderr').write_bytes(snapshot.stderr)
    assert snapshot.returncode == 0, 'Snapshot failed; evidence retained'
    baseline_source = None
    if args.baseline_production:
        baseline = args.baseline_production.resolve()
        with (baseline / 'manifest.csv').open(encoding='utf-8-sig', newline='') as stream:
            old = {r['Path'].replace('\\', '/'): r['SHA256'] for r in csv.DictReader(stream)}
        with (base / 'manifest.csv').open(encoding='utf-8-sig', newline='') as stream:
            rows = list(csv.DictReader(stream))
        expected = ['FancyWM/TilingOverlayRenderer.cs', 'FancyWM/TilingService.Private.cs', 'FancyWM/TilingService.cs']
        def sha(path):
            with path.open('rb') as stream:
                return hashlib.file_digest(stream, 'sha256').hexdigest().upper()
        roots = {'FancyWM', 'FancyWM.GUI', 'FancyWM.Layouts', 'FancyWM.ThemeEngine', 'FancyWM.DllImports',
            'FancyWM.Package', 'ModernWpf', 'winman', 'winman-windows'}
        production = [r for r in rows if pathlib.PurePosixPath(r['Path'].replace('\\', '/')).parts[0] in roots
            or r['Path'] in ['Directory.Build.props', 'FancyWM.sln', 'version.json']]
        assert sorted(r['Path'].replace('\\', '/') for r in production if old.get(r['Path'].replace('\\', '/')) != r['SHA256']) == expected
        shutil.copyfile(base / 'manifest.csv', base / 'candidate-manifest.csv')
        shutil.copyfile(base / 'snapshot.json', base / 'candidate-snapshot.json')
        for r in rows:
            name = r['Path'].replace('\\', '/')
            if name in expected:
                assert sha(baseline / 'source' / name) == old[name]
                shutil.copyfile(baseline / 'source' / name, base / 'source' / name)
                r['SHA256'] = old[name]
        with (base / 'manifest.csv').open('w', encoding='utf-8', newline='') as stream:
            writer = csv.DictWriter(stream, fieldnames=['Path', 'SHA256'])
            writer.writeheader()
            writer.writerows(rows)
        baseline_source = dict(Snapshot=str(baseline), BaselineManifestSHA256=sha(baseline / 'manifest.csv'),
            CandidateManifestSHA256=sha(base / 'candidate-manifest.csv'), BuildManifestSHA256=sha(base / 'manifest.csv'),
            ReplacedProductionFiles=expected)
        metadata = json.loads((base / 'candidate-snapshot.json').read_text(encoding='utf-8-sig'))
        metadata.update(ManifestSHA256=baseline_source['BuildManifestSHA256'], BaselineSource=baseline_source)
        (base / 'snapshot.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
        write(base / 'baseline-source.json', baseline_source)
    validation = base / 'validation'
    validation.mkdir()
    commands = []
    passed = False
    try:
        for configuration in ['Debug', 'Release']:
            for project in ['FancyWM.FullAppHarness', 'targets/FancyWM.Perf010Targets']:
                label = configuration + '-' + project.split('/')[-1]
                csproj = base / 'source/scripts/performance/fullapp' / (project + '.csproj')
                command = ['dotnet', 'build', str(csproj), '-c', configuration,
                    '-p:PlatformTarget=x64', '-p:AllowUnsafeBlocks=true', '--nologo', '-v:minimal']
                started = datetime.datetime.now(datetime.timezone.utc).isoformat()
                timed_out = False
                with (validation / (label + '.stdout')).open('xb') as stdout, \
                     (validation / (label + '.stderr')).open('xb') as stderr:
                    process = subprocess.Popen(command, stdout=stdout, stderr=stderr,
                                               creationflags=subprocess.CREATE_NO_WINDOW)
                    try:
                        code = process.wait(timeout=600)
                    except subprocess.TimeoutExpired:
                        timed_out = True
                        # This live Popen child belongs to this invocation.
                        process.kill()
                        code = process.wait(timeout=30)
                receipt = dict(Name=label, Arguments=command, ProcessId=process.pid, ExitCode=code,
                    TimedOut=timed_out, StartedUtc=started,
                    FinishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
                commands.append(receipt)
                write(validation / (label + '-command.json'), receipt)
                print(label, 'exit', code, flush=True)
                if code or timed_out:
                    print((validation / (label + '.stdout')).read_bytes()[-8000:].decode('utf-8', errors='replace'))
                    raise RuntimeError('Failed build retained')
                output = csproj.parent / 'bin' / configuration / 'net10.0-windows10.0.18362.0'
                archive = base / ('binaries-' + label)
                shutil.copytree(output, archive)
                files = []
                for path in sorted(archive.rglob('*')):
                    if path.is_file():
                        with path.open('rb') as stream:
                            digest = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
                        files.append(dict(Path=path.relative_to(archive).as_posix(),
                                          Bytes=path.stat().st_size, SHA256=digest))
                write(validation / (label + '-binary-manifest.json'), files)
        passed = True
    finally:
        write(base / 'development-validation.json', dict(StageAccepted=False, FullAppExecuted=False,
            NativeMeasurement=False, BuildsPassed=passed, Commands=commands, BaselineSource=baseline_source))


if __name__ == '__main__':
    main()
