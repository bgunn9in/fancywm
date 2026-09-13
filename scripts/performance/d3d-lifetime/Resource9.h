#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d9.h>
#include <stdio.h>
#include <stdint.h>
#include <vector>
#include <map>
#include <memory>
#pragma comment(lib,"d3d9.lib")
#pragma comment(lib,"user32.lib")
namespace Resource9 {
inline HANDLE output=INVALID_HANDLE_VALUE;inline SRWLOCK gate=SRWLOCK_INIT,registration=SRWLOCK_INIT;inline unsigned long long sequence;inline LONG nextId,errors,phase=-1;
struct Item{LONG Id;uintptr_t Identity;const char* Kind;LONG Parent,Phase;volatile LONG Closed=0;};
// Diagnostic records intentionally outlive late driver callbacks. They contain
// no resource references and are not part of an application retention claim.
inline auto& items=*new std::vector<std::unique_ptr<Item>>;inline auto& current=*new std::map<uintptr_t,Item*>;
inline const GUID privateGuid={0x9ee515e2,0x3401,0x4822,{0xb2,0x8e,0x4d,0x8f,0xc1,0xfa,0x11,0x6c}};
inline void Log(const char* event,Item* item=nullptr,uintptr_t value=0,uintptr_t extra=0,uintptr_t third=0){DWORD error=GetLastError();LARGE_INTEGER q{};QueryPerformanceCounter(&q);void* frames[24]{};USHORT count=CaptureStackBackTrace(1,24,frames,nullptr);AcquireSRWLockExclusive(&gate);char line[2200];int used=sprintf_s(line,"{\"Event\":\"%s\",\"Seq\":%llu,\"Pid\":%lu,\"Tid\":%lu,\"Qpc\":%lld,\"Phase\":%ld,\"Id\":%ld,\"Identity\":%llu,\"Kind\":\"%s\",\"Parent\":%ld,\"Closed\":%ld,\"Value\":%llu,\"Extra\":%llu,\"Third\":%llu,\"Stack\":[",event,++sequence,GetCurrentProcessId(),GetCurrentThreadId(),q.QuadPart,phase,item?item->Id:0,static_cast<unsigned long long>(item?item->Identity:0),item?item->Kind:"observer",item?item->Parent:0,item?item->Closed:0,static_cast<unsigned long long>(value),static_cast<unsigned long long>(extra),static_cast<unsigned long long>(third));for(USHORT i=0;i<count;i++)used+=sprintf_s(line+used,sizeof(line)-used,"%s%llu",i?",":"",reinterpret_cast<unsigned long long>(frames[i]));used+=sprintf_s(line+used,sizeof(line)-used,"]}\n");DWORD written=0;if(output==INVALID_HANDLE_VALUE||!WriteFile(output,line,used,&written,nullptr)||written!=static_cast<DWORD>(used))InterlockedIncrement(&errors);ReleaseSRWLockExclusive(&gate);SetLastError(error);}
class Tag final:public IUnknown{
    LONG refs=1;Item* item;
public:
    explicit Tag(Item* owner):item(owner){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** result)override{if(!result)return E_POINTER;*result=nullptr;if(iid!=__uuidof(IUnknown))return E_NOINTERFACE;*result=static_cast<IUnknown*>(this);AddRef();return S_OK;}
    ULONG STDMETHODCALLTYPE AddRef()override{return InterlockedIncrement(&refs);}
    ULONG STDMETHODCALLTYPE Release()override{LONG count=InterlockedDecrement(&refs);if(!count){InterlockedIncrement(&item->Closed);Log("private-owner-released",item);delete this;}return count;}
};
inline Item* Find(void* resource){AcquireSRWLockShared(&gate);auto found=current.find(reinterpret_cast<uintptr_t>(resource));Item* item=found==current.end()?nullptr:found->second;ReleaseSRWLockShared(&gate);return item&&!item->Closed?item:nullptr;}
inline Item* Watch(IDirect3DResource9* resource,const char* kind,LONG parent=0){
    struct Guard{Guard(){AcquireSRWLockExclusive(&registration);}~Guard(){ReleaseSRWLockExclusive(&registration);}} guard;
    if(!resource)return nullptr;IUnknown* identity=nullptr;HRESULT hr=resource->QueryInterface(__uuidof(IUnknown),reinterpret_cast<void**>(&identity));if(FAILED(hr)){InterlockedIncrement(&errors);Log("identity-failed",nullptr,static_cast<DWORD>(hr));return nullptr;}uintptr_t address=reinterpret_cast<uintptr_t>(identity);identity->Release();
    if(auto found=Find(reinterpret_cast<void*>(address))){Log("already-observed",found);return found;}
    DWORD size=0;hr=resource->GetPrivateData(privateGuid,nullptr,&size);if(hr!=D3DERR_NOTFOUND){InterlockedIncrement(&errors);Log("private-guid-collision",nullptr,static_cast<DWORD>(hr));return nullptr;}
    auto item=std::make_unique<Item>();item->Id=InterlockedIncrement(&nextId);item->Identity=address;item->Kind=kind;item->Parent=parent;item->Phase=phase;Item* raw=item.get();AcquireSRWLockExclusive(&gate);current[address]=raw;current[reinterpret_cast<uintptr_t>(resource)]=raw;items.push_back(std::move(item));ReleaseSRWLockExclusive(&gate);
    Log("attach-begin",raw);auto tag=new Tag(raw);hr=resource->SetPrivateData(privateGuid,static_cast<IUnknown*>(tag),sizeof(IUnknown*),D3DSPD_IUNKNOWN);Log("attach-result",raw,static_cast<DWORD>(hr));tag->Release();if(FAILED(hr))InterlockedIncrement(&errors);return raw;
}
inline bool Start(const wchar_t* path){output=CreateFileW(path,GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);return output!=INVALID_HANDLE_VALUE;}
inline void Mark(LONG value){InterlockedExchange(&phase,value);LONG active=0,total=0;AcquireSRWLockShared(&gate);for(const auto& item:items){total++;if(!item->Closed)active++;}ReleaseSRWLockShared(&gate);Log("checkpoint",nullptr,active,total,errors);FlushFileBuffers(output);}
inline bool Close(){Log("observer-close",nullptr,errors);BOOL flush=FlushFileBuffers(output);BOOL closed=CloseHandle(output);output=INVALID_HANDLE_VALUE;return flush&&closed&&errors==0;}
inline void Need(bool v){if(!v)throw E_FAIL;}
}
