<#
Capture the current AION2 server using a uniquely named netsh/NDIS trace.
Does not call PktMon or change its filters. Run in Administrator PowerShell;
-PlanOnly previews commands without starting a trace or requiring elevation.
#>
[CmdletBinding()]
param(
    [ValidateRange(15, 7200)][int]$DurationSeconds = 180,
    [string]$ServerAddress,
    [switch]$MovementCues,
    [switch]$MultiLayer,
    [switch]$InGame,
    [ValidateRange(64, 4096)][int]$MaxSizeMb = 128,
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
if ($MovementCues -and $DurationSeconds -lt 80) { throw 'Movement cues require at least 80 seconds.' }
$movementSchedule = @()
if ($MovementCues) {
    $movementSchedule = @(
        @{ at_seconds = 0; action = 'stand_still'; beeps = 0 },
        @{ at_seconds = 20; action = 'walk_forward'; beeps = 1 },
        @{ at_seconds = 23; action = 'stop'; beeps = 2 },
        @{ at_seconds = 43; action = 'walk_backward'; beeps = 1 },
        @{ at_seconds = 46; action = 'stop'; beeps = 2 },
        @{ at_seconds = 66; action = 'sequence_complete_stay_still'; beeps = 3 }
    )
}
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$netsh = Join-Path $env:SystemRoot 'System32\netsh.exe'
$converter = Join-Path $workspace 'research\local\tooling\etl2pcapng-1.11.0\etl2pcapng.exe'
$converterHash = 'C2D03AAC43EA0F5626FC8D909E6688393FEAF71BD6FACD39AAF8A637382D9043'

# Discovery: at login the port-13700 peer; in the game (-InGame or -MovementCues) the port-13328 game server.
# Addresses given with -ServerAddress are added to the discovered one (the login and game servers differ).
$discoveryPort = if ($MovementCues -or $InGame) { 13328 } else { 13700 }
# On a Wi-Fi link the physical layer alone records only the client's frames (2026-10-19: 2474 outbound, 0 inbound);
# the multi-layer trace records both directions (the 2026-10-13 fight). Inside the game it is always on.
if ($InGame) { $MultiLayer = $true }
$gameIds = @(Get-Process -Name AION2 -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.Id })
$discovered = @()
$rows = @(& "$env:SystemRoot\System32\netstat.exe" -ano -p tcp)
if ($LASTEXITCODE -ne 0) { throw 'Could not read the TCP endpoint table.' }
foreach ($row in $rows) {
    $fields = $row.Trim() -split '\s+'
    if ($fields.Count -eq 5 -and $fields[0] -eq 'TCP' -and $fields[4] -in $gameIds -and
        $fields[3] -eq 'ESTABLISHED' -and
        $fields[2] -match ('^(\d+\.\d+\.\d+\.\d+):' + $discoveryPort + '$')) {
        $discovered += $Matches[1]
    }
}
$discovered = @($discovered | Sort-Object -Unique)
if (-not $ServerAddress -and $discovered.Count -ne 1) {
    $screen = if ($discoveryPort -eq 13328) { 'inside the game' } else { 'at login (or use -InGame inside the game)' }
    throw "Could not identify one AION2 IPv4 server on port $discoveryPort. Keep AION2 open $screen, or supply -ServerAddress with an observed server IP."
}
$ServerAddress = (@($discovered) + @($ServerAddress -split ',' | Where-Object { $_ })) -join ','
$captureAddresses = @($ServerAddress.Split(',') | ForEach-Object {
    $address = [Net.IPAddress]::Parse($_.Trim())
    if ($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
        [Net.IPAddress]::IsLoopback($address) -or $address.Equals([Net.IPAddress]::Any)) {
        throw 'ServerAddress must contain non-loopback IPv4 addresses.'
    }
    $address.ToString()
} | Sort-Object -Unique)
if ($captureAddresses.Count -gt 16) {
    throw 'At most 16 observed server addresses may be supplied.'
}
$ServerAddress = $captureAddresses -join ','
$addressFilter = $ServerAddress
if ($captureAddresses.Count -gt 1) { $addressFilter = '(' + $ServerAddress + ')' }
$traceName = 'Aion2Research-' + [Guid]::NewGuid().ToString('N')
$directory = Join-Path $workspace ('research\local\capture-13700-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $traceName.Substring(14, 8))
$etl = Join-Path $directory 'capture.etl'
$pcap = Join-Path $directory 'capture.pcapng'
# netsh exposes address/protocol filters, not TCP-port filters on this system.
# Scope collection to the observed server, then analyze only port 13700 offline.
$startArguments = @('trace', 'start', "sessionname=$traceName", 'capture=yes',
    'capturetype=physical', 'report=no', 'persistent=no', 'correlation=disabled', 'perfMerge=no',
    "tracefile=$etl", "maxsize=$MaxSizeMb", 'filemode=circular', 'overwrite=no',
    'Ethernet.Type=IPv4', 'Protocol=TCP', "IPv4.Address=$addressFilter", ('CaptureMultiLayer=' + $(if ($MultiLayer) { 'yes' } else { 'no' })))
$stopArguments = @('trace', 'stop', "sessionname=$traceName")
$metadata = [ordered]@{
    schema_version = 2; backend = 'netsh/NDIS'; session_name = $traceName
    capture_multi_layer = [bool]$MultiLayer
    server_address = $ServerAddress; analysis_port = 13700
    server_addresses = $captureAddresses; suggested_analysis_ports = @(13700, 13328)
    capture_filter = "IPv4 TCP to/from $ServerAddress (all ports at capture time)"
    duration_seconds = $DurationSeconds; started_utc = $null; stopped_utc = $null
    file_size_limit_mb = $MaxSizeMb; log_mode = 'circular'
    capture_complete = $false; pcapng_created = $false
    start_arguments = $startArguments; stop_arguments = $stopArguments
    converter_version = '1.11.0'; converter_sha256 = $converterHash
    movement_cue_schedule = $movementSchedule
    movement_cue_note = 'Cues request manual actions. Timestamps do not measure actual key presses or character motion.'
}
if ($PlanOnly) {
    $metadata | ConvertTo-Json -Depth 5
    return
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Windows packet capture requires Administrator PowerShell. No trace changes were made.'
}
if (-not (Test-Path -LiteralPath $converter)) {
    throw "The Microsoft ETL converter is missing: $converter"
}
if ((Get-FileHash -LiteralPath $converter -Algorithm SHA256).Hash -ne $converterHash) {
    throw 'The ETL converter hash does not match the verified Microsoft release.'
}
if (Test-Path -LiteralPath $directory) { throw "Capture directory already exists: $directory" }
$null = New-Item -ItemType Directory -Path $directory
$captureStarted = $false
$captureStopped = $false

function Invoke-Netsh([string[]]$Arguments) {
    $lines = @(& $netsh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ($lines -join [Environment]::NewLine) }
    return ($lines -join [Environment]::NewLine)
}
function Save-Session {
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory 'session.json') -Encoding UTF8
}

try {
    Save-Session
    Write-Output (Invoke-Netsh -Arguments $startArguments)
    $captureStarted = $true
    $metadata.started_utc = [DateTime]::UtcNow.ToString('o')
    Save-Session
    Write-Output "Capture active for $DurationSeconds seconds, server $ServerAddress"
    Write-Output "Output: $directory"
    if ($MovementCues) {
        Write-Output 'Return to the existing game session and stand still in an open area. Keep the camera direction fixed.'
        Write-Output 'At 20 seconds: one beep = walk forward. At 23 seconds: two beeps = stop.'
        Write-Output 'At 43 seconds: one beep = walk backward. At 46 seconds: two beeps = stop.'
        Write-Output 'At 66 seconds: three beeps = movement sequence complete; remain still until capture ends.'
    } elseif ($InGame) {
        Write-Output 'Play the planned session. Between phases, tools/capture_mark.ps1 "<note>" timestamps a note into this recording.'
    } else {
        Write-Output 'For an opening handshake, restart AION2 normally through Steam, select your server, and continue through startup.'
    }
    Write-Output 'Keep this window open until conversion finishes.'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $cueIndex = 0
    while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
        while ($cueIndex -lt $movementSchedule.Count -and $timer.Elapsed.TotalSeconds -ge $movementSchedule[$cueIndex].at_seconds) {
            $cue = $movementSchedule[$cueIndex]
            $cueRecord = [ordered]@{ kind = 'manual_action_cue'; action = $cue.action
                scheduled_seconds = $cue.at_seconds; elapsed_seconds = $timer.Elapsed.TotalSeconds
                cue_utc = [DateTime]::UtcNow.ToString('o'); actual_action_confirmed = $false }
            $cueRecord | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $directory 'actions.jsonl') -Encoding UTF8
            Write-Output ("Movement cue: " + $cue.action)
            try {
                for ($beepIndex = 0; $beepIndex -lt $cue.beeps; $beepIndex++) {
                    [Console]::Beep(880, 150)
                    if ($beepIndex + 1 -lt $cue.beeps) { Start-Sleep -Milliseconds 100 }
                }
            } catch { Write-Warning 'Audio cue unavailable; use the printed schedule.' }
            $cueIndex++
        }
        if ($MovementCues) { Start-Sleep -Milliseconds 100 } else { Start-Sleep -Seconds 1 }
    }
    Write-Output (Invoke-Netsh -Arguments $stopArguments)
    $captureStopped = $true
    $metadata.stopped_utc = [DateTime]::UtcNow.ToString('o')
    $metadata.capture_complete = $true
    Save-Session
    & $converter $etl $pcap
    if ($LASTEXITCODE -ne 0) { throw "ETL conversion failed with exit code $LASTEXITCODE. The ETL is preserved at $etl" }
    $metadata.pcapng_created = Test-Path -LiteralPath $pcap
    Write-Output "Ready for analysis: $pcap"
} finally {
    if ($captureStarted -and -not $captureStopped) {
        try {
            Write-Output (Invoke-Netsh -Arguments $stopArguments)
            $metadata.stopped_utc = [DateTime]::UtcNow.ToString('o')
        } catch {
            Write-Warning "Could not stop the research trace. Recovery command: netsh trace stop sessionname=$traceName"
        }
    }
    Save-Session
}
