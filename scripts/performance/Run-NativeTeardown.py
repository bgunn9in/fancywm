"""Run only owned processes on fresh non-input desktops; never switch desktops."""
import argparse
import ctypes as C
from ctypes import wintypes as W
import hashlib
import json
import msvcrt
import os
import subprocess
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
K=C.WinDLL('kernel32',use_last_error=True); U=C.WinDLL('user32',use_last_error=True)
class STARTUPINFO(C.Structure):
    _fields_=[('cb',W.DWORD),('lpReserved',W.LPWSTR),('lpDesktop',W.LPWSTR),('lpTitle',W.LPWSTR),
        ('dwX',W.DWORD),('dwY',W.DWORD),('dwXSize',W.DWORD),('dwYSize',W.DWORD),('dwXCountChars',W.DWORD),('dwYCountChars',W.DWORD),
        ('dwFillAttribute',W.DWORD),('dwFlags',W.DWORD),('wShowWindow',W.WORD),('cbReserved2',W.WORD),('lpReserved2',C.c_void_p),
        ('hStdInput',W.HANDLE),('hStdOutput',W.HANDLE),('hStdError',W.HANDLE)]
class PROCESSINFO(C.Structure): _fields_=[('hProcess',W.HANDLE),('hThread',W.HANDLE),('dwProcessId',W.DWORD),('dwThreadId',W.DWORD)]
K.CreateProcessW.argtypes=[W.LPCWSTR,W.LPWSTR,C.c_void_p,C.c_void_p,W.BOOL,W.DWORD,C.c_void_p,W.LPCWSTR,C.POINTER(STARTUPINFO),C.POINTER(PROCESSINFO)]
K.CreateProcessW.restype=W.BOOL
K.WaitForSingleObject.argtypes=[W.HANDLE,W.DWORD]; K.WaitForSingleObject.restype=W.DWORD
K.GetExitCodeProcess.argtypes=[W.HANDLE,C.POINTER(W.DWORD)]; K.GetExitCodeProcess.restype=W.BOOL
K.TerminateProcess.argtypes=[W.HANDLE,W.UINT]; K.TerminateProcess.restype=W.BOOL
K.CloseHandle.argtypes=[W.HANDLE]; K.CloseHandle.restype=W.BOOL
U.CreateDesktopW.argtypes=[W.LPCWSTR,W.LPCWSTR,C.c_void_p,W.DWORD,W.DWORD,C.c_void_p]; U.CreateDesktopW.restype=W.HANDLE
U.CloseDesktop.argtypes=[W.HANDLE]; U.CloseDesktop.restype=W.BOOL
U.IsWindow.argtypes=[W.HWND]; U.IsWindow.restype=W.BOOL
U.GetWindowThreadProcessId.argtypes=[W.HWND,C.POINTER(W.DWORD)]; U.GetWindowThreadProcessId.restype=W.DWORD
CALLBACK=C.WINFUNCTYPE(W.BOOL,W.HWND,W.LPARAM)
U.EnumDesktopWindows.argtypes=[W.HANDLE,CALLBACK,W.LPARAM]; U.EnumDesktopWindows.restype=W.BOOL
DESKTOP_CALLBACK=C.WINFUNCTYPE(W.BOOL,W.LPWSTR,W.LPARAM)
U.GetProcessWindowStation.restype=W.HANDLE
U.EnumDesktopsW.argtypes=[W.HANDLE,DESKTOP_CALLBACK,W.LPARAM]; U.EnumDesktopsW.restype=W.BOOL
U.GetForegroundWindow.restype=W.HWND
K.GetCurrentThreadId.restype=W.DWORD
U.GetThreadDesktop.argtypes=[W.DWORD]; U.GetThreadDesktop.restype=W.HANDLE
U.GetUserObjectInformationW.argtypes=[W.HANDLE,C.c_int,C.c_void_p,W.DWORD,C.POINTER(W.DWORD)]; U.GetUserObjectInformationW.restype=W.BOOL

def launch_owned_browser(dest,exe,config):
    name=C.create_unicode_buffer(256); needed=W.DWORD()
    assert U.GetUserObjectInformationW(U.GetThreadDesktop(K.GetCurrentThreadId()),2,name,C.sizeof(name),C.byref(needed))
    output=dest/'runs'/f'{config}-browser'; assert not output.exists(); output.parent.mkdir(exist_ok=True)
    log=dest/'validation'/f'{config}-browser.log'; assert not log.exists()
    cmd=[str(exe),str(output),name.value,'browser']
    record=dict(Configuration=config,Mode='browser',Desktop=name.value,StartedUtc=now(),Command=cmd,
        InputSent=False,DesktopSwitched=False,HooksAcquired=False,OffscreenOwnedWindowsOnly=True,ForegroundBefore=U.GetForegroundWindow())
    with log.open('xb') as f:
        child=subprocess.Popen(cmd,cwd=exe.parent,stdout=f,stderr=subprocess.STDOUT,creationflags=0x08000000)
        record['ProcessId']=child.pid
        try: code=child.wait(timeout=240)
        except subprocess.TimeoutExpired:
            subprocess.run(['taskkill','/PID',str(child.pid),'/T','/F'],capture_output=True); code=child.wait(timeout=15); record['TimedOut']=True
    record.update(EndedUtc=now(),ExitCode=code,ExitHex=f'0x{code & 4294967295:08X}',LogSHA256=sha(log),ForegroundAfter=U.GetForegroundWindow())
    write(dest/'validation'/f'{config}-browser-receipt.json',record)
    assert code==0 and not record.get('TimedOut'),record
    summary=read(output/'summary.json'); browser=read(output/'browser-summary.json')
    assert summary['Verdict']=='PASS' and summary['SurvivingHwnds']==[] and all(not U.IsWindow(h) for h in summary['Hwnds'])
    assert browser['Exited'] and browser['KeeperDestroyed'] and record['ForegroundBefore']==record['ForegroundAfter']
    print(json.dumps({k:record[k] for k in ['Configuration','Mode','ProcessId','ExitHex','EndedUtc']}),flush=True)
    return record
def desktop_names():
    names=[]
    @DESKTOP_CALLBACK
    def visit(name,_): names.append(name); return True
    assert U.EnumDesktopsW(U.GetProcessWindowStation(),visit,0),C.get_last_error()
    return names
def now(): return datetime.now(timezone.utc).isoformat()
def write(p,v):
    with p.open('x',encoding='utf-8') as f: json.dump(v,f,indent=2)
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def windows(desktop):
    rows=[]
    @CALLBACK
    def visit(hwnd,_):
        pid=W.DWORD(); tid=U.GetWindowThreadProcessId(hwnd,C.byref(pid)); rows.append(dict(Hwnd=hwnd,Pid=pid.value,Thread=tid)); return True
    C.set_last_error(0); result=bool(U.EnumDesktopWindows(desktop,visit,0)); error=C.get_last_error()
    # A zero return is retained as an unsuccessful enumeration, never promoted
    # to an empty-desktop proof. Cleanup uses each acquired HWND plus process exit.
    return dict(Succeeded=result,LastError=error,Rows=rows)
def launch(dest,exe,config,mode,timeout_seconds=240):
    desktop_name='FWM_OWNED_'+uuid.uuid4().hex
    assert desktop_name not in desktop_names(), 'Desktop name already exists'
    output=dest/'runs'/f'{config}-{mode}'; assert not output.exists(); output.parent.mkdir(exist_ok=True)
    log=dest/'validation'/f'{config}-{mode}.log'; assert not log.exists()
    desktop=U.CreateDesktopW(desktop_name,None,None,0,0x1ff,None); assert desktop,C.get_last_error()
    info=PROCESSINFO(); record=dict(Configuration=config,Mode=mode,Desktop=desktop_name,StartedUtc=now(),InputSent=False,DesktopSwitched=False)
    try:
        record['DesktopBeforeLaunch']=windows(desktop)
        assert record['DesktopBeforeLaunch']['Rows']==[], 'New desktop is not empty'
        with log.open('x+b') as stream:
            handle=msvcrt.get_osfhandle(stream.fileno()); os.set_handle_inheritable(handle,True)
            start=STARTUPINFO(); start.cb=C.sizeof(start); start.lpDesktop='WinSta0\\'+desktop_name
            start.dwFlags=0x100|1; start.wShowWindow=0; start.hStdInput=handle; start.hStdOutput=handle; start.hStdError=handle
            cmd=[str(exe),str(output),desktop_name,mode]; record['Command']=cmd
            command=C.create_unicode_buffer(subprocess.list2cmdline(cmd))
            assert K.CreateProcessW(str(exe),command,None,None,True,0x08000000,None,str(exe.parent),C.byref(start),C.byref(info)),C.get_last_error()
            os.set_handle_inheritable(handle,False); K.CloseHandle(info.hThread); info.hThread=None
            record['ProcessId']=info.dwProcessId; record['NativeMainThread']=info.dwThreadId
            began=time.monotonic(); ready=None
            while K.WaitForSingleObject(info.hProcess,200)==258:
                if ready is None and (output/'crash-ready.json').exists():
                    # Exclusive child write flushes before returning; retry a partial JSON read.
                    try: ready=read(output/'crash-ready.json')
                    except json.JSONDecodeError: continue
                    assert ready['Pid']==info.dwProcessId and ready['Desktop']==desktop_name
                    seen=[]
                    for hwnd in ready['Handles']:
                        owner=W.DWORD(); tid=U.GetWindowThreadProcessId(hwnd,C.byref(owner))
                        assert U.IsWindow(hwnd) and owner.value==info.dwProcessId
                        seen.append(dict(Hwnd=hwnd,Pid=owner.value,Thread=tid))
                    record['AliveBeforeCrash']=seen; record['DesktopBeforeCrash']=windows(desktop)
                    assert record['DesktopBeforeCrash']['Succeeded']
                    assert all(w['Pid']==info.dwProcessId for w in record['DesktopBeforeCrash']['Rows'])
                    write(output/'crash.go',dict(OwnedProcessId=info.dwProcessId,RecordedUtc=now()))
                if time.monotonic()-began>timeout_seconds:
                    record['TimedOut']=True; K.TerminateProcess(info.hProcess,0xE000F017); K.WaitForSingleObject(info.hProcess,10000); break
            code=W.DWORD(); assert K.GetExitCodeProcess(info.hProcess,C.byref(code))
            record['ExitCode']=code.value; record['ExitHex']=f'0x{code.value:08X}'; record['EndedUtc']=now()
            record['DesktopAfterExit']=windows(desktop); record['ManagedProcessExitMarker']=(output/'managed-process-exit.json').exists()
            if ready:
                record['SurvivingHwnds']=[h for h in ready['Handles'] if U.IsWindow(h)]
                record['CrashReadySHA256']=sha(output/'crash-ready.json')
            record['LogSHA256']=sha(log)
    finally:
        if info.hProcess:
            if K.WaitForSingleObject(info.hProcess,0)==258:
                K.TerminateProcess(info.hProcess,0xE000F018); K.WaitForSingleObject(info.hProcess,10000)
            K.CloseHandle(info.hProcess)
        if info.hThread: K.CloseHandle(info.hThread)
        record['OwnedDesktopHandleClosed']=bool(U.CloseDesktop(desktop))
        write(dest/'validation'/f'{config}-{mode}-receipt.json',record)
    assert not record.get('TimedOut') and record['OwnedDesktopHandleClosed']
    assert record['DesktopAfterExit']['Rows']==[],record
    if mode in ['graceful','providers','browser']:
        summary=read(output/'summary.json')
        assert record['ExitCode']==0 and summary['Verdict']=='PASS' and summary['SurvivingHwnds']==[]
        assert all(not U.IsWindow(h) for h in summary['Hwnds'])
    else:
        assert ready and record['ExitCode']!=0 and record['SurvivingHwnds']==[] and not record['ManagedProcessExitMarker'],record
        if mode=='terminate': assert record['ExitCode']==0xE000F016
    print(json.dumps({k:record[k] for k in ['Configuration','Mode','ProcessId','ExitHex','EndedUtc']}),flush=True)
    return record
def main():
    p=argparse.ArgumentParser(); p.add_argument('--id',required=True); p.add_argument('--configs',nargs='+',default=['Debug','Release']); p.add_argument('--modes',nargs='+',default=['graceful','failfast','terminate']); p.add_argument('--current-desktop-browser',action='store_true'); a=p.parse_args()
    assert not a.current_desktop_browser or a.modes==['browser'], 'Current-desktop mode never admits hooks/workspace/crash modes'
    dest=ROOT/'artifacts/performance'/a.id; assert not dest.exists(); assert datetime.now().strftime('%Y%m%d') in a.id
    result=subprocess.run(['pwsh','-NoProfile','-File','scripts/performance/Save-ImplementationSnapshot.ps1','-SnapshotId',a.id],cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace')
    assert result.returncode==0,result.stderr; write(dest/'snapshot-command.json',dict(ExitCode=0,Stdout=result.stdout,Stderr=result.stderr))
    (dest/'validation').mkdir(); builds=[]; processes=[]
    for config in a.configs:
        binaries=dest/'binaries'/config; assert not binaries.exists()
        cmd=['dotnet','build','scripts/performance/native-teardown/FancyWM.NativeTeardownHarness.csproj','-c',config,'-o',str(binaries),'-v','minimal']
        start=now()
        with (dest/'validation'/f'{config}-build.log').open('xb') as log: result=subprocess.run(cmd,cwd=ROOT,stdout=log,stderr=subprocess.STDOUT,timeout=240)
        record=dict(Command=cmd,StartedUtc=start,EndedUtc=now(),ExitCode=result.returncode)
        write(dest/'validation'/f'{config}-build-receipt.json',record); assert result.returncode==0,record
        record['Files']=[dict(Path=str(f.relative_to(dest)),Bytes=f.stat().st_size,SHA256=sha(f)) for f in sorted(binaries.rglob('*')) if f.is_file()]
        builds.append(record); write(dest/f'{config}-binary-manifest.json',record['Files'])
        for mode in a.modes:
            processes.append(launch_owned_browser(dest,binaries/'FancyWM.NativeTeardownHarness.exe',config) if a.current_desktop_browser else launch(dest,binaries/'FancyWM.NativeTeardownHarness.exe',config,mode))
    write(dest/'runs.json',dict(Builds=builds,Processes=processes,ProductionChanged=False,FullStartupGraph=False,StageAccepted=False,LedgerAppended=False))
if __name__=='__main__': main()
