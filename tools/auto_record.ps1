<#
Automatic recorder for official AION2 sessions (docs/CAPTURE.md, "Automatic recording").

Waits for the game to connect to an OFFICIAL game server (port 13328, public address; your own A2Core server on the LAN
is skipped), records it with tools/capture_port13700.ps1 -InGame -AutoStop, starts a new recording when a map change
moves the client to another game server, stops when the game closes, then decodes each recording in the background with
tools/capture_timeline.py. Recordings land in research/local/capture-13700-<date>-<id>/ as usual; the oldest are removed
once all of them pass -KeepGb. Log: research/local/auto-record.log.

With -UploadTo 'user@server:captures' each finished (and decoded) recording is also copied to that folder on the Linux
server with scp, using the key %USERPROFILE%\.ssh\id_ed25519_a2core (A2Core-windows-setup.bat creates and authorizes it).
Failed uploads are retried every 5 minutes; uploaded folders get a .uploaded marker.

  # once, in Administrator PowerShell in the repository folder: start it hidden at every logon
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/auto_record.ps1 -Install
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/auto_record.ps1 -Uninstall
  # or run it in the foreground (Administrator PowerShell), Ctrl+C to stop
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/auto_record.ps1

Passive like capture.ps1: a Windows network trace of your own client's traffic; nothing is injected or modified.
Recordings are personal data (see "Sharing safely" in docs/CAPTURE.md).
#>
[CmdletBinding()]
param(
    [ValidateRange(5, 120)][int]$ChunkMinutes = 60,
    [ValidateRange(64, 4096)][int]$MaxSizeMb = 1024,
    [ValidateRange(1, 2000)][int]$KeepGb = 30,
    [switch]$KeepEtl,
    [switch]$IncludePrivate,
    [switch]$Install,
    [switch]$Uninstall,
    [string]$UploadTo
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $workspace 'research\local'
$taskName = 'A2Core auto recorder'

function Test-Admin {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($Install -or $Uninstall) {
    if (-not (Test-Admin)) { throw 'Run -Install / -Uninstall in Administrator PowerShell.' }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Output "Removed the scheduled task '$taskName'."
    }
    if ($Uninstall) { return }
    $arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -ChunkMinutes $ChunkMinutes -MaxSizeMb $MaxSizeMb -KeepGb $KeepGb"
    if ($KeepEtl) { $arguments += ' -KeepEtl' }
    if ($IncludePrivate) { $arguments += ' -IncludePrivate' }
    if ($UploadTo) { $arguments += " -UploadTo `"$UploadTo`"" }
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $workspace
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Write-Output "Installed and started '$taskName' (runs hidden at every logon). Log: $local\auto-record.log"
    return
}

if (-not (Test-Admin)) { throw 'Recording needs Administrator PowerShell (or install it with -Install).' }
$null = New-Item -ItemType Directory -Force -Path $local
$log = Join-Path $local 'auto-record.log'
function Log([string]$Message) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $Message
    Add-Content -LiteralPath $log -Value $line -Encoding UTF8
    Write-Output $line
}

function Test-Private([string]$Address) {
    $b = [Net.IPAddress]::Parse($Address).GetAddressBytes()
    return $b[0] -eq 10 -or $b[0] -eq 127 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or
        ($b[0] -eq 192 -and $b[1] -eq 168) -or ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127) -or
        ($b[0] -eq 169 -and $b[1] -eq 254)
}

function Get-OfficialPeers {
    $gameIds = @(Get-Process -Name AION2 -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.Id })
    if ($gameIds.Count -eq 0) { return @() }
    $peers = @()
    foreach ($row in @(& "$env:SystemRoot\System32\netstat.exe" -ano -p tcp)) {
        $fields = $row.Trim() -split '\s+'
        if ($fields.Count -eq 5 -and $fields[0] -eq 'TCP' -and $fields[4] -in $gameIds -and $fields[3] -eq 'ESTABLISHED' -and
            $fields[2] -match '^(\d+\.\d+\.\d+\.\d+):13328$') { $peers += $Matches[1] }
    }
    return @($peers | Sort-Object -Unique | Where-Object { $IncludePrivate -or -not (Test-Private $_) })
}

function Find-Python {
    foreach ($name in 'py', 'python', 'python3') {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command -and $command.Source -notlike '*WindowsApps*') { return $command.Source }
    }
    return $null
}

function Remove-OldRecordings {
    $dirs = @(Get-ChildItem -LiteralPath $local -Directory -Filter 'capture-13700-*' | Sort-Object CreationTime)
    $sizes = @{}
    foreach ($d in $dirs) { $sizes[$d.FullName] = (Get-ChildItem -LiteralPath $d.FullName -Recurse -File | Measure-Object Length -Sum).Sum }
    $total = ($sizes.Values | Measure-Object -Sum).Sum
    foreach ($d in $dirs) {
        if ($total -le $KeepGb * 1GB) { break }
        Remove-Item -LiteralPath $d.FullName -Recurse -Force
        $total -= $sizes[$d.FullName]
        Log "Removed old recording $($d.Name) (over $KeepGb GB)"
    }
}

$decoding = @{}
$nextUpload = [datetime]::MinValue
$uploadKey = Join-Path $env:USERPROFILE '.ssh\id_ed25519_a2core'
function Send-Recordings {
    if (-not $UploadTo -or (Get-Date) -lt $script:nextUpload) { return }
    foreach ($dir in @(Get-ChildItem -LiteralPath $local -Directory -Filter 'capture-13700-*' | Sort-Object CreationTime)) {
        if (Test-Path -LiteralPath (Join-Path $dir.FullName '.uploaded')) { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $dir.FullName 'capture.pcapng'))) { continue }
        $decoder = $decoding[$dir.FullName]
        if ($decoder -and -not $decoder.HasExited) { continue }
        $output = @(& scp.exe -B -q -r -i $uploadKey -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 $dir.FullName "$UploadTo/" 2>&1)
        if ($LASTEXITCODE -ne 0) {
            Log "Upload of $($dir.Name) to $UploadTo failed, retrying in 5 minutes: $($output -join ' ')"
            $script:nextUpload = (Get-Date).AddMinutes(5)
            return
        }
        $null = New-Item -ItemType File -Force -Path (Join-Path $dir.FullName '.uploaded')
        $decoding.Remove($dir.FullName)
        Log "Uploaded $($dir.Name) to $UploadTo"
    }
}

$python = Find-Python
Log "Auto recorder started (chunks of $ChunkMinutes min, up to $MaxSizeMb MB each, keeping $KeepGb GB). Python: $(if ($python) { $python } else { 'not found, recordings will not be decoded' })"
$capture = Join-Path $PSScriptRoot 'capture_port13700.ps1'
$waitingSaid = $false
while ($true) {
    $peers = @(Get-OfficialPeers)
    if ($peers.Count -eq 0) {
        Send-Recordings
        if (-not $waitingSaid) { Log 'Waiting for AION2 to enter an official game server...'; $waitingSaid = $true }
        Start-Sleep -Seconds 10
        continue
    }
    $waitingSaid = $false
    Log "Recording official game server $($peers -join ',')"
    $before = @(Get-ChildItem -LiteralPath $local -Directory -Filter 'capture-13700-*' | ForEach-Object FullName)
    try {
        & $capture -InGame -AutoStop -ServerAddress ($peers -join ',') -DurationSeconds ($ChunkMinutes * 60) -MaxSizeMb $MaxSizeMb 2>&1 |
            ForEach-Object { Log "  $_" }
    } catch {
        Log "Recording failed: $($_.Exception.Message)"
        Start-Sleep -Seconds 30
    }
    $new = @(Get-ChildItem -LiteralPath $local -Directory -Filter 'capture-13700-*' | Where-Object { $_.FullName -notin $before })
    foreach ($dir in $new) {
        $pcap = Join-Path $dir.FullName 'capture.pcapng'
        if (-not (Test-Path -LiteralPath $pcap)) { Log "No pcapng in $($dir.Name); the .etl is kept"; continue }
        if (-not $KeepEtl) { Remove-Item -LiteralPath (Join-Path $dir.FullName 'capture.etl') -ErrorAction SilentlyContinue }
        if ($python) {
            $timeline = Join-Path $PSScriptRoot 'capture_timeline.py'
            $decodeLog = Join-Path $dir.FullName 'decode.log'
            $decoding[$dir.FullName] = Start-Process -PassThru -FilePath $python -ArgumentList @("`"$timeline`"", "`"$pcap`"") -WorkingDirectory $workspace -NoNewWindow `
                -RedirectStandardOutput $decodeLog -RedirectStandardError (Join-Path $dir.FullName 'decode.err')
            Log "Decoding $($dir.Name) in the background (timeline-13328/)"
        }
    }
    Remove-OldRecordings
}
