#include "Device9.h"
using namespace Resource9;
using StartFn=int(__cdecl*)(const wchar_t*);using MarkFn=int(__cdecl*)(int);using StopFn=int(__cdecl*)();
int wmain(int argc,wchar_t** argv){SetErrorMode(3);if(argc!=2||!Start(L"fixture.jsonl"))return 1;int result=0;HMODULE library=nullptr;StopFn stop=nullptr;bool observing=false;
    try{{Device9 fixture(true);library=LoadLibraryW(argv[1]);Need(library!=nullptr);auto start=reinterpret_cast<StartFn>(GetProcAddress(library,"D3D9Start"));auto mark=reinterpret_cast<MarkFn>(GetProcAddress(library,"D3D9Mark"));stop=reinterpret_cast<StopFn>(GetProcAddress(library,"D3D9Stop"));Need(start&&mark&&stop);int code=start(L"observer.jsonl");Log("observer-start-result",nullptr,code);Need(code==0);observing=true;
        for(int epoch=0;epoch<3;epoch++){phase=epoch;Need(mark(epoch*10)==0);
            for(int cycle=0;cycle<50;cycle++){
                IDirect3DTexture9* texture=nullptr;IDirect3DSurface9 *target=nullptr,*depth=nullptr,*plain=nullptr,*surface=nullptr;IDirect3DVertexBuffer9* vertex=nullptr;IDirect3DIndexBuffer9* index=nullptr;bool retained=false;
                try{
                    Need(SUCCEEDED(fixture.device->CreateTexture(64,64,1,D3DUSAGE_DYNAMIC,D3DFMT_A8R8G8B8,D3DPOOL_DEFAULT,&texture,nullptr)));
                    Need(SUCCEEDED(cycle%2?fixture.device->CreateRenderTargetEx(64,64,D3DFMT_A8R8G8B8,D3DMULTISAMPLE_NONE,0,TRUE,&target,nullptr,0):fixture.device->CreateRenderTarget(64,64,D3DFMT_A8R8G8B8,D3DMULTISAMPLE_NONE,0,TRUE,&target,nullptr)));
                    Need(SUCCEEDED(cycle%2?fixture.device->CreateDepthStencilSurfaceEx(64,64,D3DFMT_D16,D3DMULTISAMPLE_NONE,0,TRUE,&depth,nullptr,0):fixture.device->CreateDepthStencilSurface(64,64,D3DFMT_D16,D3DMULTISAMPLE_NONE,0,TRUE,&depth,nullptr)));
                    Need(SUCCEEDED(cycle%2?fixture.device->CreateOffscreenPlainSurfaceEx(64,64,D3DFMT_A8R8G8B8,D3DPOOL_SYSTEMMEM,&plain,nullptr,0):fixture.device->CreateOffscreenPlainSurface(64,64,D3DFMT_A8R8G8B8,D3DPOOL_SYSTEMMEM,&plain,nullptr)));
                    Need(SUCCEEDED(fixture.device->CreateVertexBuffer(4096,D3DUSAGE_DYNAMIC|D3DUSAGE_WRITEONLY,0,D3DPOOL_DEFAULT,&vertex,nullptr)));Need(SUCCEEDED(fixture.device->CreateIndexBuffer(4096,D3DUSAGE_DYNAMIC|D3DUSAGE_WRITEONLY,D3DFMT_INDEX16,D3DPOOL_DEFAULT,&index,nullptr)));Need(SUCCEEDED(texture->GetSurfaceLevel(0,&surface)));
                    IUnknown* all[]={texture,target,depth,plain,vertex,index,surface};for(UINT i=0;i<7;i++){IUnknown* identity=nullptr;Need(SUCCEEDED(all[i]->QueryInterface(__uuidof(IUnknown),reinterpret_cast<void**>(&identity))));Log("native-resource-created",nullptr,reinterpret_cast<uintptr_t>(identity),cycle,i);identity->Release();}
                    D3DLOCKED_RECT rect{};Need(SUCCEEDED(texture->LockRect(0,&rect,nullptr,D3DLOCK_DISCARD)));Log("native-texture-map",nullptr,reinterpret_cast<uintptr_t>(rect.pBits),cycle,rect.Pitch);Need(SUCCEEDED(texture->UnlockRect(0)));
                    Need(SUCCEEDED(plain->LockRect(&rect,nullptr,0)));Log("native-surface-map",nullptr,reinterpret_cast<uintptr_t>(rect.pBits),cycle,rect.Pitch);Need(SUCCEEDED(plain->UnlockRect()));void* data=nullptr;Need(SUCCEEDED(vertex->Lock(0,4096,&data,D3DLOCK_DISCARD)));Log("native-vertex-map",nullptr,reinterpret_cast<uintptr_t>(data),cycle,4096);Need(SUCCEEDED(vertex->Unlock()));Need(SUCCEEDED(index->Lock(0,4096,&data,D3DLOCK_DISCARD)));Log("native-index-map",nullptr,reinterpret_cast<uintptr_t>(data),cycle,4096);Need(SUCCEEDED(index->Unlock()));
                    if(cycle%5==1){texture->AddRef();retained=true;Log("native-extra-retainer",nullptr,reinterpret_cast<uintptr_t>(texture),cycle);}
                    Log("native-close-begin",nullptr,cycle);texture->Release();if(!retained)texture=nullptr;target->Release();target=nullptr;depth->Release();depth=nullptr;plain->Release();plain=nullptr;vertex->Release();vertex=nullptr;index->Release();index=nullptr;Log("native-parent-held-by-surface",nullptr,reinterpret_cast<uintptr_t>(surface),cycle);surface->Release();surface=nullptr;
                    if(retained){Log("native-retainer-release",nullptr,reinterpret_cast<uintptr_t>(texture),cycle);texture->Release();texture=nullptr;retained=false;}Log("native-case-complete",nullptr,cycle);
                }catch(...){if(surface)surface->Release();if(index)index->Release();if(vertex)vertex->Release();if(plain)plain->Release();if(depth)depth->Release();if(target)target->Release();if(texture){if(retained)texture->Release();texture->Release();}throw;}
            }Need(mark(epoch*10+1)==0);Log("native-epoch-complete",nullptr,epoch,50);
        }code=stop();observing=false;Log("observer-stop-result",nullptr,code);Need(code==0);
    }}catch(HRESULT error){Log("failure",nullptr,static_cast<DWORD>(error));result=2;}
    if(observing&&stop){int code=stop();Log("observer-failure-cleanup",nullptr,code);}
    // Keep the observer loaded until process teardown; late private-data
    // callbacks must never jump into an unloaded module.
    Log("process-complete",nullptr,result);if(!Close())result=3;return result;
}
