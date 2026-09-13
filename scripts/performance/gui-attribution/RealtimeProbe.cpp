#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <string>
int wmain(int argc,wchar_t** argv)
{
    if(argc!=4)return 1;wchar_t desktop[256]{};DWORD n=0;
    if(!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&n)||std::wstring(desktop)!=argv[2]||std::wstring(desktop).find(L"FWM_OWNED_")!=0)return 2;
    std::filesystem::path root(argv[1]);if(exists(root)||!create_directory(root))return 3;
    wchar_t etw[32768]{},api[32768]{};
    if(!GetEnvironmentVariableW(L"FWM_GUI_ETW_DLL",etw,32768)||!GetEnvironmentVariableW(L"FWM_GUI_TRACE_DLL",api,32768))return 4;
    auto e=LoadLibraryW(etw),a=LoadLibraryW(api);if(!e||!a)return 5;
    auto start=(int(*)(const wchar_t*))GetProcAddress(e,"OwnedEtwStart");auto stop=(int(*)())GetProcAddress(e,"OwnedEtwStop");
    auto apiStart=(int(*)(const wchar_t*))GetProcAddress(a,"GuiTraceStart");auto control=(int(*)())GetProcAddress(a,"GuiTraceControl");auto close=(int(*)())GetProcAddress(a,"GuiTraceClose");
    if(!start||!stop||!apiStart||!control||!close)return 6;
    int started=start((root/L"realtime").c_str()),calibration=-1;
    if(!started){calibration=apiStart((root/L"gui-api.jsonl").c_str());if(!calibration)calibration=control();int c=close();if(!calibration)calibration=c;}
    int stopped=started?1:stop();bool ok=!started&&!calibration&&!stopped;
    std::ofstream result(root/"summary.json");result<<"{\"Verdict\":\""<<(ok?"PASS":"FAIL")<<"\",\"Pid\":"<<GetCurrentProcessId()<<",\"StartCode\":"<<started<<",\"CalibrationCode\":"<<calibration<<",\"StopCode\":"<<stopped<<",\"Hwnds\":[],\"SurvivingHwnds\":[]}";
    return ok?0:7;
}
