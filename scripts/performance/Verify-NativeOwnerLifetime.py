"""Verify native-owner receipts, TRX leaves, raw observations and frozen provenance."""
import argparse
import copy
import csv
import hashlib
import json
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path

NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
NAMES={'SettingsNativeCloseAndStaleNavigation','SettingsNativeFailureOrdering','SettingsNativeRetention',
       'OverlayNativeCloseAndStaleCallbacks','OverlayNativeFailureOrdering','OverlayNativeRetention',
       'ApplicationShutdownClosesNativeOwnersBeforeDispatcherStops'}
def require(condition,message):
    if not condition: raise ValueError(message)
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
CONSTRUCTION={'position','scale-add','scale-second-add','cursor-add','rollback-error'}
def construction(tree,passed):
    leaves=[r for r in tree.findall('.//t:UnitTestResult',NS) if r.find('t:InnerResults',NS) is None]
    selected=[r for r in leaves if r.get('testName','').startswith('OverlayNativeConstruction')]
    require(len(selected)==5,'construction scenarios')
    rows=[]
    for result in selected:
        require(result.get('outcome')==('Passed' if passed else 'Failed'),'construction outcome')
        if not passed:
            require('Failed production constructor retained an acquired HWND.' in result.findtext('t:Output/t:ErrorInfo/t:Message','',NS),'unexpected baseline failure')
        for line in result.findtext('t:Output/t:StdOut','',NS).splitlines():
            if line.startswith('NATIVE_OWNER '):
                row=json.loads(line[13:])
                if row['scenario']=='overlay-construction': rows.append(row)
    check_construction(rows,passed)
    return rows
def check_construction(rows,passed):
    require(len(rows)==5 and {r['mode'] for r in rows}==CONSTRUCTION,'missing construction mode')
    for row in rows:
        require(row['actualException']=='owned overlay construction '+row['mode'],'original construction exception')
        expected=1 if row['mode'] in ['position','scale-add'] else 2
        require(len(row['acquiredHwnds'])==expected and all(h>0 for h in row['acquiredHwnds']),'real construction HWND acquisition')
        require(row['survivingHwnds']==(0 if passed else expected),'surviving construction HWND')
        require(row['survivingWindows']==(0 if passed else expected),'retained construction window')
        if passed: require(row['subscriptions']==0,'retained construction owner')
def check_rows(rows):
    require(len(rows)==34,'missing/duplicate observation')
    for key in ['process','process-end']:
        selected=[r for r in rows if r['scenario']==key]
        require(len(selected)==7 and {r['test'] for r in selected}==NAMES,'missing process boundary')
    for name in NAMES:
        start=next(r for r in rows if r['scenario']=='process' and r['test']==name)
        end=next(r for r in rows if r['scenario']=='process-end' and r['test']==name)
        require(start['pid']==end['pid'] and start['pid']>0 and start['bitness']==64,'process identity')
        require(start['scope']=='minimal-App-controlled-providers','scope')
        require(datetime.fromisoformat(start['startedUtc']) < datetime.fromisoformat(end['endedUtc']),'process order')
    expected={'settings-close':{'normal','reentrant','queued'},'settings-failure':{'closed','viewmodel','combined','page'},
              'overlay-close':{'hidden','shown','reentrant','worker'},'overlay-failure':{'workspace','display','settings','closed'}}
    for scenario,modes in expected.items():
        group=[r for r in rows if r['scenario']==scenario]
        require(len(group)==len(modes) and {r['mode'] for r in group}==modes,'missing scenario')
        for row in group:
            require(row['destroyed'] is True,'surviving HWND')
            require(row['subscriptions']==0,'retained owner')
            require(row['dispatcherAlive'] is True,'dispatcher terminated before observation')
            if scenario.startswith('settings'):
                require(row['hwnd']>0 and row['closed']==1 and row['flushes']==1,'settings cleanup')
            else:
                require(row['hit']>0 and row['nonhit']>0 and row['hit']!=row['nonhit'],'native pair')
                require(row['owner']==row['nonhit'] and row['wpfOwner']>0,'native owner relationship')
                require(row['closed']==2 and row['timers']==0,'overlay cleanup')
            if scenario.endswith('failure'):
                require(row['errors']==(0 if row['mode']=='page' else 1) and row['originalException'] is True,'exception preservation')
            else: require(row['stale'] is True,'stale callback activity')
    epochs=[r for r in rows if r['scenario']=='retention']
    require(len(epochs)==4 and {(r['owner'],r['epoch']) for r in epochs}=={(o,e) for o in ['settings','overlay'] for e in [1,2]},'missing/duplicate epoch')
    for row in epochs:
        require(row['cycles']==50*row['epoch'],'retention cycles')
        require(row['references']==row['cycles']*(5 if row['owner']=='settings' else 7),'weak reference coverage')
        require(row['live']==0,'retained owner')
        require(row['dispatcherAlive'] is True,'dispatcher terminated before GC')
        require(row['initialUser']>0 and row['initialUser']==row['user'] and row['initialGdi']==row['gdi'],'USER/GDI growth')
    shutdown=[r for r in rows if r['scenario']=='shutdown']
    require(len(shutdown)==1,'shutdown coverage')
    row=shutdown[0]
    require(all(row[k]>0 for k in ['hwnd','hit','nonhit']) and row['closed']==3,'shutdown HWNDs')
    require(row['destroyed'] is True and row['subscriptions']==0 and row['beforeDispatcherStop'] is True and row['dispatcherStopped'] is True,'shutdown order')

def main():
    p=argparse.ArgumentParser(); p.add_argument('--snapshot',type=Path,required=True); p.add_argument('--output',type=Path,required=True)
    p.add_argument('--baseline',type=Path); a=p.parse_args()
    require(not a.output.exists(),'receipt already exists')
    runs=read(a.snapshot/'runs.json'); require([r['Configuration'] for r in runs]==['Debug','Release'],'Debug/Release coverage')
    require(datetime.fromisoformat(runs[0]['EndedUtc'])<datetime.fromisoformat(runs[1]['StartedUtc']),'overlapping test processes')
    outputs={}; compositions=[]
    for run in runs:
        config=run['Configuration']; require(run['ExitCode']==0,'failed command')
        require(sha(a.snapshot/f'{config}.log')==run['LogSHA256'],'log hash')
        for entry in run['Binaries']:
            path=a.snapshot/entry['Path']; require(path.stat().st_size==entry['Bytes'] and sha(path)==entry['SHA256'],'binary provenance')
        trx=a.snapshot/'validation'/config/f'{config}.trx'; tree=ET.parse(trx).getroot()
        results=tree.findall('.//t:UnitTestResult',NS); leaves=[r for r in results if r.find('t:InnerResults',NS) is None]
        require(leaves and all(r.get('outcome')=='Passed' for r in results),'failed/skipped result')
        counters=tree.find('t:ResultSummary/t:Counters',NS)
        require(counters is not None and int(counters.get('failed',-1))==0 and counters.get('passed')==counters.get('total'),'TRX counters')
        # MSTest counters include data-driven parent results; only results with
        # no InnerResults are actual test leaves.
        require(int(counters.get('total'))==len(results),'raw result cardinality')
        require(len(leaves)==(166 if a.baseline else 161),'targeted/affected test composition cardinality')
        native=[r for r in leaves if r.get('testName') in NAMES]
        require(len(native)==7 and {r.get('testName') for r in native}==NAMES,'native scenarios')
        rows=[]
        for result in native:
            for line in result.findtext('t:Output/t:StdOut','',NS).splitlines():
                if line.startswith('NATIVE_OWNER '): rows.append(json.loads(line[len('NATIVE_OWNER '):]))
        check_rows(rows)
        tests=sorted(r.get('testName') for r in leaves); compositions.append(tests)
        outputs[config]=dict(PassedLeaves=len(leaves),NativeTests=7,RelatedTests=len(leaves)-7,TRXSHA256=sha(trx),Rows=rows)
        if a.baseline:
            outputs[config]['ConstructionRows']=construction(tree,True)
            outputs[config]['NativeTests']=12; outputs[config]['RelatedTests']=154
    require(compositions[0]==compositions[1],'Debug/Release composition mismatch')
    with (a.snapshot/'manifest.csv').open(encoding='utf-8-sig',newline='') as stream:
        sources=list(csv.DictReader(stream))
    for row in sources: require(sha(a.snapshot/'source'/row['Path'])==row['SHA256'],'frozen source provenance')
    comparison=None
    if a.baseline:
        with (a.baseline/'manifest.csv').open(encoding='utf-8-sig',newline='') as f: old={r['Path']:r['SHA256'] for r in csv.DictReader(f)}
        changes=[]
        for row in sources:
            rel=row['Path']
            if Path(rel).parts[0].startswith('FancyWM') or Path(rel).parts[0] in ['ModernWpf','winman','winman-windows']:
                require(rel in old,'different fixture file set')
                require(sha(a.baseline/'source'/rel)==old[rel],'baseline source provenance')
                if old[rel]!=row['SHA256']: changes.append(rel)
        require(sorted(changes)==['FancyWM/Windows/OverlayHost.OverlayWindow.cs','FancyWM/Windows/OverlayHost.cs'],'undeclared candidate delta')
        baselineTrx=a.baseline/'validation/Debug/Debug.trx'
        comparison=dict(BaselineTRXSHA256=sha(baselineTrx),RedRows=construction(ET.parse(baselineTrx).getroot(),False),ProductionDelta=changes,CommonFixture=True)
    negative=[]
    for case in ['missing-scenario','surviving-HWND','retained-owner','duplicate-epoch']:
        altered=copy.deepcopy(outputs['Debug']['Rows'])
        if case=='missing-scenario': next(r for r in altered if r['scenario']=='overlay-close')['mode']='absent'
        if case=='surviving-HWND': next(r for r in altered if r['scenario']=='settings-close')['destroyed']=False
        if case=='retained-owner': next(r for r in altered if r['scenario']=='retention')['live']=1
        if case=='duplicate-epoch': next(r for r in altered if r['scenario']=='retention')['epoch']=2
        try: check_rows(altered)
        except ValueError as error: negative.append(dict(Case=case,Rejected=True,Reason=str(error)))
        else: raise ValueError('negative control accepted: '+case)
    if a.baseline:
        for field in ['survivingHwnds','subscriptions']:
            altered=copy.deepcopy(outputs['Debug']['ConstructionRows']); altered[0][field]=1
            try: check_construction(altered,True)
            except ValueError as error: negative.append(dict(Case='construction-'+field,Rejected=True,Reason=str(error)))
            else: raise ValueError('construction negative accepted')
    receipt=dict(NativeOwnerLifetime='PASS',WholeIdStatus='IN_PROGRESS',ProductionChanged=bool(a.baseline),PerformanceClaim=False,
        StageAccepted=False,LedgerAppended=False,SourceFilesVerified=len(sources),Runs=outputs,NegativeControls=negative,
        Scope='Production SettingsWindow/OverlayHost constructors and HWNDs in minimal App with controlled providers; hidden retention, no native-heap/GPU/presentation claim')
    if comparison: receipt['ConstructionComparison']=comparison
    with a.output.open('x',encoding='utf-8') as f: json.dump(receipt,f,indent=2)
    print(json.dumps(dict(Runs={k:v['PassedLeaves'] for k,v in outputs.items()},Observations=sum(len(v['Rows']) for v in outputs.values()),NegativeControls=len(negative),SHA256=sha(a.output))))
if __name__=='__main__': main()
