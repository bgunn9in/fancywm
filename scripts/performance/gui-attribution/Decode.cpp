#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <filesystem>
#include <fstream>
#include <cstring>
static GUID provider{ 0x8c416c79,0xd49b,0x4f01,{0xa4,0x67,0xe5,0x6d,0x3a,0xa8,0x23,0x4c} };
static std::ofstream output;
static unsigned long long seen=0,matched=0;
static ULONG errors=0;
static void WINAPI Visit(EVENT_RECORD* record)
{
    ++seen;auto& h=record->EventHeader;USHORT id=h.EventDescriptor.Id;
    if (memcmp(&h.ProviderId,&provider,sizeof(provider)) || id<452 || id>458) return;
    ++matched;
    bool transform=id==458;ULONG expected=transform ? 28 : 20;
    if (record->UserDataLength!=expected) {++errors;return;}
    auto bytes=(BYTE*)record->UserData;ULONGLONG handle=0,next=0;ULONG type=0,session=0,owner=0;
    memcpy(&handle,bytes,8);ULONG offset=8;
    if(transform){memcpy(&next,bytes+offset,8);offset+=8;}
    memcpy(&type,bytes+offset,4);memcpy(&session,bytes+offset+4,4);memcpy(&owner,bytes+offset+8,4);
    output << "{\"EventId\":" << id << ",\"Version\":" << (int)h.EventDescriptor.Version << ",\"Opcode\":" << (int)h.EventDescriptor.Opcode
        << ",\"HeaderPid\":" << h.ProcessId << ",\"HeaderTid\":" << h.ThreadId << ",\"Timestamp\":" << h.TimeStamp.QuadPart << ",\"HeaderFlags\":" << h.Flags
        << ",\"HandleValue\":" << handle << ",\"NewHandleValue\":" << next << ",\"HandleType\":" << type << ",\"SessionId\":" << session << ",\"OwnerProcessId\":" << owner
        << ",\"UserDataLength\":" << record->UserDataLength << "}\n";
}
int wmain(int argc,wchar_t** argv)
{
    if(argc!=3)return 1;
    std::filesystem::path root(argv[2]);if(exists(root)||!create_directory(root))return 2;
    output.open(root/"events.jsonl");
    EVENT_TRACE_LOGFILEW log{};log.LogFileName=argv[1];log.ProcessTraceMode=PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP;log.EventRecordCallback=Visit;
    TRACEHANDLE trace=OpenTraceW(&log);if(trace==INVALID_PROCESSTRACE_HANDLE)return GetLastError();
    ULONG code=ProcessTrace(&trace,1,nullptr,nullptr);ULONG closed=CloseTrace(trace);output.close();
    std::ofstream summary(root/"summary.json");summary << "{\"ProcessCode\":" << code << ",\"CloseCode\":" << closed << ",\"SeenEvents\":" << seen << ",\"HandleEvents\":" << matched
        << ",\"PayloadErrors\":" << errors << ",\"EventsLost\":" << log.LogfileHeader.EventsLost << ",\"QpcFrequency\":" << log.LogfileHeader.PerfFreq.QuadPart << "}";
    return code || closed || errors ? 3 : 0;
}
