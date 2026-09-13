#pragma once
#include "Resource9.h"
struct Device9 {
    HDESK original=nullptr,desktop=nullptr;HWND window=nullptr;IDirect3D9Ex* d3d=nullptr;IDirect3DDevice9Ex* device=nullptr;wchar_t name[96]{};bool registered=false;
    explicit Device9(bool privateDesktop,bool hardwareVertexProcessing=false){try{using namespace Resource9;swprintf_s(name,L"FWM_OWNED_D3D9_%lu_%llu",GetCurrentProcessId(),GetTickCount64());original=GetThreadDesktop(GetCurrentThreadId());
        if(privateDesktop){desktop=CreateDesktopW(name,nullptr,nullptr,0,DESKTOP_CREATEWINDOW|DESKTOP_READOBJECTS|DESKTOP_WRITEOBJECTS,nullptr);Need(desktop&&SetThreadDesktop(desktop));}
        wchar_t actual[256];DWORD bytes=0;Need(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,actual,sizeof(actual),&bytes)&&wcsncmp(actual,L"FWM_OWNED_",10)==0);
        WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=name;Need(RegisterClassW(&wc)!=0);registered=true;window=CreateWindowW(name,L"Owned D3D resource fixture",WS_POPUP,0,0,8,8,nullptr,nullptr,wc.hInstance,nullptr);Need(window!=nullptr);
        Need(SUCCEEDED(Direct3DCreate9Ex(D3D_SDK_VERSION,&d3d)));D3DPRESENT_PARAMETERS pp{};pp.Windowed=TRUE;pp.SwapEffect=D3DSWAPEFFECT_DISCARD;pp.hDeviceWindow=window;pp.BackBufferWidth=pp.BackBufferHeight=8;pp.BackBufferFormat=D3DFMT_A8R8G8B8;
        HRESULT hr=d3d->CreateDeviceEx(0,D3DDEVTYPE_HAL,window,(hardwareVertexProcessing?D3DCREATE_HARDWARE_VERTEXPROCESSING:D3DCREATE_SOFTWARE_VERTEXPROCESSING)|D3DCREATE_MULTITHREADED|D3DCREATE_FPU_PRESERVE,&pp,nullptr,&device);Log("device9-create",nullptr,static_cast<DWORD>(hr),reinterpret_cast<uintptr_t>(window),hardwareVertexProcessing);Need(SUCCEEDED(hr));LUID luid{};Need(SUCCEEDED(d3d->GetAdapterLUID(0,&luid)));Log("adapter9",nullptr,(static_cast<uint64_t>(static_cast<DWORD>(luid.HighPart))<<32)|luid.LowPart);
    }catch(...){Cleanup();throw;}}
    void Cleanup(){using namespace Resource9;if(device){Log("device9-release-begin");device->Release();device=nullptr;Log("device9-release-end");}if(d3d){d3d->Release();d3d=nullptr;}bool ok=true;if(window){ok=DestroyWindow(window)&&ok;window=nullptr;}if(registered){ok=UnregisterClassW(name,GetModuleHandleW(nullptr))&&ok;registered=false;}if(desktop){ok=SetThreadDesktop(original)&&ok;ok=CloseDesktop(desktop)&&ok;desktop=nullptr;}Log("device9-fixture-cleanup",nullptr,ok);if(!ok)InterlockedIncrement(&errors);}
    ~Device9(){Cleanup();}
};
