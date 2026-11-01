#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>

struct RedirectTable
{
    std::unordered_map<std::wstring, uint32_t> hostToIpv4; // network byte order in uint32

    bool LoadFromEnvironment();
    bool LoadFromFileNextToModule(void* moduleHandle);
    bool ShouldRedirect(const wchar_t* host, uint32_t* outIpv4NetworkOrder) const;
    bool ShouldRedirectAscii(const char* host, uint32_t* outIpv4NetworkOrder) const;
};

bool ParseRedirectText(const std::wstring& text, RedirectTable* out, std::wstring* error);
std::wstring RedirectMapPathNextToModule(void* moduleHandle);
