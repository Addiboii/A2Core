<#
.SYNOPSIS
Installs or updates the AION2 server on Windows 10/11 or Windows Server (the Windows counterpart of deploy/linux/install.sh).

.DESCRIPTION
Run from a checkout of this repository, in a normal PowerShell window:

  One PC, server and game on the same computer (nothing is reachable from outside):
    powershell -ExecutionPolicy Bypass -File deploy\windows\install.ps1 -Local -Data data\aion-server-data.tar.gz

  A server other players connect to (use the address they reach it by):
    powershell -ExecutionPolicy Bypass -File deploy\windows\install.ps1 -PublicAddress 203.0.113.5 -Data data\aion-server-data.tar.gz

-Data is needed on the first install. Run it again later, with no options, to update: it rebuilds the server, backs up
the database, applies new migrations and keeps your settings. Start the server with start.ps1. See README.md here.

.PARAMETER PublicAddress
The IPv4 address players connect to (not with -Local).
.PARAMETER Local
Server and game on this PC only: listens on 127.0.0.1.
.PARAMETER Data
The server data bundle (aion-server-data.tar.gz).
.PARAMETER Root
Where the server lives (default C:\AionServer): host, data, database, config and state.
.PARAMETER OpenRegistration
Anyone may make an account on the account page (default: only the first account, later ones are made by staff).
.PARAMETER PostgresZip
A PostgreSQL 18.6 Windows binaries zip you already downloaded (otherwise it is downloaded from EnterpriseDB).
#>
param(
    [string]$PublicAddress,
    [switch]$Local,
    [string]$Data,
    [string]$Root,
    [switch]$OpenRegistration,
    [string]$PostgresZip
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# OURS: the PostgreSQL build the project's database tool expects on Windows (tools/postgres_dev.py), checked by its hash.
$PgArchive = 'postgresql-18.6-1-windows-x64-binaries.zip'
$PgUrl = "https://get.enterprisedb.com/postgresql/$PgArchive"
$PgSha256 = 'fbe23da234ee31547bf8a36d29dfd81e82b849df2d2b78d2eecb43d360252f8c'
$Ports = 443, 3700, 8087, 8091, 8780, 13328

function Fail([string]$message) { Write-Host $message -ForegroundColor Red; exit 1 }
function Step([string]$message) { Write-Host "== $message" -ForegroundColor Cyan }
function Invoke-Tool {
    # Runs a program and stops the install when it fails.
    param([string]$Program, [string[]]$Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "$Program $($Arguments -join ' ') failed (exit code $LASTEXITCODE)." }
}

# ---- Options: a later run without options repeats the first one's.
if (-not $Root) { $Root = 'C:\AionServer' }
$Root = [IO.Path]::GetFullPath($Root)
$Config = Join-Path $Root 'config'
$Saved = Join-Path $Config 'install.json'
if (-not $PublicAddress -and -not $Local -and (Test-Path $Saved)) {
    $previous = Get-Content $Saved -Raw | ConvertFrom-Json
    $PublicAddress = $previous.publicAddress
    $Local = [bool]$previous.local
    if (-not $PSBoundParameters.ContainsKey('OpenRegistration')) { $OpenRegistration = [bool]$previous.openRegistration }
}
if ($Local) { $PublicAddress = '127.0.0.1'; $Bind = '127.0.0.1' } else { $Bind = '0.0.0.0' }
if (-not $PublicAddress) { Fail 'Choose -Local (this PC only) or -PublicAddress <IPv4 players connect to>. See deploy\windows\README.md.' }
$parsed = $null
if (-not [Net.IPAddress]::TryParse($PublicAddress, [ref]$parsed) -or $parsed.AddressFamily -ne 'InterNetwork') { Fail "-PublicAddress must be an IPv4 address, not '$PublicAddress'." }

# ---- What must be installed first.
Step 'Checking .NET, Python and Node.js'
$missing = @()
$sdks = ''
if (Get-Command dotnet -ErrorAction SilentlyContinue) { $sdks = (& dotnet --list-sdks) -join "`n" }
if ($sdks -notmatch '(?m)^10\.') { $missing += '.NET 10 SDK:   winget install Microsoft.DotNet.SDK.10' }
$Python = $null
foreach ($candidate in 'python', 'py') {
    if (-not (Get-Command $candidate -ErrorAction SilentlyContinue)) { continue }
    $version = & $candidate -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
    if ($LASTEXITCODE -eq 0 -and $version -match '^3\.(\d+)$' -and [int]$Matches[1] -ge 11) { $Python = $candidate; break }
}
if (-not $Python) { $missing += 'Python 3.11 or newer:   winget install Python.Python.3.13' }
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { $missing += 'Node.js (npm):   winget install OpenJS.NodeJS.LTS' }
if ($missing) {
    Write-Host 'Install these first, then open a NEW PowerShell window and run this again:' -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  $_" }
    exit 1
}
$env:PYTHONIOENCODING = 'utf-8'

# ---- Folders. The config and database folders hold passwords: only you, Administrators and SYSTEM may read them.
$DataDir = Join-Path $Root 'data'; $State = Join-Path $Root 'state'; $HostDir = Join-Path $Root 'host'
$Src = Join-Path $Root 'src'; $PgHome = Join-Path $Root 'postgres'; $Tls = Join-Path $Config 'tls'
foreach ($folder in $Root, $DataDir, $State, $Config, $Tls, $PgHome, (Join-Path $State 'ncguard'), (Join-Path $State 'backups'), (Join-Path $State 'downloads')) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
}
foreach ($private in $Config, $PgHome) {
    & icacls $private /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
}
$env:AION_PG_HOME = $PgHome

# ---- Server data (bootstrap and NCGuard fixtures, terrain, navigation meshes, catalogue).
if ($Data) {
    Step 'Unpacking the server data'
    if (-not (Test-Path $Data)) { Fail "No file at $Data. Download aion-server-data.tar.gz from the Releases page." }
    Invoke-Tool 'tar' @('-xzf', (Resolve-Path $Data).Path, '-C', $DataDir)
}
if (-not (Test-Path (Join-Path $DataDir 'bootstrap\keys\app-key.json'))) {
    Fail "No server data in $DataDir. Download aion-server-data.tar.gz from the Releases page and pass -Data <file>."
}

# ---- Stop a running server before its files are replaced.
& (Join-Path $PSScriptRoot 'stop.ps1') -Root $Root -KeepDatabase

# ---- Database tools and migrations, then the host.
Step 'Copying the database tools'
foreach ($name in 'tools', 'database') {
    & robocopy (Join-Path $Repo $name) (Join-Path $Src $name) /MIR /XD bin obj __pycache__ node_modules /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "Copying $name failed (robocopy $LASTEXITCODE)." }
}
Step 'Building the account page and the server (a few minutes the first time)'
Push-Location (Join-Path $Repo 'web')
try {
    Invoke-Tool 'npm' @('ci', '--no-audit', '--no-fund')
    Invoke-Tool 'npm' @('run', 'build')
} finally { Pop-Location }
$newHost = "$HostDir.new"
if (Test-Path $newHost) { Remove-Item -Recurse -Force $newHost }
Invoke-Tool 'dotnet' @('publish', (Join-Path $Repo 'server\Aion.Host\Aion.Host.csproj'), '-c', 'Release', '-o', $newHost, '--nologo', '-v', 'q')
if (Test-Path $HostDir) { Remove-Item -Recurse -Force $HostDir }
Rename-Item $newHost $HostDir

# ---- PostgreSQL: the project's own copy under $Root\postgres, loopback only, port 15432 (tools/postgres_dev.py; an older
#      cluster on 55432 moves at its next start, and the settings below follow).
if (-not (Test-Path (Join-Path $PgHome 'pgsql\bin\initdb.exe'))) {
    $archive = Join-Path $PgHome $PgArchive
    if ($PostgresZip) { Copy-Item (Resolve-Path $PostgresZip).Path $archive -Force }
    if (-not (Test-Path $archive)) {
        Step "Downloading PostgreSQL (about 340 MB) from $PgUrl"
        Invoke-Tool 'curl.exe' @('-L', '--fail', '-o', $archive, $PgUrl)
    }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $PgSha256) {
        Remove-Item $archive -Force
        Fail 'The PostgreSQL download does not match its expected SHA-256 and was deleted. Run this again.'
    }
}
$pg = Join-Path $Src 'tools\postgres_dev.py'
$first = -not (Test-Path (Join-Path $PgHome 'credentials.json'))
$catalog = Join-Path $DataDir 'game-catalog-v2'
if ($first) {
    Step 'Creating the database'
    Invoke-Tool $Python @($pg, 'setup')
    Invoke-Tool $Python @($pg, 'import-catalog', '--directory', $catalog)
    Invoke-Tool $Python @((Join-Path $Src 'tools\configure_starters.py'))
    Invoke-Tool $Python @((Join-Path $Src 'tools\class_skills.py'), 'install')
    Invoke-Tool $Python @((Join-Path $Src 'tools\equipment_rules.py'))
} else {
    Step 'Backing up and updating the database'
    Invoke-Tool $Python @($pg, 'start')
    $backup = Join-Path $State ("backups\before-update-{0:yyyyMMdd-HHmmss}.dump" -f (Get-Date))
    Invoke-Tool $Python @($pg, 'backup', $backup)
    Invoke-Tool $Python @($pg, 'setup')
    Invoke-Tool $Python @($pg, 'upgrade-catalog', '--directory', $catalog)
}

# ---- The account site's certificate (self-signed; the launcher accepts it by its pin).
$cert = Join-Path $Tls 'cert.pem'; $key = Join-Path $Tls 'key.pem'
$selfSigned = Join-Path $PSScriptRoot 'SelfSigned.cs'
if (-not (Test-Path $cert) -or -not (Test-Path $key)) {
    Step 'Making the account site certificate'
    & dotnet run $selfSigned -- $PublicAddress $cert $key | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'Making the certificate failed.' }
}
$pin = (& dotnet run $selfSigned -- --pin $cert | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or -not $pin) { Fail 'Could not read the certificate pin.' }

# ---- Settings. The staff dashboard address and the two-step sign-in key are kept across updates.
Step 'Writing the settings'
$envFile = Join-Path $Config 'aion.env'
$arguments = @((Join-Path $Src 'tools\server_env.py'), '--public-address', $PublicAddress, '--bind', $Bind, '--data', $DataDir,
    '--state', $State, '--certificate', $cert, '--key', $key)
if (Test-Path $envFile) {
    foreach ($line in Get-Content $envFile) {
        if ($line -match '^Aion__Web__StaffPath=(.+)$') { $arguments += @('--staff-path', $Matches[1]) }
        if ($line -match '^Aion__Totp__EncryptionKey=(.+)$') { $arguments += @('--totp-key', $Matches[1]) }
    }
}
if ($OpenRegistration) { $arguments += '--open-registration' }
$lines = & $Python @arguments
if ($LASTEXITCODE -ne 0) { Fail 'Writing the settings failed.' }
[IO.File]::WriteAllLines($envFile, [string[]]$lines, (New-Object Text.UTF8Encoding $false))
$localEnv = Join-Path $Config 'local.env'
if (-not (Test-Path $localEnv)) {
    [IO.File]::WriteAllLines($localEnv, [string[]]@(
        '# Your own server settings (KEY=value, read after aion.env). install.ps1 never rewrites this file.',
        '# Restart the server (stop.ps1, start.ps1) after a change.',
        '# Rates (1 = normal, 0 = off, at most 100):',
        '#Aion__Rates__Experience=1',
        '#Aion__Rates__Kinah=1',
        '#Aion__Rates__Drops=1'), (New-Object Text.UTF8Encoding $false))
}
$savedOptions = @{ publicAddress = $PublicAddress; local = [bool]$Local; openRegistration = [bool]$OpenRegistration }
[IO.File]::WriteAllText($Saved, ($savedOptions | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

# ---- Windows Firewall, for a server other PCs connect to (needs an administrator window).
if (-not $Local) {
    $rule = 'AION2 server'
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue) {
        Write-Host "Windows Firewall rule '$rule' is already there."
    } elseif ($admin) {
        New-NetFirewallRule -DisplayName $rule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Ports | Out-Null
        Write-Host "Windows Firewall rule '$rule' added (TCP $($Ports -join ', '))."
    } else {
        Write-Host 'Other PCs cannot connect until Windows Firewall lets them in. In an administrator PowerShell window, run once:' -ForegroundColor Yellow
        Write-Host "  New-NetFirewallRule -DisplayName '$rule' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $($Ports -join ',')"
    }
}

$staffPath = (Get-Content $envFile | Where-Object { $_ -match '^Aion__Web__StaffPath=' }) -replace '^[^=]+=', ''
Write-Host ''
Write-Host 'Installed.' -ForegroundColor Green
$rootOption = ''
if ($Root -ne 'C:\AionServer') { $rootOption = " -Root `"$Root`"" }
Write-Host "Start the server:   powershell -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'start.ps1')`"$rootOption"
Write-Host "Account page:       https://${PublicAddress}:8780   (your browser warns about the self-signed certificate: continue)"
Write-Host "Staff dashboard:    https://${PublicAddress}:8780$staffPath"
Write-Host 'The first account you register there works even with registration closed. Make it staff with:'
Write-Host "  `$env:AION_PG_HOME='$PgHome'; $Python `"$pg`" role <username> admin"
Write-Host 'For the launcher, put these in launcher\launcher.json before building it:'
Write-Host "  `"accountUrl`": `"https://${PublicAddress}:8780`", `"serverAddress`": `"$PublicAddress`", `"certificatePins`": [`"$pin`"]"
