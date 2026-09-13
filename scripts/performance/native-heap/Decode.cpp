#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <filesystem>
#include <fstream>
#include <stdint.h>
static GUID HeapGuid{0x222962ab,0x6180,0x4b88,{0xa8,0x25,0x34,0x6b,0x75,0xf2,0xa2,0x4a}};
static GUID StackGuid{0xdef2fe46,0x7bd6,0x4b80,{0xbd,0x94,0xf5,0x7f,0xe2,0x0d,0x0c,0xe3}};
static GUID HeaderGuid{0x68fdd900,0x4a3e,0x11d1,{0x84,0xf4,0x00,0x00,0xf8,0x04,0x64,0xe3}};
struct Record { GUID Provider; uint32_t Pid,Tid; int64_t Qpc; uint16_t Id; uint8_t Version,Opcode; uint16_t Flags,Bytes; };
static_assert(sizeof(Record)==40,"raw event schema");
static std::ofstream Output;
static uint64_t Count,HeapCount,StackCount,OtherCount;
static int64_t StartQpc;
static bool MetadataOnly;
static void WINAPI Event(EVENT_RECORD* e) {
    bool heap=IsEqualGUID(e->EventHeader.ProviderId,HeapGuid),stack=IsEqualGUID(e->EventHeader.ProviderId,StackGuid);
    if (!heap && !stack) {
        if(IsEqualGUID(e->EventHeader.ProviderId,HeaderGuid)&&e->EventHeader.EventDescriptor.Opcode==0)StartQpc=e->EventHeader.TimeStamp.QuadPart;
        OtherCount++; return;
    }
    Record r{e->EventHeader.ProviderId,e->EventHeader.ProcessId,e->EventHeader.ThreadId,e->EventHeader.TimeStamp.QuadPart,
        e->EventHeader.EventDescriptor.Id,e->EventHeader.EventDescriptor.Version,e->EventHeader.EventDescriptor.Opcode,e->EventHeader.Flags,e->UserDataLength};
    if(!MetadataOnly) {
        Output.write(reinterpret_cast<const char*>(&r),sizeof(r));
        Output.write(reinterpret_cast<const char*>(e->UserData),e->UserDataLength);
    }
    Count++; if(heap)HeapCount++;else StackCount++;
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=3&&argc!=4)return 1;
    MetadataOnly=argc==4;
    std::filesystem::path dest(argv[2]);if(exists(dest)||!create_directory(dest))return 2;
    Output.open(dest/L"raw.bin",std::ios::binary|std::ios::out);
    EVENT_TRACE_LOGFILEW log{};log.LogFileName=argv[1];
    log.ProcessTraceMode=PROCESS_TRACE_MODE_EVENT_RECORD|PROCESS_TRACE_MODE_RAW_TIMESTAMP;log.EventRecordCallback=Event;
    TRACEHANDLE trace=OpenTraceW(&log);if(trace==INVALID_PROCESSTRACE_HANDLE)return 3;
    auto code=ProcessTrace(&trace,1,nullptr,nullptr);auto closed=CloseTrace(trace);
    Output.flush();bool ok=Output.good();Output.close();
    std::ofstream summary(dest/L"summary.json");
    summary<<"{\"ProcessTraceCode\":"<<code<<",\"CloseTraceCode\":"<<closed<<",\"WritePassed\":"<<(ok?"true":"false")
        <<",\"Records\":"<<Count<<",\"HeapRecords\":"<<HeapCount<<",\"StackRecords\":"<<StackCount<<",\"OtherMetadataRecords\":"<<OtherCount
        <<",\"EventsLost\":"<<log.LogfileHeader.EventsLost<<",\"BuffersLost\":"<<log.LogfileHeader.BuffersLost
        <<",\"PerfFrequency\":"<<log.LogfileHeader.PerfFreq.QuadPart<<",\"StartTime\":"<<log.LogfileHeader.StartTime.QuadPart
        <<",\"TraceStartQpc\":"<<StartQpc<<",\"MetadataOnly\":"<<(MetadataOnly?"true":"false")<<"}";
    return !code&&!closed&&ok?0:4;
}
