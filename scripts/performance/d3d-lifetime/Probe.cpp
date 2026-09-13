#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <stdio.h>
#pragma comment(lib,"d3d11.lib")
static LONG callbacks;
static void __stdcall Destroyed(void*) { InterlockedIncrement(&callbacks); }
static void Probe(IUnknown* object,const char* name){
    ID3DDestructionNotifier* notify=nullptr;HRESULT hr=object->QueryInterface(__uuidof(ID3DDestructionNotifier),reinterpret_cast<void**>(&notify));UINT id=0;HRESULT reg=E_NOINTERFACE;
    if(SUCCEEDED(hr)){reg=notify->RegisterDestructionCallback(Destroyed,nullptr,&id);notify->Release();}
    printf("{\"Probe\":\"%s\",\"QueryHResult\":%lu,\"RegisterHResult\":%lu,\"CallbackId\":%u}\n",name,static_cast<DWORD>(hr),static_cast<DWORD>(reg),id);fflush(stdout);
}
int main(){
    SetErrorMode(3);ID3D11Device* device=nullptr;ID3D11DeviceContext* context=nullptr;D3D_FEATURE_LEVEL feature{};
    HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&feature,&context);if(FAILED(hr)){printf("{\"DeviceFailure\":%lu}\n",static_cast<DWORD>(hr));return 1;}
    IDXGIDevice* dxgi=nullptr;IDXGIAdapter* adapter=nullptr;DXGI_ADAPTER_DESC desc{};
    hr=device->QueryInterface(__uuidof(IDXGIDevice),reinterpret_cast<void**>(&dxgi));if(FAILED(hr))return 2;hr=dxgi->GetAdapter(&adapter);if(FAILED(hr))return 3;hr=adapter->GetDesc(&desc);if(FAILED(hr))return 4;
    printf("{\"Device\":true,\"Pid\":%lu,\"HardwareRequested\":true,\"FeatureLevel\":%u,\"VendorId\":%u,\"DeviceId\":%u,\"AdapterLuidLow\":%lu,\"AdapterLuidHigh\":%ld}\n",GetCurrentProcessId(),static_cast<unsigned>(feature),desc.VendorId,desc.DeviceId,desc.AdapterLuid.LowPart,desc.AdapterLuid.HighPart);fflush(stdout);adapter->Release();dxgi->Release();
    Probe(device,"device");Probe(context,"context");ID3D11Buffer* buffer=nullptr;D3D11_BUFFER_DESC bd{};bd.ByteWidth=65536;bd.Usage=D3D11_USAGE_DYNAMIC;bd.BindFlags=D3D11_BIND_VERTEX_BUFFER;bd.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
    hr=device->CreateBuffer(&bd,nullptr,&buffer);if(FAILED(hr))return 5;Probe(buffer,"buffer");buffer->Release();context->ClearState();context->Flush();context->Release();device->Release();
    printf("{\"Cleanup\":true,\"Callbacks\":%ld}\n",callbacks);return 0;
}
