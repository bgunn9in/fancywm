#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d9.h>
#include <d3dcommon.h>
#include <stdio.h>
#pragma comment(lib,"d3d9.lib")
#pragma comment(lib,"user32.lib")
static LONG callbacks;
static void __stdcall Destroyed(void*){InterlockedIncrement(&callbacks);}
static void Probe(IUnknown* object,const char* name){ID3DDestructionNotifier* notify=nullptr;HRESULT hr=object->QueryInterface(__uuidof(ID3DDestructionNotifier),reinterpret_cast<void**>(&notify));HRESULT reg=E_NOINTERFACE;UINT id=0;if(SUCCEEDED(hr)){reg=notify->RegisterDestructionCallback(Destroyed,nullptr,&id);notify->Release();}printf("{\"Probe\":\"%s\",\"QueryHResult\":%lu,\"RegisterHResult\":%lu,\"CallbackId\":%u}\n",name,static_cast<DWORD>(hr),static_cast<DWORD>(reg),id);fflush(stdout);}
int main(){
    SetErrorMode(3);wchar_t name[64];swprintf_s(name,L"FWM_OWNED_D3D9_%lu",GetCurrentProcessId());HDESK original=GetThreadDesktop(GetCurrentThreadId());HDESK desktop=CreateDesktopW(name,nullptr,nullptr,0,DESKTOP_CREATEWINDOW|DESKTOP_READOBJECTS|DESKTOP_WRITEOBJECTS,nullptr);if(!desktop||!SetThreadDesktop(desktop))return 1;
    WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=name;if(!RegisterClassW(&wc))return 2;HWND window=CreateWindowW(name,L"Owned D3D9 probe",WS_POPUP,0,0,8,8,nullptr,nullptr,wc.hInstance,nullptr);if(!window)return 3;
    IDirect3D9Ex* d3d=nullptr;HRESULT hr=Direct3DCreate9Ex(D3D_SDK_VERSION,&d3d);if(FAILED(hr))return 4;D3DPRESENT_PARAMETERS pp{};pp.Windowed=TRUE;pp.SwapEffect=D3DSWAPEFFECT_DISCARD;pp.hDeviceWindow=window;pp.BackBufferWidth=pp.BackBufferHeight=8;pp.BackBufferFormat=D3DFMT_A8R8G8B8;
    IDirect3DDevice9Ex* device=nullptr;hr=d3d->CreateDeviceEx(0,D3DDEVTYPE_HAL,window,D3DCREATE_SOFTWARE_VERTEXPROCESSING|D3DCREATE_MULTITHREADED|D3DCREATE_FPU_PRESERVE,&pp,nullptr,&device);printf("{\"Pid\":%lu,\"CreateDeviceHResult\":%lu,\"Hwnd\":%llu,\"PrivateDesktop\":true,\"PresentCalled\":false}\n",GetCurrentProcessId(),static_cast<DWORD>(hr),reinterpret_cast<unsigned long long>(window));fflush(stdout);if(FAILED(hr))return 5;
    Probe(d3d,"direct3d9ex");Probe(device,"device9ex");IDirect3DTexture9* texture=nullptr;hr=device->CreateTexture(64,64,1,D3DUSAGE_DYNAMIC,D3DFMT_A8R8G8B8,D3DPOOL_DEFAULT,&texture,nullptr);if(FAILED(hr))return 6;Probe(texture,"texture9");IDirect3DSurface9* surface=nullptr;hr=texture->GetSurfaceLevel(0,&surface);if(FAILED(hr))return 7;Probe(surface,"surface9");surface->Release();texture->Release();device->Release();d3d->Release();BOOL destroyed=DestroyWindow(window);BOOL unregistered=UnregisterClassW(name,wc.hInstance);BOOL restored=SetThreadDesktop(original);BOOL closed=CloseDesktop(desktop);
    printf("{\"Cleanup\":true,\"Callbacks\":%ld,\"HwndDestroyed\":%s,\"ClassUnregistered\":%s,\"OriginalDesktopRestored\":%s,\"OwnedDesktopClosed\":%s}\n",callbacks,destroyed?"true":"false",unregistered?"true":"false",restored?"true":"false",closed?"true":"false");return destroyed&&unregistered&&restored&&closed?0:8;
}
