#include "Device9.h"
using namespace Resource9;
using StartFn=int(__cdecl*)(const wchar_t*);using MarkFn=int(__cdecl*)(int);using StopFn=int(__cdecl*)();
template<class T>void Sample(T* resource,int kind,int device,int epoch,int cycle){
    IUnknown* identity=nullptr;Need(SUCCEEDED(resource->QueryInterface(__uuidof(IUnknown),reinterpret_cast<void**>(&identity))));uintptr_t id=reinterpret_cast<uintptr_t>(identity);identity->Release();
    void** table=*reinterpret_cast<void***>(resource);Log("producer-resource",nullptr,id,kind,(static_cast<uint64_t>(device)<<32)|cycle);Log("producer-methods",nullptr,id,reinterpret_cast<uintptr_t>(table[11]),reinterpret_cast<uintptr_t>(table[12]));
    for(int i=0;i<3;i++){void* pointer=nullptr;DWORD flags=kind==3?0:i==1?D3DLOCK_NOOVERWRITE:D3DLOCK_DISCARD;HRESULT hr=resource->Lock(0,128,&pointer,flags);Log("producer-lock-result",nullptr,static_cast<DWORD>(hr),id,flags);Need(SUCCEEDED(hr)&&pointer);memset(pointer,epoch+i,128);Log("producer-map",nullptr,id,reinterpret_cast<uintptr_t>(pointer),i);MEMORY_BASIC_INFORMATION info{};Need(VirtualQuery(pointer,&info,sizeof(info))==sizeof(info));Log("producer-map-va",nullptr,id,(static_cast<uint64_t>(info.State)<<32)|info.Type,info.Protect);Need(SUCCEEDED(resource->Unlock()));Log("producer-unmap",nullptr,id,i);}
    Log("producer-release-begin",nullptr,id);resource->Release();Log("producer-release-end",nullptr,id);
}
int wmain(int argc,wchar_t** argv){SetErrorMode(3);if(argc!=2||!Start(L"fixture.jsonl"))return 1;int result=0;HMODULE observer=nullptr;StopFn stop=nullptr;bool observing=false;IDirect3DDevice9Ex* hardware=nullptr;
    try{{Device9 fixture(true);D3DPRESENT_PARAMETERS pp{};pp.Windowed=TRUE;pp.SwapEffect=D3DSWAPEFFECT_DISCARD;pp.hDeviceWindow=fixture.window;pp.BackBufferWidth=pp.BackBufferHeight=8;pp.BackBufferFormat=D3DFMT_A8R8G8B8;
        HRESULT hr=fixture.d3d->CreateDeviceEx(0,D3DDEVTYPE_HAL,fixture.window,D3DCREATE_HARDWARE_VERTEXPROCESSING|D3DCREATE_MULTITHREADED|D3DCREATE_FPU_PRESERVE,&pp,nullptr,&hardware);Log("hardware-device-created",nullptr,static_cast<DWORD>(hr));Need(SUCCEEDED(hr));
        observer=LoadLibraryW(argv[1]);Need(observer!=nullptr);auto start=reinterpret_cast<StartFn>(GetProcAddress(observer,"D3D9Start"));auto mark=reinterpret_cast<MarkFn>(GetProcAddress(observer,"D3D9Mark"));stop=reinterpret_cast<StopFn>(GetProcAddress(observer,"D3D9Stop"));Need(start&&mark&&stop);int code=start(L"observer.jsonl");Log("observer-start-result",nullptr,code);Need(code==0);observing=true;
        IDirect3DDevice9Ex* devices[]={fixture.device,hardware};
        for(int d=0;d<2;d++){Log("producer-device",nullptr,d,d==0?D3DCREATE_SOFTWARE_VERTEXPROCESSING:D3DCREATE_HARDWARE_VERTEXPROCESSING);
            for(int epoch=0;epoch<3;epoch++){phase=d*100+epoch;Need(mark(d*100+epoch*10)==0);
                for(int cycle=0;cycle<50;cycle++){
                    IDirect3DVertexBuffer9* vertex=nullptr;IDirect3DIndexBuffer9* index=nullptr;
                    Need(SUCCEEDED(devices[d]->CreateVertexBuffer(640032,D3DUSAGE_DYNAMIC|D3DUSAGE_WRITEONLY,0,D3DPOOL_DEFAULT,&vertex,nullptr)));Sample(vertex,1,d,epoch,cycle);
                    Need(SUCCEEDED(devices[d]->CreateIndexBuffer(120006,D3DUSAGE_DYNAMIC|D3DUSAGE_WRITEONLY,D3DFMT_INDEX16,D3DPOOL_DEFAULT,&index,nullptr)));Sample(index,2,d,epoch,cycle);
                    Need(SUCCEEDED(devices[d]->CreateVertexBuffer(128,D3DUSAGE_WRITEONLY,0,D3DPOOL_DEFAULT,&vertex,nullptr)));Sample(vertex,3,d,epoch,cycle);
                    Log("producer-cycle-complete",nullptr,cycle,d,epoch);
                }Need(mark(d*100+epoch*10+1)==0);
            }
        }
        hardware->Release();hardware=nullptr;Log("hardware-device-released");code=stop();observing=false;Log("observer-stop-result",nullptr,code);Need(code==0);
    }}catch(HRESULT error){Log("failure",nullptr,static_cast<DWORD>(error));result=2;}
    if(hardware){hardware->Release();hardware=nullptr;}if(observing&&stop)Log("observer-failure-cleanup",nullptr,stop());
    // Native callbacks keep their loaded observer module until process exit.
    Log("process-complete",nullptr,result);if(!Close())result=3;return result;
}
