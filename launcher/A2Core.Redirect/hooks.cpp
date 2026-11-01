#include "redirect_map.h"

#include <MinHook.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#pragma comment(lib, "ws2_32.lib")

static RedirectTable g_table;
static HMODULE g_self = nullptr;
static bool g_hooksInstalled = false;

using GetAddrInfoWFn = INT(WINAPI*)(PCWSTR, PCWSTR, const ADDRINFOW*, PADDRINFOW*);
using GetAddrInfoExWFn = INT(WINAPI*)(PCWSTR, PCWSTR, DWORD, LPGUID, const ADDRINFOEXW*, PADDRINFOEXW*, time_t*, LPOVERLAPPED, LPLOOKUPSERVICE_COMPLETION_ROUTINE, LPHANDLE);
using GetAddrInfoFn = INT(WINAPI*)(PCSTR, PCSTR, const ADDRINFOA*, PADDRINFOA*);
using GethostbynameFn = hostent*(WINAPI*)(const char*);

static GetAddrInfoWFn Real_GetAddrInfoW = nullptr;
static GetAddrInfoExWFn Real_GetAddrInfoExW = nullptr;
static GetAddrInfoFn Real_getaddrinfo = nullptr;
static GethostbynameFn Real_gethostbyname = nullptr;

static INT MakeAddrInfoW(uint32_t ipv4NetworkOrder, PCWSTR canonicalName, const ADDRINFOW* hints, PADDRINFOW* result)
{
    auto* info = static_cast<ADDRINFOW*>(calloc(1, sizeof(ADDRINFOW)));
    auto* addr = static_cast<sockaddr_in*>(calloc(1, sizeof(sockaddr_in)));
    if (!info || !addr)
    {
        free(info);
        free(addr);
        return WSANO_RECOVERY;
    }
    info->ai_family = hints && hints->ai_family != AF_UNSPEC ? hints->ai_family : AF_INET;
    if (info->ai_family != AF_INET)
    {
        free(info);
        free(addr);
        return WSATYPE_NOT_FOUND;
    }
    info->ai_socktype = hints ? hints->ai_socktype : SOCK_STREAM;
    info->ai_protocol = hints ? hints->ai_protocol : IPPROTO_TCP;
    info->ai_addrlen = sizeof(sockaddr_in);
    info->ai_addr = reinterpret_cast<sockaddr*>(addr);
    addr->sin_family = AF_INET;
    addr->sin_addr.S_un.S_addr = ipv4NetworkOrder;
    if (canonicalName)
    {
        const size_t bytes = (wcslen(canonicalName) + 1) * sizeof(wchar_t);
        info->ai_canonname = static_cast<PWSTR>(malloc(bytes));
        if (info->ai_canonname) memcpy(info->ai_canonname, canonicalName, bytes);
    }
    *result = info;
    return 0;
}

static INT MakeAddrInfoExW(uint32_t ipv4NetworkOrder, PCWSTR canonicalName, const ADDRINFOEXW* hints, PADDRINFOEXW* result)
{
    auto* info = static_cast<ADDRINFOEXW*>(calloc(1, sizeof(ADDRINFOEXW)));
    auto* addr = static_cast<sockaddr_in*>(calloc(1, sizeof(sockaddr_in)));
    if (!info || !addr)
    {
        free(info);
        free(addr);
        return WSANO_RECOVERY;
    }
    info->ai_family = hints && hints->ai_family != AF_UNSPEC ? hints->ai_family : AF_INET;
    if (info->ai_family != AF_INET)
    {
        free(info);
        free(addr);
        return WSATYPE_NOT_FOUND;
    }
    info->ai_socktype = hints ? hints->ai_socktype : SOCK_STREAM;
    info->ai_protocol = hints ? hints->ai_protocol : IPPROTO_TCP;
    info->ai_addrlen = sizeof(sockaddr_in);
    info->ai_addr = reinterpret_cast<sockaddr*>(addr);
    addr->sin_family = AF_INET;
    addr->sin_addr.S_un.S_addr = ipv4NetworkOrder;
    *result = info;
    return 0;
}

static INT MakeAddrInfoA(uint32_t ipv4NetworkOrder, PCSTR canonicalName, const ADDRINFOA* hints, PADDRINFOA* result)
{
    auto* info = static_cast<ADDRINFOA*>(calloc(1, sizeof(ADDRINFOA)));
    auto* addr = static_cast<sockaddr_in*>(calloc(1, sizeof(sockaddr_in)));
    if (!info || !addr)
    {
        free(info);
        free(addr);
        return WSANO_RECOVERY;
    }
    info->ai_family = hints && hints->ai_family != AF_UNSPEC ? hints->ai_family : AF_INET;
    if (info->ai_family != AF_INET)
    {
        free(info);
        free(addr);
        return WSATYPE_NOT_FOUND;
    }
    info->ai_socktype = hints ? hints->ai_socktype : SOCK_STREAM;
    info->ai_protocol = hints ? hints->ai_protocol : IPPROTO_TCP;
    info->ai_addrlen = sizeof(sockaddr_in);
    info->ai_addr = reinterpret_cast<sockaddr*>(addr);
    addr->sin_family = AF_INET;
    addr->sin_addr.S_un.S_addr = ipv4NetworkOrder;
    if (canonicalName)
    {
        const size_t bytes = strlen(canonicalName) + 1;
        info->ai_canonname = static_cast<PSTR>(malloc(bytes));
        if (info->ai_canonname) memcpy(info->ai_canonname, canonicalName, bytes);
    }
    *result = info;
    return 0;
}

static hostent* MakeHostent(uint32_t ipv4NetworkOrder, const char* name)
{
    thread_local static char canonical[256];
    thread_local static char* aliases[1] = {nullptr};
    thread_local static in_addr addr;
    thread_local static char* addresses[2];
    thread_local static hostent entry;

    strncpy_s(canonical, name, _TRUNCATE);
    addr.S_un.S_addr = ipv4NetworkOrder;
    addresses[0] = reinterpret_cast<char*>(&addr);
    addresses[1] = nullptr;
    entry.h_name = canonical;
    entry.h_aliases = aliases;
    entry.h_addrtype = AF_INET;
    entry.h_length = static_cast<int>(sizeof(in_addr));
    entry.h_addr_list = addresses;
    return &entry;
}

static INT WINAPI Hook_GetAddrInfoW(PCWSTR node, PCWSTR service, const ADDRINFOW* hints, PADDRINFOW* result)
{
    uint32_t ip = 0;
    if (node && g_table.ShouldRedirect(node, &ip)) return MakeAddrInfoW(ip, node, hints, result);
    return Real_GetAddrInfoW(node, service, hints, result);
}

static INT WINAPI Hook_GetAddrInfoExW(PCWSTR name, PCWSTR service, DWORD namespaceId, LPGUID provider, const ADDRINFOEXW* hints,
    PADDRINFOEXW* result, time_t* timeout, LPOVERLAPPED overlapped, LPLOOKUPSERVICE_COMPLETION_ROUTINE completion, LPHANDLE nameHandle)
{
    uint32_t ip = 0;
    if (name && overlapped == nullptr && completion == nullptr && g_table.ShouldRedirect(name, &ip))
        return MakeAddrInfoExW(ip, name, hints, result);
    return Real_GetAddrInfoExW(name, service, namespaceId, provider, hints, result, timeout, overlapped, completion, nameHandle);
}

static INT WINAPI Hook_getaddrinfo(PCSTR node, PCSTR service, const ADDRINFOA* hints, PADDRINFOA* result)
{
    uint32_t ip = 0;
    if (node && g_table.ShouldRedirectAscii(node, &ip)) return MakeAddrInfoA(ip, node, hints, result);
    return Real_getaddrinfo(node, service, hints, result);
}

static hostent* WINAPI Hook_gethostbyname(const char* name)
{
    uint32_t ip = 0;
    if (name && g_table.ShouldRedirectAscii(name, &ip)) return MakeHostent(ip, name);
    return Real_gethostbyname(name);
}

static bool InstallHooks()
{
    if (MH_Initialize() != MH_OK) return false;
    if (MH_CreateHookApi(L"ws2_32", "GetAddrInfoW", reinterpret_cast<LPVOID>(&Hook_GetAddrInfoW), reinterpret_cast<LPVOID*>(&Real_GetAddrInfoW)) != MH_OK) return false;
    if (MH_CreateHookApi(L"ws2_32", "GetAddrInfoExW", reinterpret_cast<LPVOID>(&Hook_GetAddrInfoExW), reinterpret_cast<LPVOID*>(&Real_GetAddrInfoExW)) != MH_OK) return false;
    if (MH_CreateHookApi(L"ws2_32", "getaddrinfo", reinterpret_cast<LPVOID>(&Hook_getaddrinfo), reinterpret_cast<LPVOID*>(&Real_getaddrinfo)) != MH_OK) return false;
    if (MH_CreateHookApi(L"ws2_32", "gethostbyname", reinterpret_cast<LPVOID>(&Hook_gethostbyname), reinterpret_cast<LPVOID*>(&Real_gethostbyname)) != MH_OK) return false;
    return MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
}

extern "C" BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        g_self = module;
        if (!g_table.LoadFromEnvironment()) g_table.LoadFromFileNextToModule(module);
        if (g_table.hostToIpv4.empty()) return TRUE;
        g_hooksInstalled = InstallHooks();
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        if (g_hooksInstalled)
        {
            MH_DisableHook(MH_ALL_HOOKS);
            MH_Uninitialize();
        }
    }
    return TRUE;
}
