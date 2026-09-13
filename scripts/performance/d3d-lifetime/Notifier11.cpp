#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <stdio.h>
#include <stdint.h>
#include <vector>
#include <memory>
#include <thread>
#pragma comment(lib,"d3d11.lib")
static SRWLOCK logGate=SRWLOCK_INIT;static unsigned long long sequence;static LONG nextId;
struct Owner{LONG Id;const char* Kind;LONG Epoch,Cycle;volatile LONG Count=0;};
static std::vector<std::unique_ptr<Owner>> owners;
static void Row(const char* event,Owner* owner,uintptr_t value=0,uintptr_t extra=0){LARGE_INTEGER q{};QueryPerformanceCounter(&q);AcquireSRWLockExclusive(&logGate);printf("{\"Event\":\"%s\",\"Seq\":%llu,\"Pid\":%lu,\"Tid\":%lu,\"Qpc\":%lld,\"Id\":%ld,\"Kind\":\"%s\",\"Epoch\":%ld,\"Cycle\":%ld,\"Count\":%ld,\"Value\":%llu,\"Extra\":%llu}\n",event,++sequence,GetCurrentProcessId(),GetCurrentThreadId(),q.QuadPart,owner?owner->Id:0,owner?owner->Kind:"process",owner?owner->Epoch:-1,owner?owner->Cycle:-1,owner?owner->Count:0,static_cast<unsigned long long>(value),static_cast<unsigned long long>(extra));fflush(stdout);ReleaseSRWLockExclusive(&logGate);}
static void __stdcall Destroyed(void* state){auto owner=static_cast<Owner*>(state);InterlockedIncrement(&owner->Count);Row("destroyed",owner);}
static Owner* Track(IUnknown* resource,const char* kind,int epoch,int cycle){auto owner=std::make_unique<Owner>();owner->Id=InterlockedIncrement(&nextId);owner->Kind=kind;owner->Epoch=epoch;owner->Cycle=cycle;auto raw=owner.get();owners.push_back(std::move(owner));IUnknown* identity=nullptr;HRESULT hr=resource->QueryInterface(__uuidof(IUnknown),reinterpret_cast<void**>(&identity));if(FAILED(hr))throw hr;Row("created",raw,reinterpret_cast<uintptr_t>(identity));identity->Release();ID3DDestructionNotifier* notifier=nullptr;hr=resource->QueryInterface(__uuidof(ID3DDestructionNotifier),reinterpret_cast<void**>(&notifier));if(FAILED(hr))throw hr;UINT callback=0;hr=notifier->RegisterDestructionCallback(Destroyed,raw,&callback);notifier->Release();Row("registered",raw,static_cast<DWORD>(hr),callback);if(FAILED(hr))throw hr;return raw;}
static void Need(bool condition){if(!condition)throw E_FAIL;}
static void Done(Owner* owner){for(int i=0;owner->Count==0&&i<100;i++)Sleep(1);Need(owner->Count==1);}
static void Case(ID3D11Device* device,ID3D11DeviceContext* context,int epoch,int cycle,int scenario){
    ID3D11Buffer* buffer=nullptr;ID3D11ShaderResourceView* view=nullptr;bool mapped=false;ID3DDestructionNotifier* notifier=nullptr;bool extraRef=false;
    try{
        D3D11_BUFFER_DESC desc{};desc.ByteWidth=65536;desc.Usage=scenario==2?D3D11_USAGE_DEFAULT:D3D11_USAGE_DYNAMIC;desc.BindFlags=scenario==2?D3D11_BIND_SHADER_RESOURCE:D3D11_BIND_VERTEX_BUFFER;desc.CPUAccessFlags=scenario==2?0:D3D11_CPU_ACCESS_WRITE;if(scenario==2){desc.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;desc.StructureByteStride=16;}
        HRESULT hr=device->CreateBuffer(&desc,nullptr,&buffer);Need(SUCCEEDED(hr));Owner* owner=Track(buffer,"buffer",epoch,cycle);Row("scenario",owner,scenario);
        if(scenario!=2){for(int map=0;map<3;map++){D3D11_MAPPED_SUBRESOURCE data{};hr=context->Map(buffer,0,D3D11_MAP_WRITE_DISCARD,0,&data);Need(SUCCEEDED(hr));mapped=true;memset(data.pData,0x60+map,desc.ByteWidth);MEMORY_BASIC_INFORMATION memory{};Need(VirtualQuery(data.pData,&memory,sizeof(memory))==sizeof(memory));Row("mapped",owner,reinterpret_cast<uintptr_t>(data.pData),map);context->Unmap(buffer,0);mapped=false;Row("unmapped",owner,map);Need(owner->Count==0);}}
        if(scenario==0){Row("release-begin",owner);buffer->Release();buffer=nullptr;Row("release-end",owner);Done(owner);}
        else if(scenario==1){buffer->AddRef();extraRef=true;Row("retainer-acquired",owner);buffer->Release();extraRef=false;Row("original-released",owner);Need(owner->Count==0);std::thread worker([&]{Row("worker-release-begin",owner);buffer->Release();Row("worker-release-end",owner);});worker.join();buffer=nullptr;Done(owner);}
        else if(scenario==2){hr=device->CreateShaderResourceView(buffer,nullptr,&view);Need(SUCCEEDED(hr));Owner* viewOwner=Track(view,"view",epoch,cycle);Row("view-retains-resource",viewOwner,owner->Id);buffer->Release();buffer=nullptr;Row("original-released",owner);Need(owner->Count==0);Row("view-release-begin",viewOwner);view->Release();view=nullptr;Row("view-release-end",viewOwner);Done(viewOwner);Done(owner);}
        else if(scenario==3){UINT stride=16,offset=0;context->IASetVertexBuffers(0,1,&buffer,&stride,&offset);Row("context-retains-resource",owner);buffer->Release();buffer=nullptr;Row("original-released",owner);Need(owner->Count==0);Row("context-clear-begin",owner);context->ClearState();context->Flush();Row("context-clear-end",owner);Done(owner);}
        else{
            auto canceled=std::make_unique<Owner>();canceled->Id=InterlockedIncrement(&nextId);canceled->Kind="unregistered";canceled->Epoch=epoch;canceled->Cycle=cycle;auto state=canceled.get();owners.push_back(std::move(canceled));hr=buffer->QueryInterface(__uuidof(ID3DDestructionNotifier),reinterpret_cast<void**>(&notifier));Need(SUCCEEDED(hr));UINT id=0;hr=notifier->RegisterDestructionCallback(Destroyed,state,&id);Need(SUCCEEDED(hr));Row("cancel-register",state,owner->Id,id);hr=notifier->UnregisterDestructionCallback(id);Row("unregister",state,static_cast<DWORD>(hr),id);Need(SUCCEEDED(hr));notifier->Release();notifier=nullptr;Row("release-begin",owner);buffer->Release();buffer=nullptr;Row("release-end",owner);Done(owner);Need(state->Count==0);Row("canceled-settled",state);
        }
        Row("case-complete",owner,scenario);
    }catch(...){if(notifier)notifier->Release();if(mapped&&buffer)context->Unmap(buffer,0);context->ClearState();context->Flush();if(view)view->Release();if(buffer){if(extraRef)buffer->Release();buffer->Release();}throw;}
}
int main(){
    SetErrorMode(3);ID3D11Device* device=nullptr;ID3D11DeviceContext* context=nullptr;D3D_FEATURE_LEVEL feature{};int result=0;
    HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&feature,&context);if(FAILED(hr)){Row("device-failure",nullptr,static_cast<DWORD>(hr));return 1;}
    try{IDXGIDevice* dxgi=nullptr;IDXGIAdapter* adapter=nullptr;DXGI_ADAPTER_DESC desc{};Need(SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice),reinterpret_cast<void**>(&dxgi))));Need(SUCCEEDED(dxgi->GetAdapter(&adapter)));Need(SUCCEEDED(adapter->GetDesc(&desc)));Row("adapter",nullptr,(static_cast<uint64_t>(static_cast<DWORD>(desc.AdapterLuid.HighPart))<<32)|desc.AdapterLuid.LowPart,(static_cast<uint64_t>(desc.VendorId)<<32)|desc.DeviceId);adapter->Release();dxgi->Release();Row("device-ready",nullptr,feature);
        Owner* deviceOwner=Track(device,"device",-1,-1);Owner* contextOwner=Track(context,"context",-1,-1);
        for(int epoch=0;epoch<3;epoch++){for(int cycle=0;cycle<50;cycle++)Case(device,context,epoch,cycle,cycle%5);Row("epoch-complete",nullptr,epoch,50);Need(deviceOwner->Count==0&&contextOwner->Count==0);}
        Row("shutdown-begin",nullptr);context->ClearState();context->Flush();context->Release();context=nullptr;Row("external-context-released",contextOwner);Need(contextOwner->Count==0&&deviceOwner->Count==0);Row("device-release-begin",deviceOwner);device->Release();device=nullptr;Row("device-release-end",deviceOwner);Done(contextOwner);Done(deviceOwner);Row("shutdown-complete",nullptr);
    }catch(HRESULT error){Row("failure",nullptr,static_cast<DWORD>(error));result=2;}
    if(context){context->ClearState();context->Flush();context->Release();}if(device)device->Release();Row("process-complete",nullptr,result,owners.size());return result;
}
