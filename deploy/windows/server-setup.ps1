# A2Core one-step Windows server setup (served as http://<server>:8091/a2core/server-setup.txt).
# In an ADMINISTRATOR PowerShell on the new server:
#   $env:A2CORE_SOURCE='<server>:8091'; irm http://<server>:8091/a2core/server-setup.txt | iex
# Moving the Linux server (a token from `sudo tools/windows_migration_token.sh` on the Linux server, valid 24 h):
#   $env:A2CORE_MIGRATE_FROM='<linux server>'; $env:A2CORE_TOKEN='<token>'; irm http://<linux server>:8091/a2core/server-setup.txt | iex
# Optional: $env:A2CORE_ROOT='E:\A2Core' (default: <the fixed drive with the most free space>:\A2Core),
#           $env:A2CORE_ADDRESS='203.0.113.7' (default: the address of the network adapter with the default gateway),
#           $env:A2CORE_SOURCE='<server>:8091' (where the bundle is downloaded from; required unless A2CORE_MIGRATE_FROM is set).
# Windows 11, Windows 10 22H2 and Windows 10 1607 (build 14393) or later, 64-bit; PowerShell 5.1 (built in).
& {
    $ErrorActionPreference = "Stop"
    $ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is many times faster without the progress bar on PowerShell 5.1
    function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { Write-Host "Run this in an ADMINISTRATOR PowerShell (right-click PowerShell > Run as administrator)." -ForegroundColor Red; return }

    $source = if ($env:A2CORE_SOURCE) { $env:A2CORE_SOURCE } elseif ($env:A2CORE_MIGRATE_FROM) { "$($env:A2CORE_MIGRATE_FROM):8091" } else { Write-Host "Set `$env:A2CORE_SOURCE='<server>:8091' (the server the bundle comes from) first." -ForegroundColor Red; return }
    $base = "http://$source/a2core"
    if (-not $env:A2CORE_ROOT) {
        $drive = Get-WmiObject Win32_LogicalDisk -Filter "DriveType=3" | Sort-Object FreeSpace -Descending | Select-Object -First 1
        $root = "$($drive.DeviceID)\A2Core"
        Write-Host ("   data drive {0} ({1:N0} GB free)" -f $drive.DeviceID, ($drive.FreeSpace / 1GB))
    } else { $root = $env:A2CORE_ROOT }
    if ((Get-WmiObject Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f $root.Substring(0, 2))).FreeSpace -lt 8GB) {
        Write-Host "At least 8 GB free are needed on $($root.Substring(0, 2))." -ForegroundColor Red; return }
    $setup = "$root\setup"
    New-Item -ItemType Directory -Force $setup | Out-Null

    Step "Downloading the A2Core bundle from $base"
    $version = Invoke-RestMethod "$base/version.json" -UseBasicParsing
    Write-Host "   build $($version.commit) ($($version.built))"
    foreach ($name in "A2Core-Windows.zip", "A2Core-Data.zip", "postgresql-18-slim.zip", "vc_redist.x64.exe") {
        $file = "$setup\$name"
        $want = $version.files.$name
        if ((Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash -eq $want.sha256.ToUpper()) { Write-Host "   $name (cached)"; continue }
        Write-Host ("   {0} ({1:N0} MB)" -f $name, ($want.size / 1MB))
        Invoke-WebRequest "$base/$name" -OutFile $file -UseBasicParsing
        if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $want.sha256.ToUpper()) { throw "${name}: download damaged (checksum differs); run the setup again" }
    }

    $migration = $null
    if ($env:A2CORE_MIGRATE_FROM) {
        Step "Downloading the migration files from $($env:A2CORE_MIGRATE_FROM)"
        if (-not $env:A2CORE_TOKEN) { throw "A2CORE_TOKEN is missing (run sudo tools/windows_migration_token.sh on the Linux server)" }
        $migration = "$setup\migration"
        New-Item -ItemType Directory -Force $migration | Out-Null
        $mbase = "http://$($env:A2CORE_MIGRATE_FROM):8091/a2core/migrate/$($env:A2CORE_TOKEN)"
        foreach ($name in "manifest.json", "roles.sql", "aion.dump", "aion.env", "cert.pem", "key.pem", "credentials.json") {
            Invoke-WebRequest "$mbase/$name" -OutFile "$migration\$name" -UseBasicParsing
        }
        Write-Host "   $((Get-Content "$migration\manifest.json" -Raw | ConvertFrom-Json).created)"
    }

    Step "Installing"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $scripts = "$setup\scripts"
    if (Test-Path $scripts) { Remove-Item $scripts -Recurse -Force }
    $zip = [System.IO.Compression.ZipFile]::OpenRead("$setup\A2Core-Windows.zip")
    try {
        New-Item -ItemType Directory -Force $scripts | Out-Null
        foreach ($entry in $zip.Entries | Where-Object { $_.FullName -like "A2Core/scripts/*" -and $_.Name }) {
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "$scripts\$($entry.Name)", $true) }
    } finally { $zip.Dispose() }
    $arguments = @{ Root = $root; HostZip = "$setup\A2Core-Windows.zip"; DataZip = "$setup\A2Core-Data.zip"
                    PostgresZip = "$setup\postgresql-18-slim.zip"; VcRedist = "$setup\vc_redist.x64.exe" }
    if ($migration) { $arguments.MigrationDir = $migration }
    if ($env:A2CORE_ADDRESS) { $arguments.PublicAddress = $env:A2CORE_ADDRESS }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$scripts\install.ps1" @arguments
    if ($LASTEXITCODE -ne 0) { Write-Host "The setup stopped with an error (above). Fix it and run the same command again: finished steps are skipped." -ForegroundColor Red }
    if ($migration) { Remove-Item $migration -Recurse -Force }   # it held database passwords and the TLS key
}
