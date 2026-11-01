<#
Passive official-server capture: uniquely named netsh/NDIS trace (pktmon is not used; netsh is built-in on all targets).
Run in Administrator PowerShell; -PlanOnly previews commands without starting a trace or requiring elevation.
Press Q in the capture window to stop early.
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
. (Join-Path $PSScriptRoot 'capture_session.ps1')

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
$workspace = Get-CaptureWorkspace
$converterInfo = if ($PlanOnly) { @{ Sha256 = 'C2D03AAC43EA0F5626FC8D909E6688393FEAF71BD6FACD39AAF8A637382D9043'; Version = '1.11.0' } } else { Get-VerifiedEtlConverter }

# Discovery: at login the port-13700 peer; in the game (-InGame or -MovementCues) the port-13328 game server.
$discoveryPort = if ($MovementCues -or $InGame) { 13328 } else { 13700 }
if ($InGame) { $MultiLayer = $true }

$gameIds = Get-AionGameProcessIds
$discovered = Discover-AionServerAddresses -RemotePort $discoveryPort -ProcessIds $gameIds
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
if ($captureAddresses.Count -gt 16) { throw 'At most 16 observed server addresses may be supplied.' }
$ServerAddress = $captureAddresses -join ','
$addressFilter = if ($captureAddresses.Count -gt 1) { '(' + $ServerAddress + ')' } else { $ServerAddress }

$traceName = 'Aion2Research-' + [Guid]::NewGuid().ToString('N')
$directory = Join-Path $workspace ('research\local\capture-' + $discoveryPort + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $traceName.Substring(14, 8))
$etl = Join-Path $directory 'capture.etl'
$pcap = Join-Path $directory 'capture.pcapng'

$startArguments = @('trace', 'start', "sessionname=$traceName", 'capture=yes',
    'capturetype=physical', 'report=no', 'persistent=no', 'correlation=disabled', 'perfMerge=no',
    "tracefile=$etl", "maxsize=$MaxSizeMb", 'filemode=circular', 'overwrite=no',
    'Ethernet.Type=IPv4', 'Protocol=TCP', "IPv4.Address=$addressFilter", ('CaptureMultiLayer=' + $(if ($MultiLayer) { 'yes' } else { 'no' })))
$stopArguments = @('trace', 'stop', "sessionname=$traceName")
$metadata = [ordered]@{
    schema_version = 3; backend = 'netsh/NDIS'; session_name = $traceName
    capture_multi_layer = [bool]$MultiLayer; discovery_port = $discoveryPort
    server_address = $ServerAddress; analysis_port = $discoveryPort
    server_addresses = $captureAddresses; suggested_analysis_ports = @(13700, 13328)
    capture_filter = "IPv4 TCP to/from $ServerAddress (all ports at capture time)"
    duration_seconds = $DurationSeconds; started_utc = $null; stopped_utc = $null; stopped_early = $false
    file_size_limit_mb = $MaxSizeMb; log_mode = 'circular'
    capture_complete = $false; pcapng_created = $false
    output_directory = $directory; pcapng_path = $pcap
    start_arguments = $startArguments; stop_arguments = $stopArguments
    converter_version = $converterInfo.Version; converter_sha256 = $converterInfo.Sha256
    movement_cue_schedule = $movementSchedule
    movement_cue_note = 'Cues request manual actions. Timestamps do not measure actual key presses or character motion.'
}
if ($PlanOnly) {
    $metadata | ConvertTo-Json -Depth 6
    return
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Windows packet capture requires Administrator PowerShell. No trace changes were made.'
}
if (Test-Path -LiteralPath $directory) { throw "Capture directory already exists: $directory" }
$null = New-Item -ItemType Directory -Path $directory
$captureStarted = $false
$captureStopped = $false
$stoppedEarly = $false

try {
    Save-CaptureSession -Directory $directory -Metadata $metadata
    Write-Output (Invoke-NetshTrace -Arguments $startArguments)
    $captureStarted = $true
    $metadata.started_utc = [DateTime]::UtcNow.ToString('o')
    Save-CaptureSession -Directory $directory -Metadata $metadata
    Write-Output "Capture active for up to $DurationSeconds seconds, server $ServerAddress (port $discoveryPort filter for discovery)"
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
    Write-Output 'Press Q in this window to stop early. Keep it open until conversion finishes.'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $cueIndex = 0
    while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq 'Q') {
                $stoppedEarly = $true
                Write-Output 'Stopping capture early (Q pressed).'
                break
            }
        }
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
        if ($MovementCues) { Start-Sleep -Milliseconds 100 } else { Start-Sleep -Milliseconds 250 }
    }
    Write-Output (Invoke-NetshTrace -Arguments $stopArguments)
    $captureStopped = $true
    $metadata.stopped_utc = [DateTime]::UtcNow.ToString('o')
    $metadata.capture_complete = $true
    $metadata.stopped_early = [bool]$stoppedEarly
    Save-CaptureSession -Directory $directory -Metadata $metadata
    Complete-CaptureRecording -Directory $directory -SkipStop
} finally {
    if ($captureStarted -and -not $captureStopped) {
        try {
            Write-Output (Invoke-NetshTrace -Arguments $stopArguments)
            $metadata.stopped_utc = [DateTime]::UtcNow.ToString('o')
            $metadata.capture_complete = $true
            $metadata.stopped_early = [bool]$stoppedEarly
        } catch {
            Write-Warning "Could not stop the research trace. Recovery: tools/capture-stop.cmd or netsh trace stop sessionname=$traceName"
        }
        Save-CaptureSession -Directory $directory -Metadata $metadata
        try { Complete-CaptureRecording -Directory $directory -SkipStop } catch { Write-Warning $_ }
    }
}
