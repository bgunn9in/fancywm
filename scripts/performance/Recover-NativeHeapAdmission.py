"""Close only the exact child recorded by this failed admission's session name."""
import ctypes as C, json, re, subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[2]
dest=root/'artifacts/performance/FWM-NATIVE-HEAP-20260913-E1'
record=json.loads((dest/'own-session-before.json').read_text(encoding='utf-8'))
session=record['Command'][2];pid=int(re.fullmatch(r'FWM_OWNED_HEAP_(\d+)_\d+',session)[1])
k=C.WinDLL('kernel32',use_last_error=True)
k.OpenProcess.argtypes=[C.c_ulong,C.c_int,C.c_ulong];k.OpenProcess.restype=C.c_void_p
k.QueryFullProcessImageNameW.argtypes=[C.c_void_p,C.c_ulong,C.c_wchar_p,C.POINTER(C.c_ulong)]
k.GetExitCodeProcess.argtypes=[C.c_void_p,C.POINTER(C.c_ulong)]
k.TerminateProcess.argtypes=[C.c_void_p,C.c_uint];k.WaitForSingleObject.argtypes=[C.c_void_p,C.c_ulong];k.CloseHandle.argtypes=[C.c_void_p]
handle=k.OpenProcess(0x1000|0x100000|1,False,pid);data=dict(Pid=pid,Session=session)
if not handle:
    data['OpenProcessError']=C.get_last_error();assert data['OpenProcessError']==87;data['Exited']=True
else:
    try:
        name=C.create_unicode_buffer(32768);size=C.c_ulong(len(name));assert k.QueryFullProcessImageNameW(handle,0,name,C.byref(size))
        expected=root/'artifacts/performance/FWM-NATIVE-HEAP-20260913-T2/HeapControl.exe'
        assert Path(name.value).resolve()==expected.resolve();data['Path']=name.value
        code=C.c_ulong();assert k.GetExitCodeProcess(handle,C.byref(code))
        if code.value==259:assert k.TerminateProcess(handle,0xE000F016);assert k.WaitForSingleObject(handle,20000)==0
        assert k.GetExitCodeProcess(handle,C.byref(code)) and code.value!=259;data.update(Exited=True,ExitCode=code.value)
    finally:k.CloseHandle(handle)
for label,cmd in [('own-session',['logman','query',session,'-ets']),('wpr',['wpr','-status'])]:
    r=subprocess.run(cmd,capture_output=True);data[label]=dict(Command=cmd,ExitCode=r.returncode,StdoutHex=r.stdout.hex())
    if label=='own-session':assert (r.returncode&4294967295)==0x80300002
    else:assert r.returncode==0 and b'WPR is not recording' in r.stdout
with (dest/'recovery.json').open('x',encoding='utf-8') as f:json.dump(data,f,indent=2)
print(json.dumps(data))
