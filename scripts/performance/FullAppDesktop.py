"""Observe the current native desktop and compare it with the validated controls.

WMI video-controller resolution does not describe an RDP or disconnected desktop.
The DPI probe uses temporary hidden owned HWNDs; no input or display settings change.
"""
import ctypes as C
import datetime


def snapshot():
    user = C.WinDLL('user32', use_last_error=True)
    kernel = C.WinDLL('kernel32', use_last_error=True)
    class Rect(C.Structure):
        _fields_ = [(name, C.c_long) for name in ['Left', 'Top', 'Right', 'Bottom']]
    class MonitorInfo(C.Structure):
        _fields_ = [('Size', C.c_uint), ('Monitor', Rect), ('WorkArea', Rect),
                    ('Flags', C.c_uint), ('Device', C.c_wchar * 32)]
    def rectangle(rect):
        return {name: getattr(rect, name) for name, _ in Rect._fields_}
    user.SetThreadDpiAwarenessContext.argtypes = [C.c_void_p]
    user.SetThreadDpiAwarenessContext.restype = C.c_void_p
    user.GetThreadDesktop.argtypes = [C.c_uint]
    user.GetThreadDesktop.restype = C.c_void_p
    user.GetUserObjectInformationW.argtypes = [C.c_void_p, C.c_int, C.c_void_p, C.c_uint, C.POINTER(C.c_uint)]
    user.OpenInputDesktop.argtypes = [C.c_uint, C.c_bool, C.c_uint]
    user.OpenInputDesktop.restype = C.c_void_p
    user.CloseDesktop.argtypes = [C.c_void_p]
    user.GetMonitorInfoW.argtypes = [C.c_void_p, C.POINTER(MonitorInfo)]
    user.CreateWindowExW.argtypes = [C.c_uint, C.c_wchar_p, C.c_wchar_p, C.c_uint,
        C.c_int, C.c_int, C.c_int, C.c_int, C.c_void_p, C.c_void_p, C.c_void_p, C.c_void_p]
    user.CreateWindowExW.restype = C.c_void_p
    user.GetDpiForWindow.argtypes = [C.c_void_p]
    user.GetDpiForWindow.restype = C.c_uint
    user.DestroyWindow.argtypes = [C.c_void_p]
    user.IsWindow.argtypes = [C.c_void_p]
    def desktop_name(handle):
        name = C.create_unicode_buffer(256)
        required = C.c_uint()
        if not user.GetUserObjectInformationW(handle, 2, name, C.sizeof(name), C.byref(required)):
            raise C.WinError(C.get_last_error())
        return name.value
    previous = user.SetThreadDpiAwarenessContext(C.c_void_p(-4))
    if not previous:
        raise C.WinError(C.get_last_error())
    monitors, failures = [], []
    callback_type = C.WINFUNCTYPE(C.c_int, C.c_void_p, C.c_void_p, C.POINTER(Rect), C.c_ssize_t)
    @callback_type
    def visit(handle, dc, rect, data):
        try:
            info = MonitorInfo()
            info.Size = C.sizeof(info)
            if not user.GetMonitorInfoW(handle, C.byref(info)):
                raise C.WinError(C.get_last_error())
            hwnd = user.CreateWindowExW(0, 'STATIC', 'FancyWM owned DPI probe', 0x80000000,
                info.Monitor.Left, info.Monitor.Top, 1, 1, None, None, None, None)
            if not hwnd:
                raise C.WinError(C.get_last_error())
            try:
                dpi = user.GetDpiForWindow(hwnd)
            finally:
                destroyed = bool(user.DestroyWindow(hwnd)) and not user.IsWindow(hwnd)
            monitors.append(dict(Hmonitor=handle, Device=info.Device, Primary=bool(info.Flags & 1),
                Rectangle=rectangle(info.Monitor), WorkArea=rectangle(info.WorkArea), Dpi=dpi,
                DpiProbeDestroyed=destroyed))
            return 1
        except Exception as error:
            # A ctypes callback must return its failure to the caller explicitly.
            failures.append(repr(error))
            return 0
    try:
        user.EnumDisplayMonitors.argtypes = [C.c_void_p, C.c_void_p, callback_type, C.c_ssize_t]
        enumeration = bool(user.EnumDisplayMonitors(None, None, visit, 0))
        thread_desktop = desktop_name(user.GetThreadDesktop(kernel.GetCurrentThreadId()))
        input_handle = user.OpenInputDesktop(0, False, 1)
        input_error = C.get_last_error() if not input_handle else 0
        input_name = None
        if input_handle:
            try:
                input_name = desktop_name(input_handle)
            finally:
                if not user.CloseDesktop(input_handle):
                    failures.append('Could not close the owned input-desktop handle')
        return dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
            EnumerationSucceeded=enumeration, Monitors=monitors, ProbeFailures=failures,
            ThreadDesktop=thread_desktop, InputDesktop=input_name, InputDesktopError=input_error,
            RemoteSession=bool(user.GetSystemMetrics(0x1000)),
            VirtualScreen=[user.GetSystemMetrics(i) for i in [76, 77, 78, 79]])
    finally:
        if not user.SetThreadDpiAwarenessContext(previous):
            raise C.WinError(C.get_last_error())


def control_failures(current, expected_dpis, expected_workareas):
    failures = []
    if not current['EnumerationSucceeded'] or not current['Monitors'] or current['ProbeFailures']:
        failures.append('Native monitor enumeration or owned DPI probe failed')
    if current['InputDesktop'] is None or current['InputDesktop'] != current['ThreadDesktop']:
        failures.append('The active input desktop is inaccessible or differs from the executing desktop')
    if current['RemoteSession']:
        failures.append('Remote-session presentation differs from the validated local hardware-display workload')
    if any(not m['DpiProbeDestroyed'] for m in current['Monitors']):
        failures.append('An owned DPI probe window was not destroyed')
    if {m['Dpi'] for m in current['Monitors']} != set(expected_dpis):
        failures.append('Native monitor DPI differs from the behavior-validated DPI')
    actual = sorted(tuple(m['WorkArea'][k] for k in ['Left', 'Top', 'Right', 'Bottom']) for m in current['Monitors'])
    expected = sorted(tuple(r[k] for k in ['Left', 'Top', 'Right', 'Bottom']) for r in expected_workareas)
    if actual != expected:
        failures.append('Native monitor work areas differ from the accepted topology controls')
    return failures
