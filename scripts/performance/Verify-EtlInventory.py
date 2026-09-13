"""Hash all workspace ETLs, including ignored files, and verify the prior inventory."""
import argparse,hashlib,json,subprocess
from datetime import datetime,timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def write(p,v):
    with p.open('x',encoding='utf-8') as f:json.dump(v,f,indent=2)
def main():
    p=argparse.ArgumentParser();p.add_argument('--id',required=True);p.add_argument('--before',type=Path,required=True);a=p.parse_args()
    dest=ROOT/'artifacts/performance'/a.id;assert not dest.exists() and datetime.now().strftime('%Y%m%d') in a.id;dest.mkdir()
    command=['rg','--files','--hidden','--no-ignore','-g','*.[eE][tT][lL]'];r=subprocess.run(command,cwd=ROOT,capture_output=True);assert r.returncode==0
    with (dest/'paths.raw').open('xb') as f:f.write(r.stdout)
    write(dest/'command.json',dict(Command=command,ExitCode=r.returncode,StartedUtc=datetime.now(timezone.utc).isoformat()))
    rows=[];paths=sorted(set(r.stdout.decode('utf-8').splitlines()))
    for i,rel in enumerate(paths):
        file=(ROOT/rel).resolve();assert file.is_relative_to(ROOT) and file.suffix.lower()=='.etl'
        stat=file.stat();digest=sha(file);end=file.stat();assert (stat.st_size,stat.st_mtime_ns)==(end.st_size,end.st_mtime_ns)
        rows.append(dict(Path=rel,Bytes=stat.st_size,SHA256=digest));print('ETL verified',i+1,len(paths),stat.st_size,flush=True)
    old=json.loads(a.before.read_text(encoding='utf-8'));current={r['Path']:r for r in rows}
    for row in old:assert current.get(row['Path'])==row,row['Path']
    oldnames={r['Path'] for r in old};added=[r for r in rows if r['Path'] not in oldnames]
    assert len(added)==3 and all(('FWM-GUI-ATTRIBUTION-20260913-E1' in r['Path'] or 'FWM-GUI-ATTRIBUTION-20260913-E2' in r['Path']) for r in added)
    write(dest/'etl-after.json',rows)
    write(dest/'verification.json',dict(Verdict='PASS',PriorFiles=len(old),PriorBytes=sum(r['Bytes'] for r in old),PriorInventorySHA256=sha(a.before),AfterInventorySHA256=sha(dest/'etl-after.json'),NewOwnedEtls=added,OriginalFilesUnchanged=True,IncludesIgnored=True,CaseInsensitiveSuffix=True,RecordedUtc=datetime.now(timezone.utc).isoformat()))
    print('ETL INTEGRITY PASS',len(rows),flush=True)
if __name__=='__main__':main()
