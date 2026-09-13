#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string>
using BeginFn=DWORD(__cdecl*)(const wchar_t*,DWORD*);using ReadFn=DWORD(__cdecl*)(const wchar_t*,const void*,DWORD);using RegionsFn=DWORD(__cdecl*)(const wchar_t*);using EndFn=DWORD(__cdecl*)();
static bool Save(const std::wstring& path,const char* data){HANDLE f=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(f==INVALID_HANDLE_VALUE)return false;DWORD n=0,b=static_cast<DWORD>(strlen(data));BOOL ok=WriteFile(f,data,b,&n,nullptr)&&n==b;ok=FlushFileBuffers(f)&&ok;ok=CloseHandle(f)&&ok;return ok;}
int wmain(int argc,wchar_t** argv){if(argc!=2||!CreateDirectoryW(L"shared",nullptr))return 1;SetErrorMode(3);HMODULE dll=LoadLibraryW(argv[1]);if(!dll)return 2;auto begin=reinterpret_cast<BeginFn>(GetProcAddress(dll,"PssBegin"));auto read=reinterpret_cast<ReadFn>(GetProcAddress(dll,"PssRead"));auto regions=reinterpret_cast<RegionsFn>(GetProcAddress(dll,"PssCloneRegions"));auto end=reinterpret_cast<EndFn>(GetProcAddress(dll,"PssEnd"));if(!begin||!read||!regions||!end)return 3;int result=0;
    for(int epoch=0;epoch<2&&!result;epoch++){
        HANDLE section[2]{};void* view[2]{};void* ordinary=VirtualAlloc(nullptr,65536,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);if(!ordinary)return 4;memset(ordinary,0x41+epoch,65536);
        for(int i=0;i<2;i++){DWORD protect=i?PAGE_EXECUTE_READWRITE:PAGE_READWRITE;section[i]=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,protect|SEC_RESERVE,0,65536,nullptr);if(!section[i]){result=5;break;}view[i]=MapViewOfFile(section[i],FILE_MAP_ALL_ACCESS|(i?FILE_MAP_EXECUTE:0),0,0,65536);if(!view[i]||!VirtualAlloc(view[i],4096,MEM_COMMIT,protect)){result=6;break;}memset(view[i],0x50+epoch+i,4096);}
        wchar_t name[32];swprintf_s(name,L"shared\\%d",epoch);DWORD clone=0;DWORD captured=result?ERROR_INVALID_STATE:begin(name,&clone);if(!result&&captured)result=7;
        DWORD before=ERROR_INVALID_STATE,after=ERROR_INVALID_STATE,probe0=ERROR_INVALID_STATE,probe1=ERROR_INVALID_STATE;
        LARGE_INTEGER commitBegin{},commitEnd{};
        if(!result){before=regions(L"clone-va-0.bin");probe0=read(L"private-before.bin",ordinary,65536);QueryPerformanceCounter(&commitBegin);
            for(int i=0;i<2;i++){auto next=static_cast<unsigned char*>(view[i])+4096;if(!VirtualAlloc(next,16384,MEM_COMMIT,i?PAGE_EXECUTE_READWRITE:PAGE_READWRITE)){result=8;break;}memset(next,0x70+epoch+i,16384);}memset(ordinary,0x61+epoch,65536);QueryPerformanceCounter(&commitEnd);
            after=regions(L"clone-va-1.bin");probe1=read(L"private-after.bin",ordinary,65536);
        }
        DWORD released=captured==0?end():ERROR_INVALID_STATE;if(before||after||probe0||probe1||released)result=result?result:9;
        char json[1400];sprintf_s(json,"{\"Pid\":%lu,\"ClonePid\":%lu,\"Epoch\":%d,\"Private\":%llu,\"SharedReadWrite\":%llu,\"SharedExecuteReadWrite\":%llu,\"Bytes\":65536,\"CommitOffset\":4096,\"CommitBytes\":16384,\"CommitBegin\":%lld,\"CommitEnd\":%lld,\"CaptureCode\":%lu,\"BeforeQuery\":%lu,\"AfterQuery\":%lu,\"BeforeRead\":%lu,\"AfterRead\":%lu,\"ReleaseCode\":%lu,\"Result\":%d,\"CloneExplicitlyResumed\":false,\"ExecutableBytesExecuted\":false}",GetCurrentProcessId(),clone,epoch,reinterpret_cast<unsigned long long>(ordinary),reinterpret_cast<unsigned long long>(view[0]),reinterpret_cast<unsigned long long>(view[1]),commitBegin.QuadPart,commitEnd.QuadPart,captured,before,after,probe0,probe1,released,result);if(!Save(std::wstring(name)+L"\\witness.json",json))result=10;
        for(int i=0;i<2;i++){if(view[i]&&!UnmapViewOfFile(view[i]))result=11;if(section[i]&&!CloseHandle(section[i]))result=12;}if(!VirtualFree(ordinary,0,MEM_RELEASE))result=13;
        printf("{\"Pid\":%lu,\"Epoch\":%d,\"Result\":%d,\"OwnedViewsUnmapped\":%s}\n",GetCurrentProcessId(),epoch,result,result==0?"true":"false");fflush(stdout);
    }FreeLibrary(dll);return result;
}
