#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <string>
int wmain(int argc, wchar_t** argv)
{
    if (argc != 4) return 21;
    wchar_t desktop[256]{}; DWORD needed = 0;
    if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UOI_NAME, desktop, sizeof(desktop), &needed)
        || std::wstring(desktop) != argv[2] || std::wstring(desktop).find(L"FWM_OWNED_") != 0) return 22;
    std::filesystem::path output(argv[1]);
    if (std::filesystem::exists(output) || !std::filesystem::create_directory(output)) return 23;
    wchar_t dll[32768]{}; if (!GetEnvironmentVariableW(L"FWM_GUI_TRACE_DLL", dll, 32768)) return 24;
    HMODULE module = LoadLibraryW(dll); if (!module) return 25;
    auto start = reinterpret_cast<int(*)(const wchar_t*)>(GetProcAddress(module, "GuiTraceStart"));
    auto control = reinterpret_cast<int(*)()>(GetProcAddress(module, "GuiTraceControl"));
    auto close = reinterpret_cast<int(*)()>(GetProcAddress(module, "GuiTraceClose"));
    if (!start || !control || !close) return 26;
    int code = start((output / "gui-api.jsonl").c_str());
    if (!code) code = control();
    int closeCode = close(); if (!code) code = closeCode;
    std::ofstream result(output / "summary.json");
    result << "{\"Verdict\":\"" << (code ? "FAIL" : "PASS") << "\",\"ControlCode\":" << code
           << ",\"Pid\":" << GetCurrentProcessId() << ",\"GdiFlag\":" << GR_GDIOBJECTS
           << ",\"UserFlag\":" << GR_USEROBJECTS << ",\"Hwnds\":[],\"SurvivingHwnds\":[],\"NativeCalibrationOnly\":true}";
    return code;
}
