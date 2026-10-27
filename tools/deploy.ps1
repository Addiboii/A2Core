<#
.SYNOPSIS
Deploys from this PC to the server over SSH (ssh config host "aion"; see deploy/linux/README.md).

.DESCRIPTION
-Launcher   Publishes A2Core Launcher (launcher/AionLauncher) as one exe, writes its version file and
            copies both to the server's downloads folder. Installed copies then offer the update.
-Server     Pushes the current branch to GitHub and runs deploy/linux/update.sh on the server
            (database backup, git pull, rebuild, restart). Players online get a one-minute warning.
-Status     Shows the host and database services and the last log lines.
Several switches can be combined; with none, -Status runs.

.EXAMPLE
tools/deploy.ps1 -Launcher
tools/deploy.ps1 -Server
tools/deploy.ps1 -Launcher -Server
#>
[CmdletBinding()]
param(
    [switch]$Launcher,
    [switch]$Server,
    [switch]$Status,
    [string]$SshHost = "aion",
    [string]$RepoOnServer = "~/aion",
    [string]$DownloadsOnServer = "/var/lib/aion/downloads"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not ($Launcher -or $Server -or $Status)) { $Status = $true }

# Native programs write progress to stderr, which Windows PowerShell 5.1 turns into terminating errors
# under $ErrorActionPreference = Stop; cmd merges the streams so only the exit code counts.
function Run([string]$commandLine) {
    cmd /c "$commandLine 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "failed ($LASTEXITCODE): $commandLine" }
}

function Invoke-Ssh([string]$command) {
    Run "ssh -o BatchMode=yes -o ConnectTimeout=15 $SshHost `"$command`""
}

if ($Launcher) {
    $out = Join-Path $root "research/local/launcher-publish"
    Write-Host "Publishing A2Core Launcher..."
    Run "dotnet publish `"$(Join-Path $root 'launcher/AionLauncher')`" -c Release -r win-x64 -o `"$out`" -p:DebugType=none -m:1 -p:UseSharedCompilation=false" | Out-Null
    $exe = Join-Path $out "A2Core Launcher.exe"
    $version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
    [IO.File]::WriteAllText("$exe.version", "$version`n")
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item $exe).Length
    Write-Host "A2Core Launcher $version, $size bytes, SHA-256 $hash"
    Write-Host "Uploading to ${SshHost}:$DownloadsOnServer ..."
    Run "scp -o BatchMode=yes `"$exe`" `"$exe.version`" `"${SshHost}:$DownloadsOnServer/`""
    # The host serves the files as the aion user; the old launcher name is no longer served.
    Invoke-Ssh "cd '$DownloadsOnServer' && chown aion:aion 'A2Core Launcher.exe' 'A2Core Launcher.exe.version' && chmod 644 'A2Core Launcher.exe' 'A2Core Launcher.exe.version' && rm -f Launcher.exe Launcher.exe.version && ls -la"
    Write-Host "Done. Update the pinned message in Discord #downloads with version $version and hash $hash."
}

if ($Server) {
    $branch = (& git -C $root rev-parse --abbrev-ref HEAD).Trim()
    $dirty = (& git -C $root status --porcelain)
    if ($dirty) { throw "Uncommitted changes in $root; commit them first (the server pulls from GitHub)." }
    Write-Host "Pushing $branch to GitHub..."
    Run "git -C `"$root`" push origin $branch"
    Write-Host "Updating the server (backup, pull, rebuild, restart)..."
    Invoke-Ssh "cd $RepoOnServer && sudo deploy/linux/update.sh"
}

if ($Status) {
    Invoke-Ssh "systemctl --no-pager --lines=0 status aion-host aion-postgres; echo; journalctl -u aion-host -n 20 --no-pager; echo; ls -la $DownloadsOnServer 2>/dev/null"
}
