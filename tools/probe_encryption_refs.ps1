# Read-only targeted string/code-reference collection; no full memory dump.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Install,
    [string]$FollowupPlan
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probe = Join-Path $PSScriptRoot 'inspect_encryption_refs.py'
$processes = @(Get-Process -Name AION2 -ErrorAction Stop)
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$failed = 0
foreach ($aionProcess in $processes) {
    $output = Join-Path $workspace ('research/local/encryption-refs-' + $stamp + '-' + $aionProcess.Id + '.json')
    $probeArguments = @($probe, '--pid', $aionProcess.Id, '--install', $Install, '--output', $output)
    if ($FollowupPlan) { $probeArguments += @('--followup-plan', $FollowupPlan) }
    & python @probeArguments
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
if ($failed) { exit 1 }
