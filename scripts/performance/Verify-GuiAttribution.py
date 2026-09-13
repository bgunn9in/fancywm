"""Verify actual GUI counter identity and preserve corrected historical derivations."""
import argparse, copy, hashlib, importlib.util, json, re
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]; ART=ROOT/'artifacts/performance'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def rows(p):return [json.loads(x) for x in p.read_text(encoding='utf-8').splitlines()]
def require(value,message):
    if not value:raise ValueError(message)
def write(p,value):
    with p.open('x',encoding='utf-8') as f:json.dump(value,f,indent=2)
def calibration(rr,gdi=0,user=1,installed=30):
    require((gdi,user)==(0,1),'counter flags reversed')
    require([r['Sequence'] for r in rr]==list(range(1,len(rr)+1)),'missing/duplicate native API record')
    markers=[r for r in rr if r['Action']=='mark' and r['Phase']<=-100]
    require([r['Phase'] for r in markers]==[-100,-101,-102,-103,-104],'missing calibration phase')
    b,bits,menu,icon,end=markers
    require(bits['Argument']==b['Argument']+24 and bits['Result']==b['Result'],'GDI bitmap calibration')
    require(menu['Argument']==b['Argument'] and menu['Result']==b['Result']+24,'USER menu calibration')
    require(icon['Result']==b['Result']+24 and end['Argument']==b['Argument'] and end['Result']==b['Result'],'icon release/counter return')
    require(len([r for r in rr if r['Action']=='installed'])==installed,'instrumentation coverage')
    for name,kind,phase,release in [('CreateBitmap','Bitmap',-100,'DeleteObject'),('CreateMenu','Menu',-101,'DestroyMenu'),('CreateIconIndirect','Icon',-102,'DestroyIcon')]:
        acquire=[r for r in rr if r['Api']==name and r['Action']=='acquire' and r['Phase']==phase and r['Depth']==0]
        require(len(acquire)==24 and len({r['Handle'] for r in acquire})==24,'missing known '+kind)
        for a in acquire:
            require(a['Handle']>0 and a['Result']==a['Handle'] and len(a['Stack'])>1,'native allocation identity/stack')
            freed=[r for r in rr if r['Api']==release and r['Action']=='release' and r['Handle']==a['Handle'] and a['Sequence']<r['Sequence']<end['Sequence']]
            require(len(freed)==1 and freed[0]['Result']==1,'surviving known '+kind)
    return dict(Markers=[dict(Phase=r['Phase'],GDI=r['Argument'],USER=r['Result']) for r in markers],KnownLifetimes=72,InstalledApis=installed)

def historical():
    corrected=[]
    for root in sorted(ART.iterdir()):
        if not root.name.startswith(('FWM-FULLGRAPH-','FWM-WORKER-RETENTION-')) or '20260912' not in root.name:continue
        source=root/'source/scripts/performance/fullgraph-lifetime/Program.Retention.cs'
        files=sorted((root/'runs').glob('*/observations.jsonl'))
        if not source.exists() or not files:continue
        mapping=re.findall(r'USER = GetGuiResources\([^\n]*?Handle, (\d)\), GDI = GetGuiResources\([^\n]*?Handle, (\d)\)',source.read_text(encoding='utf-8'))
        require(mapping and all(m==('0','1') for m in mapping),'unknown historical API mapping '+root.name)
        for file in files:
            rr=[r for r in rows(file) if 'USER' in r and 'GDI' in r]
            if not rr:continue
            corrected.append(dict(Path=str(file.relative_to(ART)),Bytes=file.stat().st_size,SHA256=sha(file),SourceSHA256=sha(source),
                Rows=len(rr),OriginalLabelsReversed=True,CorrectedEpochs=[dict(Epoch=r['Epoch'],GDI=r['USER'],USER=r['GDI']) for r in rr if r['Kind']=='Epoch']))
    require(len(corrected)>5,'historical evidence coverage')
    return corrected

def main():
    p=argparse.ArgumentParser();p.add_argument('--calibration',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--extended',action='store_true');a=p.parse_args();require(not a.output.exists(),'receipt exists');installed=46 if a.extended else 30
    root=a.calibration;run=read(root/'validation/Native-graceful-receipt.json');summary=read(root/'runs/Native-graceful/summary.json')
    require(run['ExitCode']==0 and run['OwnedDesktopHandleClosed'] and not run['InputSent'] and not run['DesktopSwitched'] and run['Desktop'].startswith('FWM_OWNED_'),'native process isolation')
    require(summary['ControlCode']==0 and summary['Pid']==run['ProcessId'],'native calibration summary')
    rr=rows(root/'runs/Native-graceful/gui-api.jsonl');result=calibration(rr,summary['GdiFlag'],summary['UserFlag'],installed);controls=[]
    for case in ['swapped-flags','missing-native-call','missing-phase','surviving-menu','surviving-icon','wrong-counter']:
        bad=copy.deepcopy(rr);flags=(0,1)
        if case=='swapped-flags':flags=(1,0)
        if case=='missing-native-call':bad.remove(next(r for r in bad if r['Api']=='CreateBitmap' and r['Phase']==-100))
        if case=='missing-phase':bad.remove(next(r for r in bad if r['Action']=='mark' and r['Phase']==-103))
        if case=='surviving-menu':next(r for r in bad if r['Api']=='DestroyMenu' and r['Phase']==-102)['Result']=0
        if case=='surviving-icon':next(r for r in bad if r['Api']=='DestroyIcon' and r['Phase']==-103)['Result']=0
        if case=='wrong-counter':next(r for r in bad if r['Action']=='mark' and r['Phase']==-101)['Argument']-=1
        try:calibration(bad,*flags,installed)
        except ValueError as e:controls.append(dict(Case=case,Rejected=True,Reason=str(e)))
        else:raise ValueError('negative control accepted: '+case)
    old=historical()
    write(a.output,dict(Verdict='PASS',Scope='Native GUI counter identity and historical label correction',RecordedUtc=datetime.now(timezone.utc).isoformat(),
        Contract='https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguiresources',GdiFlag=0,UserFlag=1,Calibration=result,NegativeControls=controls,HistoricalDerivations=old,
        ProductionChanged=False,PerformanceClaim=False,StageAccepted=False,WholeIdStatus='IN_PROGRESS',LedgerAppended=False,HistoricalRawFilesModified=False,
        OldC5Corrected=dict(Debug=dict(GDI=[110,110,110],USER=[122,131,126]),Release=dict(GDI=[110,110,110],USER=[134,133,133])),
        CalibrationRawSHA256=sha(root/'runs/Native-graceful/gui-api.jsonl'),CalibrationProcessReceiptSHA256=sha(root/'validation/Native-graceful-receipt.json')))
    print(json.dumps(dict(Verdict='PASS',HistoricalRuns=len(old),NegativeControls=len(controls),SHA256=sha(a.output))))
if __name__=='__main__':main()
