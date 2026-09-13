#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <stdint.h>
#include <stdio.h>
#include <string>
#pragma comment(lib,"d3d11.lib")
using BeginFn=DWORD(__cdecl*)(const wchar_t*,DWORD*);using ReadFn=DWORD(__cdecl*)(const wchar_t*,const void*,DWORD);using RegionsFn=DWORD(__cdecl*)(const wchar_t*);using EndFn=DWORD(__cdecl*)();
struct Block { uint64_t Address;DWORD Bytes,Protect,Index,Pattern; };
static bool Save(const std::wstring& path,const void* data,DWORD bytes){HANDLE f=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(f==INVALID_HANDLE_VALUE)return false;DWORD n=0;BOOL ok=WriteFile(f,data,bytes,&n,nullptr)&&n==bytes;ok=FlushFileBuffers(f)&&ok;CloseHandle(f);return ok;}
int wmain(int argc,wchar_t** argv){
    if(argc!=3||!CreateDirectoryW(argv[2],nullptr))return 1;SetErrorMode(3);auto lib=LoadLibraryW(argv[1]);if(!lib)return 2;
    auto begin=reinterpret_cast<BeginFn>(GetProcAddress(lib,"PssBegin"));auto read=reinterpret_cast<ReadFn>(GetProcAddress(lib,"PssRead"));auto regions=reinterpret_cast<RegionsFn>(GetProcAddress(lib,"PssCloneRegions"));auto end=reinterpret_cast<EndFn>(GetProcAddress(lib,"PssEnd"));if(!begin||!read||!regions||!end)return 3;
    ID3D11Device* device=nullptr;ID3D11DeviceContext* context=nullptr;D3D_FEATURE_LEVEL feature{};
    HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&feature,&context);
    printf("{\"Device\":true,\"Pid\":%lu,\"HResult\":%lu,\"FeatureLevel\":%u,\"HardwareRequested\":true,\"SwapchainCreated\":false}\n",GetCurrentProcessId(),static_cast<DWORD>(hr),static_cast<unsigned>(feature));fflush(stdout);if(FAILED(hr))return 4;
    std::wstring root=argv[2];int result=0;
    for(int epoch=0;epoch<2&&!result;epoch++){
        Block blocks[3]{};ID3D11Buffer* buffers[3]{};bool mapped[3]{};DWORD sizes[]={4096,65536,1048576};
        for(DWORD i=0;i<3;i++){
            D3D11_BUFFER_DESC desc{};desc.ByteWidth=sizes[i];desc.Usage=D3D11_USAGE_DYNAMIC;desc.BindFlags=D3D11_BIND_VERTEX_BUFFER;desc.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
            hr=device->CreateBuffer(&desc,nullptr,&buffers[i]);if(FAILED(hr)){result=5;break;}
            D3D11_MAPPED_SUBRESOURCE map{};hr=context->Map(buffers[i],0,D3D11_MAP_WRITE_DISCARD,0,&map);if(FAILED(hr)){result=6;break;}mapped[i]=true;
            memset(map.pData,0x50+i,sizes[i]);MEMORY_BASIC_INFORMATION info{};if(VirtualQuery(map.pData,&info,sizeof(info))!=sizeof(info)){result=7;break;}
            blocks[i]={reinterpret_cast<uint64_t>(map.pData),sizes[i],info.Protect,i,0x50+i};
        }
        wchar_t phase[32];swprintf_s(phase,L"%d",epoch);std::wstring folder=root+L"\\"+phase;DWORD clone=0;DWORD code=result?ERROR_NOT_ENOUGH_MEMORY:begin(folder.c_str(),&clone);
        if(!result&&code==0){
            if(!Save(folder+L"\\blocks.bin",blocks,sizeof(blocks)))result=8;
            for(DWORD i=0;i<3;i++){
                auto& b=blocks[i];wchar_t file[64];swprintf_s(file,L"clone-%lu.bin",i);DWORD readCode=read(file,reinterpret_cast<void*>(b.Address),b.Bytes);
                swprintf_s(file,L"live-%lu.bin",i);if(!Save(folder+L"\\"+file,reinterpret_cast<void*>(b.Address),b.Bytes))result=9;
                MEMORY_BASIC_INFORMATION info{};SIZE_T queried=VirtualQuery(reinterpret_cast<void*>(b.Address),&info,sizeof(info));
                printf("{\"Block\":true,\"Pid\":%lu,\"ClonePid\":%lu,\"Epoch\":%d,\"Index\":%lu,\"ReadCode\":%lu,\"LiveQueryBytes\":%zu,\"LiveState\":%lu,\"LiveType\":%lu,\"LiveProtect\":%lu}\n",GetCurrentProcessId(),clone,epoch,i,readCode,queried,info.State,info.Type,info.Protect);fflush(stdout);
            }
            if(regions(L"clone-va-0.bin")||regions(L"clone-va-1.bin"))result=10;
        }else if(!result)result=11;
        DWORD released=end();if(released&&!result)result=12;
        printf("{\"EpochComplete\":true,\"Pid\":%lu,\"ClonePid\":%lu,\"Epoch\":%d,\"CaptureCode\":%lu,\"ReleaseCode\":%lu,\"Result\":%d}\n",GetCurrentProcessId(),clone,epoch,code,released,result);fflush(stdout);
        for(DWORD i=0;i<3;i++){if(mapped[i])context->Unmap(buffers[i],0);if(buffers[i])buffers[i]->Release();}
    }
    context->ClearState();context->Flush();ULONG contextRefs=context->Release();ULONG deviceRefs=device->Release();
    printf("{\"D3DCleanup\":true,\"Pid\":%lu,\"ContextReleaseCount\":%lu,\"DeviceReleaseCount\":%lu,\"Result\":%d}\n",GetCurrentProcessId(),contextRefs,deviceRefs,result);fflush(stdout);
    FreeLibrary(lib);return result;
}
