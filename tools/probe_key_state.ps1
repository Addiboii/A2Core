# Read bounded state from anchored encryption-lookup globals; never call game code.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Install)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probe = Join-Path $PSScriptRoot 'inspect_key_state.py'
$plan = Join-Path $workspace 'research/local/key-state-plan.json'
$processes = @(Get-Process -Name AION2 -ErrorAction Stop)
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$failed = 0
foreach ($aionProcess in $processes) {
    $output = Join-Path $workspace ('research/local/key-state-' + $stamp + '-' + $aionProcess.Id + '.json')
    & python $probe --pid $aionProcess.Id --install $Install --plan $plan --output $output
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
if ($failed) { exit 1 }
