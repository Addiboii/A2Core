<#
.SYNOPSIS
Stops the AION2 server started by start.ps1 -Background, and its database.

.DESCRIPTION
  powershell -ExecutionPolicy Bypass -File deploy\windows\stop.ps1
A server running in its own window (start.ps1 without -Background) is stopped with Ctrl+C in that window; run this
afterwards to stop the database too.

.PARAMETER Root
Where the server was installed (default C:\AionServer).
.PARAMETER KeepDatabase
Stop only the server, leave the database running.
#>
param([string]$Root, [switch]$KeepDatabase)
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = 'C:\AionServer' }
$Root = [IO.Path]::GetFullPath($Root)

$pidFile = Join-Path $Root 'state\host.pid'
if (Test-Path $pidFile) {
    $process = Get-Process -Id ([int](Get-Content $pidFile)) -ErrorAction SilentlyContinue
    # Only our own host: the id may have been reused by another program since.
    if ($process -and $process.ProcessName -eq 'dotnet') {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(15000) | Out-Null
        Write-Host "Server stopped (process $($process.Id))."
    }
    Remove-Item $pidFile -Force
}
if ($KeepDatabase) { exit 0 }
$tool = Join-Path $Root 'src\tools\postgres_dev.py'
if ((Test-Path $tool) -and (Test-Path (Join-Path $Root 'postgres\data\PG_VERSION'))) {
    $python = 'python'
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { $python = 'py' }
    $env:AION_PG_HOME = Join-Path $Root 'postgres'
    & $python $tool stop
    Write-Host 'Database stopped.'
}
