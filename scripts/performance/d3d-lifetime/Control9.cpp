#include "Device9.h"
#include <thread>
using namespace Resource9;
int main(){SetErrorMode(3);if(!Start(L"resources.jsonl"))return 1;int result=0;try{{Device9 fixture(true);
    for(int epoch=0;epoch<3;epoch++){
        phase=epoch;
        for(int cycle=0;cycle<50;cycle++){
            IDirect3DTexture9* texture=nullptr;IDirect3DSurface9* surface=nullptr;bool extra=false,locked=false;
            try{Need(SUCCEEDED(fixture.device->CreateTexture(64,64,1,D3DUSAGE_DYNAMIC,D3DFMT_A8R8G8B8,D3DPOOL_DEFAULT,&texture,nullptr)));auto item=Watch(texture,"texture9");Need(item&&Watch(texture,"texture9")==item);int scenario=cycle%5;Log("scenario",item,scenario,cycle);
                D3DLOCKED_RECT rect{};Need(SUCCEEDED(texture->LockRect(0,&rect,nullptr,D3DLOCK_DISCARD)));locked=true;Need(rect.Pitch>=256);for(int y=0;y<64;y++)memset(static_cast<unsigned char*>(rect.pBits)+y*rect.Pitch,0x40+scenario,256);Log("control-lock",item,reinterpret_cast<uintptr_t>(rect.pBits),rect.Pitch);Need(SUCCEEDED(texture->UnlockRect(0)));locked=false;Log("control-unlock",item);Need(item->Closed==0);
                if(scenario==1){texture->AddRef();extra=true;Log("retainer-acquired",item);texture->Release();extra=false;Log("original-released",item);Need(item->Closed==0);}
                if(scenario==2){Need(SUCCEEDED(texture->GetSurfaceLevel(0,&surface)));auto child=Watch(surface,"surface9",item->Id);Need(child);texture->Release();texture=nullptr;Log("parent-external-released",item);Need(item->Closed==0&&child->Closed==0);Log("child-release-begin",child);surface->Release();surface=nullptr;Log("child-release-end",child);Need(child->Closed==1&&item->Closed==1);}
                else if(scenario==3){std::thread worker([&]{Log("worker-release-begin",item);texture->Release();Log("worker-release-end",item);});worker.join();texture=nullptr;Need(item->Closed==1);}
                else if(scenario==4){Log("explicit-private-free-begin",item);Need(SUCCEEDED(texture->FreePrivateData(privateGuid)));Log("explicit-private-free-end",item);Need(item->Closed==1);D3DSURFACE_DESC desc{};Need(SUCCEEDED(texture->GetLevelDesc(0,&desc))&&desc.Width==64);Log("resource-still-valid-after-private-free",item,desc.Width,desc.Height);texture->Release();texture=nullptr;}
                else{Log("release-begin",item);texture->Release();texture=nullptr;Log("release-end",item);Need(item->Closed==1);}
                Log("case-complete",item,scenario,cycle);
            }catch(...){if(locked&&texture)texture->UnlockRect(0);if(surface)surface->Release();if(texture){if(extra)texture->Release();texture->Release();}throw;}
        }Mark(epoch);
    }
    }Mark(3);}catch(HRESULT error){Log("failure",nullptr,static_cast<DWORD>(error));result=2;}
    Log("process-complete",nullptr,result);if(!Close())result=3;return result;
}
