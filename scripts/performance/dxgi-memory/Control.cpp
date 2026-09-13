#include "Device9.h"
#include <dxgi1_4.h>
#include <wrl/client.h>
#pragma comment(lib,"dxgi.lib")
using namespace Resource9;
using Microsoft::WRL::ComPtr;
using StartFn=int(__cdecl*)(const wchar_t*);using SampleFn=int(__cdecl*)(int,int);using StopFn=int(__cdecl*)();
void Fence(IDirect3DDevice9Ex* device){
    ComPtr<IDirect3DQuery9> query;Need(SUCCEEDED(device->CreateQuery(D3DQUERYTYPE_EVENT,&query)));Need(SUCCEEDED(query->Issue(D3DISSUE_END)));
    ULONGLONG begin=GetTickCount64();HRESULT hr=S_FALSE;while(hr==S_FALSE&&GetTickCount64()-begin<2000){hr=query->GetData(nullptr,0,D3DGETDATA_FLUSH);if(hr==S_FALSE)Sleep(1);}Log("fence",nullptr,static_cast<DWORD>(hr));Need(hr==S_OK);
}
int wmain(int argc,wchar_t** argv){
    SetErrorMode(3);if(argc!=2||!Start(L"fixture.jsonl"))return 1;int result=0;HMODULE dll=nullptr;StopFn stop=nullptr;bool started=false;
    try{{Device9 fixture(true,true);LUID luid{};Need(SUCCEEDED(fixture.d3d->GetAdapterLUID(0,&luid)));auto identity=(static_cast<unsigned long long>(static_cast<DWORD>(luid.HighPart))<<32)|luid.LowPart;
        ComPtr<IDXGIFactory4> factory;ComPtr<IDXGIAdapter3> adapter;Need(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))));Need(SUCCEEDED(factory->EnumAdapterByLuid(luid,IID_PPV_ARGS(&adapter))));DXGI_ADAPTER_DESC desc{};Need(SUCCEEDED(adapter->GetDesc(&desc)));Need(desc.AdapterLuid.LowPart==luid.LowPart&&desc.AdapterLuid.HighPart==luid.HighPart);
        DXGI_QUERY_VIDEO_MEMORY_INFO admission{};Need(SUCCEEDED(adapter->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&admission)));Log("budget-admission",nullptr,admission.Budget,admission.CurrentUsage,identity);Need(admission.Budget>=256ull*1024*1024&&admission.CurrentUsage+32ull*1024*1024<admission.Budget);
        dll=LoadLibraryW(argv[1]);Need(dll!=nullptr);auto start=reinterpret_cast<StartFn>(GetProcAddress(dll,"DxgiStart"));auto sample=reinterpret_cast<SampleFn>(GetProcAddress(dll,"DxgiSample"));stop=reinterpret_cast<StopFn>(GetProcAddress(dll,"DxgiStop"));Need(start&&sample&&stop);Need(start(L"dxgi.jsonl")==0);started=true;
        for(int epoch=0;epoch<3;epoch++){
            phase=epoch;
            for(int cycle=0;cycle<50;cycle++){
                const int key=epoch*10000+cycle*10;
                auto observe=[&](int step){Log("sample-begin",nullptr,key+step,cycle);Need(sample(key+step,0)==0);Sleep(20);Need(sample(key+step,1)==0);Log("sample-end",nullptr,key+step,cycle);};
                observe(0);ComPtr<IDirect3DSurface9> surfaces[4];
                for(UINT i=0;i<4;i++){Need(SUCCEEDED(fixture.device->CreateRenderTarget(1024,1024,D3DFMT_A8R8G8B8,D3DMULTISAMPLE_NONE,0,FALSE,&surfaces[i],nullptr)));Log("owned-surface",nullptr,reinterpret_cast<uintptr_t>(surfaces[i].Get()),cycle,i);Need(SUCCEEDED(fixture.device->ColorFill(surfaces[i].Get(),nullptr,0xff334455)));}
                Fence(fixture.device);observe(1);
                for(UINT i=1;i<4;i++)surfaces[i].Reset();Log("partial-release-fence",nullptr,cycle);Fence(fixture.device);Log("one-owner-held",nullptr,reinterpret_cast<uintptr_t>(surfaces[0].Get()),cycle);observe(2);
                surfaces[0].Reset();Fence(fixture.device);Log("all-owned-surfaces-released",nullptr,cycle);observe(3);Log("cycle-complete",nullptr,cycle);
            }
            Log("epoch-complete",nullptr,epoch,50);
        }
        Need(stop()==0);started=false;Log("observer-stopped");
    }}catch(HRESULT error){Log("failure",nullptr,static_cast<DWORD>(error));result=2;}
    if(started&&stop){int code=stop();Log("observer-failure-cleanup",nullptr,code);}
    if(dll){BOOL ok=FreeLibrary(dll);Log("observer-unloaded",nullptr,ok);if(!ok)result=3;}
    Log("process-complete",nullptr,result);if(!Close())result=4;return result;
}
