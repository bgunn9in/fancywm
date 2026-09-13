#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <evntprov.h>
#include <tdh.h>
#include <filesystem>
#include <fstream>
#include <string>
static GUID provider{ 0x8c416c79,0xd49b,0x4f01,{0xa4,0x67,0xe5,0x6d,0x3a,0xa8,0x23,0x4c} };
struct Properties { EVENT_TRACE_PROPERTIES p{}; wchar_t name[256]{}; wchar_t path[1024]{}; };
int wmain(int argc,wchar_t** argv)
{
    if (argc!=4) return 1;
    wchar_t desktop[256]{};DWORD needed=0;
    if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&needed)
        || std::wstring(desktop)!=argv[2] || std::wstring(desktop).find(L"FWM_OWNED_")!=0) return 2;
    std::filesystem::path root(argv[1]);if(exists(root)||!create_directory(root)) return 3;
    Properties prop;prop.p.Wnode.BufferSize=sizeof(prop);prop.p.Wnode.Flags=WNODE_FLAG_TRACED_GUID;prop.p.Wnode.ClientContext=1;
    prop.p.BufferSize=64;prop.p.MinimumBuffers=8;prop.p.MaximumBuffers=64;prop.p.FlushTimer=1;
    prop.p.LogFileMode=EVENT_TRACE_FILE_MODE_SEQUENTIAL;
    prop.p.LoggerNameOffset=offsetof(Properties,name);prop.p.LogFileNameOffset=offsetof(Properties,path);
    swprintf_s(prop.name,L"FWM_OWNED_GUI_%lu_%llu",GetCurrentProcessId(),GetTickCount64());
    wcscpy_s(prop.path,(root/L"user-handles.etl").c_str());
    if (std::filesystem::exists(prop.path)) return 4;
    TRACEHANDLE session=0;ULONG start=StartTraceW(&session,prop.name,&prop.p);
    ULONG enable=ERROR_INVALID_FUNCTION,disable=ERROR_INVALID_FUNCTION,stop=ERROR_INVALID_FUNCTION,payloadCode=ERROR_INVALID_FUNCTION;int calibration=-1;
    std::ofstream admission(root/"admission.json");
    admission << "{\"Pid\":" << GetCurrentProcessId() << ",\"SessionName\":\"";
    for (const wchar_t* c=prop.name;*c;++c) admission << (char)*c;
    admission << "\",\"StartCode\":" << start << ",\"OwnedSessionCreated\":" << (start==0 ? "true" : "false") << "}";admission.close();
    if (!start) {
        DWORD pid=GetCurrentProcessId();struct EventIds{BOOLEAN FilterIn;UCHAR Reserved;USHORT Count;USHORT Events[7];} ids{TRUE,0,7,{452,453,454,455,456,457,458}};
        EVENT_FILTER_DESCRIPTOR filters[2]{{},{(ULONGLONG)&ids,sizeof(ids),EVENT_FILTER_TYPE_EVENT_ID}};
        PVOID payloads[7]{};wchar_t pidText[32]{};swprintf_s(pidText,L"%lu",pid);
        wchar_t field[]=L"OwnerProcessId";PAYLOAD_FILTER_PREDICATE predicate{field,PAYLOADFIELD_EQ,pidText};payloadCode=0;
        for(USHORT id=452;id<=458&&!payloadCode;++id) {
            EVENT_DESCRIPTOR descriptor{id,0,16,4,(UCHAR)(id<=454?id-452+28:id-455+28),(USHORT)(id<=454?443:442),(ULONGLONG)(id<=454?0x8000020000000000:0x8000010000000000)};
            payloadCode=TdhCreatePayloadFilter(&provider,&descriptor,FALSE,1,&predicate,&payloads[id-452]);
        }
        if(!payloadCode)payloadCode=TdhAggregatePayloadFilters(7,payloads,nullptr,&filters[0]);
        ENABLE_TRACE_PARAMETERS parameters{};parameters.Version=ENABLE_TRACE_PARAMETERS_VERSION_2;parameters.EnableFilterDesc=filters;parameters.FilterDescCount=2;
        if(!payloadCode)enable=EnableTraceEx2(session,&provider,EVENT_CONTROL_CODE_ENABLE_PROVIDER,TRACE_LEVEL_VERBOSE,0x30000000000ULL,0,5000,&parameters);
        if(filters[0].Ptr)TdhCleanupPayloadEventFilterDescriptor(&filters[0]);
        for(auto& value:payloads)if(value)TdhDeletePayloadFilter(&value);
        if (!enable) {
            wchar_t dll[32768]{};
            if(GetEnvironmentVariableW(L"FWM_GUI_TRACE_DLL",dll,32768)) {
                HMODULE module=LoadLibraryW(dll);
                if(module) {
                    auto traceStart=(int(*)(const wchar_t*))GetProcAddress(module,"GuiTraceStart");
                    auto control=(int(*)())GetProcAddress(module,"GuiTraceControl");
                    auto close=(int(*)())GetProcAddress(module,"GuiTraceClose");
                    if(traceStart&&control&&close) {calibration=traceStart((root/L"gui-api.jsonl").c_str());if(!calibration)calibration=control();int end=close();if(!calibration)calibration=end;}
                }
            }
            disable=EnableTraceEx2(session,&provider,EVENT_CONTROL_CODE_DISABLE_PROVIDER,0,0,0,5000,nullptr);
        }
        stop=ControlTraceW(session,prop.name,&prop.p,EVENT_TRACE_CONTROL_STOP);
    }
    std::ofstream summary(root/"summary.json");bool ok=!start&&!enable&&!disable&&!stop&&calibration==0;
    summary << "{\"Verdict\":\"" << (ok ? "PASS" : "ADMISSION_UNAVAILABLE") << "\",\"Pid\":" << GetCurrentProcessId()
        << ",\"StartCode\":" << start << ",\"EnableCode\":" << enable << ",\"DisableCode\":" << disable << ",\"StopCode\":" << stop
        << ",\"CalibrationCode\":" << calibration << ",\"EventsLost\":" << prop.p.EventsLost << ",\"BuffersLost\":" << prop.p.LogBuffersLost
        << ",\"Hwnds\":[],\"SurvivingHwnds\":[],\"PidFilter\":false,\"OwnerPayloadFilter\":true,\"PayloadFilterCode\":" << payloadCode << ",\"EventIdFilter\":[452,453,454,455,456,457,458],\"PerformanceClaim\":false}";
    return ok ? 0 : 5;
}
