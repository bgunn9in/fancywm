#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <tdh.h>
#include <filesystem>
#include <fstream>
#include <vector>
#include <string>
static GUID provider{ 0x8c416c79,0xd49b,0x4f01,{0xa4,0x67,0xe5,0x6d,0x3a,0xa8,0x23,0x4c} };
static std::string text(const wchar_t* value) { std::string r; for (; *value; ++value) { if (*value == '"' || *value == '\\') r += '\\'; r += *value < 128 ? (char)*value : '?'; } return r; }
int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 1;
    std::filesystem::path root(argv[1]); if (exists(root) || !create_directory(root)) return 2;
    std::ofstream output(root / "schema.json"); output << "[";
    for (USHORT id = 452; id <= 458; ++id) {
        EVENT_DESCRIPTOR descriptor{ id,0,16,4,(UCHAR)(id <= 454 ? id-452+28 : id-455+28),(USHORT)(id<=454 ? 443 : 442),(ULONGLONG)(id<=454 ? 0x8000020000000000 : 0x8000010000000000) };
        ULONG size = 0; ULONG code = TdhGetManifestEventInformation(&provider, &descriptor, nullptr, &size);
        if (code != ERROR_INSUFFICIENT_BUFFER) return (int)code;
        std::vector<BYTE> data(size); auto info = (TRACE_EVENT_INFO*)data.data();
        code = TdhGetManifestEventInformation(&provider, &descriptor, info, &size); if (code) return code;
        std::ofstream raw(root / ("event-" + std::to_string(id) + ".tdh"), std::ios::binary); raw.write((char*)data.data(), size);
        output << (id>452 ? "," : "") << "{\"Id\":" << id << ",\"Task\":" << info->EventDescriptor.Task << ",\"Opcode\":" << (int)info->EventDescriptor.Opcode << ",\"Properties\":[";
        for (ULONG i=0; i<info->TopLevelPropertyCount; ++i) {
            auto& p=info->EventPropertyInfoArray[i];
            output << (i ? "," : "") << "{\"Name\":\"" << text((wchar_t*)(data.data()+p.NameOffset)) << "\",\"Flags\":" << p.Flags
                << ",\"InType\":" << p.nonStructType.InType << ",\"OutType\":" << p.nonStructType.OutType << ",\"Count\":" << p.count << ",\"Length\":" << p.length << "}";
        }
        output << "]}";
    }
    output << "]"; return 0;
}
