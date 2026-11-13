<#
.SYNOPSIS
  Installs A2Core as the "A2Core" Windows Service. Normally started by server-setup.txt (the one-step setup), which downloads
  the bundle and passes the folders; it can also be run by hand.

.DESCRIPTION
  Windows 11 (the main target), Windows 10 22H2, and Windows 10 1607 (build 14393, the LTSB test VM) or later; 64-bit.
  PowerShell 5.1 only; no tar/curl/winget (1607 has none of them).
   1. Checks: administrator, 64-bit, build 14393+, free ports (443 is often IIS).
   2. Visual C++ runtime (vc_redist.x64.exe) for PostgreSQL.
   3. PostgreSQL 18 from the slim binaries zip: initdb in <Root>\postgres\data (port 55432, 127.0.0.1 only, scram), registered as
      the "A2Core-PostgreSQL" service (NetworkService).
   4. The database: migration (-MigrationDir with roles.sql, aion.dump, credentials.json from the Linux server) or a fresh one
      (new roles with random passwords; the host makes the tables at its first start).
   5. The game data (A2Core-Data.zip) into <Root>\data; the host (self-contained, Aion.Host.exe) into <Root>\host.
   6. Settings: the Linux aion.env (migration) with its paths moved to <Root>, or fresh ones; stored as the service's
      environment and in <Root>\aion.env. TLS: the Linux cert.pem/key.pem (migration) or a new self-signed certificate for this
      machine's name and address (a .pfx).
   7. The service (automatic, restart on failure), firewall rules (TCP 443, 3700, 8087, 8091, 8780, 13328), a nightly database
      dump (14 days) as a Scheduled Task; then it starts and waits for the ports.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Root,          # e.g. D:\A2Core (the drive with the most free space)
    [Parameter(Mandatory)] [string] $HostZip,       # A2Core-Windows.zip (host\ inside A2Core\)
    [Parameter(Mandatory)] [string] $DataZip,       # A2Core-Data.zip
    [Parameter(Mandatory)] [string] $PostgresZip,   # postgresql-18-slim.zip (pgsql\ inside)
    [string] $VcRedist,                             # vc_redist.x64.exe
    [string] $MigrationDir,                         # roles.sql, aion.dump, aion.env, cert.pem, key.pem, credentials.json
    [string] $PublicAddress,
    [int] $PgPort = 55432
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function NewSecret { -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ }) }
# Native tools (psql, pg_restore, initdb) write progress to stderr; under PowerShell 5.1 with ErrorActionPreference Stop that
# would be fatal, so they run with Continue and their exit code is checked instead.
function Native([scriptblock] $block) {
    $saved = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { & $block } finally { $ErrorActionPreference = $saved }
}
function Unzip($zip, $to) {
    # .NET's own zip code (fast; Expand-Archive on PowerShell 5.1 is slow for the 1.4 GB data).
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $to)
}

# 1. Checks ------------------------------------------------------------------------------------------------------------
Step "Checking Windows"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this as administrator." }
if (-not [Environment]::Is64BitOperatingSystem) { throw "A2Core needs 64-bit Windows." }
$build = [int](Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion").CurrentBuildNumber
if ($build -lt 14393) { throw "Windows 10 1607 (build 14393) or later is needed; this is build $build." }
$osName = if ($build -ge 22000) { "Windows 11" } else { "Windows 10" }
Write-Host "   $osName, build $build"
$ports = 443, 3700, 8087, 8091, 8780, 13328
$listening = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $ports -contains $_.LocalPort })
foreach ($b in $listening) {
    $p = Get-Process -Id $b.OwningProcess -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -ne "Aion.Host") {
        Write-Warning ("port {0} is already used by {1} (pid {2}); the server needs it (IIS: remove the 443 binding of 'Default Web Site')" -f $b.LocalPort, $p.ProcessName, $b.OwningProcess) } }

New-Item -ItemType Directory -Force $Root, "$Root\data", "$Root\traces", "$Root\backups", "$Root\api-overrides", "$Root\item-icons", `
    "$Root\ncguard", "$Root\tls", "$Root\downloads", "$Root\logs" | Out-Null

# 2. Visual C++ runtime -------------------------------------------------------------------------------------------------
Step "Visual C++ runtime"
$vc = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue
if (-not $vc -or $vc.Installed -ne 1) {
    if (-not $VcRedist) { throw "the Visual C++ runtime is missing and no vc_redist.x64.exe was given" }
    $proc = Start-Process -FilePath $VcRedist -ArgumentList "/install", "/quiet", "/norestart" -Wait -PassThru
    if ($proc.ExitCode -notin 0, 1638, 3010) { throw "vc_redist failed with exit code $($proc.ExitCode)" }
} else { Write-Host "   already installed ($($vc.Version))" }

# 3. PostgreSQL ---------------------------------------------------------------------------------------------------------
Step "PostgreSQL 18"
$pgHome = "$Root\pgsql"
$pgBin = "$pgHome\bin"
$pgData = "$Root\postgres\data"
$superFile = "$Root\postgres\superuser.txt"
if (-not (Test-Path "$pgBin\pg_ctl.exe")) { Unzip $PostgresZip $Root }
if (-not (Test-Path "$pgData\PG_VERSION")) {
    New-Item -ItemType Directory -Force (Split-Path $pgData) | Out-Null
    $super = NewSecret
    Set-Content -Path $superFile -Value $super -Encoding ASCII
    $pw = "$Root\postgres\pw.tmp"; Set-Content -Path $pw -Value $super -Encoding ASCII
    Native { & "$pgBin\initdb.exe" -D $pgData -U postgres --pwfile=$pw -A scram-sha-256 -E UTF8 --locale=C 2>&1 | Out-Host }
    Remove-Item $pw
    if ($LASTEXITCODE -ne 0) { throw "initdb failed" }
    Add-Content "$pgData\postgresql.auto.conf" "port = $PgPort`r`nlisten_addresses = '127.0.0.1'`r`nlogging_collector = on`r`n"
    # The service account must own its data folder.
    icacls $pgData /grant "NT AUTHORITY\NetworkService:(OI)(CI)F" /T | Out-Null
    icacls "$Root\postgres" /grant "NT AUTHORITY\NetworkService:(OI)(CI)F" | Out-Null
}
if (-not (Get-Service A2Core-PostgreSQL -ErrorAction SilentlyContinue)) {
    Native { & "$pgBin\pg_ctl.exe" register -N A2Core-PostgreSQL -U "NT AUTHORITY\NetworkService" -D $pgData -S auto -w 2>&1 | Out-Host }
}
Start-Service A2Core-PostgreSQL
$env:PGPASSWORD = (Get-Content $superFile -Raw).Trim()
$psql = "$pgBin\psql.exe"
function Sql($text, $db = "postgres") { Native { & $psql -h 127.0.0.1 -p $PgPort -U postgres -d $db -v ON_ERROR_STOP=1 -Atc $text 2>$null } }
for ($i = 0; $i -lt 30 -and (Sql "select 1") -ne "1"; $i++) { Start-Sleep 1 }
if ((Sql "select 1") -ne "1") { throw "PostgreSQL did not start: see $pgData\log" }

# 4. Database -----------------------------------------------------------------------------------------------------------
Step "Database"
$credentials = "$Root\postgres\credentials.json"
if ((Sql "select 1 from pg_database where datname='aion'") -ne "1") {
    if ($MigrationDir) {
        Native { & $psql -h 127.0.0.1 -p $PgPort -U postgres -d postgres -q -f "$MigrationDir\roles.sql" 2>&1 | Where-Object { "$_" -notmatch "already exists" } | Out-Host }
        Sql "create database aion owner aion_owner" | Out-Null
        Native { & "$pgBin\pg_restore.exe" -h 127.0.0.1 -p $PgPort -U postgres -d aion --no-owner --role=aion_owner "$MigrationDir\aion.dump" 2>&1 | Out-Host }
        Copy-Item "$MigrationDir\credentials.json" $credentials -Force
        Write-Host ("   restored: " + (Sql "select (select count(*) from auth.accounts)||' accounts, '||(select count(*) from game.characters)||' characters'" "aion"))
    } else {
        $secrets = @{ aion_admin = NewSecret; aion_owner = NewSecret; aion_app = NewSecret; aion_auth = NewSecret }
        Sql ("create role aion_admin login superuser replication password '{0}'" -f $secrets.aion_admin) | Out-Null
        foreach ($r in "aion_owner", "aion_app", "aion_auth") { Sql ("create role {0} login password '{1}'" -f $r, $secrets[$r]) | Out-Null }
        Sql "create database aion owner aion_owner" | Out-Null
        $secrets | ConvertTo-Json | Set-Content -Path $credentials -Encoding ASCII
        Write-Host "   fresh database"
    }
} else { Write-Host "   database aion exists (kept)" }

# 5. Data and host ------------------------------------------------------------------------------------------------------
Step "Game data"
if (-not (Test-Path "$Root\data\bootstrap")) { Unzip $DataZip "$Root\data" } else { Write-Host "   already there" }
Step "Host"
if (Get-Service A2Core -ErrorAction SilentlyContinue) { Stop-Service A2Core -ErrorAction SilentlyContinue; Start-Sleep 2 }
$stage = "$Root\host.new"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Unzip $HostZip $stage
if (Test-Path "$Root\host") { Remove-Item "$Root\host" -Recurse -Force }
Move-Item "$stage\A2Core\host" "$Root\host"
Copy-Item "$stage\A2Core\scripts\*" $Root -Force
Remove-Item $stage -Recurse -Force

# 6. Settings and certificate -------------------------------------------------------------------------------------------
Step "Settings"
if (-not $PublicAddress) {
    $PublicAddress = (Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq "Up" } |
        Select-Object -First 1).IPv4Address.IPAddress }
Write-Host "   address $PublicAddress"
$c = Get-Content $credentials -Raw | ConvertFrom-Json
$settings = [ordered]@{}
if ($MigrationDir -and (Test-Path "$MigrationDir\aion.env")) {
    foreach ($line in Get-Content "$MigrationDir\aion.env") { if ($line -match '^\s*([A-Za-z0-9_]+)=(.*)$') { $settings[$Matches[1]] = $Matches[2] } }
} else {
    $settings["ASPNETCORE_ENVIRONMENT"] = "Production"
    $settings["ConnectionStrings__Aion"] = "Host=127.0.0.1;Port=$PgPort;Database=aion;Username=aion_app;Password=$($c.aion_app)"
    $settings["ConnectionStrings__Auth"] = "Host=127.0.0.1;Port=$PgPort;Database=aion;Username=aion_auth;Password=$($c.aion_auth)"
    $fresh = [ordered]@{ Aion__LocalPort = "8780"; Aion__Accounts__Registration = "true"; Aion__Network__BindAddress = "0.0.0.0";
        Aion__Native__LoginPort = "3700"; Aion__Native__GamePort = "13328"; Aion__Native__Logins = "accounts"; Aion__Native__MaxConnections = "200";
        Aion__ChatPort = "8091"; Aion__Platform__Port = "8087"; Aion__Platform__FixtureDirectory = "$Root\data\bootstrap\keys";
        Aion__Platform__PolicyFile = "$Root\data\bootstrap\policy\app-policy.json"; Aion__Platform__CustomData__SecondaryAuthCheckType = "None";
        Aion__NcGuard__Port = "443"; Aion__NcGuard__FixtureFile = "$Root\data\ncguard\fixture.json"; Aion__NcGuard__DataDirectory = "$Root\ncguard";
        Aion__NcGuard__Capture = "false"; Aion__Native__TerrainDirectory = "$Root\data\terrain"; Aion__Native__NavMeshDirectory = "$Root\data\navmesh";
        Aion__Web__Downloads = "$Root\downloads"; Aion__Platform__GeolocationFile = "$Root\geolocation-response.json";
        Aion__Web__StaffPath = "/" + (NewSecret).Substring(0, 16).ToLower(); Aion__Native__PreloadMaps__0 = "1000" }
    foreach ($kv in $fresh.GetEnumerator()) { $settings[$kv.Key] = $kv.Value }
}
# Linux paths (a migrated aion.env) -> <Root>, the same layout: /var/lib/aion/x, /var/log/aion/x and /etc/aion/x -> <Root>\x.
foreach ($k in @($settings.Keys)) {
    $v = [string]$settings[$k]
    foreach ($linux in "/var/lib/aion", "/var/log/aion", "/etc/aion") { if ($v.StartsWith($linux)) { $v = $Root + $v.Substring($linux.Length) } }
    if ($v -match '^[A-Za-z]:\\') { $v = $v -replace '/', '\' }
    $settings[$k] = $v
}
$settings["Aion__DataDir"] = $Root
$settings["Aion__Native__TraceDirectory"] = "$Root\traces"
$settings["Aion__Backups__Directory"] = "$Root\backups"
$settings["Aion__Network__PublicAddress"] = $PublicAddress
$settings.Remove("Aion__Backups__Tool"); $settings.Remove("Aion__Backups__PgHome")   # the Linux backup tool (Python); Windows has the task below
# Linux-server-only settings: the Windows setup download folder and the migration downloads it serves.
$settings.Remove("Aion__Web__WindowsSetupDir"); $settings.Remove("Aion__Web__MigrationDir")
if (-not (Test-Path "$Root\geolocation-response.json")) {
    Set-Content "$Root\geolocation-response.json" '{"geolocation": "NO", "coordinates": {"latitude": "59.9139", "longitude": "10.7522"}}' -Encoding ASCII }
if ($MigrationDir -and (Test-Path "$MigrationDir\cert.pem")) {
    Copy-Item "$MigrationDir\cert.pem" "$Root\tls\cert.pem" -Force; Copy-Item "$MigrationDir\key.pem" "$Root\tls\key.pem" -Force
    $settings["Aion__Https__CertificateFile"] = "$Root\tls\cert.pem"; $settings["Aion__Https__KeyFile"] = "$Root\tls\key.pem"
    $settings.Remove("Aion__Https__PfxPassword")
} else {
    if (-not (Test-Path "$Root\tls\server.pfx")) {
        # A self-signed certificate for this machine (name and address); the launcher must be told to trust it (see the README).
        $ext = "2.5.29.17={text}DNS=$env:COMPUTERNAME&IPAddress=$PublicAddress"
        # -Subject, not -DnsName: -DnsName with a SAN text extension is refused ("DnsName parameter conflicts with supplied Subject
        # Alternative Name extension", first run on the Dell 7920, Windows 11 26200).
        $cert = New-SelfSignedCertificate -Subject "CN=$env:COMPUTERNAME" -CertStoreLocation "Cert:\LocalMachine\My" -TextExtension @($ext) `
            -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(5) -FriendlyName "A2Core server"
        $pfxPassword = NewSecret
        Export-PfxCertificate -Cert $cert -FilePath "$Root\tls\server.pfx" -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force) | Out-Null
        Export-Certificate -Cert $cert -FilePath "$Root\tls\server.cer" | Out-Null
        Set-Content "$Root\tls\pfx-password.txt" $pfxPassword -Encoding ASCII
    }
    $settings["Aion__Https__CertificateFile"] = "$Root\tls\server.pfx"
    $settings["Aion__Https__PfxPassword"] = (Get-Content "$Root\tls\pfx-password.txt" -Raw).Trim()
    $settings.Remove("Aion__Https__KeyFile")
}
# Safety: no Linux file path may be left (the staff page path is a web address, not a file).
$linuxLeft = @($settings.GetEnumerator() | Where-Object { $_.Key -ne "Aion__Web__StaffPath" -and ([string]$_.Value).StartsWith("/") } | ForEach-Object { "$($_.Key)=$($_.Value)" })
if ($linuxLeft.Count -gt 0) { throw ("Linux paths left in the settings (report this): " + ($linuxLeft -join "; ")) }
$envLines = @($settings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
Set-Content -Path "$Root\aion.env" -Value $envLines -Encoding UTF8
foreach ($secret in "$Root\aion.env", "$Root\tls", "$Root\postgres\credentials.json", $superFile) {
    if (Test-Path $secret -PathType Container) { icacls $secret /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null }
    elseif (Test-Path $secret) { icacls $secret /inheritance:r /grant:r "SYSTEM:F" "Administrators:F" | Out-Null } }

# 7. Service, firewall, backup task -------------------------------------------------------------------------------------
Step "Service"
$bin = "`"$Root\host\Aion.Host.exe`""
if (-not (Get-Service A2Core -ErrorAction SilentlyContinue)) {
    New-Service -Name A2Core -DisplayName "A2Core game server" -BinaryPathName $bin -StartupType Automatic -DependsOn "A2Core-PostgreSQL" `
        -Description "AION2 server emulator (A2Core)" | Out-Null
} else { sc.exe config A2Core binPath= $bin | Out-Null }
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\A2Core" -Name Environment -Type MultiString -Value ([string[]]$envLines)
sc.exe failure A2Core reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
sc.exe failureflag A2Core 1 | Out-Null

Step "Firewall"
foreach ($p in $ports) {
    $name = "A2Core TCP $p"
    if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Protocol TCP -LocalPort $p -Action Allow -Profile Any | Out-Null } }

Step "Nightly backup task"
$backupScript = "$Root\backup.ps1"
@"
`$ErrorActionPreference = 'Stop'
`$env:PGPASSWORD = (Get-Content '$credentials' -Raw | ConvertFrom-Json).aion_admin
`$out = '$Root\backups\aion-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '.dump'
& '$pgBin\pg_dump.exe' -h 127.0.0.1 -p $PgPort -U aion_admin -Fc -f `$out aion
Get-ChildItem '$Root\backups' -Filter 'aion-*.dump' | Where-Object { `$_.LastWriteTime -lt (Get-Date).AddDays(-14) } | Remove-Item
"@ | Set-Content -Path $backupScript -Encoding UTF8
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$backupScript`""
Register-ScheduledTask -TaskName "A2Core nightly backup" -Action $action -Trigger (New-ScheduledTaskTrigger -Daily -At "05:00") -User "SYSTEM" -RunLevel Highest -Force | Out-Null
# The full backup script (retention, status for the health page, alerts), the catch-up after a missed night and the monthly
# restore test, when the deploy scripts are next to this one.
if (Test-Path (Join-Path $PSScriptRoot "ops-tasks.ps1")) { & (Join-Path $PSScriptRoot "ops-tasks.ps1") -Root $Root }

Step "Starting"
Start-Service A2Core
$want = 3700, 8780, 13328
for ($i = 0; $i -lt 90; $i++) {
    $up = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $want -contains $_.LocalPort } | Select-Object -ExpandProperty LocalPort -Unique)
    if ($up.Count -ge $want.Count) { break }
    Start-Sleep 2
}
$up = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $ports -contains $_.LocalPort } | Select-Object -ExpandProperty LocalPort -Unique)
Write-Host "   listening on: $($up -join ', ')"
if (($want | Where-Object { $up -notcontains $_ }).Count -gt 0) {
    Write-Warning "the server is not listening on every port: see the Event Log (Windows Logs > Application, source A2Core) and $Root\logs"
    exit 1 }
Write-Host "A2Core is running on $osName ($PublicAddress). Data: $Root. Settings: $Root\aion.env" -ForegroundColor Green
