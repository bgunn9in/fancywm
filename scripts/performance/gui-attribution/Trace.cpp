#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <detours.h>
#include <cstdio>
#include <cstdint>
#include <vector>
#include <set>

// Test-only, process-local interception. Never loads into a foreign process.
// API arguments, result and LastError are preserved. Native stacks are raw PCs;
// the module inventory supplies load bases rather than guessed symbol names.
static HANDLE output = INVALID_HANDLE_VALUE;
static SRWLOCK gate = SRWLOCK_INIT;
static unsigned long long sequence = 0;
static volatile LONG phase = -1, failedWrites = 0;
static thread_local int depth = 0;
static std::set<uintptr_t> observedWindows;
static HWINEVENTHOOK ownWindowObserver = nullptr;

static void Record(const char* api, const char* family, const char* action,
                   uintptr_t handle, uintptr_t argument, uintptr_t result,
                   unsigned long flags, int callDepth, LONGLONG counterBegin = 0, LONGLONG counterEnd = 0)
{
    DWORD error = GetLastError();
    LARGE_INTEGER stamp{}; QueryPerformanceCounter(&stamp);
    void* frames[32]{};
    USHORT count = CaptureStackBackTrace(2, 32, frames, nullptr);
    char line[2400];
    AcquireSRWLockExclusive(&gate);
    if (output == INVALID_HANDLE_VALUE) { ReleaseSRWLockExclusive(&gate); SetLastError(error); return; }
    if (handle && (!strcmp(action, "event-create") || (!strcmp(family, "Window") && !strcmp(action, "acquire")))) observedWindows.insert(handle);
    int used = sprintf_s(line, "{\"Kind\":\"Api\",\"Sequence\":%llu,\"Thread\":%lu,\"Phase\":%ld,\"Depth\":%d,\"Api\":\"%s\",\"Family\":\"%s\",\"Action\":\"%s\",\"Handle\":%llu,\"Argument\":%llu,\"Result\":%llu,\"Flags\":%lu,\"LastError\":%lu,\"Qpc\":%lld,\"CounterQpcBegin\":%lld,\"CounterQpcEnd\":%lld,\"Stack\":[",
        ++sequence, GetCurrentThreadId(), phase, callDepth, api, family, action,
        (unsigned long long)handle, (unsigned long long)argument,
        (unsigned long long)result, flags, error, stamp.QuadPart, counterBegin, counterEnd);
    for (USHORT i = 0; i < count; ++i)
        used += sprintf_s(line + used, sizeof(line) - used, "%s%llu", i ? "," : "", (unsigned long long)frames[i]);
    used += sprintf_s(line + used, sizeof(line) - used, "]}\n");
    DWORD written = 0;
    if (!WriteFile(output, line, used, &written, nullptr) || written != (DWORD)used)
        InterlockedIncrement(&failedWrites);
    ReleaseSRWLockExclusive(&gate);
    SetLastError(error);
}

// Log nested calls as well: API aliases are never silently counted as distinct
// allocations. The verifier keys returned handles and preserves the call depth.
#define HOOK(ret, name, params, args, family, action, handle, argument, flags) \
    static decltype(&name) Real_##name = name; \
    static ret WINAPI Trace_##name params { \
        int traceDepth = depth++; ret r = Real_##name args; \
        Record(#name, family, action, (uintptr_t)(handle), (uintptr_t)(argument), (uintptr_t)r, flags, traceDepth); \
        --depth; return r; }

HOOK(HICON, CreateIconIndirect, (PICONINFO p), (p), p && p->fIcon ? "Icon" : "Cursor", "acquire", r, 0, 0)
HOOK(HICON, CreateIcon, (HINSTANCE a,int b,int c,BYTE d,BYTE e,const BYTE* f,const BYTE* g), (a,b,c,d,e,f,g), "Icon","acquire",r,0,0)
HOOK(HICON, CreateIconFromResourceEx, (PBYTE a,DWORD b,BOOL c,DWORD d,int e,int f,UINT g), (a,b,c,d,e,f,g), c ? "Icon" : "Cursor","acquire",r,0,g)
HOOK(HICON, CopyIcon, (HICON a), (a), "Icon","acquire",r,a,0)
HOOK(HANDLE, CopyImage, (HANDLE a,UINT b,int c,int d,UINT e), (a,b,c,d,e), "Image","acquire",r,a,e)
HOOK(HANDLE, LoadImageW, (HINSTANCE a,LPCWSTR b,UINT c,int d,int e,UINT f), (a,b,c,d,e,f), "Image","load",r,c,f)
HOOK(HANDLE, LoadImageA, (HINSTANCE a,LPCSTR b,UINT c,int d,int e,UINT f), (a,b,c,d,e,f), "Image","load",r,c,f)
HOOK(HICON, LoadIconW, (HINSTANCE a,LPCWSTR b), (a,b), "Icon","shared-load",r,0,0)
HOOK(HICON, LoadIconA, (HINSTANCE a,LPCSTR b), (a,b), "Icon","shared-load",r,0,0)
HOOK(HCURSOR, LoadCursorW, (HINSTANCE a,LPCWSTR b), (a,b), "Cursor","shared-load",r,0,0)
HOOK(HCURSOR, LoadCursorA, (HINSTANCE a,LPCSTR b), (a,b), "Cursor","shared-load",r,0,0)
HOOK(HCURSOR, LoadCursorFromFileW, (LPCWSTR a), (a), "Cursor","acquire",r,0,0)
HOOK(HCURSOR, CreateCursor, (HINSTANCE a,int b,int c,int d,int e,const VOID* f,const VOID* g), (a,b,c,d,e,f,g), "Cursor","acquire",r,0,0)
HOOK(BOOL, DestroyIcon, (HICON a), (a), "Icon","release",a,0,0)
HOOK(BOOL, DestroyCursor, (HCURSOR a), (a), "Cursor","release",a,0,0)
HOOK(HMENU, CreateMenu, (void), (), "Menu","acquire",r,0,0)
HOOK(HMENU, CreatePopupMenu, (void), (), "Menu","acquire",r,0,0)
HOOK(BOOL, DestroyMenu, (HMENU a), (a), "Menu","release",a,0,0)
HOOK(HACCEL, CreateAcceleratorTableW, (LPACCEL a,int b), (a,b), "Accelerator","acquire",r,0,0)
HOOK(HACCEL, CreateAcceleratorTableA, (LPACCEL a,int b), (a,b), "Accelerator","acquire",r,0,0)
HOOK(BOOL, DestroyAcceleratorTable, (HACCEL a), (a), "Accelerator","release",a,0,0)
HOOK(HHOOK, SetWindowsHookExW, (int a,HOOKPROC b,HINSTANCE c,DWORD d), (a,b,c,d), "Hook","acquire",r,d,a)
HOOK(HHOOK, SetWindowsHookExA, (int a,HOOKPROC b,HINSTANCE c,DWORD d), (a,b,c,d), "Hook","acquire",r,d,a)
HOOK(BOOL, UnhookWindowsHookEx, (HHOOK a), (a), "Hook","release",a,0,0)
HOOK(HWND, CreateWindowExW, (DWORD a,LPCWSTR b,LPCWSTR c,DWORD d,int e,int f,int g,int h,HWND i,HMENU j,HINSTANCE k,LPVOID l), (a,b,c,d,e,f,g,h,i,j,k,l), "Window","acquire",r,i,d)
HOOK(HWND, CreateWindowExA, (DWORD a,LPCSTR b,LPCSTR c,DWORD d,int e,int f,int g,int h,HWND i,HMENU j,HINSTANCE k,LPVOID l), (a,b,c,d,e,f,g,h,i,j,k,l), "Window","acquire",r,i,d)
HOOK(BOOL, DestroyWindow, (HWND a), (a), "Window","release",a,0,0)
HOOK(HWINEVENTHOOK, SetWinEventHook, (DWORD a,DWORD b,HMODULE c,WINEVENTPROC d,DWORD e,DWORD f,DWORD g), (a,b,c,d,e,f,g), "EventHook","acquire",r,e,g)
HOOK(BOOL, UnhookWinEvent, (HWINEVENTHOOK a), (a), "EventHook","release",a,0,0)
HOOK(UINT_PTR, SetTimer, (HWND a,UINT_PTR b,UINT c,TIMERPROC d), (a,b,c,d), "Timer","set",r,a,c)
HOOK(BOOL, KillTimer, (HWND a,UINT_PTR b), (a,b), "Timer","release",b,a,0)
HOOK(UINT_PTR, SetCoalescableTimer, (HWND a,UINT_PTR b,UINT c,TIMERPROC d,ULONG e), (a,b,c,d,e), "Timer","set",r,a,c)
HOOK(HDWP, BeginDeferWindowPos, (int a), (a), "WindowPosition","acquire",r,a,0)
HOOK(HDWP, DeferWindowPos, (HDWP a,HWND b,HWND c,int d,int e,int f,int g,UINT h), (a,b,c,d,e,f,g,h), "WindowPosition","replace",r,a,h)
HOOK(BOOL, EndDeferWindowPos, (HDWP a), (a), "WindowPosition","release",a,0,0)
HOOK(HMENU, LoadMenuW, (HINSTANCE a,LPCWSTR b), (a,b), "Menu","acquire",r,0,0)
HOOK(HMENU, LoadMenuA, (HINSTANCE a,LPCSTR b), (a,b), "Menu","acquire",r,0,0)
HOOK(HMENU, LoadMenuIndirectW, (const MENUTEMPLATE* a), (a), "Menu","acquire",r,0,0)
HOOK(HMENU, LoadMenuIndirectA, (const MENUTEMPLATE* a), (a), "Menu","acquire",r,0,0)
HOOK(ATOM, RegisterClassExW, (const WNDCLASSEXW* a), (a), "Class","register",r,0,0)
HOOK(ATOM, RegisterClassExA, (const WNDCLASSEXA* a), (a), "Class","register",r,0,0)
HOOK(BOOL, UnregisterClassW, (LPCWSTR a,HINSTANCE b), (a,b), "Class","unregister",IS_INTRESOURCE(a) ? (uintptr_t)a : 0,b,0)
HOOK(BOOL, UnregisterClassA, (LPCSTR a,HINSTANCE b), (a,b), "Class","unregister",IS_INTRESOURCE(a) ? (uintptr_t)a : 0,b,0)
HOOK(HBITMAP, CreateBitmap, (int a,int b,UINT c,UINT d,const VOID* e), (a,b,c,d,e), "Bitmap","acquire",r,0,0)
HOOK(HBRUSH, CreateSolidBrush, (COLORREF a), (a), "Brush","acquire",r,0,0)
HOOK(BOOL, DeleteObject, (HGDIOBJ a), (a), "Gdi","release",a,0,0)

struct Hook { PVOID* pointer; PVOID replacement; const char* name; };
#define ENTRY(n) { reinterpret_cast<PVOID*>(&Real_##n), reinterpret_cast<PVOID>(Trace_##n), #n }
static Hook hooks[] = {
    ENTRY(CreateIconIndirect), ENTRY(CreateIcon), ENTRY(CreateIconFromResourceEx),
    ENTRY(CopyIcon), ENTRY(CopyImage), ENTRY(LoadImageW), ENTRY(LoadImageA),
    ENTRY(LoadIconW), ENTRY(LoadIconA), ENTRY(LoadCursorW), ENTRY(LoadCursorA),
    ENTRY(LoadCursorFromFileW), ENTRY(CreateCursor), ENTRY(DestroyIcon), ENTRY(DestroyCursor),
    ENTRY(CreateMenu), ENTRY(CreatePopupMenu), ENTRY(DestroyMenu),
    ENTRY(CreateAcceleratorTableW), ENTRY(CreateAcceleratorTableA), ENTRY(DestroyAcceleratorTable),
    ENTRY(SetWindowsHookExW), ENTRY(SetWindowsHookExA), ENTRY(UnhookWindowsHookEx),
    ENTRY(CreateWindowExW), ENTRY(CreateWindowExA), ENTRY(DestroyWindow),
    ENTRY(SetWinEventHook), ENTRY(UnhookWinEvent), ENTRY(SetTimer), ENTRY(KillTimer), ENTRY(SetCoalescableTimer),
    ENTRY(BeginDeferWindowPos), ENTRY(DeferWindowPos), ENTRY(EndDeferWindowPos),
    ENTRY(LoadMenuW), ENTRY(LoadMenuA), ENTRY(LoadMenuIndirectW), ENTRY(LoadMenuIndirectA),
    ENTRY(RegisterClassExW), ENTRY(RegisterClassExA), ENTRY(UnregisterClassW), ENTRY(UnregisterClassA),
    ENTRY(CreateBitmap), ENTRY(CreateSolidBrush), ENTRY(DeleteObject)
};

static void CALLBACK OwnWindowEvent(HWINEVENTHOOK, DWORD event, HWND hwnd, LONG object, LONG child, DWORD thread, DWORD)
{
    if (object != OBJID_WINDOW || child != CHILDID_SELF || !hwnd) return;
    Record("OwnWindowEvent", "Window", event == EVENT_OBJECT_CREATE ? "event-create" : "event-destroy", (uintptr_t)hwnd, thread, 1, 0, 0);
}

extern "C" __declspec(dllexport) int GuiTraceStart(const wchar_t* path)
{
    if (output != INVALID_HANDLE_VALUE) return ERROR_ALREADY_EXISTS;
    output = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (output == INVALID_HANDLE_VALUE) return GetLastError();
    // Enlist only this process's extant threads. Collect handles before the
    // transaction; no thread or process outside GetCurrentProcessId is opened.
    std::vector<HANDLE> threads;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return GetLastError();
    THREADENTRY32 t{ sizeof(t) };
    for (BOOL more = Thread32First(snap, &t); more; more = Thread32Next(snap, &t))
        if (t.th32OwnerProcessID == GetCurrentProcessId() && t.th32ThreadID != GetCurrentThreadId()) {
            HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, t.th32ThreadID);
            if (h) threads.push_back(h);
            else if (GetLastError() != ERROR_INVALID_PARAMETER) return GetLastError();
        }
    CloseHandle(snap);
    LONG error = DetourTransactionBegin();
    if (!error) error = DetourUpdateThread(GetCurrentThread());
    for (HANDLE h : threads) if (!error) error = DetourUpdateThread(h);
    for (Hook& h : hooks) if (!error) error = DetourAttach(h.pointer, h.replacement);
    if (error) DetourTransactionAbort(); else error = DetourTransactionCommit();
    for (HANDLE h : threads) CloseHandle(h);
    if (error) return error;
    for (Hook& h : hooks) Record(h.name, "Instrumentation", "installed", (uintptr_t)*h.pointer, 0, 1, 0, 0);
    ownWindowObserver = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY, nullptr, OwnWindowEvent, GetCurrentProcessId(), 0, WINEVENT_OUTOFCONTEXT);
    if (!ownWindowObserver) return GetLastError() ? GetLastError() : ERROR_INVALID_HANDLE;
    return 0;
}

extern "C" __declspec(dllexport) int GuiTraceMark(int value)
{
    InterlockedExchange(&phase, value);
    if (value >= 0) {
        AcquireSRWLockExclusive(&gate);
        std::vector<uintptr_t> windows(observedWindows.begin(), observedWindows.end());
        ReleaseSRWLockExclusive(&gate);
        for (uintptr_t h : windows) {
            DWORD pid = 0; DWORD tid = GetWindowThreadProcessId((HWND)h, &pid);
            // Only a current HWND owned by this exact process is inspected.
            if (pid == GetCurrentProcessId() && tid && IsWindow((HWND)h))
                Record("WindowWitness", "Window", "witness", h, tid, pid, (DWORD)GetClassLongPtrW((HWND)h, GCW_ATOM), 0);
        }
    }
    LARGE_INTEGER begin{}, end{}; QueryPerformanceCounter(&begin);
    DWORD gdi = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS), user = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
    QueryPerformanceCounter(&end);
    Record("GuiTraceMark", "Marker", "mark", value, gdi, user, 0, 0, begin.QuadPart, end.QuadPart);
    FlushFileBuffers(output);
    return failedWrites;
}

extern "C" __declspec(dllexport) int GuiTraceCheck() { return failedWrites; }
extern "C" __declspec(dllexport) int GuiTraceClose()
{
    if (ownWindowObserver) {
        if (!UnhookWinEvent(ownWindowObserver)) InterlockedIncrement(&failedWrites);
        ownWindowObserver = nullptr;
    }
    AcquireSRWLockExclusive(&gate);
    if (output != INVALID_HANDLE_VALUE) {
        if (!FlushFileBuffers(output) || !CloseHandle(output)) InterlockedIncrement(&failedWrites);
        output = INVALID_HANDLE_VALUE;
    }
    ReleaseSRWLockExclusive(&gate);
    return failedWrites;
}

// This standalone control exercises real native objects on the caller's owned
// non-input desktop. Holds 24 simultaneous objects; it never deletes borrowed
// resources. Bitmap preparation is excluded from the USER calibration boundary.
extern "C" __declspec(dllexport) int GuiTraceControl()
{
    HBITMAP bitmaps[24]{}; HMENU menus[24]{}; HICON icons[24]{};
    HBITMAP color = CreateBitmap(16, 16, 1, 32, nullptr), mask = CreateBitmap(16, 16, 1, 1, nullptr);
    struct Cleanup {
        HBITMAP* bitmaps; HMENU* menus; HICON* icons; HBITMAP& color; HBITMAP& mask;
        ~Cleanup() { for (int i = 0; i < 24; i++) { if (bitmaps[i]) DeleteObject(bitmaps[i]); if (menus[i]) DestroyMenu(menus[i]); if (icons[i]) DestroyIcon(icons[i]); } if (color) DeleteObject(color); if (mask) DeleteObject(mask); }
    } cleanup{ bitmaps, menus, icons, color, mask };
    if (!color || !mask) return 1;
    ICONINFO info{ TRUE, 0, 0, mask, color };
    HICON warm = CreateIconIndirect(&info); if (!warm || !DestroyIcon(warm)) return 2;
    HMENU warmMenu = CreateMenu(); if (!warmMenu || !DestroyMenu(warmMenu)) return 15;
    GdiFlush();
    GuiTraceMark(-100);
    DWORD g = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS), u = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
    for (auto& b : bitmaps) { b = CreateBitmap(16, 16, 1, 1, nullptr); if (!b) return 3; }
    GuiTraceMark(-101);
    if (GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS) != g + 24 || GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS) != u) return 4;
    for (auto& b : bitmaps) { if (!DeleteObject(b)) return 5; b = nullptr; }
    GdiFlush();
    for (auto& m : menus) { m = CreateMenu(); if (!m) return 6; }
    GuiTraceMark(-102);
    if (GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS) != g || GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS) != u + 24) return 7;
    for (auto& m : menus) { if (!DestroyMenu(m)) return 8; m = nullptr; }
    for (auto& i : icons) { i = CreateIconIndirect(&info); if (!i) return 9; }
    GuiTraceMark(-103);
    if (GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS) != u + 24) return 10;
    for (auto& i : icons) { if (!DestroyIcon(i)) return 11; i = nullptr; }
    GdiFlush();
    GuiTraceMark(-104);
    if (GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS) != u || GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS) != g) return 12;
    if (!DeleteObject(color) || !DeleteObject(mask)) return 13;
    color = mask = nullptr;
    return failedWrites ? 14 : 0;
}
