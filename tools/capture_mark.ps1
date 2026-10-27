# Timestamp a note into the newest recording's actions.jsonl (no elevation needed):
#   powershell.exe -NoProfile -File ./tools/capture_mark.ps1 "boss engaged"
# The note is a manual cue: its time is when it was typed, not when the thing happened in the game.
[CmdletBinding()]
param([Parameter(Mandatory = $true, Position = 0)][string]$Note)
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$directory = Get-ChildItem -LiteralPath (Join-Path $workspace 'research\local') -Directory -Filter 'capture-13700-*' |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $directory) { throw 'No recording directory under research/local.' }
$record = [ordered]@{ kind = 'manual_note'; note = $Note; cue_utc = [DateTime]::UtcNow.ToString('o') }
$record | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $directory.FullName 'actions.jsonl') -Encoding UTF8
Write-Output ("Noted in " + $directory.Name + ": " + $Note)
