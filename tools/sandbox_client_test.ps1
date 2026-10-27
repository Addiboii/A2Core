function Save-TestDiagnostics {
    param([switch]$IncludeEvents)
    $log = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ncglog.txt'
    try {
        if (Test-Path -LiteralPath $log) {
            Copy-Item -LiteralPath $log -Destination ($reportPath + '.ncglog.txt') -Force
        }
    } catch {
        $_ | Out-String | Add-Content ($reportPath + '.collection-errors.txt')
    }
    if ($IncludeEvents) {
        foreach ($channel in @('Application', 'System')) {
            try {
                $events = @(Get-WinEvent -FilterHashtable @{
                    LogName = $channel
                    StartTime = [DateTime]::Parse($report.started_utc).ToLocalTime()
                    Level = @(1,2,3)
                } -ErrorAction Stop |
                    Select-Object TimeCreated,ProviderName,Id,Message)
                ConvertTo-Json -InputObject $events -Depth 5 |
                    Set-Content ($reportPath + ".$channel.json") -Encoding UTF8
            } catch {
                $_ | Out-String |
                    Add-Content ($reportPath + '.collection-errors.txt')
            }
        }
    }
}
# Generated Sandbox configuration invokes this inside the disposable guest only.
# Do not run on the host. No authentication arguments or host AppData are supplied.
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or !(Test-Path -LiteralPath 'C:\AionTest\Results')) {
    throw 'This test is intended only for the prepared Windows Sandbox guest.'
}
$exe = 'C:\AionTest\Client\Aion2\Binaries\Win64\AION2.exe'
$reportPath = Join-Path 'C:\AionTest\Results' ('startup-' + [guid]::NewGuid().ToString('N') + '.json')
$report = [ordered]@{
    schema_version = 1
    started_utc = [DateTime]::UtcNow.ToString('o')
    status = 'preflight'
    arguments = @('-steam', '-saveddirsuffix=AionDev', '-NoDebugExecBindings')
    saved_files = @()
    audit_errors = @()
    limitations = @('Offline guest only; no account credentials supplied.',
        'Read-only installation can prevent startup that needs installation writes.',
        'Saved-file snapshot is not a complete file/registry/process trace.',
        'VM graphics, absent Steam, or other missing services may prevent startup.',
        'Success here would not prove that a suffix isolates a normal host launch.')
}
try {
    $report.executable_sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($report.executable_sha256 -ne 'd93b050d623550fcf509888a9afe9cb8bd3864869f06dd3fe9ecaa697337f1ba') {
        throw 'Unexpected development executable hash'
    }
    $client = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $report.arguments -PassThru -WindowStyle Hidden
    $report.pid = $client.Id
    $report.status = 'observing'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    # Two minutes maximum; an exited launcher does not establish child completion.
    for ($sample = 0; $sample -lt 24; $sample++) { Start-Sleep -Seconds 5; Save-TestDiagnostics }
    $client.Refresh()
    $report.status = if ($client.HasExited) { 'initial_process_exited' } else { 'initial_process_running' }
    if ($client.HasExited) { $report.exit_code = $client.ExitCode }
    $savedRoot = Join-Path $env:LOCALAPPDATA 'AION2'
    $report.saved_root_present = Test-Path -LiteralPath $savedRoot
    if ($report.saved_root_present) {
        $scanErrors = @()
        $report.saved_files = @(Get-ChildItem -LiteralPath $savedRoot -Recurse -File -ErrorAction SilentlyContinue -ErrorVariable scanErrors | ForEach-Object {
            [ordered]@{ path = $_.FullName.Substring($savedRoot.Length + 1); size = $_.Length }
        })
        $report.audit_errors = @($scanErrors | ForEach-Object { $_.Exception.Message })
    }
} catch {
    $report.status = 'error'
    $report.error = $_.Exception.Message
} finally { Save-TestDiagnostics -IncludeEvents
    $report.finished_utc = [DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "Report saved to $reportPath. Close Windows Sandbox to discard the guest."
}

