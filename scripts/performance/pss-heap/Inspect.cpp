#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string>
#include <vector>
struct Entry { uint64_t Address,Bytes,Heap;DWORD Flags,Pid; };
int wmain(int argc,wchar_t** argv){
    if(argc!=4||!CreateDirectoryW(argv[3],nullptr))return 1;SetErrorMode(3);DWORD pid=wcstoul(argv[1],nullptr,10);uint64_t wanted=_wcstoui64(argv[2],nullptr,10);if(!pid||pid==GetCurrentProcessId()||!wanted)return 2;
    std::wstring root=argv[3];HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPHEAPLIST,pid);DWORD snapshotError=snapshot==INVALID_HANDLE_VALUE?GetLastError():0;DWORD listError=0,walkError=0;bool found=false;std::vector<Entry> entries;DWORD heaps=0;
    if(snapshot!=INVALID_HANDLE_VALUE){HEAPLIST32 heap{};heap.dwSize=sizeof(heap);BOOL more=Heap32ListFirst(snapshot,&heap);
        while(more){heaps++;if(heap.th32ProcessID!=pid){listError=ERROR_INVALID_DATA;break;}
            if(heap.th32HeapID==wanted){found=true;HEAPENTRY32 e{};e.dwSize=sizeof(e);BOOL has=Heap32First(&e,pid,heap.th32HeapID);
                while(has){entries.push_back({e.dwAddress,e.dwBlockSize,e.th32HeapID,e.dwFlags,e.th32ProcessID});if(entries.size()>1000000){walkError=ERROR_BUFFER_OVERFLOW;break;}e.dwSize=sizeof(e);has=Heap32Next(&e);}if(!walkError)walkError=GetLastError();}
            heap.dwSize=sizeof(heap);more=Heap32ListNext(snapshot,&heap);
        }
        if(!listError)listError=GetLastError();CloseHandle(snapshot);
    }
    HANDLE f=CreateFileW((root+L"\\heap-entries.bin").c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(f==INVALID_HANDLE_VALUE)return 3;DWORD written=0;BOOL saved=WriteFile(f,entries.data(),static_cast<DWORD>(entries.size()*sizeof(Entry)),&written,nullptr)&&written==entries.size()*sizeof(Entry);CloseHandle(f);
    printf("{\"InspectorPid\":%lu,\"TargetPid\":%lu,\"Heap\":%llu,\"SnapshotError\":%lu,\"ListError\":%lu,\"WalkError\":%lu,\"HeapFound\":%s,\"Heaps\":%lu,\"Entries\":%zu,\"Saved\":%s}\n",GetCurrentProcessId(),pid,wanted,snapshotError,listError,walkError,found?"true":"false",heaps,entries.size(),saved?"true":"false");
    return saved?0:4;
}
