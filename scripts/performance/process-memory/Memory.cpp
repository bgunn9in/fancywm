#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#pragma comment(lib,"psapi.lib")
namespace {
HANDLE output=INVALID_HANDLE_VALUE;SRWLOCK gate=SRWLOCK_INIT;unsigned long long sequence=0;DWORD errors=0;
struct Guard{Guard(){AcquireSRWLockExclusive(&gate);}~Guard(){ReleaseSRWLockExclusive(&gate);}};
void Write(const char* line){DWORD bytes=static_cast<DWORD>(strlen(line)),written=0;if(!WriteFile(output,line,bytes,&written,nullptr)||written!=bytes)errors++;}
void Event(const char* name){LARGE_INTEGER q{};QueryPerformanceCounter(&q);char line[256];sprintf_s(line,"{\"Event\":\"%s\",\"Seq\":%llu,\"Pid\":%lu,\"Qpc\":%lld,\"Errors\":%lu}\n",name,++sequence,GetCurrentProcessId(),q.QuadPart,errors);Write(line);}
}
extern "C" __declspec(dllexport) int __cdecl MemoryStart(const wchar_t* path){Guard lock;if(output!=INVALID_HANDLE_VALUE)return 1;output=CreateFileW(path,GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(output==INVALID_HANDLE_VALUE)return 2;Event("started");return errors?3:0;}
extern "C" __declspec(dllexport) int __cdecl MemorySample(int phase,int sample){
    Guard lock;if(output==INVALID_HANDLE_VALUE)return 1;LARGE_INTEGER begin{},end{};QueryPerformanceCounter(&begin);PROCESS_MEMORY_COUNTERS_EX info{};info.cb=sizeof(info);
    BOOL ok=GetProcessMemoryInfo(GetCurrentProcess(),reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&info),sizeof(info));DWORD code=ok?0:GetLastError();QueryPerformanceCounter(&end);if(!ok)errors++;
    char line[1024];sprintf_s(line,"{\"Event\":\"sample\",\"Seq\":%llu,\"Pid\":%lu,\"Tid\":%lu,\"Phase\":%d,\"Sample\":%d,\"QpcBegin\":%lld,\"QpcEnd\":%lld,\"Code\":%lu,\"StructureBytes\":%lu,\"PrivateUsage\":%llu,\"PagefileUsage\":%llu,\"PeakPagefileUsage\":%llu,\"WorkingSetSize\":%llu,\"PeakWorkingSetSize\":%llu,\"PageFaultCount\":%lu}\n",++sequence,GetCurrentProcessId(),GetCurrentThreadId(),phase,sample,begin.QuadPart,end.QuadPart,code,info.cb,static_cast<unsigned long long>(info.PrivateUsage),static_cast<unsigned long long>(info.PagefileUsage),static_cast<unsigned long long>(info.PeakPagefileUsage),static_cast<unsigned long long>(info.WorkingSetSize),static_cast<unsigned long long>(info.PeakWorkingSetSize),info.PageFaultCount);Write(line);return errors?2:0;
}
extern "C" __declspec(dllexport) int __cdecl MemoryStop(){Guard lock;if(output==INVALID_HANDLE_VALUE)return 1;Event("stopped");BOOL flushed=FlushFileBuffers(output),closed=CloseHandle(output);output=INVALID_HANDLE_VALUE;return errors||!flushed||!closed?2:0;}
