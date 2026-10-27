# Run the loopback TLS MITM sink against NCGuard's external HTTPS hosts.
# Requires an elevated PowerShell (writes the hosts file). Bounded by default.
param(
    [ValidateSet('dotnet','python')][string]$Backend = 'dotnet',
    [int]$DurationMinutes = 10,
    [int]$Port = 443,
    [string]$Hosts = 'bladnoch.ncsoft.com,lagavulin.ncsoft.com,atp-json.ncsoft.com,sdk.gpa.ncsoft.com,stun.gpa.ncsoft.com,nccr-api.global.nc.com',
    [string]$RespondFile = '',
    [switch]$SkipHostsFile,
    [switch]$TrustCa,
    [string]$StopFile = ''
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$p = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $SkipHostsFile -and -not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'This script must run from an elevated (Administrator) PowerShell to modify the hosts file.'
}
if ($DurationMinutes -lt 1 -or $DurationMinutes -gt 60) {
    Write-Error '-DurationMinutes must be 1..60.'
}
$seconds = $DurationMinutes * 60
if ($Backend -eq 'dotnet') {
    if ($TrustCa) { throw 'The .NET sink does not install a CA in the trust store.' }
    $hostDll = 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll'
    if (-not (Test-Path -LiteralPath $hostDll)) { throw 'Build server/Aion.Host/Aion.Host.csproj first.' }
    $sinkArgs = @($hostDll, '--Aion:LocalPort', '0', '--Aion:NcGuard:Port', "$Port",
        '--Aion:NcGuard:DataDirectory', 'research/local', '--Aion:NcGuard:DurationSeconds', "$seconds",
        '--Aion:NcGuard:RewriteHosts', (-not $SkipHostsFile).ToString())
    $hostNames = $Hosts.Split(',')
    for ($i = 0; $i -lt $hostNames.Length; $i++) { $sinkArgs += @("--Aion:NcGuard:Hosts:$i", $hostNames[$i]) }
    if ($RespondFile) { $sinkArgs += @('--Aion:NcGuard:FixtureFile', $RespondFile) }
    if ($StopFile) { $sinkArgs += @('--Aion:NcGuard:StopFile', $StopFile) }
    & dotnet @sinkArgs
    exit $LASTEXITCODE
}
$args = @('tools\loopback_tls_mitm.py', '--duration', "$seconds", '--hosts', $Hosts, '--port', "$Port")
if ($RespondFile) { $args += @('--respond', $RespondFile) }
if ($SkipHostsFile) { $args += @('--skip-hosts-file') }
if ($TrustCa) { $args += @('--trust-ca') }
if ($StopFile) { $args += @('--stop-file', $StopFile) }
& python @args
exit $LASTEXITCODE
