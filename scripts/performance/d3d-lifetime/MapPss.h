#pragma once
#include <string>
#include <set>
namespace MapPss {
using BeginFn=DWORD(__cdecl*)(const wchar_t*,DWORD*);using ReadFn=DWORD(__cdecl*)(const wchar_t*,const void*,DWORD);using RegionsFn=DWORD(__cdecl*)(const wchar_t*);using EndFn=DWORD(__cdecl*)();
inline BeginFn begin;inline ReadFn read;inline RegionsFn regions;inline EndFn end;inline HMODULE module;
inline SRWLOCK captureGate=SRWLOCK_INIT;inline bool enabled;inline std::wstring root;inline LONG nextSnapshot;
inline std::set<std::pair<LONG,std::string>> sampled;
struct Guard{Guard(){AcquireSRWLockExclusive(&captureGate);}~Guard(){ReleaseSRWLockExclusive(&captureGate);}};
inline bool Save(const std::wstring& path,const char* data){HANDLE f=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(f==INVALID_HANDLE_VALUE)return false;DWORD n=0,bytes=static_cast<DWORD>(strlen(data));BOOL ok=WriteFile(f,data,bytes,&n,nullptr)&&n==bytes;ok=FlushFileBuffers(f)&&ok;ok=CloseHandle(f)&&ok;return ok;}
inline void Configure(){using namespace Resource9;wchar_t path[32768],folder[32768];DWORD n=GetEnvironmentVariableW(L"FWM_D3D9_MAP_PSS_DLL",path,32768);if(!n)return;Need(n<32768);n=GetEnvironmentVariableW(L"FWM_D3D9_MAP_ROOT",folder,32768);Need(n>0&&n<32768);Need(CreateDirectoryW(folder,nullptr));root=folder;module=LoadLibraryW(path);Need(module!=nullptr);begin=reinterpret_cast<BeginFn>(GetProcAddress(module,"PssBegin"));read=reinterpret_cast<ReadFn>(GetProcAddress(module,"PssRead"));regions=reinterpret_cast<RegionsFn>(GetProcAddress(module,"PssCloneRegions"));end=reinterpret_cast<EndFn>(GetProcAddress(module,"PssEnd"));Need(begin&&read&&regions&&end);enabled=true;Log("mapped-snapshot-configured",nullptr,reinterpret_cast<uintptr_t>(module));}
inline void Capture(const char* api,Resource9::Item* owner,void* pointer,uintptr_t extent,DWORD flags){
    using namespace Resource9;if(!module||!owner||!pointer||!currentMap||currentMap->Depth!=1)return;Guard guard;
    LONG p=phase;if(!enabled||!(p==-1||(p>=0&&p%10==0))||sampled.count({p,owner->Kind}))return;
    sampled.insert({p,owner->Kind});LONG id=++nextSnapshot;wchar_t name[64];swprintf_s(name,L"%ld",id);std::wstring folder=root+L"\\"+name;
    LARGE_INTEGER first{},last{};QueryPerformanceCounter(&first);MEMORY_BASIC_INFORMATION a{},b{};SIZE_T query=VirtualQuery(pointer,&a,sizeof(a));
    Log("mapped-snapshot-begin",owner,id,reinterpret_cast<uintptr_t>(pointer),currentMap->Id);DWORD clone=0;DWORD capture=begin(folder.c_str(),&clone),r0=ERROR_INVALID_STATE,r1=ERROR_INVALID_STATE,readCode=ERROR_INVALID_STATE;
    if(!capture){r0=regions(L"clone-va-0.bin");r1=regions(L"clone-va-1.bin");readCode=read(L"clone-probe.bin",pointer,1);}
    // Only the clone is read. In particular, the observer never reads from a
    // live D3DUSAGE_WRITEONLY buffer or changes the producer's mapped bytes.
    DWORD release=end();SIZE_T queryAfter=VirtualQuery(pointer,&b,sizeof(b));QueryPerformanceCounter(&last);
    char json[2400];sprintf_s(json,"{\"Pid\":%lu,\"Tid\":%lu,\"Snapshot\":%ld,\"Phase\":%ld,\"OwnerId\":%ld,\"OwnerIdentity\":%llu,\"OwnerKind\":\"%s\",\"Call\":%ld,\"API\":\"%s\",\"Pointer\":%llu,\"ExtentOrPitch\":%llu,\"Flags\":%lu,\"Begin\":%lld,\"End\":%lld,\"ClonePid\":%lu,\"CaptureCode\":%lu,\"FirstQuery\":%lu,\"SecondQuery\":%lu,\"CloneReadCode\":%lu,\"ProbeBytes\":1,\"ReleaseCode\":%lu,\"SourceQueryBytes\":%zu,\"AfterQueryBytes\":%zu,\"Base\":%llu,\"AllocationBase\":%llu,\"Bytes\":%zu,\"State\":%lu,\"Type\":%lu,\"Protect\":%lu,\"AfterBase\":%llu,\"AfterAllocationBase\":%llu,\"AfterBytes\":%zu,\"AfterState\":%lu,\"AfterType\":%lu,\"AfterProtect\":%lu,\"LiveBytesReadOrWritten\":false}",GetCurrentProcessId(),GetCurrentThreadId(),id,p,owner->Id,static_cast<unsigned long long>(owner->Identity),owner->Kind,currentMap->Id,api,reinterpret_cast<unsigned long long>(pointer),static_cast<unsigned long long>(extent),flags,first.QuadPart,last.QuadPart,clone,capture,r0,r1,readCode,release,query,queryAfter,reinterpret_cast<unsigned long long>(a.BaseAddress),reinterpret_cast<unsigned long long>(a.AllocationBase),a.RegionSize,a.State,a.Type,a.Protect,reinterpret_cast<unsigned long long>(b.BaseAddress),reinterpret_cast<unsigned long long>(b.AllocationBase),b.RegionSize,b.State,b.Type,b.Protect);
    bool saved=Save(folder+L"\\mapped-owner.json",json);if(capture||r0||r1||release||!saved||query!=sizeof(a)||queryAfter!=sizeof(b)||(readCode!=0&&readCode!=ERROR_PARTIAL_COPY))InterlockedIncrement(&errors);
    Log("mapped-snapshot-end",owner,id,clone,readCode);
}
inline void Mark(LONG value){Guard guard;Resource9::Mark(value);}
inline void Stop(){Guard guard;enabled=false;}
}
