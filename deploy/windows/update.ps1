<#
.SYNOPSIS
  Updates an installed A2Core (Windows 10 22H2 / 11) from a source checkout: backup, build, checks, restart (the Windows twin of
  tools/redeploy.sh).

.DESCRIPTION
  1. A database dump into C:\ProgramData\A2Core\backups (as the nightly task).
  2. dotnet publish of the host for win-x64 (needs the .NET 10 SDK) into a staging folder.
  3. The checks (tests/Aion.Native.Checks); a failure stops here and the running server is not touched (-SkipChecks to skip).
  4. Stop the service, apply new database migrations (database\migrations not yet in public.schema_migrations, as
     tools/postgres_dev.py migrate does on Linux), copy the new build over, start it, wait for the ports.
  5. The post-deploy health check (health-check.ps1: ports, handshake probes, database, no crash or error burst in the first
     60 s). Report only: its result is printed and written to <InstallDir>\logs\health-check.json; it does not roll back.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File deploy\windows\update.ps1 -Source C:\A2Core\src
  powershell -ExecutionPolicy Bypass -File deploy\windows\update.ps1 -Source C:\A2Core\src -CheckMigrations   # dry run, rolled back
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $InstallDir = ((Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\A2Core").ImagePath -replace '^"?(.*)\\host\\Aion\.Host\.exe"?$', '$1'),
    [string] $DataDir = (Join-Path $env:ProgramData "A2Core"),
    [switch] $SkipChecks,
    [switch] $SkipHealthCheck,   # no post-deploy health check (deploy\windows\health-check.ps1, report only)
    [switch] $CheckMigrations,  # only run the pending migrations inside a transaction that is rolled back, then exit
    [switch] $UpdateGateway     # also rebuild and restart the gateway (install-gateway.ps1); this one disconnects players
)
$ErrorActionPreference = "Stop"
function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# Pending migrations as one psql script (the Linux tools/postgres_dev.py migration_script): one transaction under an advisory
# lock as aion_owner; an applied migration whose file changed stops everything; each new one is applied and recorded.
function Invoke-Migrations([bool] $DryRun) {
    $psql = Join-Path $InstallDir "pgsql\bin\psql.exe"
    $cred = Get-Content (Join-Path $InstallDir "postgres\credentials.json") -Raw | ConvertFrom-Json
    $port = 55432
    $envFile = Join-Path $InstallDir "aion.env"
    if ((Test-Path $envFile) -and ((Get-Content $envFile -Raw) -match 'ConnectionStrings__Aion=[^\r\n]*Port=(\d+)')) { $port = [int]$Matches[1] }
    function Literal($text) { "'" + ($text -replace "'", "''") + "'" }
    $lines = @("\set ON_ERROR_STOP 1", "BEGIN; SELECT pg_advisory_xact_lock(731924001); SET LOCAL ROLE aion_owner;",
        "CREATE TABLE IF NOT EXISTS public.schema_migrations(version text PRIMARY KEY, sha256 text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());")
    foreach ($file in Get-ChildItem (Join-Path $Source "database\migrations") -Filter *.sql | Sort-Object Name) {
        $version = Literal $file.Name
        $digest = Literal ((Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
        $lines += "DO `$check`$ BEGIN IF EXISTS(SELECT FROM public.schema_migrations WHERE version=$version AND sha256<>$digest) THEN RAISE EXCEPTION 'Applied migration changed: %', $version; END IF; END `$check`$;"
        $lines += "SELECT NOT EXISTS(SELECT FROM public.schema_migrations WHERE version=$version) AS apply \gset"
        $lines += "\if :apply"
        $lines += "\echo applying $($file.Name)"
        $lines += [IO.File]::ReadAllText($file.FullName)
        $lines += "INSERT INTO public.schema_migrations(version,sha256) VALUES($version,$digest);"
        $lines += "\endif"
    }
    $lines += $(if ($DryRun) { "ROLLBACK;" } else { "COMMIT;" })
    $script = Join-Path $env:TEMP ("a2core-migrate-" + (Get-Date -Format "yyyyMMddHHmmss") + ".sql")
    [IO.File]::WriteAllText($script, ($lines -join "`n"), (New-Object Text.UTF8Encoding $false))
    $env:PGPASSWORD = $cred.aion_admin
    # psql writes notices to stderr; under PowerShell 5.1 with ErrorActionPreference Stop that would abort, so judge by its exit code.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $psql -h 127.0.0.1 -p $port -U aion_admin -d aion -q -At -f $script 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue; Remove-Item $script -ErrorAction SilentlyContinue
    }
    $output | ForEach-Object { Write-Host "   $_" }
    if ($code -ne 0) { throw "database migration failed (nothing was changed)" }
}

if ($CheckMigrations) {
    Step "Migrations (dry run, rolled back)"
    Invoke-Migrations $true
    exit 0
}

Step "Backup"
$backup = Join-Path $InstallDir "backup.ps1"
if (Test-Path $backup) { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $backup } else { Write-Warning "no backup.ps1 (run install-package.ps1 first)" }

Step "Build"
$stage = Join-Path $env:TEMP ("a2core-publish-" + (Get-Date -Format "yyyyMMddHHmmss"))
dotnet publish (Join-Path $Source "server\Aion.Host") -c Release -r win-x64 --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not $SkipChecks) {
    Step "Checks"
    dotnet run --project (Join-Path $Source "tests\Aion.Native.Checks")
    if ($LASTEXITCODE -ne 0) { throw "checks failed: the server was not touched" }
}

# With the gateway (install-gateway.ps1) the host listens on the local link port and players stay connected through the restart.
$gatewayPort = $null
$hostEnv = @((Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\A2Core").Environment)
foreach ($line in $hostEnv) { if ($line -match '^Aion__Native__GatewayPort=(\d+)$') { $gatewayPort = [int]$Matches[1] } }
if ($UpdateGateway -and $gatewayPort) {
    Step "Gateway (players are disconnected)"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Source "deploy\windows\install-gateway.ps1") -Source $Source -InstallDir $InstallDir -LinkPort $gatewayPort
    if ($LASTEXITCODE -ne 0) { throw "gateway update failed" }
}

Step "Restart"
Stop-Service A2Core -ErrorAction SilentlyContinue
# The host can hang on a graceful service stop (Windows), leaving its DLLs locked so the copy silently fails and the OLD build
# keeps running (root cause of deploys not landing, 2026-11-08). Force-kill the host, then wait until the copy target unlocks.
Get-Process Aion.Host -ErrorAction SilentlyContinue | Stop-Process -Force
$deployTarget = Join-Path (Join-Path $InstallDir "host") "Aion.World.dll"
for ($w = 0; $w -lt 40; $w++) { try { if (Test-Path $deployTarget) { ([IO.File]::OpenWrite($deployTarget)).Close() }; break } catch { Start-Sleep -Milliseconds 500 } }
Step "Migrations"
try { Invoke-Migrations $false }
catch { Start-Service A2Core; throw "$_ - the old build was started again" }
Copy-Item -Path (Join-Path $stage "*") -Destination (Join-Path $InstallDir "host") -Recurse -Force
Start-Service A2Core
$ports = if ($gatewayPort) { 3700, 8780, $gatewayPort } else { 3700, 8780, 13328 }
for ($i = 0; $i -lt 60; $i++) {
    $up = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $ports -contains $_.LocalPort } | Select-Object -ExpandProperty LocalPort -Unique
    if (@($up).Count -ge $ports.Count) {
        Write-Host "Updated: listening on $($up -join ', ')" -ForegroundColor Green
        Remove-Item $stage -Recurse -Force
        $healthCheck = Join-Path $Source "deploy\windows\health-check.ps1"
        if (-not $SkipHealthCheck -and (Test-Path $healthCheck)) {
            Step "Health check (report only)"
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $healthCheck -InstallDir $InstallDir
            if ($LASTEXITCODE -ne 0) { Write-Warning "the health check found problems (see above and $InstallDir\logs\health-check.json)" }
        }
        exit 0
    }
    Start-Sleep -Seconds 2
}
throw "the service did not come back within 2 minutes: check the Event Log (Application, A2Core)"
