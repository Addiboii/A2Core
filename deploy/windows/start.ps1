<#
.SYNOPSIS
Starts the AION2 server installed by install.ps1: the database, then the host.

.DESCRIPTION
  powershell -ExecutionPolicy Bypass -File deploy\windows\start.ps1
Keeps running in this window and shows the server's log; press Ctrl+C to stop the server (the database keeps running;
stop.ps1 stops both). With -Background it returns at once and the server runs hidden (stop it with stop.ps1); its log
then goes to <Root>\state\logs\host.log.

.PARAMETER Root
Where the server was installed (default C:\AionServer).
.PARAMETER Background
Start hidden and return.
#>
param([string]$Root, [switch]$Background)
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = 'C:\AionServer' }
$Root = [IO.Path]::GetFullPath($Root)
function Fail([string]$message) { Write-Host $message -ForegroundColor Red; exit 1 }

$Config = Join-Path $Root 'config'; $State = Join-Path $Root 'state'
$hostDll = Join-Path $Root 'host\Aion.Host.dll'
$envFile = Join-Path $Config 'aion.env'
if (-not (Test-Path $hostDll) -or -not (Test-Path $envFile)) { Fail "No server installed in $Root. Run deploy\windows\install.ps1 first." }

$pidFile = Join-Path $State 'host.pid'
if (Test-Path $pidFile) {
    $running = Get-Process -Id ([int](Get-Content $pidFile)) -ErrorAction SilentlyContinue
    if ($running -and $running.ProcessName -eq 'dotnet') { Fail "The server is already running (process $($running.Id)). Stop it with stop.ps1 first." }
    Remove-Item $pidFile -Force
}

# Settings: aion.env, then your own local.env (later lines win), as the Linux service reads them.
foreach ($file in $envFile, (Join-Path $Config 'local.env')) {
    if (-not (Test-Path $file)) { continue }
    foreach ($line in Get-Content $file -Encoding UTF8) {
        if ($line -match '^\s*#' -or $line -notmatch '^([^=\s]+)=(.*)$') { continue }
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
    }
}

# A port another program holds stops the server at once: say which.
$ports = 443, 3700, 8087, 8091, 8780, 13328
$busy = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $ports -contains $_.LocalPort })
if ($busy) {
    foreach ($entry in ($busy | Sort-Object LocalPort -Unique)) {
        $owner = Get-Process -Id $entry.OwningProcess -ErrorAction SilentlyContinue
        Write-Host "Port $($entry.LocalPort) is in use by $($owner.ProcessName) (process $($entry.OwningProcess))." -ForegroundColor Red
    }
    Fail 'Close those programs (port 443 is often a web server, VMware or Skype), then start again.'
}

$python = 'python'
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { $python = 'py' }
$env:AION_PG_HOME = Join-Path $Root 'postgres'
& $python (Join-Path $Root 'src\tools\postgres_dev.py') start
if ($LASTEXITCODE -ne 0) { Fail "The database did not start. See $(Join-Path $Root 'postgres\server.log')." }

$address = [Environment]::GetEnvironmentVariable('Aion__Network__PublicAddress', 'Process')
$staff = [Environment]::GetEnvironmentVariable('Aion__Web__StaffPath', 'Process')
Write-Host "Account page: https://${address}:8780    Staff dashboard: https://${address}:8780$staff"
if ($Background) {
    $logs = Join-Path $State 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $process = Start-Process dotnet -ArgumentList "`"$hostDll`"" -WorkingDirectory (Split-Path $hostDll) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'host.log') -RedirectStandardError (Join-Path $logs 'host-errors.log')
    Set-Content -Path $pidFile -Value $process.Id -Encoding ascii
    Write-Host "Server started in the background (process $($process.Id)). Log: $(Join-Path $logs 'host.log'). Stop it with stop.ps1."
    exit 0
}
Write-Host 'Server starting. Leave this window open while you play; press Ctrl+C to stop.' -ForegroundColor Green
Push-Location (Split-Path $hostDll)
try { & dotnet $hostDll } finally { Pop-Location }
