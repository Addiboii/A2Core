<#
Samples processes from the AION2 installation and their TCP/UDP endpoints.
Does not launch the game, capture packet contents, or collect command lines.
#>
[CmdletBinding()]
param(
    [string]$Install = 'C:\Program Files (x86)\Steam\steamapps\common\AION2',
    [ValidateRange(1, 3600)][int]$DurationSeconds = 90,
    [ValidateRange(1, 60)][int]$IntervalSeconds = 2,
    [string]$Output = (Join-Path $PSScriptRoot ('..\research\local\startup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.jsonl'))
)

$ErrorActionPreference = 'Stop'
$installRoot = (Resolve-Path -LiteralPath $Install).Path.TrimEnd('\') + '\'
$outputPath = [IO.Path]::GetFullPath($Output)
if ($outputPath.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must be outside the game installation.'
}
$null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($outputPath)) -Force
if (Test-Path -LiteralPath $outputPath) { throw "Output already exists: $outputPath" }
$writer = [IO.StreamWriter]::new($outputPath, $false, [Text.UTF8Encoding]::new($false))
$timer = [Diagnostics.Stopwatch]::StartNew()
$sampleCount = 0
$seenProcesses = @{}
$seenConnections = @{}

function Write-Record($record) {
    $writer.WriteLine(($record | ConvertTo-Json -Depth 8 -Compress))
    $writer.Flush()
}

try {
    Write-Record ([ordered]@{
        kind = 'session'; schema_version = 1; collected_utc = [DateTime]::UtcNow.ToString('o')
        installation = $installRoot; duration_seconds = $DurationSeconds
        interval_seconds = $IntervalSeconds; connection_source = 'netstat -ano'
        limitations = @('Polling can miss short-lived processes and connections.',
            'UDP reports local bindings, not remote peers.',
            'AION2 processes with inaccessible paths are included by name and marked unverified.',
            'Other processes outside the installation are excluded.',
            'Modules may be inaccessible for protected processes.')
    })
    Write-Output "Observing AION2 for $DurationSeconds seconds. Report: $outputPath"
    do {
        $processes = @()
        $issues = @()
        foreach ($process in @(Get-Process)) {
            try { $exePath = $process.Path } catch { $exePath = $null }
            $pathVerified = $exePath -and $exePath.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase)
            $nameOnly = -not $exePath -and $process.ProcessName -eq 'AION2'
            if (-not $pathVerified -and -not $nameOnly) { continue }
            $processKey = [string]$process.Id
            $record = [ordered]@{ pid = $process.Id; name = $process.ProcessName
                executable = $null; match_basis = 'process_name_only' }
            if ($pathVerified) {
                $record.executable = $exePath.Substring($installRoot.Length)
                $record.match_basis = 'installation_path'
            } else { $issues += "Executable path unavailable for PID $processKey; included by name only" }
            try { $record.started_utc = $process.StartTime.ToUniversalTime().ToString('o') }
            catch { $record.started_utc = $null }
            $record.installed_modules = @()
            try {
                $modules = @($process.Modules)
                if ($modules.Count -eq 0 -or $null -eq $modules[0]) {
                    $issues += "Module enumeration returned no data for PID $processKey"
                }
                $record.installed_modules = @($modules | Where-Object { $null -ne $_ } | ForEach-Object {
                    if ($_.FileName.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase)) {
                        $_.FileName.Substring($installRoot.Length)
                    }
                } | Sort-Object -Unique)
            } catch {
                $issues += "Module enumeration unavailable for PID $processKey"
            }
            $processes += $record
            if (-not $seenProcesses.ContainsKey($processKey)) {
                Write-Output "Observed $($process.ProcessName), PID $processKey"
                $seenProcesses[$processKey] = $true
            }
        }
        $trackedIds = @{}
        foreach ($process in $processes) { $trackedIds[[string]$process.pid] = $true }
        $connections = @()
        if ($processes.Count -gt 0) {
            # netstat reads the endpoint tables without the CIM permissions
            # required by Get-NetTCPConnection in some restricted environments.
            $lines = @(& "$env:SystemRoot\System32\netstat.exe" -ano 2>&1)
            if ($LASTEXITCODE -ne 0) { $issues += "netstat failed with exit code $LASTEXITCODE" }
            else {
                foreach ($line in $lines) {
                    $fields = ([string]$line).Trim() -split '\s+'
                    if ($fields.Count -lt 4 -or $fields[0] -notin @('TCP', 'UDP')) { continue }
                    $owner = $fields[-1]
                    if (-not $trackedIds.ContainsKey($owner)) { continue }
                    $endpoint = [ordered]@{ protocol = $fields[0]; pid = [int]$owner
                        local_endpoint = $fields[1] }
                    if ($fields[0] -eq 'TCP' -and $fields.Count -eq 5) {
                        $endpoint.remote_endpoint = $fields[2]
                        $endpoint.state = $fields[3]
                    } elseif ($fields[0] -eq 'UDP' -and $fields.Count -eq 4) {
                        $endpoint.remote_endpoint = $null
                        $endpoint.state = 'BOUND'
                    } else { continue }
                    $connections += $endpoint
                    $connectionKey = $endpoint | ConvertTo-Json -Compress
                    $seenConnections[$connectionKey] = $true
                }
            }
        }
        Write-Record ([ordered]@{ kind = 'sample'; collected_utc = [DateTime]::UtcNow.ToString('o')
            processes = @($processes); connections = @($connections); issues = @($issues) })
        $sampleCount++
        $remaining = $DurationSeconds - $timer.Elapsed.TotalSeconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]([Math]::Min($IntervalSeconds, $remaining) * 1000)) }
    } while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds)
    Write-Record ([ordered]@{ kind = 'summary'; collected_utc = [DateTime]::UtcNow.ToString('o')
        samples = $sampleCount; distinct_pids = $seenProcesses.Count
        distinct_endpoint_states = $seenConnections.Count })
    Write-Output "Finished: $sampleCount samples, $($seenProcesses.Count) process IDs, $($seenConnections.Count) endpoint states."
} finally {
    $writer.Dispose()
}
