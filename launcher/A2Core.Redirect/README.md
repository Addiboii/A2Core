# A2Core.Redirect (Windows x64)

Small ws2_32 DNS hook DLL injected by A2Core Launcher. Redirects the NC hostnames used for
NCGuard and platform calls to the private server IPv4. Everything else passes through unchanged.

## Configuration

The DLL loads a hostname map on attach (first match wins):

1. Environment variable `A2CORE_REDIRECT` — `host=ip;host=ip` (semicolon-separated).
2. Otherwise `A2Core.Redirect.map` in the same folder as the DLL (same format, UTF-8).

The launcher writes the map file next to the copied DLL under
`%LOCALAPPDATA%\\A2CorePrivate\\` and copies or embeds `A2Core.Redirect.dll` beside the published
launcher exe.

## Build on Linux (cross-compile, x64)

Used by `deploy/linux/build_launcher.sh` on the server. Requires `mingw-w64`, `cmake`, and `git`
(MinHook sources are fetched at configure time; MIT license).

```bash
launcher/A2Core.Redirect/build-mingw.sh
# -> build/a2core-redirect-mingw/A2Core.Redirect.dll
```

Toolchain: `toolchain-mingw-w64.cmake` (`x86_64-w64-mingw32-g++`, static `libgcc`/`libstdc++`).
The script checks the PE with `x86_64-w64-mingw32-objdump` / `nm` (ws2_32 import, `DllMain`).

## Build on Windows (native MSVC)

Requires CMake 3.20+, Visual Studio 2022 or Build Tools, and Git.

```powershell
cmake -S launcher/A2Core.Redirect -B build/a2core-redirect -A x64
cmake --build build/a2core-redirect --config Release
copy build\a2core-redirect\Release\A2Core.Redirect.dll build\a2core-redirect-mingw\
```

Publish the launcher after the DLL exists at `build/a2core-redirect-mingw/A2Core.Redirect.dll`
(so `dotnet publish` embeds it into `A2Core Launcher.exe`).

## Hooks

MinHook detours in `ws2_32.dll`:

- `GetAddrInfoW`, `GetAddrInfoExW` (synchronous path only), `getaddrinfo`, `gethostbyname`

## Verification status

In-game behaviour with NCGuard while redirected by this DLL is **not yet verified** on a live
client; the legacy hosts-file path remains available via `redirectMode: "hosts"`.
