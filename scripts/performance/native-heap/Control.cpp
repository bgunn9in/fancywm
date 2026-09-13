#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
int wmain(int argc, wchar_t** argv) {
    if ((argc != 3 && argc != 4) || !CreateDirectoryW(argv[2], nullptr)) return 1;
    auto module = LoadLibraryW(argv[1]); if (!module) return 2;
    auto control = reinterpret_cast<int(__cdecl*)(const wchar_t*)>(GetProcAddress(module, "HeapControl"));
    if (!control) return 3;
    if (argc == 4) { printf("{\"Ready\":true,\"Pid\":%lu}\n", GetCurrentProcessId()); fflush(stdout); if (getchar() != '\n') return 4; }
    int result = control(argv[2]);
    printf("{\"Pid\":%lu,\"Result\":%d,\"PrivateAndDefaultHeap\":true,\"Processes\":1}\n", GetCurrentProcessId(), result);
    fflush(stdout);
    if (argc == 4 && getchar() != '\n') return 5;
    FreeLibrary(module); return result;
}
