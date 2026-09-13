"""Read-only checkpoint verification; receipts use exclusive creation."""
import argparse
import csv
import hashlib
import json
import platform
import subprocess
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCS = ['PERFORMANCE_STATUS.md', 'PERFORMANCE_TODO.md', 'docs/performance/AUDIT.md',
        'docs/performance/IMPLEMENTATION_RESULTS.md', 'scripts/performance/README.md',
        'docs/performance/TOAST_NATIVE_LIFETIME.md', 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv']
PROD = {'FancyWM', 'FancyWM.GUI', 'FancyWM.Layouts', 'FancyWM.ThemeEngine', 'FancyWM.DllImports',
        'FancyWM.Package', 'ModernWpf', 'winman', 'winman-windows'}
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest().upper()
def info(p): return dict(Bytes=p.stat().st_size, SHA256=sha(p))
def run(args):
    p = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
    assert p.returncode == 0, (args, p.stderr)
    return dict(Command=args, ExitCode=p.returncode, Stdout=p.stdout, Stderr=p.stderr)
def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--candidate', type=Path)
    parser.add_argument('--mica-candidate', type=Path)
    parser.add_argument('--worker-candidate', type=Path)
    args = parser.parse_args(); assert not args.output.exists()
    r = dict(RecordedUtc=datetime.now(timezone.utc).isoformat(), OS=platform.platform(),
             Architecture=platform.machine(), Documents={}, Git=[], Production=[], OldReceipts={})
    for rel in DOCS:
        p=ROOT/rel; r['Documents'][rel] = dict(**info(p), Lines=len(p.read_bytes().splitlines()))
    for extra in ['docs/performance/NATIVE_OWNER_LIFETIME.md','docs/performance/FULLGRAPH_LIFETIME.md','docs/performance/GUI_RESOURCE_ATTRIBUTION.md','docs/performance/NATIVE_HEAP_LIFETIME.md','docs/performance/NATIVE_HEAP_COHORTS.md','docs/performance/CLR_NATIVE_HEAP_ATTRIBUTION.md']:
        if (ROOT/extra).exists(): r['Documents'][extra]=dict(**info(ROOT/extra),Lines=len((ROOT/extra).read_bytes().splitlines()))
    assert r['Documents'][DOCS[-1]] == dict(Bytes=41782945, Lines=41326,
        SHA256='84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11')
    for tail in [['rev-parse','HEAD','HEAD^{tree}'],['status','--short'],['diff','--check'],
                 ['diff','--cached','--check'],['submodule','status','--recursive'],
                 ['submodule','foreach','--recursive','git status --short'],
                 ['-C','winman-windows','rev-parse','HEAD','HEAD^{tree}'],
                 ['-C','winman-windows','diff','--check'],['-C','winman-windows','diff','--cached','--check']]:
        r['Git'].append(run(['git','-c','core.safecrlf=false',*tail]))
    r['Git'].append(run(['git','-c','core.safecrlf=false','submodule','foreach','--recursive','git -c core.safecrlf=false diff --check && git -c core.safecrlf=false diff --cached --check']))
    assert r['Git'][0]['Stdout'].splitlines() == ['4fb943be3ee3768bb6de42b16b0a22bdbafa2e0b','48c574b1abd3adb858942bd8c6a0f4d9fb3e39b7']
    assert r['Git'][6]['Stdout'].splitlines()[0] == 'adb9f55b84b567db9f6e0ee8df4c33d11a12d91f'
    base=ROOT/'artifacts/performance/FWM-OVERLAY-NESTED-NOTIFICATION-20260912-C2'
    allowed={'FancyWM/Windows/OverlayHost.cs','FancyWM/Windows/OverlayHost.OverlayWindow.cs'} if args.candidate else set()
    if args.mica_candidate: allowed.add('FancyWM/Utilities/FauxMicaProvider.cs')
    worker_paths={'FancyWM/Utilities/AnimationThread.cs','winman-windows/src/WinMan.Windows/Utilities/EventLoop.cs'}
    if args.worker_candidate: allowed.update(worker_paths)
    deltas=[]
    with (base/'manifest.csv').open(encoding='utf-8-sig',newline='') as f:
        for row in csv.DictReader(f):
            rel=row['Path'].replace('\\','/')
            if Path(rel).parts[0] not in PROD and rel not in ['Directory.Build.props','FancyWM.sln','version.json']: continue
            assert sha(base/'source'/rel) == row['SHA256'], rel
            if sha(ROOT/rel) != row['SHA256']:
                owner=args.worker_candidate if rel in worker_paths else args.mica_candidate if rel=='FancyWM/Utilities/FauxMicaProvider.cs' else args.candidate
                assert rel in allowed and sha(ROOT/rel)==sha(owner/'source'/rel), rel
                deltas.append(dict(Path=rel,OriginalSHA256=row['SHA256'],CandidateSHA256=sha(ROOT/rel)))
            r['Production'].append(dict(Path=rel, **info(ROOT/rel)))
    assert len(r['Production']) == 2155
    assert sum(x['Path'].startswith('winman-windows/') for x in r['Production']) == 72
    assert {x['Path'] for x in deltas}==allowed
    r['ProductionDelta']=deltas
    old=ROOT/'artifacts/performance/FWM-TOAST-NATIVE-20260912-R2'
    for name, expected in [('verification.json','C7F2D881DC4331D0F3E3357C3C4C6F893AA85ED1555E4BC062B71F3D2DA13E66'),
                           ('evidence-manifest.json','085F552DC736989074A397593C440858D20168A091EB55DF133E6E4AA8A4CD85')]:
        assert sha(old/name) == expected; r['OldReceipts'][name]=info(old/name)
    entries=json.loads((old/'evidence-manifest.json').read_text(encoding='utf-8-sig'))
    for entry in entries:
        assert info(old/entry['Path']) == {k:entry[k] for k in ['Bytes','SHA256']}, entry['Path']
    r['OldToastEvidenceFilesVerified']=len(entries)
    dpi=ROOT/'artifacts/performance/FWM-ANIMATION-FULLAPP-20260912-DPI120-RESTORATION-R1/multi-dpi-criterion.json'
    assert sha(dpi)=='342BA909BB03906E60136340D4F06D76A3AA5076F4CB9627589958792783A556'
    r['OldReceipts']['dpi-restoration']=info(dpi)
    ledger=(ROOT/DOCS[-1]).read_bytes(); prefix=ledger[:41782945]; suffix=ledger[41782945:]
    r['LedgerIntegrity']=dict(PrefixBytes=len(prefix),PrefixSHA256=hashlib.sha256(prefix).hexdigest().upper(),
        SuffixBytes=len(suffix),SuffixSHA256=hashlib.sha256(suffix).hexdigest().upper(),DataRows=len(ledger.splitlines())-1)
    assert r['LedgerIntegrity']['PrefixSHA256']==r['Documents'][DOCS[-1]]['SHA256'] and len(suffix)==0
    r['LedgerAppended']=False; r['ProductionChanged']=bool(deltas)
    with args.output.open('x',encoding='utf-8') as f: json.dump(r,f,indent=2)
    print(json.dumps(dict(ProductionFiles=len(r['Production']),OldToastFiles=len(entries),Receipt=info(args.output),Documents=r['Documents'])))
if __name__=='__main__': main()
