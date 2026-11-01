#include "redirect_map.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <algorithm>
#include <cctype>
#include <sstream>
#include <vector>

static std::wstring Trim(const std::wstring& value)
{
    size_t start = 0;
    while (start < value.size() && iswspace(value[start])) start++;
    size_t end = value.size();
    while (end > start && iswspace(value[end - 1])) end--;
    return value.substr(start, end - start);
}

static std::wstring ToLower(std::wstring value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
    return value;
}

static bool ParseIpv4(const std::wstring& text, uint32_t* outNetworkOrder)
{
    IN_ADDR addr{};
    std::string ascii(text.begin(), text.end());
    if (InetPtonA(AF_INET, ascii.c_str(), &addr) != 1) return false;
    *outNetworkOrder = addr.S_un.S_addr;
    return true;
}

bool ParseRedirectText(const std::wstring& text, RedirectTable* out, std::wstring* error)
{
    out->hostToIpv4.clear();
    std::wstringstream stream(text);
    std::wstring segment;
    while (std::getline(stream, segment, L';'))
    {
        segment = Trim(segment);
        if (segment.empty()) continue;
        const auto eq = segment.find(L'=');
        if (eq == std::wstring::npos || eq == 0)
        {
            if (error) *error = L"Expected host=ip segments separated by ';'.";
            return false;
        }
        auto host = ToLower(Trim(segment.substr(0, eq)));
        auto ipText = Trim(segment.substr(eq + 1));
        uint32_t ip = 0;
        if (host.empty() || !ParseIpv4(ipText, &ip))
        {
            if (error) *error = L"Invalid host=ip entry.";
            return false;
        }
        out->hostToIpv4[host] = ip;
    }
    return true;
}

std::wstring RedirectMapPathNextToModule(void* moduleHandle)
{
    wchar_t path[MAX_PATH]{};
    if (!GetModuleFileNameW(static_cast<HMODULE>(moduleHandle), path, MAX_PATH)) return L"";
    std::wstring file(path);
    const auto slash = file.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return L"A2Core.Redirect.map";
    return file.substr(0, slash + 1) + L"A2Core.Redirect.map";
}

bool RedirectTable::LoadFromEnvironment()
{
    wchar_t buffer[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"A2CORE_REDIRECT", buffer, static_cast<DWORD>(std::size(buffer)));
    if (length == 0 || length >= std::size(buffer)) return false;
    RedirectTable parsed{};
    std::wstring error;
    if (!ParseRedirectText(buffer, &parsed, &error)) return false;
    hostToIpv4 = std::move(parsed.hostToIpv4);
    return !hostToIpv4.empty();
}

bool RedirectTable::LoadFromFileNextToModule(void* moduleHandle)
{
    const auto path = RedirectMapPathNextToModule(moduleHandle);
    if (path.empty()) return false;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    std::vector<char> bytes;
    for (;;)
    {
        char chunk[4096];
        DWORD read = 0;
        if (!ReadFile(file, chunk, sizeof(chunk), &read, nullptr)) break;
        if (read == 0) break;
        bytes.insert(bytes.end(), chunk, chunk + read);
    }
    CloseHandle(file);
    if (bytes.empty()) return false;
    int wideLength = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(), static_cast<int>(bytes.size()), nullptr, 0);
    UINT codePage = CP_UTF8;
    DWORD flags = MB_ERR_INVALID_CHARS;
    if (wideLength <= 0)
    {
        codePage = CP_ACP;
        flags = 0;
        wideLength = MultiByteToWideChar(codePage, flags, bytes.data(), static_cast<int>(bytes.size()), nullptr, 0);
    }
    if (wideLength <= 0) return false;
    std::wstring text(static_cast<size_t>(wideLength), L'\0');
    MultiByteToWideChar(codePage, flags, bytes.data(), static_cast<int>(bytes.size()), text.data(), wideLength);
    RedirectTable parsed{};
    std::wstring error;
    if (!ParseRedirectText(text, &parsed, &error)) return false;
    hostToIpv4 = std::move(parsed.hostToIpv4);
    return !hostToIpv4.empty();
}

bool RedirectTable::ShouldRedirect(const wchar_t* host, uint32_t* outIpv4NetworkOrder) const
{
    if (!host || !*host) return false;
    const auto it = hostToIpv4.find(ToLower(host));
    if (it == hostToIpv4.end()) return false;
    *outIpv4NetworkOrder = it->second;
    return true;
}

bool RedirectTable::ShouldRedirectAscii(const char* host, uint32_t* outIpv4NetworkOrder) const
{
    if (!host || !*host) return false;
    int wideLength = MultiByteToWideChar(CP_UTF8, 0, host, -1, nullptr, 0);
    if (wideLength <= 0) return false;
    std::wstring wide(wideLength, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, host, -1, wide.data(), wideLength);
    if (!wide.empty() && wide.back() == L'\0') wide.pop_back();
    return ShouldRedirect(wide.c_str(), outIpv4NetworkOrder);
}
