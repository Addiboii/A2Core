# Tests textual and compiled-constant candidates against Pak hashes; read-only.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Install)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probe = Join-Path $PSScriptRoot 'probe_archive_keys.py'
$processes = @(Get-Process -Name AION2 -ErrorAction Stop)
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$failed = 0
foreach ($aionProcess in $processes) {
    $output = Join-Path $workspace ('research/local/archive-keys-' + $stamp + '-' + $aionProcess.Id + '.json')
    & python $probe --pid $aionProcess.Id --install $Install --output $output
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
if ($failed) { exit 1 }
