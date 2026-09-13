#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <imm.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#include <thread>
#include <mutex>
#include <atomic>

static std::ofstream rows;
static std::mutex gate;
static std::atomic<int> errors{0};
static LONGLONG qpc(){LARGE_INTEGER q{};QueryPerformanceCounter(&q);return q.QuadPart;}
static void record(const char* kind,UINT_PTR handle=0,int result=1)
{
    std::lock_guard<std::mutex> lock(gate);
    rows<<"{\"Kind\":\""<<kind<<"\",\"Tid\":"<<GetCurrentThreadId()<<",\"Qpc\":"<<qpc()<<",\"Handle\":"<<handle<<",\"Result\":"<<result<<"}\n";
}
static void mark(const char* kind)
{
    auto before=qpc();auto user=GetGuiResources(GetCurrentProcess(),GR_USEROBJECTS);auto gdi=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);auto after=qpc();
    std::lock_guard<std::mutex> lock(gate);
    rows<<"{\"Kind\":\""<<kind<<"\",\"Tid\":"<<GetCurrentThreadId()<<",\"QpcBegin\":"<<before<<",\"QpcEnd\":"<<after<<",\"USER\":"<<user<<",\"GDI\":"<<gdi<<"}\n";
}
int wmain(int argc,wchar_t** argv)
{
    if(argc!=4)return 1;wchar_t desktop[256]{};DWORD bytes=0;
    if(!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&bytes)||std::wstring(desktop)!=argv[2]||std::wstring(desktop).find(L"FWM_OWNED_")!=0)return 2;
    std::filesystem::path root(argv[1]);if(exists(root)||!create_directory(root))return 3;
    wchar_t dll[32768]{};if(!GetEnvironmentVariableW(L"FWM_GUI_ETW_DLL",dll,32768))return 4;
    auto module=LoadLibraryW(dll);if(!module)return 5;
    auto start=(int(*)(const wchar_t*))GetProcAddress(module,"OwnedEtwStart");auto stop=(int(*)())GetProcAddress(module,"OwnedEtwStop");if(!start||!stop)return 6;
    rows.open(root/"input-contexts.jsonl");int started=start((root/L"realtime").c_str());if(started)return 7;
    HWND warm=CreateWindowExW(0,L"STATIC",L"FWM owned input context calibration",0,0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
    if(!warm)++errors;else{auto context=ImmGetContext(warm);record("WarmDefaultContext",(UINT_PTR)context);if(context)ImmReleaseContext(warm,context);if(!DestroyWindow(warm))++errors;}
    mark("Baseline");std::vector<HIMC> contexts;
    for(int i=0;i<24;++i){auto context=ImmCreateContext();record("ExplicitCreate",(UINT_PTR)context,context!=nullptr);if(!context)++errors;else contexts.push_back(context);}
    mark("ExplicitHeld");for(auto context:contexts){int ok=ImmDestroyContext(context);record("ExplicitDestroy",(UINT_PTR)context,ok);if(!ok)++errors;}
    mark("ExplicitReleased");
    HANDLE closeWindows=CreateEventW(nullptr,TRUE,FALSE,nullptr),exitThreads=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    std::atomic<int> ready{0},closed{0};std::vector<std::thread> threads;
    for(int i=0;i<24;++i)threads.emplace_back([&]{
        record("ThreadBegin");HWND window=CreateWindowExW(0,L"STATIC",L"FWM own worker",0,0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        auto context=window?ImmGetContext(window):nullptr;record("WorkerDefaultContext",(UINT_PTR)context,context!=nullptr);if(!context)++errors;else if(!ImmReleaseContext(window,context))++errors;
        record("WorkerWindow",(UINT_PTR)window,window!=nullptr);++ready;
        if(WaitForSingleObject(closeWindows,30000)!=WAIT_OBJECT_0)++errors;
        int ok=window?DestroyWindow(window):0;record("WorkerWindowDestroyed",(UINT_PTR)window,ok);if(!ok)++errors;++closed;
        if(WaitForSingleObject(exitThreads,30000)!=WAIT_OBJECT_0)++errors;record("ThreadLeaving");
    });
    while(ready<24)Sleep(1);mark("WorkerWindowsHeld");SetEvent(closeWindows);
    while(closed<24)Sleep(1);mark("WorkerWindowsClosedThreadsAlive");SetEvent(exitThreads);
    for(auto& thread:threads)thread.join();mark("WorkerThreadsJoined");CloseHandle(closeWindows);CloseHandle(exitThreads);
    int stopped=stop();rows.close();bool ok=!errors&&!stopped;
    std::ofstream result(root/"summary.json");result<<"{\"Verdict\":\""<<(ok?"PASS":"FAIL")<<"\",\"Pid\":"<<GetCurrentProcessId()<<",\"StartCode\":"<<started<<",\"Errors\":"<<errors<<",\"StopCode\":"<<stopped<<",\"ThreadsJoined\":24,\"Hwnds\":[],\"SurvivingHwnds\":[]}";return ok?0:8;
}
