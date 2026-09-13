#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <processsnapshot.h>
#include <stdint.h>
#include <stdio.h>
#include <string>
#include <vector>
static HPSS Snapshot;
static HANDLE Clone;
static std::wstring Folder;
static uint64_t Begin,End;
static DWORD ClonePid;
struct Region { uint64_t Base,AllocationBase,Bytes; DWORD AllocationProtect,State,Protect,Type; };
struct Header { char Magic[8]; DWORD Pid,ClonePid; uint64_t Frequency,Begin,End; DWORD Count,Error; };
static_assert(sizeof(Header)==48 && sizeof(Region)==40,"PSS raw schema");
static uint64_t Qpc(){ LARGE_INTEGER value;QueryPerformanceCounter(&value);return value.QuadPart; }
static bool Save(const wchar_t* name,const void* data,DWORD bytes){
    std::wstring path=Folder+L"\\"+name;HANDLE f=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(f==INVALID_HANDLE_VALUE)return false;
    DWORD written=0;bool ok=WriteFile(f,data,bytes,&written,nullptr)&&written==bytes;ok=FlushFileBuffers(f)&&ok;ok=CloseHandle(f)&&ok;return ok;
}
static bool Regions(const wchar_t* name,const std::vector<Region>& rows,DWORD error){
    LARGE_INTEGER freq;QueryPerformanceFrequency(&freq);Header h={{'F','W','M','P','S','S','V','1'},GetCurrentProcessId(),ClonePid,static_cast<uint64_t>(freq.QuadPart),Begin,End,static_cast<DWORD>(rows.size()),error};
    std::vector<unsigned char> bytes(sizeof(h)+rows.size()*sizeof(Region));memcpy(bytes.data(),&h,sizeof(h));memcpy(bytes.data()+sizeof(h),rows.data(),rows.size()*sizeof(Region));return Save(name,bytes.data(),static_cast<DWORD>(bytes.size()));
}
extern "C" __declspec(dllexport) DWORD __cdecl PssBegin(const wchar_t* folder,DWORD* clonePid){
    if(Snapshot||!CreateDirectoryW(folder,nullptr))return ERROR_ALREADY_EXISTS;Folder=folder;Begin=Qpc();
    auto flags=PSS_CAPTURE_VA_CLONE|PSS_CAPTURE_VA_SPACE|PSS_CAPTURE_VA_SPACE_SECTION_INFORMATION|PSS_CAPTURE_THREADS|PSS_CAPTURE_THREAD_CONTEXT|PSS_CREATE_USE_VM_ALLOCATIONS;
    DWORD code=PssCaptureSnapshot(GetCurrentProcess(),flags,CONTEXT_ALL,&Snapshot);End=Qpc();
    char admission[512];sprintf_s(admission,"{\"Pid\":%lu,\"CaptureCode\":%lu,\"Flags\":%lu,\"Begin\":%llu,\"End\":%llu}",GetCurrentProcessId(),code,static_cast<DWORD>(flags),Begin,End);if(!Save(L"capture.json",admission,static_cast<DWORD>(strlen(admission))))return ERROR_WRITE_FAULT;if(code)return code;
    PSS_VA_CLONE_INFORMATION clone{};code=PssQuerySnapshot(Snapshot,PSS_QUERY_VA_CLONE_INFORMATION,&clone,sizeof(clone));if(code)return code;Clone=clone.VaCloneHandle;ClonePid=GetProcessId(Clone);*clonePid=ClonePid;
    PSS_PROCESS_INFORMATION process{};code=PssQuerySnapshot(Snapshot,PSS_QUERY_PROCESS_INFORMATION,&process,sizeof(process));if(code||process.ProcessId!=GetCurrentProcessId()||!ClonePid||ClonePid==process.ProcessId)return ERROR_INVALID_DATA;
    PSS_VA_SPACE_INFORMATION va{};code=PssQuerySnapshot(Snapshot,PSS_QUERY_VA_SPACE_INFORMATION,&va,sizeof(va));if(code)return code;
    if(!Save(L"process-info.bin",&process,sizeof(process)))return ERROR_WRITE_FAULT;
    HPSSWALK marker=nullptr;code=PssWalkMarkerCreate(nullptr,&marker);if(code)return code;
    std::vector<Region> rows;PSS_VA_SPACE_ENTRY entry{};
    while((code=PssWalkSnapshot(Snapshot,PSS_WALK_VA_SPACE,marker,&entry,sizeof(entry)))==ERROR_SUCCESS){
        rows.push_back({reinterpret_cast<uint64_t>(entry.BaseAddress),reinterpret_cast<uint64_t>(entry.AllocationBase),entry.RegionSize,entry.AllocationProtect,entry.State,entry.Protect,entry.Type});
        if(rows.size()>1000000){code=ERROR_BUFFER_OVERFLOW;break;}
    }
    DWORD freed=PssWalkMarkerFree(marker);if(!Regions(L"snapshot-va.bin",rows,code))return ERROR_WRITE_FAULT;
    sprintf_s(admission,"{\"Pid\":%lu,\"ClonePid\":%lu,\"RegionCount\":%lu,\"WalkRows\":%zu,\"WalkCode\":%lu,\"MarkerFreeCode\":%lu,\"CloneExplicitlyResumed\":false}",process.ProcessId,ClonePid,va.RegionCount,rows.size(),code,freed);
    if(!Save(L"ready.json",admission,static_cast<DWORD>(strlen(admission))))return ERROR_WRITE_FAULT;
    return code==ERROR_NO_MORE_ITEMS&&freed==0&&va.RegionCount==rows.size()?ERROR_SUCCESS:ERROR_INVALID_DATA;
}
extern "C" __declspec(dllexport) DWORD __cdecl PssRead(const wchar_t* name,const void* address,DWORD bytes){
    if(!Snapshot||!Clone||bytes>1024*1024*16)return ERROR_INVALID_STATE;std::vector<unsigned char> data(bytes);SIZE_T copied=0;
    if(!ReadProcessMemory(Clone,address,data.data(),bytes,&copied)||copied!=bytes)return GetLastError();return Save(name,data.data(),bytes)?0:ERROR_WRITE_FAULT;
}
extern "C" __declspec(dllexport) DWORD __cdecl PssCloneRegions(const wchar_t* name){
    if(!Clone)return ERROR_INVALID_STATE;std::vector<Region> rows;SYSTEM_INFO system{};GetSystemInfo(&system);uint64_t address=0,max=reinterpret_cast<uint64_t>(system.lpMaximumApplicationAddress);MEMORY_BASIC_INFORMATION info{};DWORD error=0;
    while(address<=max){
        if(VirtualQueryEx(Clone,reinterpret_cast<void*>(address),&info,sizeof(info))!=sizeof(info)){error=GetLastError();break;}
        rows.push_back({reinterpret_cast<uint64_t>(info.BaseAddress),reinterpret_cast<uint64_t>(info.AllocationBase),info.RegionSize,info.AllocationProtect,info.State,info.Protect,info.Type});
        uint64_t next=reinterpret_cast<uint64_t>(info.BaseAddress)+info.RegionSize;if(next<=address){error=ERROR_INVALID_DATA;break;}address=next;
    }
    FILETIME created{},exited{},kernel{},user{};BOOL times=GetProcessTimes(Clone,&created,&exited,&kernel,&user);
    char activity[384];sprintf_s(activity,"{\"Pid\":%lu,\"ClonePid\":%lu,\"GetTimesPassed\":%s,\"Created\":%llu,\"Kernel\":%llu,\"User\":%llu}",GetCurrentProcessId(),ClonePid,times?"true":"false",(static_cast<uint64_t>(created.dwHighDateTime)<<32)|created.dwLowDateTime,(static_cast<uint64_t>(kernel.dwHighDateTime)<<32)|kernel.dwLowDateTime,(static_cast<uint64_t>(user.dwHighDateTime)<<32)|user.dwLowDateTime);
    std::wstring activityName=std::wstring(name)+L".activity.json";if(!Save(activityName.c_str(),activity,static_cast<DWORD>(strlen(activity))))return ERROR_WRITE_FAULT;
    return Regions(name,rows,error)?0:ERROR_WRITE_FAULT;
}
extern "C" __declspec(dllexport) DWORD __cdecl PssEnd(){
    if(!Snapshot)return ERROR_INVALID_STATE;HANDLE retained=nullptr;
    BOOL dup=DuplicateHandle(GetCurrentProcess(),Clone,GetCurrentProcess(),&retained,SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,0);
    DWORD code=PssFreeSnapshot(GetCurrentProcess(),Snapshot);if(code==0){Snapshot=nullptr;Clone=nullptr;}
    DWORD wait=dup?WaitForSingleObject(retained,0):WAIT_FAILED;
    HANDLE held=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,ClonePid);DWORD heldError=held?0:GetLastError();if(held)CloseHandle(held);
    BOOL closed=retained?CloseHandle(retained):FALSE;
    DWORD afterError=0,probes=0;
    for(;probes<200;){Sleep(10);probes++;HANDLE after=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,ClonePid);afterError=after?0:GetLastError();if(after)CloseHandle(after);if(afterError==ERROR_INVALID_PARAMETER)break;}
    char json[576];sprintf_s(json,"{\"Pid\":%lu,\"ClonePid\":%lu,\"FreeCode\":%lu,\"RetainedHandle\":%s,\"CloneWait\":%lu,\"HeldOpenError\":%lu,\"RetainedHandleClosed\":%s,\"AfterCloseOpenError\":%lu,\"AbsenceProbes\":%lu,\"CloneExplicitlyResumed\":false}",GetCurrentProcessId(),ClonePid,code,dup?"true":"false",wait,heldError,closed?"true":"false",afterError,probes);
    if(!Save(L"released.json",json,static_cast<DWORD>(strlen(json))))return ERROR_WRITE_FAULT;return code==0&&dup&&closed&&afterError==ERROR_INVALID_PARAMETER?0:ERROR_INVALID_DATA;
}
