#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
struct Pair { uint64_t Before,After,Locked,BeforeUnlock,AfterUnlock,Pointer; DWORD Tid,Size; BOOL CompletedWhileLocked,LockPassed,UnlockPassed,FreePassed; };
static Pair Rows[24];
static HANDLE Start,Entered,Done;
static uint64_t Qpc() { LARGE_INTEGER value; QueryPerformanceCounter(&value); return value.QuadPart; }
static DWORD WINAPI Worker(void*) {
    for (int i=0;i<24;i++) {
        if(WaitForSingleObject(Start,30000)!=WAIT_OBJECT_0)return 10;
        Pair& row=Rows[i]; row.Tid=GetCurrentThreadId(); row.Before=Qpc(); SetEvent(Entered);
        row.Pointer=reinterpret_cast<uint64_t>(HeapAlloc(GetProcessHeap(),0,row.Size));
        row.After=Qpc(); SetEvent(Done);
    }
    return 0;
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=2||!CreateDirectoryW(argv[1],nullptr))return 1;
    SetErrorMode(3);
    wchar_t path[32768]; if(swprintf_s(path,L"%s\\pairs.bin",argv[1])<0)return 2;
    HANDLE file=CreateFileW(path,GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(file==INVALID_HANDLE_VALUE)return 3;
    Start=CreateEventW(nullptr,FALSE,FALSE,nullptr);Entered=CreateEventW(nullptr,FALSE,FALSE,nullptr);Done=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    if(!Start||!Entered||!Done)return 4;
    for(int i=0;i<24;i++)Rows[i].Size=i<12?32:65536;
    HANDLE worker=CreateThread(nullptr,0,Worker,nullptr,0,nullptr);if(!worker)return 5;
    printf("{\"Ready\":true,\"Pid\":%lu,\"PairBytes\":%zu}\n",GetCurrentProcessId(),sizeof(Pair));fflush(stdout);
    if(getchar()!='\n')return 6;
    int result=0;
    for(int i=0;i<24&&!result;i++) {
        Pair& row=Rows[i];row.LockPassed=HeapLock(GetProcessHeap());row.Locked=Qpc();
        if(!row.LockPassed){result=7;break;}
        SetEvent(Start);DWORD entered=WaitForSingleObject(Entered,10000);
        Sleep(100);row.CompletedWhileLocked=WaitForSingleObject(Done,0)==WAIT_OBJECT_0;
        row.BeforeUnlock=Qpc();row.UnlockPassed=HeapUnlock(GetProcessHeap());row.AfterUnlock=Qpc();
        if(entered!=WAIT_OBJECT_0||!row.UnlockPassed){result=8;break;}
        if(!row.CompletedWhileLocked&&WaitForSingleObject(Done,10000)!=WAIT_OBJECT_0){result=9;break;}
        if(!row.Pointer){result=11;break;}
        row.FreePassed=HeapFree(GetProcessHeap(),0,reinterpret_cast<void*>(row.Pointer));if(!row.FreePassed)result=12;
    }
    DWORD written=0;BOOL saved=WriteFile(file,Rows,sizeof(Rows),&written,nullptr)&&written==sizeof(Rows);FlushFileBuffers(file);CloseHandle(file);
    if(result==0&&WaitForSingleObject(worker,10000)!=WAIT_OBJECT_0)result=13;
    DWORD workerCode=0;GetExitCodeThread(worker,&workerCode);CloseHandle(worker);CloseHandle(Start);CloseHandle(Entered);CloseHandle(Done);
    printf("{\"Done\":true,\"Pid\":%lu,\"Result\":%d,\"Saved\":%s,\"WorkerExit\":%lu,\"Rows\":24,\"PairBytes\":%zu}\n",GetCurrentProcessId(),result,saved?"true":"false",workerCode,sizeof(Pair));fflush(stdout);
    if(getchar()!='\n')return 14;return result?result:saved&&workerCode==0?0:15;
}
static_assert(sizeof(Pair)==72,"fixed boundary schema");
