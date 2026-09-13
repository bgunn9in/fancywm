#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string>
using BeginFn=DWORD(__cdecl*)(const wchar_t*,DWORD*);using ReadFn=DWORD(__cdecl*)(const wchar_t*,const void*,DWORD);using RegionsFn=DWORD(__cdecl*)(const wchar_t*);using EndFn=DWORD(__cdecl*)();
struct Block { uint64_t Address;DWORD Bytes,Kind,Index,Pattern; };
static_assert(sizeof(Block)==24,"owned block schema");
static bool Save(const std::wstring& path,const void* data,DWORD bytes){HANDLE f=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(f==INVALID_HANDLE_VALUE)return false;DWORD written=0;bool ok=WriteFile(f,data,bytes,&written,nullptr)&&written==bytes;ok=FlushFileBuffers(f)&&ok;CloseHandle(f);return ok;}
int wmain(int argc,wchar_t** argv){
    if(argc!=3||!CreateDirectoryW(argv[2],nullptr))return 1;SetErrorMode(3);auto lib=LoadLibraryW(argv[1]);if(!lib)return 2;
    auto begin=reinterpret_cast<BeginFn>(GetProcAddress(lib,"PssBegin"));auto read=reinterpret_cast<ReadFn>(GetProcAddress(lib,"PssRead"));auto regions=reinterpret_cast<RegionsFn>(GetProcAddress(lib,"PssCloneRegions"));auto end=reinterpret_cast<EndFn>(GetProcAddress(lib,"PssEnd"));if(!begin||!read||!regions||!end)return 3;
    std::wstring root=argv[2];int result=0;
    for(int epoch=0;epoch<2&&!result;epoch++){
        Block blocks[52]{};
        for(DWORD i=0;i<52;i++){
            DWORD size=i<16?32:i<32?4096:65536;void* pointer=i<48?HeapAlloc(GetProcessHeap(),0,size):VirtualAlloc(nullptr,size,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
            if(!pointer){result=4;break;}memset(pointer,0x20+i,size);blocks[i]={reinterpret_cast<uint64_t>(pointer),size,i<48?0u:1u,i,0x20+i};
        }
        wchar_t phase[32];swprintf_s(phase,L"%d",epoch);std::wstring folder=root+L"\\"+phase;DWORD clone=0;DWORD code=result?ERROR_NOT_ENOUGH_MEMORY:begin(folder.c_str(),&clone);
        if(!result&&code==0){
            if(!Save(folder+L"\\blocks.bin",blocks,sizeof(blocks)))result=5;
            for(int pass=0;pass<2&&!result;pass++){
                for(DWORD i=0;i<52;i++){
                    wchar_t file[64];swprintf_s(file,L"read-%d-%lu.bin",pass,i);if(read(file,reinterpret_cast<void*>(blocks[i].Address),blocks[i].Bytes)){result=6;break;}
                }
                wchar_t file[64];swprintf_s(file,L"clone-va-%d.bin",pass);if(regions(file))result=7;
                if(pass==0)for(auto& b:blocks){
                    memset(reinterpret_cast<void*>(b.Address),0xa0+b.Index,b.Bytes);
                    wchar_t live[64];swprintf_s(live,L"live-after-%lu.bin",b.Index);
                    if(!Save(folder+L"\\"+live,reinterpret_cast<void*>(b.Address),b.Bytes))result=14;
                }
            }
        } else if(!result)result=8;
        printf("{\"Ready\":true,\"Pid\":%lu,\"ClonePid\":%lu,\"Heap\":%llu,\"Epoch\":%d,\"CaptureCode\":%lu,\"Result\":%d}\n",GetCurrentProcessId(),clone,reinterpret_cast<uint64_t>(GetProcessHeap()),epoch,code,result);fflush(stdout);
        if(getchar()!='\n')result=9;
        if(code==0&&!result){
            for(DWORD i=0;i<52;i++){wchar_t file[64];swprintf_s(file,L"read-2-%lu.bin",i);if(read(file,reinterpret_cast<void*>(blocks[i].Address),blocks[i].Bytes)){result=12;break;}}
            if(regions(L"clone-va-2.bin"))result=13;
        }
        DWORD freed=end();if(freed&&!result)result=10;
        printf("{\"Released\":true,\"Pid\":%lu,\"ClonePid\":%lu,\"Epoch\":%d,\"FreeCode\":%lu,\"Result\":%d}\n",GetCurrentProcessId(),clone,epoch,freed,result);fflush(stdout);
        for(auto& b:blocks)if(b.Address){BOOL ok=b.Kind?VirtualFree(reinterpret_cast<void*>(b.Address),0,MEM_RELEASE):HeapFree(GetProcessHeap(),0,reinterpret_cast<void*>(b.Address));if(!ok&&!result)result=11;}
    }
    FreeLibrary(lib);return result;
}
