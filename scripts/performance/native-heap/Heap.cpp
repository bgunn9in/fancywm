#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>

// Diagnostic only. No CRT, allocation, logging or managed callback while the
// default heap is locked. The fixed output buffer comes from VirtualAlloc.
// Never walk a third-party private heap returned by GetProcessHeaps: that
// handle can be destroyed concurrently. Only our control owns a private heap.
struct Header {
    char Magic[8]; uint32_t Schema, Pid, Tid, Count;
    uint64_t Heap, Begin, End;
    uint32_t WalkError, Locked, Unlocked, Overflow;
};
struct Entry { uint64_t Address; uint32_t Bytes; uint16_t Flags; uint8_t Overhead, Region; };
static_assert(sizeof(Header) == 64 && sizeof(Entry) == 16, "Raw schema");
static int Snapshot(HANDLE heap, const wchar_t* path) {
    constexpr DWORD capacity = 1024 * 1024;
    auto entries = static_cast<Entry*>(VirtualAlloc(nullptr, capacity * sizeof(Entry), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!entries) return 10;
    HANDLE file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) { VirtualFree(entries, 0, MEM_RELEASE); return 11; }
    Header h = {{'F','W','M','H','E','A','P','1'}, 1, GetCurrentProcessId(), GetCurrentThreadId(), 0,
        reinterpret_cast<uint64_t>(heap), 0, 0, 0, 0, 0, 0};
    LARGE_INTEGER qpc;
    QueryPerformanceCounter(&qpc); h.Begin = qpc.QuadPart;
    h.Locked = HeapLock(heap);
    if (h.Locked) {
        PROCESS_HEAP_ENTRY e = {};
        while (HeapWalk(heap, &e)) {
            if (h.Count == capacity) { h.Overflow = 1; break; }
            entries[h.Count++] = {reinterpret_cast<uint64_t>(e.lpData), e.cbData, e.wFlags, e.cbOverhead, e.iRegionIndex};
        }
        h.WalkError = GetLastError();
        h.Unlocked = HeapUnlock(heap);
    } else h.WalkError = GetLastError();
    QueryPerformanceCounter(&qpc); h.End = qpc.QuadPart;
    DWORD written;
    bool ok = WriteFile(file, &h, sizeof(h), &written, nullptr) && written == sizeof(h);
    DWORD bytes = h.Count * sizeof(Entry);
    ok = ok && WriteFile(file, entries, bytes, &written, nullptr) && written == bytes;
    ok = FlushFileBuffers(file) && ok;
    ok = CloseHandle(file) && ok;
    ok = VirtualFree(entries, 0, MEM_RELEASE) && ok;
    return ok && h.Locked && h.Unlocked && !h.Overflow && h.WalkError == ERROR_NO_MORE_ITEMS ? 0 : 12;
}
extern "C" __declspec(dllexport) int __cdecl HeapSnapshot(const wchar_t* path) {
    return Snapshot(GetProcessHeap(), path);
}
extern "C" __declspec(dllexport) int __cdecl HeapControl(const wchar_t* directory) {
    // Control uses exact pointer/size witnesses, not incidental total heap size.
    wchar_t path[32768];
    auto snap = [&](HANDLE heap, const wchar_t* name) {
        if (swprintf_s(path, L"%s\\%s.bin", directory, name) < 0) return 20;
        return Snapshot(heap, path);
    };
    HANDLE privateHeap = HeapCreate(0, 0, 0);
    if (!privateHeap) return 21;
    int result = 0;
    for (int kind = 0; kind != 2 && !result; ++kind) {
        HANDLE heap = kind == 0 ? privateHeap : GetProcessHeap();
        const wchar_t* prefix = kind == 0 ? L"private" : L"default";
        wchar_t phase[80]; void* blocks[24] = {};
        swprintf_s(phase, L"%s-baseline", prefix); result = snap(heap, phase);
        for (int i = 0; i != 24 && !result; ++i) {
            blocks[i] = HeapAlloc(heap, HEAP_ZERO_MEMORY, 4096 + i * 256);
            if (!blocks[i]) result = 22;
        }
        swprintf_s(phase, L"%s-held", prefix); if (!result) result = snap(heap, phase);
        swprintf_s(path, L"%s\\%s-held-pointers.bin", directory, prefix);
        HANDLE heldFile = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        DWORD heldWritten = 0;
        if (heldFile == INVALID_HANDLE_VALUE) result = 28;
        else { if (!WriteFile(heldFile, blocks, sizeof(blocks), &heldWritten, nullptr) || heldWritten != sizeof(blocks)) result = 29; CloseHandle(heldFile); }
        for (int i = 0; i != 24 && !result; ++i) {
            void* moved = HeapReAlloc(heap, HEAP_ZERO_MEMORY, blocks[i], 16384 + i * 256);
            if (!moved) result = 23; else blocks[i] = moved;
        }
        swprintf_s(phase, L"%s-resized", prefix); if (!result) result = snap(heap, phase);
        // Record actual final pointers before release, with an exclusive file.
        swprintf_s(path, L"%s\\%s-pointers.bin", directory, prefix);
        HANDLE f = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        DWORD written = 0;
        if (f == INVALID_HANDLE_VALUE) result = 24;
        else { if (!WriteFile(f, blocks, sizeof(blocks), &written, nullptr) || written != sizeof(blocks)) result = 25; CloseHandle(f); }
        for (int i = 0; i != 24; ++i) if (blocks[i] && !HeapFree(heap, 0, blocks[i])) result = 26;
        swprintf_s(phase, L"%s-released", prefix); if (!result) result = snap(heap, phase);
    }
    if (!HeapDestroy(privateHeap)) result = 27;
    return result;
}
