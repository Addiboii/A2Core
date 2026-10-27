# Run from a normal or Administrator PowerShell with AION2 already open.
# Does not elevate, launch/stop the game, change memory, or change system policy.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Install
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probe = Join-Path $PSScriptRoot 'inspect_live_build.py'
$processes = @(Get-Process -Name AION2 -ErrorAction Stop)
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$failed = 0
foreach ($aionProcess in $processes) {
    # The Python probe validates the executable path before inspecting memory.
    $output = Join-Path $workspace ('research/local/live-build-' + $stamp + '-' + $aionProcess.Id + '.json')
    & python $probe --pid $aionProcess.Id --install $Install --output $output
    if ($LASTEXITCODE -ne 0) { $failed++ }
    Write-Output "Process $($aionProcess.Id): report target $output"
}
if ($failed) {
    Write-Output "$failed of $($processes.Count) process probes failed. Read the reports; access denial does not establish marker absence."
    exit 1
}
