# A2Core Client Editor

Windows desktop tool (`editor/AionClientEditor`) for editing the supported AION2 Global client build (**Steam build 25767555**, manifest `1699273097574069541`) while keeping the shipped paks untouched. It mirrors the launcher stack (.NET 10, WinForms, single-file publish) and reuses the shared mount helper in `tools/AionClientProbe` (same AES + Oodle + usmap path as `tools/WorldProbe`).

## Requirements

- Windows PC with the A2Core client installed via the launcher (default Paks folder: `%LOCALAPPDATA%\<A2Core data folder>\AION2\Aion2\Content\Paks`).
- Your **archive key JSON** locally (`key_hex` field). Never commit this file.
- Oodle: `oo2core_9_win64.dll` or `oodle-data-shared.dll` (WorldProbe uses `research/local/tooling/oodle-2.9.16/oodle-data-shared.dll`). The open flow lets you pick the DLL; defaults are auto-detected from the repo checkout.
- Usmap: `research/local/tooling/mappings/*.usmap` (default `AION2-5.3.2.0-2.0.6.0.usmap`). Picked on open; last paths are stored in `%LOCALAPPDATA%\\<A2Core data folder>\\client-editor-settings.json`.

## Build & publish

```powershell
dotnet build editor/AionClientEditor.Core
dotnet build tests/Aion.ClientEditor.WinFormsBuild/Aion.ClientEditor.WinFormsBuild.csproj
dotnet test tests/Aion.ClientEditor.Tests --filter "FullyQualifiedName~Aion.ClientEditor"
dotnet publish editor/AionClientEditor -c Release -r win-x64 -o out/client-editor -p:DebugType=none
```

## Workflow

1. **Open Paks** — choose the client `Content\Paks` folder and your key JSON.
2. **Browse / filter** — tables under `AION2/Content/Data/…` and `L10NString.dat` per locale.
3. **Edit** — grid edits for L10N keys; data tables show row summaries (full struct editing continues in patch JSON + re-save pipeline).
4. **Save to patch** — writes encrypted on-disk bytes into `%LOCALAPPDATA%\<A2Core data folder>\client-patches\` (manifest + payloads). Base paks are not modified.
5. **Build patch pak** — writes `z_A2Core_Patch-Windows_P.pak` (+ empty `.sig` placeholder) into the selected Paks folder and mirrors to `%LOCALAPPDATA%\<A2Core data folder>\client-patches\deploy\` for the launcher.
6. **Export / import JSON** — share edits as `patch-project.json` (paths + payload file names; binary payloads stay alongside).
7. **Revert patch** — clears the local patch project.

### Tables commonly edited for private servers

| Path | Purpose |
|------|---------|
| `AION2/Content/Data/Table/ServerName.dat` | Display names for world/server selection (grid shows **RowId** from the table header ids plus one column per struct field; nested values as JSON in the details pane) |
| `AION2/Content/Data/Table/ServerList.dat` | Server list entries |
| `AION2/Content/Data/Table/RegionList.dat` | Region list at selection |
| `AION2/Content/Data/Table/CountryList.dat` | Country list at selection |
| `AION2/Content/Data/L10N/<locale>/L10NString.dat` | UI strings |

## Integrity checks and loading patches

### A2Core Launcher (documented behavior)

| Check | What it verifies | Patch impact |
|-------|------------------|--------------|
| **Sizes** (every launch) | Every file listed in `launcher/supported-build.json` exists with the expected **size** | **Extra** patch files (`z_A2Core_*`) are **not** in the manifest → **allowed** |
| **Quick hash** (when `.utoc`/`.sig`/exe timestamps change) | SHA-256 of `AION2.exe` and every `Aion2/Content/Paks/**/*.utoc` and `.sig` listed in the manifest | Patch pak adds **new** utoc/sig names → quick set unchanged → **allowed** |
| **Full verify** (player-initiated) | SHA-256 of **all** manifest files including `.ucas` | Unchanged base files pass; patch files are outside the manifest |

Modified **base** utoc/ucas/pak bytes **fail** quick/full checks — the editor never writes them.

**Launcher opt-in:** `launcher-settings.json` → `"AllowClientPatchPaks": true` copies `z_A2Core_Patch-Windows_P.*` from `%LOCALAPPDATA%\<A2Core data folder>\client-patches\deploy\` into the game Paks folder immediately before start (`ClientPatchSupport` in `Aion.Launcher.Core`).

### Game client (best-effort, not exhaustively reversed here)

- IoStore containers ship with `.sig` sidecars; `tools/extract_iostore.py` notes signatures are **not** verified during extraction.
- NCGuard / executable integrity beyond pak mounts was **not** fully reversed in this repo. Treat in-game rejection of unsigned patch paks as **possible** until validated on a live client.
- UE mount order loads later `pakchunk*` / `z_*` names after base chunks; a separate patch pak is the intended modding path (same as other UE titles).

## Serialization round-trip tests

`tests/Aion.ClientEditor.Tests` covers:

| Format | Round-trip |
|--------|------------|
| L10N plaintext (`version` 1 map) | read → write → read |
| L10N encrypted container (`uint` 2 + LZ4/AES) | encrypt → decrypt |
| Small `.dat` stream container (`uint` 13, ≤4 KiB, XorAES) | encrypt → decrypt |
| `MapHierarchy` binary | read → write → read |
| Patch project JSON | save → load |

Full encrypted **data table** property serialization round-trips require a mounted client + usmap and are validated manually after editing real tables.

## Shared probe library

`tools/AionClientProbe/AionClientSession.cs` centralizes:

- Oodle init
- `DefaultFileProvider` + `GAME_Aion2`
- AES key from JSON
- Usmap mappings

`tools/WorldProbe` now references this library instead of inlining the setup.
