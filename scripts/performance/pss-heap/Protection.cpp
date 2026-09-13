#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string>
using BeginFn=DWORD(__cdecl*)(const wchar_t*,DWORD*);using ReadFn=DWORD(__cdecl*)(const wchar_t*,const void*,DWORD);using RegionsFn=DWORD(__cdecl*)(const wchar_t*);using EndFn=DWORD(__cdecl*)();
struct Block { uint64_t Address;DWORD Bytes,Protect,Index,Pattern; };
static bool Save(const std::wstring& path,const void* data,DWORD bytes){HANDLE f=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(f==INVALID_HANDLE_VALUE)return false;DWORD n=0;BOOL ok=WriteFile(f,data,bytes,&n,nullptr)&&n==bytes;ok=FlushFileBuffers(f)&&ok;CloseHandle(f);return ok;}
int wmain(int argc,wchar_t** argv){
    if(argc!=3||!CreateDirectoryW(argv[2],nullptr))return 1;SetErrorMode(3);auto lib=LoadLibraryW(argv[1]);if(!lib)return 2;
    auto begin=reinterpret_cast<BeginFn>(GetProcAddress(lib,"PssBegin"));auto read=reinterpret_cast<ReadFn>(GetProcAddress(lib,"PssRead"));auto regions=reinterpret_cast<RegionsFn>(GetProcAddress(lib,"PssCloneRegions"));auto end=reinterpret_cast<EndFn>(GetProcAddress(lib,"PssEnd"));if(!begin||!read||!regions||!end)return 3;
    std::wstring root=argv[2];int result=0;
    for(int epoch=0;epoch<2&&!result;epoch++){
        Block blocks[12]{};DWORD protections[]={PAGE_READWRITE,PAGE_READONLY,PAGE_READWRITE|PAGE_WRITECOMBINE,PAGE_READWRITE|PAGE_NOCACHE};DWORD sizes[]={4096,65536,1048576};
        for(DWORD i=0;i<12;i++){
            DWORD protection=protections[i/3],size=sizes[i%3];void* pointer=VirtualAlloc(nullptr,size,MEM_RESERVE|MEM_COMMIT,protection==PAGE_READONLY?PAGE_READWRITE:protection);
            if(!pointer){result=4;break;}memset(pointer,0x30+i,size);blocks[i]={reinterpret_cast<uint64_t>(pointer),size,protection,i,0x30+i};
            if(protection==PAGE_READONLY){DWORD old;if(!VirtualProtect(pointer,size,protection,&old)){result=5;break;}}
        }
        wchar_t phase[32];swprintf_s(phase,L"%d",epoch);std::wstring folder=root+L"\\"+phase;DWORD clone=0;DWORD code=result?ERROR_NOT_ENOUGH_MEMORY:begin(folder.c_str(),&clone);
        if(!result&&code==0){
            if(!Save(folder+L"\\blocks.bin",blocks,sizeof(blocks)))result=6;
            for(DWORD i=0;i<12;i++){
                auto& b=blocks[i];wchar_t file[64];swprintf_s(file,L"clone-%lu.bin",i);DWORD readCode=read(file,reinterpret_cast<void*>(b.Address),b.Bytes);
                swprintf_s(file,L"live-%lu.bin",i);if(!Save(folder+L"\\"+file,reinterpret_cast<void*>(b.Address),b.Bytes))result=7;
                MEMORY_BASIC_INFORMATION info{};SIZE_T queried=VirtualQuery(reinterpret_cast<void*>(b.Address),&info,sizeof(info));
                printf("{\"Block\":true,\"Pid\":%lu,\"ClonePid\":%lu,\"Epoch\":%d,\"Index\":%lu,\"ReadCode\":%lu,\"LiveQueryBytes\":%zu,\"LiveState\":%lu,\"LiveType\":%lu,\"LiveProtect\":%lu}\n",GetCurrentProcessId(),clone,epoch,i,readCode,queried,info.State,info.Type,info.Protect);fflush(stdout);
            }
            if(regions(L"clone-va-0.bin")||regions(L"clone-va-1.bin"))result=8;
        }else if(!result)result=9;
        DWORD released=end();if(released&&!result)result=10;
        printf("{\"EpochComplete\":true,\"Pid\":%lu,\"ClonePid\":%lu,\"Epoch\":%d,\"CaptureCode\":%lu,\"ReleaseCode\":%lu,\"Result\":%d}\n",GetCurrentProcessId(),clone,epoch,code,released,result);fflush(stdout);
        for(auto& b:blocks)if(b.Address&&!VirtualFree(reinterpret_cast<void*>(b.Address),0,MEM_RELEASE)&&!result)result=11;
    }
    FreeLibrary(lib);return result;
}
