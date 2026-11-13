<#
.SYNOPSIS
  Puts the A2Core gateway in front of the game host (updates without kicking players), or takes it out again (-Disable).

.DESCRIPTION
  The gateway (server\Aion.Gateway, service "A2Core-Gateway") owns the public game port 13328: it keeps the players' connections
  and relays them to the host on a local link (127.0.0.1:13329, Aion__Native__GatewayPort). When the host restarts (update.ps1),
  players stay connected and get a short map reload instead of a disconnect.
  This switch restarts the host once (players are disconnected this one time). Later deploys (update.ps1) restart only the host.
  The gateway itself is updated only with update.ps1 -UpdateGateway (that one does disconnect players).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File deploy\windows\install-gateway.ps1 -Source G:\A2Core-project\aion\A2Core
  powershell -ExecutionPolicy Bypass -File deploy\windows\install-gateway.ps1 -Source G:\A2Core-project\aion\A2Core -Disable
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $InstallDir = ((Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\A2Core").ImagePath -replace '^"?(.*)\\host\\Aion\.Host\.exe"?$', '$1'),
    [int] $GamePort = 13328,
    [int] $LinkPort = 13329,
    [switch] $Disable
)
$ErrorActionPreference = "Stop"
function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\A2Core"
$envFile = Join-Path $InstallDir "aion.env"

# The host's setting Aion__Native__GatewayPort, in the service environment (what the service reads) and aion.env (the record).
function Set-HostGatewayPort([string] $value) {
    $current = @((Get-ItemProperty $serviceKey).Environment) | Where-Object { $_ -and $_ -notmatch '^Aion__Native__GatewayPort=' }
    if ($value) { $current += "Aion__Native__GatewayPort=$value" }
    Set-ItemProperty $serviceKey -Name Environment -Type MultiString -Value $current
    if (Test-Path $envFile) {
        $lines = @(Get-Content $envFile) | Where-Object { $_ -notmatch '^Aion__Native__GatewayPort=' }
        if ($value) { $lines += "Aion__Native__GatewayPort=$value" }
        [IO.File]::WriteAllLines($envFile, $lines)
    }
}

# A graceful stop can hang on Windows (update.ps1, 2026-11-08): stop, then force the process down.
function Restart-Host {
    Stop-Service A2Core -ErrorAction SilentlyContinue
    Get-Process Aion.Host -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Service A2Core
}

function Wait-Ports([int[]] $ports) {
    for ($i = 0; $i -lt 60; $i++) {
        $up = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $ports -contains $_.LocalPort } | Select-Object -ExpandProperty LocalPort -Unique
        if (@($up).Count -ge $ports.Count) { Write-Host "Listening on $($up -join ', ')" -ForegroundColor Green; return }
        Start-Sleep -Seconds 2
    }
    throw "ports $($ports -join ', ') did not come up within 2 minutes"
}

if ($Disable) {
    Step "Taking the gateway out (the host owns $GamePort again)"
    Stop-Service A2Core-Gateway -ErrorAction SilentlyContinue
    Set-HostGatewayPort $null
    Restart-Host
    Set-Service A2Core-Gateway -StartupType Disabled -ErrorAction SilentlyContinue
    Wait-Ports @(3700, 8780, $GamePort)
    exit 0
}

Step "Build the gateway"
$stage = Join-Path $env:TEMP ("a2core-gateway-" + (Get-Date -Format "yyyyMMddHHmmss"))
dotnet publish (Join-Path $Source "server\Aion.Gateway") -c Release -r win-x64 --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw "gateway build failed" }

Step "Install the gateway service"
$gatewayDir = Join-Path $InstallDir "gateway"
Stop-Service A2Core-Gateway -ErrorAction SilentlyContinue
Get-Process Aion.Gateway -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $gatewayDir | Out-Null
Copy-Item -Path (Join-Path $stage "*") -Destination $gatewayDir -Recurse -Force
Remove-Item $stage -Recurse -Force
$bind = "0.0.0.0"
if ((Test-Path $envFile) -and ((Get-Content $envFile -Raw) -match 'Aion__Network__BindAddress=([^\r\n]+)')) { $bind = $Matches[1].Trim() }
$settings = @{ Gateway = @{ Bind = $bind; Port = $GamePort; Upstream = "127.0.0.1"; UpstreamPort = $LinkPort } } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $gatewayDir "appsettings.json"), $settings, (New-Object Text.UTF8Encoding $false))
if (-not (Get-Service A2Core-Gateway -ErrorAction SilentlyContinue)) {
    New-Service -Name A2Core-Gateway -BinaryPathName ('"' + (Join-Path $gatewayDir "Aion.Gateway.exe") + '"') `
        -DisplayName "A2Core gateway (keeps players online during updates)" -StartupType Automatic | Out-Null
    sc.exe failure A2Core-Gateway reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}
Set-Service A2Core-Gateway -StartupType Automatic

Step "Switch the game port to the gateway (players are disconnected this one time)"
Set-HostGatewayPort "$LinkPort"
Restart-Host
Wait-Ports @(3700, 8780, $LinkPort)
Start-Service A2Core-Gateway
Wait-Ports @($GamePort)
Write-Host "Gateway on: clients -> :$GamePort (gateway) -> 127.0.0.1:$LinkPort (host). Deploys with update.ps1 keep players connected." -ForegroundColor Green
