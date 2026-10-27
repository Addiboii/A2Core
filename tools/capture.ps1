# Short entry point to avoid escaped ampersands/underscores when copying commands.
[CmdletBinding()]
param(
    [ValidateRange(15, 7200)][int]$DurationSeconds = 300,
    [string]$ServerAddress,
    [switch]$MovementCues,
    [switch]$MultiLayer,
    [switch]$InGame,
    [ValidateRange(64, 4096)][int]$MaxSizeMb = 128,
    [switch]$PlanOnly
)
$PSBoundParameters['DurationSeconds'] = $DurationSeconds
& (Join-Path $PSScriptRoot 'capture_port13700.ps1') @PSBoundParameters
