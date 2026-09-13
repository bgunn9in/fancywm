#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <vector>
#include <stdio.h>
#pragma comment(lib,"dxgi.lib")
using Microsoft::WRL::ComPtr;
namespace {
struct Adapter { ComPtr<IDXGIAdapter3> Api; DXGI_ADAPTER_DESC1 Desc{}; UINT Ordinal; };
ComPtr<IDXGIFactory1> factory;
std::vector<Adapter> adapters;
HANDLE output=INVALID_HANDLE_VALUE;
SRWLOCK gate=SRWLOCK_INIT;
unsigned long long sequence=0;
DWORD errors=0;
struct Guard { Guard(){AcquireSRWLockExclusive(&gate);} ~Guard(){ReleaseSRWLockExclusive(&gate);} };
unsigned long long Luid(LUID value){return (static_cast<unsigned long long>(static_cast<DWORD>(value.HighPart))<<32)|value.LowPart;}
void Write(const char* line){DWORD bytes=static_cast<DWORD>(strlen(line)),written=0;if(!WriteFile(output,line,bytes,&written,nullptr)||written!=bytes)errors++;}
void Event(const char* name,HRESULT hr=0,UINT count=0){LARGE_INTEGER q{};QueryPerformanceCounter(&q);char line[512];sprintf_s(line,"{\"Event\":\"%s\",\"Seq\":%llu,\"Pid\":%lu,\"Tid\":%lu,\"Qpc\":%lld,\"Code\":%lu,\"Count\":%u}\n",name,++sequence,GetCurrentProcessId(),GetCurrentThreadId(),q.QuadPart,static_cast<DWORD>(hr),count);Write(line);}
void Cleanup(){adapters.clear();factory.Reset();}
}
extern "C" __declspec(dllexport) int __cdecl DxgiStart(const wchar_t* path){
    Guard lock;if(output!=INVALID_HANDLE_VALUE)return 1;
    output=CreateFileW(path,GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(output==INVALID_HANDLE_VALUE)return 2;
    HRESULT hr=CreateDXGIFactory1(IID_PPV_ARGS(&factory));Event("factory-created",hr);
    if(SUCCEEDED(hr))for(UINT i=0;;i++){
        ComPtr<IDXGIAdapter1> api;hr=factory->EnumAdapters1(i,&api);
        if(hr==DXGI_ERROR_NOT_FOUND){Event("enumeration-ended",hr,i);hr=S_OK;break;}
        if(FAILED(hr))break;
        Adapter adapter{};adapter.Ordinal=i;hr=api->GetDesc1(&adapter.Desc);if(FAILED(hr))break;
        char line[512];sprintf_s(line,"{\"Event\":\"adapter\",\"Seq\":%llu,\"Pid\":%lu,\"Ordinal\":%u,\"Luid\":%llu,\"Vendor\":%u,\"Device\":%u,\"Flags\":%u,\"DedicatedVideoMemory\":%llu}\n",++sequence,GetCurrentProcessId(),i,Luid(adapter.Desc.AdapterLuid),adapter.Desc.VendorId,adapter.Desc.DeviceId,adapter.Desc.Flags,static_cast<unsigned long long>(adapter.Desc.DedicatedVideoMemory));Write(line);
        if(adapter.Desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)continue;
        hr=api.As(&adapter.Api);if(FAILED(hr))break;adapters.push_back(std::move(adapter));
    }
    if(FAILED(hr)||adapters.empty()){Event("start-failed",hr);Cleanup();CloseHandle(output);output=INVALID_HANDLE_VALUE;return 3;}
    Event("started",0,static_cast<UINT>(adapters.size()));FlushFileBuffers(output);return errors?4:0;
}
extern "C" __declspec(dllexport) int __cdecl DxgiSample(int phase,int sample){
    Guard lock;if(output==INVALID_HANDLE_VALUE||adapters.empty())return 1;
    for(auto& adapter:adapters)for(UINT segment=0;segment<2;segment++){
        LARGE_INTEGER begin{},end{};QueryPerformanceCounter(&begin);DXGI_QUERY_VIDEO_MEMORY_INFO info{};
        HRESULT hr=adapter.Api->QueryVideoMemoryInfo(0,static_cast<DXGI_MEMORY_SEGMENT_GROUP>(segment),&info);QueryPerformanceCounter(&end);
        char line[1024];sprintf_s(line,"{\"Event\":\"sample\",\"Seq\":%llu,\"Pid\":%lu,\"Tid\":%lu,\"Phase\":%d,\"Sample\":%d,\"QpcBegin\":%lld,\"QpcEnd\":%lld,\"Ordinal\":%u,\"Luid\":%llu,\"Node\":0,\"Segment\":%u,\"Code\":%lu,\"Budget\":%llu,\"CurrentUsage\":%llu,\"AvailableForReservation\":%llu,\"CurrentReservation\":%llu}\n",++sequence,GetCurrentProcessId(),GetCurrentThreadId(),phase,sample,begin.QuadPart,end.QuadPart,adapter.Ordinal,Luid(adapter.Desc.AdapterLuid),segment,static_cast<DWORD>(hr),info.Budget,info.CurrentUsage,info.AvailableForReservation,info.CurrentReservation);Write(line);
        if(FAILED(hr))errors++;
    }
    return errors?2:0;
}
extern "C" __declspec(dllexport) int __cdecl DxgiStop(){
    Guard lock;if(output==INVALID_HANDLE_VALUE)return 1;Cleanup();Event("stopped",0,static_cast<UINT>(adapters.size()));BOOL flushed=FlushFileBuffers(output);BOOL closed=CloseHandle(output);output=INVALID_HANDLE_VALUE;return errors||!flushed||!closed?2:0;
}
