"""Actual Startup.Main on a private desktop, with explicit test-copy adapters."""
import argparse, importlib.util, shutil, subprocess, os
import re
from pathlib import Path
spec=importlib.util.spec_from_file_location('native',Path(__file__).with_name('Run-NativeTeardown.py'))
n=importlib.util.module_from_spec(spec); spec.loader.exec_module(n)
ROOT=n.ROOT

def close_owned_etw(dest,config):
    folder=dest/'runs'/f'{config}-graceful'/'realtime'
    if not (folder/'admission.json').exists():
        n.write(dest/'validation'/f'{config}-owned-etw-cleanup.json',dict(Started=False)); return
    admission=n.read(folder/'admission.json'); process=n.read(dest/'validation'/f'{config}-graceful-receipt.json')
    assert admission['Pid']==process['ProcessId'] and re.fullmatch(r'FWM_OWNED_GUI_RT_'+str(process['ProcessId'])+r'_\d+',admission['SessionName'])
    summary=n.read(folder/'summary.json') if (folder/'summary.json').exists() else {}
    commands=[]
    if admission['StartCode']==0 and summary.get('StopCode')!=0:
        cmd=['logman','stop',admission['SessionName'],'-ets'];r=subprocess.run(cmd,capture_output=True,text=True,encoding='utf-8',errors='replace')
        commands.append(dict(Command=cmd,ExitCode=r.returncode,Stdout=r.stdout,Stderr=r.stderr));assert r.returncode==0
    cmd=['logman','query',admission['SessionName'],'-ets'];r=subprocess.run(cmd,capture_output=True,text=True,encoding='utf-8',errors='replace')
    commands.append(dict(Command=cmd,ExitCode=r.returncode,Stdout=r.stdout,Stderr=r.stderr))
    assert r.returncode!=0 and 'not found' in (r.stdout+r.stderr).lower(),'own ETW session absence is unverified'
    n.write(dest/'validation'/f'{config}-owned-etw-cleanup.json',dict(Started=admission['StartCode']==0,OwnPid=process['ProcessId'],Commands=commands,Inactive=True,ForeignSessionsModified=False))

def adapt(source,work,owned_membership=False):
    shutil.copytree(source,work)
    changes=[]
    def change(rel,old,new):
        p=work/rel; raw=p.read_bytes(); a=old.replace('\n','\r\n').encode() if b'\r\n' in raw else old.encode(); b=new.replace('\n','\r\n').encode() if b'\r\n' in raw else new.encode()
        assert raw.count(a)==1,(rel,old)
        before=n.sha(p); p.write_bytes(raw.replace(a,b)); changes.append(dict(Path=rel,BeforeSHA256=before,AfterSHA256=n.sha(p),Old=old,New=new))
    change('FancyWM/Utilities/SystemParameters.cs','public static SystemParameters Instance { get; } = new();','public static SystemParameters Instance { get; } = new();\n        public static Action<bool>? HarnessWindowArrangingWrite;')
    change('FancyWM/Utilities/SystemParameters.cs','''unsafe
                {
                    uint param = value ? 1u : 0u;
                    if (!SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETWINARRANGING, param, null, SystemParametersInfo_fWinIni.SPIF_SENDCHANGE))
                        throw new Win32Exception("Failed to set window arranging enabled state.");
                }''','''(HarnessWindowArrangingWrite ?? throw new InvalidOperationException("Missing owned setting adapter"))(value);''')
    change('FancyWM/Startup.cs','string roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);','string roamingPath = Environment.GetEnvironmentVariable("FWM_OWNED_STARTUP_ROOT") ?? throw new InvalidOperationException("Missing owned startup profile");')
    change('FancyWM/Startup.cs','app.InitializeComponent();','app.InitializeComponent();\n                app.StartupUri = new Uri($"/FancyWM;V{typeof(MainWindow).Assembly.GetName().Version};component/mainwindow.xaml", UriKind.Relative);')
    change('FancyWM/Startup.cs','int exitCode = app.Run();','int exitCode = app.Run();\n                System.Threading.Thread.Sleep(1500); // Owned probe widens the existing App.Run/provider-disposal boundary.')
    if owned_membership:
        change('winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktop.cs','public bool HasWindow(IWindow window)','public static Func<IWindow, bool?>? HarnessWindowMembership;\n\n        public bool HasWindow(IWindow window)')
        change('winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktop.cs','public bool HasWindow(IWindow window)\n        {\n            try','public bool HasWindow(IWindow window)\n        {\n            if (HarnessWindowMembership?.Invoke(window) is bool owned) { return IsCurrent && owned; }\n            try')
    return changes

def main():
    p=argparse.ArgumentParser(); p.add_argument('--id',required=True); p.add_argument('--shutdown-probe',action='store_true'); p.add_argument('--owned-membership',action='store_true'); p.add_argument('--warmup',type=int,choices=[10,50],default=10); p.add_argument('--configs',nargs='+',default=['Debug','Release']); p.add_argument('--modes',nargs='+',default=['graceful','failfast','terminate','handler']); p.add_argument('--gui-trace',type=Path); p.add_argument('--gui-etw',type=Path)
    p.add_argument('--gui-quiescence',type=int,choices=[0,45],default=0)
    p.add_argument('--gui-stable-window',type=int,choices=[0,30],default=0)
    p.add_argument('--compact-targets',action='store_true')
    p.add_argument('--heap-observer',type=Path)
    p.add_argument('--heap-trace',action='store_true')
    p.add_argument('--clr-source',type=Path)
    p.add_argument('--pss-tool',type=Path)
    p.add_argument('--d3d9-tool',type=Path)
    p.add_argument('--d3d9-mapped',action='store_true')
    p.add_argument('--dxgi-memory-tool',type=Path)
    p.add_argument('--process-memory-tool',type=Path)
    a=p.parse_args()
    os.environ['FWM_FULLGRAPH_GUI_QUIESCENCE']=str(a.gui_quiescence)
    os.environ['FWM_FULLGRAPH_GUI_STABLE_WINDOW']=str(a.gui_stable_window)
    os.environ['FWM_FULLGRAPH_COMPACT_TARGETS']='1' if a.compact_targets else '0'
    os.environ.pop('FWM_GUI_TRACE_DLL',None)
    os.environ.pop('FWM_GUI_ETW_DLL',None)
    os.environ.pop('FWM_NATIVE_HEAP_DLL',None)
    os.environ.pop('FWM_CLR_HEAP_SOURCE',None)
    os.environ.pop('FWM_PSS_DLL',None)
    os.environ.pop('FWM_D3D9_DLL',None)
    os.environ.pop('FWM_D3D9_MAP_PSS_DLL',None)
    os.environ.pop('FWM_D3D9_MAP_ROOT',None)
    os.environ.pop('FWM_DXGI_MEMORY_DLL',None)
    os.environ.pop('FWM_PROCESS_MEMORY_DLL',None)
    if a.process_memory_tool:assert a.dxgi_memory_tool
    if a.d3d9_mapped:assert a.d3d9_tool and a.pss_tool
    os.environ['FWM_NATIVE_HEAP_TRACE']='1' if a.heap_trace else '0'
    if a.heap_trace:
        assert a.heap_observer
        proof=ROOT/'artifacts/performance/FWM-NATIVE-HEAP-20260913-E3/verification.json'
        assert n.read(proof)['Verdict']=='SCOPED_NATIVE_HEAP_TRACE_ADMISSION_PASS'
        os.environ['FWM_NATIVE_HEAP_XPERF']='C:/Program Files (x86)/Windows Kits/10/Windows Performance Toolkit/xperf.exe'
    if a.heap_observer:
        a.heap_observer=a.heap_observer.resolve()
        assert a.modes==['graceful'] and a.heap_observer.name=='FancyWM.NativeHeap.dll'
        calibration=n.read(a.heap_observer.parent/'verification.json')
        assert calibration['Verdict']=='SCOPED_NATIVE_CONTROL_PASS' and calibration['ObserverSHA256']==n.sha(a.heap_observer)
    if a.gui_etw:
        a.gui_etw=a.gui_etw.resolve(); assert a.gui_trace and a.gui_etw.is_file()
        prior=ROOT/'artifacts/performance/FWM-GUI-ATTRIBUTION-20260913-E3'
        assert n.read(prior/'verification.json')['ScopeFilterPassed'] and n.sha(a.gui_etw)==n.read(prior/'runs.json')['RealtimeDLLSHA256']
        assert (ROOT/'artifacts/performance/FWM-GUI-ATTRIBUTION-20260913-E0/etl-before.json').is_file()
    if a.gui_trace:
        a.gui_trace=a.gui_trace.resolve(); assert a.gui_trace.name=='FancyWM.GuiTrace.dll' and a.gui_trace.is_file()
        assert a.modes==['graceful'], 'GUI attribution has no hard-crash coverage claim'
    os.environ['FWM_FULLGRAPH_SHUTDOWN_PROBE']='1' if a.shutdown_probe else '0'
    os.environ['FWM_OWNED_MEMBERSHIP']='1' if a.owned_membership else '0'
    os.environ['FWM_FULLGRAPH_WARMUP']=str(a.warmup)
    dest=ROOT/'artifacts/performance'/a.id; assert not dest.exists(); assert n.datetime.now().strftime('%Y%m%d') in a.id
    cmd=['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id]
    result=subprocess.run(cmd,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace'); assert result.returncode==0,result.stderr
    n.write(dest/'snapshot-command.json',dict(Command=cmd,ExitCode=result.returncode,Stdout=result.stdout,Stderr=result.stderr))
    (dest/'validation').mkdir(); work=dest/'work'; changes=adapt(dest/'source',work,a.owned_membership)
    n.write(dest/'invocation.json',dict(Command=['python',__file__,*os.sys.argv[1:]],Environment={key:value for key,value in sorted(os.environ.items()) if key.startswith('FWM_')},RecordedUtc=n.now(),NativeProcessesNotYetStarted=True))
    changed=[str(p.relative_to(work)).replace('\\','/') for p in work.rglob('*') if p.is_file() and n.sha(p)!=n.sha(dest/'source'/p.relative_to(work))]
    assert sorted(changed)==['FancyWM/Startup.cs','FancyWM/Utilities/SystemParameters.cs']+(['winman-windows/src/WinMan.Windows/Windows/Win32VirtualDesktop.cs'] if a.owned_membership else [])
    n.write(dest/'adapters.json',dict(Changes=changes,ChangedFiles=sorted(changed),OriginalSourceImmutable=True,RootProductionChanged=False,UnmodifiedStartup=False))
    if a.gui_trace:
        tool=a.gui_trace.parent
        assert n.read(tool/'build-receipt.json')['ExitCode']==0
        for rel in ['Trace.cpp','Control.cpp']:
            assert n.sha(tool/'source'/rel)==n.sha(dest/'source/scripts/performance/gui-attribution'/rel)
        n.write(dest/'gui-trace-provenance.json',dict(Path=str(a.gui_trace),SHA256=n.sha(a.gui_trace),BuildReceiptSHA256=n.sha(tool/'build-receipt.json'),SourceManifestSHA256=n.sha(tool/'source-provenance.json'),HostOnly=True,OwnProcessInterception=True,TraceTimingIsNotPerformanceEvidence=True))
    if a.gui_etw:
        assert n.sha(a.gui_etw.parent/'source/Realtime.cpp')==n.sha(dest/'source/scripts/performance/gui-attribution/Realtime.cpp')
        n.write(dest/'realtime-provenance.json',dict(DllPath=str(a.gui_etw),SHA256=n.sha(a.gui_etw),NativeSourceSHA256=n.sha(a.gui_etw.parent/'source/Realtime.cpp'),AdmissionSHA256=n.sha(prior/'verification.json'),OwnResourceConsumerFilter=True))
        cmd=['wpr','-status'];r=subprocess.run(cmd,capture_output=True)
        n.write(dest/'validation/wpr-before.json',dict(Command=cmd,ExitCode=r.returncode,Stdout=r.stdout.decode('utf-8',errors='replace')))
        assert r.returncode==0 and b'WPR is not recording' in r.stdout
    builds=[]; processes=[]
    if a.dxgi_memory_tool:
        a.dxgi_memory_tool=a.dxgi_memory_tool.resolve()
        assert a.modes==['graceful'] and a.warmup==50 and not any([a.pss_tool,a.d3d9_tool,a.heap_observer,a.heap_trace,a.clr_source,a.gui_trace,a.gui_etw])
        proof=ROOT/'artifacts/performance/FWM-DXGI-MEMORY-20260913-V1/verification.json'
        assert n.sha(proof)=='F18AC09ACD9326CC57198B75BCBB255889981111D53D696921BA47937F09F7ED'
        calibration=n.read(proof);assert calibration['Verdict']=='SCOPED_NATIVE_DXGI_MEMORY_SOURCE_PASS' and all(r['Rejected'] for r in calibration['NegativeControls'])
        manifest=n.read(a.dxgi_memory_tool/'provenance.json')
        for row in manifest['Sources']+manifest['Binaries']:assert n.sha(a.dxgi_memory_tool/row['Path'])==row['SHA256']
        assert n.sha(a.dxgi_memory_tool/'source/DxgiMemory.cpp')==n.sha(dest/'source/scripts/performance/dxgi-memory/DxgiMemory.cpp')
        controls=[r for r in calibration['Configurations'] if r['PartialReleaseFence']]
        assert [r['Configuration'] for r in controls]==['Debug','Release']
        assert all(r['ToolProvenanceSHA256']==n.sha(a.dxgi_memory_tool/'provenance.json') and r['ObserverSHA256']==n.sha(a.dxgi_memory_tool/'binaries'/r['Configuration']/'DxgiMemory.dll') for r in controls)
        n.write(dest/'dxgi-provenance.json',dict(Tool=str(a.dxgi_memory_tool),ToolProvenanceSHA256=n.sha(a.dxgi_memory_tool/'provenance.json'),CalibrationSHA256=n.sha(proof),NodeIndex=0,ReservationChanged=False,CompleteGraphicsMemoryClaim=False,NoTracingSession=True,HookInterception=False))
    if a.process_memory_tool:
        a.process_memory_tool=a.process_memory_tool.resolve();proof=ROOT/'artifacts/performance/FWM-DXGI-MEMORY-20260913-V2/verification.json'
        assert n.sha(proof)=='973D2574CAE4871BE0341B80EBB2589E1B409E4FA952B9E35933321CBFBAEEAA'
        calibration=n.read(proof);assert calibration['Verdict']=='SCOPED_NATIVE_PROCESS_COMMIT_SOURCE_PASS' and all(r['Rejected'] for r in calibration['NegativeControls'])
        manifest=n.read(a.process_memory_tool/'provenance.json')
        for row in manifest['Sources']+manifest['Binaries']:assert n.sha(a.process_memory_tool/row['Path'])==row['SHA256']
        assert n.sha(a.process_memory_tool/'source/Memory.cpp')==n.sha(dest/'source/scripts/performance/process-memory/Memory.cpp')
        assert all(r['ToolProvenanceSHA256']==n.sha(a.process_memory_tool/'provenance.json') and r['ObserverSHA256']==n.sha(a.process_memory_tool/'binaries'/r['Configuration']/'Memory.dll') for r in calibration['Configurations'])
        n.write(dest/'memory-provenance.json',dict(Tool=str(a.process_memory_tool),ToolProvenanceSHA256=n.sha(a.process_memory_tool/'provenance.json'),CalibrationSHA256=n.sha(proof),CurrentProcessOnly=True,AtomicWithDxgi=False,NoTracingSession=True,HookInterception=False))
    if a.d3d9_tool:
        a.d3d9_tool=a.d3d9_tool.resolve();assert a.modes==['graceful'] and a.pss_tool and not a.heap_trace and not a.clr_source
        proof=ROOT/('artifacts/performance/FWM-D3D-MAP-20260913-V3/verification.json' if a.d3d9_mapped else 'artifacts/performance/FWM-D3D-LIFETIME-20260913-V3/verification.json')
        assert n.sha(proof)==('C02D61900F1995B2F6266CE18B72620BECD0D28F036714EA19DC557A50F8F489' if a.d3d9_mapped else '834A960771929092A30997488ABAD9A99CA408B04B1545E6A7E54F6BA0406662')
        calibration=n.read(proof);assert calibration['Verdict']=='SCOPED_HOOKED_D3D9_PRIVATE_OWNERS_PASS'
        manifest=n.read(a.d3d9_tool/'provenance.json')
        for row in manifest['Sources']+manifest['Binaries']:assert n.sha(a.d3d9_tool/row['Path'])==row['SHA256']
        for name in ['Observer9.cpp','Resource9.h','Device9.h']:
            assert n.sha(a.d3d9_tool/'source'/name)==n.sha(dest/'source/scripts/performance/d3d-lifetime'/name)
        assert all(r['ToolProvenanceSHA256']==n.sha(a.d3d9_tool/'provenance.json') and r['ObserverSHA256']==n.sha(a.d3d9_tool/'binaries'/r['Configuration']/'Observer9.dll') for r in calibration['Configurations'])
        n.write(dest/'d3d9-provenance.json',dict(Tool=str(a.d3d9_tool),ToolProvenanceSHA256=n.sha(a.d3d9_tool/'provenance.json'),CalibrationSHA256=n.sha(proof),PrivateDataOwnersOnly=True,CompleteResourceDestructionClaim=False,NoTracingSession=True))
        if a.d3d9_mapped:
            map_proof=ROOT/'artifacts/performance/FWM-D3D-MAP-20260913-V4/verification.json';method_proof=ROOT/'artifacts/performance/FWM-D3D-MAP-20260913-V2/verification.json'
            assert n.sha(map_proof)=='20A039D7263D6C674A2EB7E44559D668C8B509689D39760930777FADEDE37B5B' and n.sha(method_proof)=='5C5C2DCA81A1110CBA632141C7AF8C0FFB9868AB95905F925259AF7F9591448E'
            assert n.sha(a.d3d9_tool/'source/MapPss.h')==n.sha(dest/'source/scripts/performance/d3d-lifetime/MapPss.h')
            n.write(dest/'mapped-pss-provenance.json',dict(CalibrationSHA256=n.sha(map_proof),MethodCalibrationSHA256=n.sha(method_proof),ObserverTool=str(a.d3d9_tool),SeparatePssModule=True,LiveBufferBytesReadOrWritten=False,NoTracingSession=True))
    if a.pss_tool:
        a.pss_tool=a.pss_tool.resolve();assert a.modes==['graceful'] and not a.heap_trace and not a.clr_source
        proof=ROOT/'artifacts/performance/FWM-PSS-HEAP-20260913-V1/verification.json';calibration=n.read(proof)
        assert calibration['Verdict']=='SCOPED_PSS_PRIVATE_MEMORY_SOURCE_PASS' and calibration['ToolProvenanceSHA256']==n.sha(a.pss_tool/'provenance.json')
        manifest=n.read(a.pss_tool/'provenance.json')
        for row in manifest['Sources']+manifest['Binaries']:assert n.sha(a.pss_tool/row['Path'])==row['SHA256']
        assert n.sha(a.pss_tool/'source/Pss.cpp')==n.sha(dest/'source/scripts/performance/pss-heap/Pss.cpp')
        n.write(dest/'pss-provenance.json',dict(Tool=str(a.pss_tool),ToolProvenanceSHA256=n.sha(a.pss_tool/'provenance.json'),CalibrationSHA256=n.sha(proof),PrivateVaInventoryOnly=True,HeapBlockEnumeration=False,NoTracingSession=True))
    if a.clr_source:
        a.clr_source=a.clr_source.resolve();assert a.heap_trace and a.modes==['graceful']
        calibration=ROOT/'artifacts/performance/FWM-CLR-HEAP-20260913-V1/verification.json'
        assert n.read(calibration)['Verdict']=='SCOPED_CLR_NATIVE_CALLER_SOURCE_PASS'
        tool=ROOT/'artifacts/performance/FWM-CLR-HEAP-20260913-T2';manifest=n.read(tool/'provenance.json')
        row=next(r for r in manifest['Binaries'] if (tool/r['Path']).resolve()==a.clr_source)
        assert n.sha(a.clr_source)==row['SHA256']
        os.environ['FWM_CLR_HEAP_SOURCE']=str(a.clr_source)
        n.write(dest/'clr-source-provenance.json',dict(Executable=str(a.clr_source),SHA256=n.sha(a.clr_source),CalibrationSHA256=n.sha(calibration),ToolProvenanceSHA256=n.sha(tool/'provenance.json'),StartedBeforeStartup=True,LogicalOwnerClaim=False))
    if a.heap_observer:
        assert n.sha(a.heap_observer.parent/'source/Heap.cpp')==n.sha(dest/'source/scripts/performance/native-heap/Heap.cpp')
        n.write(dest/'heap-observer-provenance.json',dict(Path=str(a.heap_observer),SHA256=n.sha(a.heap_observer),CalibrationSHA256=n.sha(a.heap_observer.parent/'verification.json'),NativeSourceSHA256=n.sha(a.heap_observer.parent/'source/Heap.cpp'),DefaultProcessHeapOnly=True,NoTracingSession=not a.heap_trace))
    if a.heap_trace:
        for name,cmd in [('wpr-before-heap',['wpr','-status']),('token-before-heap',['whoami','/all'])]:
            r=subprocess.run(cmd,capture_output=True);n.write(dest/'validation'/(name+'.json'),dict(Command=cmd,ExitCode=r.returncode,RecordedUtc=n.now()))
            with (dest/'validation'/(name+'.stdout')).open('xb') as f:f.write(r.stdout)
            with (dest/'validation'/(name+'.stderr')).open('xb') as f:f.write(r.stderr)
            assert r.returncode==0
            if name=='wpr-before-heap':assert b'WPR is not recording' in r.stdout
        n.write(dest/'heap-trace-provenance.json',dict(AdmissionSHA256=n.sha(proof),XperfPath=os.environ['FWM_NATIVE_HEAP_XPERF'],XperfSHA256=n.sha(Path(os.environ['FWM_NATIVE_HEAP_XPERF'])),OwnPidOnly=True,StartAfterWarmup=True,NoGlobalKernelSession=True,NoRegistryChange=True))
    for config in a.configs:
        binaries=dest/'binaries'/config
        for kind,project,out in [('graph','scripts/performance/fullgraph-lifetime/FancyWM.FullGraphLifetime.csproj',binaries),('targets','scripts/performance/fullgraph-targets/FancyWM.Perf010Targets.csproj',binaries/'targets')]:
            cmd=['dotnet','build',project,'-c',config,'-o',str(out),'-v','minimal']; began=n.now()
            with (dest/'validation'/f'{config}-{kind}-build.log').open('xb') as log: result=subprocess.run(cmd,cwd=work,stdout=log,stderr=subprocess.STDOUT,timeout=240)
            record=dict(Command=cmd,WorkingDirectory=str(work),StartedUtc=began,EndedUtc=n.now(),ExitCode=result.returncode)
            n.write(dest/'validation'/f'{config}-{kind}-build.json',record); assert result.returncode==0,record; builds.append(record)
        if a.gui_trace:
            observer=binaries/a.gui_trace.name; assert not observer.exists(); shutil.copyfile(a.gui_trace,observer)
            os.environ['FWM_GUI_TRACE_DLL']=str(observer)
        if a.gui_etw:
            observer=binaries/a.gui_etw.name; assert not observer.exists(); shutil.copyfile(a.gui_etw,observer)
            os.environ['FWM_GUI_ETW_DLL']=str(observer)
        if a.heap_observer:
            observer=binaries/a.heap_observer.name; assert not observer.exists(); shutil.copyfile(a.heap_observer,observer)
            os.environ['FWM_NATIVE_HEAP_DLL']=str(observer)
        if a.dxgi_memory_tool:
            observer=binaries/'DxgiMemory.dll';assert not observer.exists();shutil.copyfile(a.dxgi_memory_tool/'binaries'/config/observer.name,observer)
            os.environ['FWM_DXGI_MEMORY_DLL']=str(observer)
            n.write(dest/f'{config}-dxgi-binary.json',dict(Path=str(observer.relative_to(dest)),SHA256=n.sha(observer),NativeConfiguration=config,Environment={'FWM_DXGI_MEMORY_DLL':str(observer)}))
        if a.process_memory_tool:
            observer=binaries/'Memory.dll';assert not observer.exists();shutil.copyfile(a.process_memory_tool/'binaries'/config/observer.name,observer)
            os.environ['FWM_PROCESS_MEMORY_DLL']=str(observer)
            n.write(dest/f'{config}-memory-binary.json',dict(Path=str(observer.relative_to(dest)),SHA256=n.sha(observer),NativeConfiguration=config,Environment={'FWM_PROCESS_MEMORY_DLL':str(observer)}))
        n.write(dest/f'{config}-binary-manifest.json',[dict(Path=str(p.relative_to(dest)),Bytes=p.stat().st_size,SHA256=n.sha(p)) for p in sorted(binaries.rglob('*')) if p.is_file()])
        if a.pss_tool:
            observer=binaries/'FancyWM.NativePss.dll';assert not observer.exists();shutil.copyfile(a.pss_tool/'binaries'/config/observer.name,observer)
            os.environ['FWM_PSS_DLL']=str(observer)
            n.write(dest/f'{config}-pss-binary.json',dict(Path=str(observer.relative_to(dest)),SHA256=n.sha(observer),NativeConfiguration=config))
        for mode in a.modes:
            if a.d3d9_tool:
                observer=binaries/'Observer9.dll';assert not observer.exists();shutil.copyfile(a.d3d9_tool/'binaries'/config/observer.name,observer)
                os.environ['FWM_D3D9_DLL']=str(observer)
                n.write(dest/f'{config}-d3d9-binary.json',dict(Path=str(observer.relative_to(dest)),SHA256=n.sha(observer),NativeConfiguration=config))
                if a.d3d9_mapped:
                    mapped=binaries/'FancyWM.MapPss.dll';assert not mapped.exists();shutil.copyfile(a.pss_tool/'binaries'/config/'FancyWM.NativePss.dll',mapped)
                    os.environ['FWM_D3D9_MAP_PSS_DLL']=str(mapped);os.environ['FWM_D3D9_MAP_ROOT']=str(dest/'runs'/(config+'-graceful')/'d3d9-mapped-pss')
                    n.write(dest/f'{config}-mapped-pss-binary.json',dict(Path=str(mapped.relative_to(dest)),SHA256=n.sha(mapped),NativeConfiguration=config,Environment={k:v for k,v in os.environ.items() if k.startswith('FWM_D3D9_MAP_')}))
            try: processes.append(n.launch(dest,binaries/'FancyWM.FullGraphLifetime.exe',config,mode,timeout_seconds=600))
            finally:
                if a.clr_source:
                    folder=dest/'runs'/f'{config}-graceful'
                    if (folder/'clr-created.json').exists():
                        own=n.read(folder/'clr-created.json');record=n.read(dest/'validation'/f'{config}-graceful-receipt.json')
                        assert own['Pid']==record['ProcessId'] and re.fullmatch(r'FWM_OWNED_CLR_'+str(own['Pid'])+r'_[0-9a-f]{32}',own['Session'])
                        # Match retained child creation time before any recovery.
                        script='$p=Get-Process -Id '+str(own['CollectorPid'])+' -ErrorAction SilentlyContinue; if ($p -and $p.StartTime.ToUniversalTime().Ticks -eq '+str(own['CollectorStartUtcTicks'])+') { if (-not $p.WaitForExit(20000)) { $p.Kill(); $p.WaitForExit() }; "Owned collector recovery" } else { "Original collector absent" }'
                        cmd=['pwsh','-NoProfile','-Command',script];r=subprocess.run(cmd,capture_output=True);checks=[dict(Command=cmd,ExitCode=r.returncode,StdoutHex=r.stdout.hex(),StderrHex=r.stderr.hex())];assert r.returncode==0
                        cmd=['logman','query',own['Session'],'-ets'];r=subprocess.run(cmd,capture_output=True);checks.append(dict(Command=cmd,ExitCode=r.returncode,StdoutHex=r.stdout.hex()))
                        if r.returncode==0:
                            cmd=['logman','stop',own['Session'],'-ets'];stop=subprocess.run(cmd,capture_output=True);checks.append(dict(Command=cmd,ExitCode=stop.returncode,StdoutHex=stop.stdout.hex()));assert stop.returncode==0
                            cmd=['logman','query',own['Session'],'-ets'];r=subprocess.run(cmd,capture_output=True);checks.append(dict(Command=cmd,ExitCode=r.returncode,StdoutHex=r.stdout.hex()))
                        assert r.returncode&4294967295==0x80300002
                        n.write(dest/'validation'/f'{config}-clr-cleanup.json',dict(OwnPid=own['Pid'],Session=own['Session'],Inactive=True,Commands=checks))
                if a.gui_etw: close_owned_etw(dest,config)
                if a.heap_trace:
                    folder=dest/'runs'/f'{config}-graceful'
                    if (folder/'heap-session.json').exists():
                        session=n.read(folder/'heap-session.json');record=n.read(dest/'validation'/f'{config}-graceful-receipt.json')
                        assert session['Pid']==record['ProcessId'] and re.fullmatch(r'FWM_OWNED_GRAPH_HEAP_'+str(record['ProcessId'])+r'_[0-9a-f]{32}',session['Session'])
                        cmd=['logman','query',session['Session'],'-ets'];r=subprocess.run(cmd,capture_output=True)
                        checks=[dict(Command=cmd,ExitCode=r.returncode,StdoutHex=r.stdout.hex(),StderrHex=r.stderr.hex())]
                        if r.returncode==0:
                            cmd=[os.environ['FWM_NATIVE_HEAP_XPERF'],'-stop',session['Session']];stop=subprocess.run(cmd,capture_output=True)
                            checks.append(dict(Command=cmd,ExitCode=stop.returncode,StdoutHex=stop.stdout.hex(),StderrHex=stop.stderr.hex()));assert stop.returncode==0
                            cmd=['logman','query',session['Session'],'-ets'];r=subprocess.run(cmd,capture_output=True);checks.append(dict(Command=cmd,ExitCode=r.returncode,StdoutHex=r.stdout.hex()))
                        assert (r.returncode&4294967295)==0x80300002
                        n.write(dest/'validation'/f'{config}-heap-cleanup.json',dict(OwnPid=record['ProcessId'],Session=session['Session'],Inactive=True,Commands=checks))
    n.write(dest/'runs.json',dict(Builds=builds,Processes=processes,RootProductionChanged=False,FullStartupGraphWithAdapters=True,ControlledTargetMembership=a.owned_membership,UnmodifiedStartup=False,StageAccepted=False,PerformanceClaim=False,LedgerAppended=False))
if __name__=='__main__': main()
