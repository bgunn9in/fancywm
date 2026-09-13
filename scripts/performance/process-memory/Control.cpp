#include "Resource9.h"
using namespace Resource9;
using StartFn=int(__cdecl*)(const wchar_t*);using SampleFn=int(__cdecl*)(int,int);using StopFn=int(__cdecl*)();
struct Block{
    void* value=nullptr;
    void Release(){if(value){BOOL ok=VirtualFree(value,0,MEM_RELEASE);Log("block-free",nullptr,reinterpret_cast<uintptr_t>(value),ok);if(!ok)InterlockedIncrement(&errors);value=nullptr;}}
    ~Block(){Release();}
};
int wmain(int argc,wchar_t** argv){SetErrorMode(3);if(argc!=2||!Start(L"fixture.jsonl"))return 1;int result=0;HMODULE dll=nullptr;StopFn stop=nullptr;bool started=false;
    try{dll=LoadLibraryW(argv[1]);Need(dll!=nullptr);auto start=reinterpret_cast<StartFn>(GetProcAddress(dll,"MemoryStart"));auto sample=reinterpret_cast<SampleFn>(GetProcAddress(dll,"MemorySample"));stop=reinterpret_cast<StopFn>(GetProcAddress(dll,"MemoryStop"));Need(start&&sample&&stop);Need(start(L"memory.jsonl")==0);started=true;
        for(int i=0;i<16;i++)Need(sample(-100,i)==0);
        for(int epoch=0;epoch<3;epoch++){phase=epoch;
            for(int cycle=0;cycle<50;cycle++){
                int key=epoch*10000+cycle*10;Log("cycle-begin",nullptr,cycle);Block blocks[4];
                auto observe=[&](int step){Log("sample-begin",nullptr,key+step,cycle);Need(sample(key+step,0)==0);Sleep(5);Need(sample(key+step,1)==0);Log("sample-end",nullptr,key+step,cycle);};
                observe(0);
                for(int i=0;i<4;i++){blocks[i].value=VirtualAlloc(nullptr,8*1024*1024,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);Need(blocks[i].value!=nullptr);memset(blocks[i].value,0x41+epoch,8*1024*1024);Log("block-created",nullptr,reinterpret_cast<uintptr_t>(blocks[i].value),8*1024*1024,i);}
                observe(1);for(int i=1;i<4;i++)blocks[i].Release();Log("one-block-held",nullptr,reinterpret_cast<uintptr_t>(blocks[0].value),cycle);observe(2);
                blocks[0].Release();Log("all-blocks-released",nullptr,cycle);observe(3);Log("cycle-complete",nullptr,cycle);
            }Log("epoch-complete",nullptr,epoch,50);
        }Need(stop()==0);started=false;
    }catch(HRESULT error){Log("failure",nullptr,static_cast<DWORD>(error));result=2;}
    if(started&&stop){int code=stop();Log("observer-failure-cleanup",nullptr,code);}
    if(dll){BOOL ok=FreeLibrary(dll);Log("observer-unloaded",nullptr,ok);if(!ok)result=3;}
    Log("process-complete",nullptr,result);if(!Close())result=4;return result;
}
