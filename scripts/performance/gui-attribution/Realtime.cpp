#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <filesystem>
#include <fstream>
#include <set>
#include <string>
#include <cstring>
static GUID provider{0x8c416c79,0xd49b,0x4f01,{0xa4,0x67,0xe5,0x6d,0x3a,0xa8,0x23,0x4c}};
struct Properties {EVENT_TRACE_PROPERTIES p{};wchar_t name[256]{};};
static Properties properties;static TRACEHANDLE session=0,consumer=INVALID_PROCESSTRACE_HANDLE;static HANDLE worker=nullptr;
static std::ofstream output,raw;static std::filesystem::path destination;static std::set<std::pair<bool,ULONGLONG>> owned;
static ULONG startCode=1,enableCode=1,disableCode=1,stopCode=1,consumeCode=1,decodeErrors=0,writeErrors=0;
static unsigned long long seen=0,written=0,other=0;static DWORD ownPid=0;
#pragma pack(push,1)
struct RawHeader{USHORT id;BYTE version,opcode;ULONG pid,tid;LONGLONG qpc;USHORT length,flags;};
#pragma pack(pop)
static void WINAPI Visit(EVENT_RECORD* record)
{
    auto& h=record->EventHeader;USHORT id=h.EventDescriptor.Id;
    if(memcmp(&h.ProviderId,&provider,sizeof(provider))||id<452||id>458)return;
    ++seen;bool transform=id==458;if(record->UserDataLength!=(transform?28:20)){++decodeErrors;return;}
    auto bytes=(BYTE*)record->UserData;ULONGLONG handle=0,next=0;ULONG type=0,sid=0,pid=0,offset=8;
    memcpy(&handle,bytes,8);if(transform){memcpy(&next,bytes+offset,8);offset+=8;}
    memcpy(&type,bytes+offset,4);memcpy(&sid,bytes+offset+4,4);memcpy(&pid,bytes+offset+8,4);
    auto key=std::make_pair(id<=454,handle);bool prior=owned.count(key)!=0;
    if(pid!=ownPid&&!prior){++other;return;}
    if(pid==ownPid&&(id==452||id==455||id==454||id==457))owned.insert(key);
    if(id==453||id==456||((id==454||id==457)&&pid!=ownPid))owned.erase(key);
    if(transform){owned.erase(key);if(pid==ownPid)owned.insert({false,next});}
    // No non-owned resource record reaches either persistent stream. An owner
    // transfer/destruction is retained only while following an already owned key.
    RawHeader header{id,h.EventDescriptor.Version,h.EventDescriptor.Opcode,h.ProcessId,h.ThreadId,h.TimeStamp.QuadPart,record->UserDataLength,h.Flags};
    raw.write((char*)&header,sizeof(header));raw.write((char*)bytes,record->UserDataLength);
    output << "{\"EventId\":" << id << ",\"Version\":" << (int)header.version << ",\"Opcode\":" << (int)header.opcode
        << ",\"HeaderPid\":" << header.pid << ",\"HeaderTid\":" << header.tid << ",\"Timestamp\":" << header.qpc << ",\"HeaderFlags\":" << header.flags
        << ",\"HandleValue\":" << handle << ",\"NewHandleValue\":" << next << ",\"HandleType\":" << type << ",\"SessionId\":" << sid << ",\"OwnerProcessId\":" << pid
        << ",\"UserDataLength\":" << header.length << ",\"PriorOwnedKey\":" << (prior?"true":"false") << "}\n";
    ++written;if(!raw.good()||!output.good())++writeErrors;
}
static DWORD WINAPI Consume(void*){consumeCode=ProcessTrace(&consumer,1,nullptr,nullptr);return consumeCode;}
static void Status()
{
    LARGE_INTEGER frequency{};QueryPerformanceFrequency(&frequency);
    std::ofstream f(destination/"summary.json");
    f << "{\"Pid\":" << ownPid << ",\"StartCode\":" << startCode << ",\"EnableCode\":" << enableCode << ",\"DisableCode\":" << disableCode << ",\"StopCode\":" << stopCode
      << ",\"ConsumeCode\":" << consumeCode << ",\"DecodeErrors\":" << decodeErrors << ",\"WriteErrors\":" << writeErrors << ",\"SeenHandleEvents\":" << seen << ",\"WrittenOwnedEvents\":" << written
      << ",\"DiscardedOtherResourceEvents\":" << other << ",\"EventsLost\":" << properties.p.EventsLost << ",\"BuffersLost\":" << properties.p.LogBuffersLost
      << ",\"RealTimeBuffersLost\":" << properties.p.RealTimeBuffersLost << ",\"QpcFrequency\":" << frequency.QuadPart
      << ",\"ProviderFilteringClaim\":false,\"FilterBeforePersistentStorage\":true,\"RawHeaderBytes\":" << sizeof(RawHeader) << "}";
}
extern "C" __declspec(dllexport) int OwnedEtwStop()
{
    if(startCode==0&&session){
        if(enableCode==0)disableCode=EnableTraceEx2(session,&provider,EVENT_CONTROL_CODE_DISABLE_PROVIDER,0,0,0,5000,nullptr);
        stopCode=ControlTraceW(session,properties.name,&properties.p,EVENT_TRACE_CONTROL_STOP);session=0;
    }
    if(worker){
        if(WaitForSingleObject(worker,30000)!=WAIT_OBJECT_0){CloseTrace(consumer);WaitForSingleObject(worker,30000);++writeErrors;}
        CloseHandle(worker);worker=nullptr;
    }
    if(consumer!=INVALID_PROCESSTRACE_HANDLE){CloseTrace(consumer);consumer=INVALID_PROCESSTRACE_HANDLE;}
    raw.close();output.close();Status();
    return startCode||enableCode||disableCode||stopCode||consumeCode||decodeErrors||writeErrors||properties.p.EventsLost||properties.p.LogBuffersLost||properties.p.RealTimeBuffersLost?1:0;
}
extern "C" __declspec(dllexport) int OwnedEtwStart(const wchar_t* path)
{
    destination=path;if(exists(destination)||!create_directory(destination))return ERROR_ALREADY_EXISTS;ownPid=GetCurrentProcessId();
    raw.open(destination/"owned-events.raw",std::ios::binary);output.open(destination/"owned-events.jsonl");
    properties.p.Wnode.BufferSize=sizeof(properties);properties.p.Wnode.Flags=WNODE_FLAG_TRACED_GUID;properties.p.Wnode.ClientContext=1;
    properties.p.BufferSize=64;properties.p.MinimumBuffers=16;properties.p.MaximumBuffers=128;properties.p.FlushTimer=1;
    properties.p.LogFileMode=EVENT_TRACE_REAL_TIME_MODE;properties.p.LoggerNameOffset=offsetof(Properties,name);
    swprintf_s(properties.name,L"FWM_OWNED_GUI_RT_%lu_%llu",ownPid,GetTickCount64());
    startCode=StartTraceW(&session,properties.name,&properties.p);
    std::ofstream admission(destination/"admission.json");admission << "{\"Pid\":" << ownPid << ",\"StartCode\":" << startCode << ",\"SessionName\":\"";
    for(const wchar_t* c=properties.name;*c;++c)admission<<(char)*c;admission << "\",\"OwnResourceConsumerFilter\":true}";admission.close();
    if(startCode){OwnedEtwStop();return startCode;}
    EVENT_TRACE_LOGFILEW log{};log.LoggerName=properties.name;log.ProcessTraceMode=PROCESS_TRACE_MODE_REAL_TIME|PROCESS_TRACE_MODE_EVENT_RECORD|PROCESS_TRACE_MODE_RAW_TIMESTAMP;log.EventRecordCallback=Visit;
    consumer=OpenTraceW(&log);if(consumer==INVALID_PROCESSTRACE_HANDLE){ULONG e=GetLastError();OwnedEtwStop();return e;}
    worker=CreateThread(nullptr,0,Consume,nullptr,0,nullptr);if(!worker){ULONG e=GetLastError();OwnedEtwStop();return e;}
    enableCode=EnableTraceEx2(session,&provider,EVENT_CONTROL_CODE_ENABLE_PROVIDER,TRACE_LEVEL_VERBOSE,0x30000000000ULL,0,5000,nullptr);
    if(enableCode){OwnedEtwStop();return enableCode;}return 0;
}
