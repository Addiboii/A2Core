<#
Export every gameplay table (.dat under Data/) of your installed AION2 client to readable JSON with tools/WorldProbe,
so the table builders in tools/ (enchant_data.py, shop_data.py, ...) can be rerun and new ones written.

  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/dump_tables.ps1
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/dump_tables.ps1 -Filter '*Enchant*' -GameDir 'D:\SteamLibrary\steamapps\common\AION2'

Needs the .NET 10 SDK and, as for every extraction (README "Game data"), the client archive key that you provide
yourself in research/local/archive-key-validated.json ({"key_hex": "0x..."}), plus the usmap under
research/local/tooling/mappings/. The Oodle DLL is taken from the game folder.

Writes research/local/tables-<date>/: one <path under Data, '/' as '_'>.json per table (e.g. Item_Enchant.json),
index.json (table path -> file, status) and failures in index.json. Map data (Data/Map, Data/WorldMap) is skipped
unless -IncludeMaps (it is large). WorldProbe takes 64 paths per run, so this runs it in batches.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$KeyReport,
    [string]$Usmap,
    [string]$Oodle,
    [string]$Filter = '*',
    [switch]$IncludeMaps
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $workspace   # WorldProbe only writes under ./research/local
$local = Join-Path $workspace 'research\local'

function Find-GameDir {
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $steam = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue)
        foreach ($p in 'SteamPath', 'InstallPath') { if ($steam.$p) { $roots += $steam.$p } }
    }
    foreach ($root in @($roots | Sort-Object -Unique)) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        $libraries = @($root)
        if (Test-Path -LiteralPath $vdf) {
            $libraries += @(Select-String -LiteralPath $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' })
        }
        foreach ($lib in $libraries) {
            $common = Join-Path $lib 'steamapps\common'
            if (-not (Test-Path -LiteralPath $common)) { continue }
            $hit = Get-ChildItem -LiteralPath $common -Directory | Where-Object { $_.Name -like 'AION2*' -or $_.Name -like 'Aion 2*' } | Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }
    $process = Get-Process -Name AION2 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($process -and $process.Path) { return (Split-Path (Split-Path (Split-Path (Split-Path $process.Path)))) }
    return $null
}

if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir -or -not (Test-Path -LiteralPath $GameDir)) { throw 'AION2 install not found: pass -GameDir "<folder that contains the game>".' }
$paks = Get-ChildItem -LiteralPath $GameDir -Directory -Recurse -Filter 'Paks' | Select-Object -First 1
if (-not $paks) { throw "No Paks folder under $GameDir" }
if (-not $Oodle) { $Oodle = (Get-ChildItem -LiteralPath $GameDir -Recurse -File -Filter 'oo2core*.dll' | Select-Object -First 1).FullName }
if (-not $Oodle) { throw 'No oo2core*.dll in the game folder: pass -Oodle <path>.' }
if (-not $KeyReport) { $KeyReport = Join-Path $local 'archive-key-validated.json' }
if (-not (Test-Path -LiteralPath $KeyReport)) { throw "Archive key report missing: $KeyReport (see README, Game data)." }
if (-not $Usmap) { $Usmap = (Get-ChildItem -LiteralPath (Join-Path $local 'tooling\mappings') -Filter '*.usmap' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1).FullName }
if (-not $Usmap) { throw 'No usmap in research/local/tooling/mappings: pass -Usmap <path>.' }
Write-Output "Game: $GameDir`nPaks: $($paks.FullName)`nOodle: $Oodle`nUsmap: $Usmap"

Write-Output 'Building WorldProbe...'
& dotnet build (Join-Path $PSScriptRoot 'WorldProbe\WorldProbe.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'WorldProbe build failed (is the .NET 10 SDK installed?).' }
$probe = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'WorldProbe\bin\Release') -Recurse -Filter 'WorldProbe.dll' | Select-Object -First 1
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $local "tables-$stamp"
$work = Join-Path $local "tables-$stamp-work"
$null = New-Item -ItemType Directory -Path $out, $work

function Invoke-Probe([string[]]$Paths, [string]$Name) {
    $selection = Join-Path $work "$Name.json"
    ConvertTo-Json -InputObject @($Paths) | Set-Content -LiteralPath $selection -Encoding UTF8
    $dir = Join-Path $work $Name
    & dotnet $probe.FullName $paks.FullName $KeyReport $Oodle $Usmap $selection $dir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "WorldProbe failed on batch $Name" }
    return $dir
}

$inventoryDir = Invoke-Probe @() 'inventory'
$tables = @(Get-Content -LiteralPath (Join-Path $inventoryDir 'table-inventory.json') -Raw | ConvertFrom-Json) |
    Where-Object { ($IncludeMaps -or $_ -notmatch '/Data/(Map|WorldMap)/') -and ([IO.Path]::GetFileNameWithoutExtension($_) -like $Filter) }
$tables = @($tables | Sort-Object -Unique)
Write-Output "$($tables.Count) tables to export"

$index = [ordered]@{}
for ($start = 0; $start -lt $tables.Count; $start += 64) {
    $batch = @($tables[$start..([Math]::Min($start + 63, $tables.Count - 1))])
    $name = 'batch-{0:D3}' -f ($start / 64)
    Write-Output ("{0}: tables {1}-{2} of {3}" -f $name, ($start + 1), ($start + $batch.Count), $tables.Count)
    $dir = Invoke-Probe $batch $name
    $summary = Get-Content -LiteralPath (Join-Path $dir 'summary.json') -Raw | ConvertFrom-Json
    foreach ($report in $summary.packages) {
        $entry = [ordered]@{ status = $report.status }
        if ($report.file) {
            $relative = ($report.path -replace '^.*?/Data/', '') -replace '\.dat$', ''
            $file = ($relative -replace '[/\\]', '_') + '.json'
            Move-Item -LiteralPath (Join-Path $dir $report.file) -Destination (Join-Path $out $file)
            $entry.file = $file
        } elseif ($report.error) { $entry.error = ($report.error -split "`n")[0] }
        $index[$report.path] = $entry
    }
}
$index | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'index.json') -Encoding UTF8
Remove-Item -LiteralPath $work -Recurse -Force
$ok = @($index.Values | Where-Object { $_.file }).Count
Write-Output "Done: $ok of $($tables.Count) tables in $out"
Write-Output 'Copy that folder to the server machine under ~/aion/A2Core/research/local/ to build server data from it.'
