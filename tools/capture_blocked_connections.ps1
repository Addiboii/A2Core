# Record blocked dev-client traffic without permitting external connections.
[CmdletBinding()]
param(
    [ValidateRange(30,900)][int]$DurationSeconds = 360,
    [switch]$PlanOnly,
    [switch]$CaptureDns,
    [string]$RestorePolicyFrom
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$clientRoot = (Resolve-Path (Join-Path $workspace 'research/local/dev-client')).Path
# These language-independent GUIDs are Filtering Platform Packet Drop / Connection.
$categories = @('0cce9225-69ae-11d9-bed3-505054503030','0cce9226-69ae-11d9-bed3-505054503030')
if ($PlanOnly) {
    "Record Security events 5152/5157 for EXEs below $clientRoot for $DurationSeconds seconds."
    'Temporarily enable failure auditing for two WFP subcategories; restore it in finally.'
    'No firewall rules, endpoints, client files or network permissions are changed.'
    if ($CaptureDns) { 'Also record DNS Client ETW events to a bounded 32 MB local trace.' }
    return
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this recorder from Administrator PowerShell.'
}
function Read-AuditPolicy([string]$id) {
    $csv = & auditpol.exe /get /subcategory:"{$id}" /r
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read WFP audit policy.' }
    $rows = @($csv | ConvertFrom-Csv)
    if ($rows.Count -ne 1) { throw 'Unexpected auditpol response.' }
    $values = @($rows[0].PSObject.Properties | ForEach-Object { $_.Value })
    $flags = switch ($values[4]) {
        'No Auditing' { 0 }
        'Success' { 1 }
        'Failure' { 2 }
        'Success and Failure' { 3 }
        default { throw "Unrecognized auditpol inclusion setting: $($values[4])" }
    }
    return @{Flags=$flags}
}
function Set-FailureAudit([string]$id, [bool]$enabled) {
    $setting = if ($enabled) { 'enable' } else { 'disable' }
    $null = & auditpol.exe /set /subcategory:"{$id}" /failure:$setting
    if ($LASTEXITCODE -ne 0) { throw 'Cannot set WFP failure auditing.' }
    $actual = Read-AuditPolicy $id
    if ((($actual.Flags -band 2) -ne 0) -ne $enabled) { throw 'WFP audit readback mismatch.' }
}
function Restore-Policy($saved) {
    foreach ($row in $saved) {
        if ($row.id -notin $categories) { throw 'Unexpected audit subcategory in recovery file.' }
        # Restore only the failure setting; preserve the success setting.
        Set-FailureAudit $row.id (([uint32]$row.flags -band 2) -ne 0)
    }
}
if ($RestorePolicyFrom) {
    Restore-Policy (Get-Content -LiteralPath $RestorePolicyFrom -Raw | ConvertFrom-Json)
    'Restored WFP failure-audit settings.'
    return
}
$out = Join-Path $workspace ('research/local/blocked-connections-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $out | Out-Null
$saved = @($categories | ForEach-Object {
    $policy = (Read-AuditPolicy $_)
    @{id=$_; flags=$policy.Flags}
})
$saved | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'audit-policy-before.json') -Encoding UTF8
$start = Get-Date
$since = $start
$seen = New-Object 'System.Collections.Generic.HashSet[long]'
$suffix = $clientRoot.Substring(2).TrimEnd('\') + '\'
$count = 0
$dnsStarted = $false
$dnsSession = 'AionDns-' + [guid]::NewGuid().ToString('N')
try {
    if ($CaptureDns) {
        $null = & logman.exe start $dnsSession -ets -p 'Microsoft-Windows-DNS-Client' 0xffffffffffffffff 5 -o (Join-Path $out 'dns.etl') -f bincirc -max 32
        if ($LASTEXITCODE -ne 0) { throw 'DNS ETW recorder could not start.' }
        $dnsStarted = $true
        $dnsSession | Set-Content -LiteralPath (Join-Path $out 'dns-session.txt') -Encoding UTF8
    }
    foreach ($id in $categories) {
        Set-FailureAudit $id $true
    }
    @($categories | ForEach-Object {
        $verified = (Read-AuditPolicy $_)
        @{id=$_; flags=$verified.Flags}
    }) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'audit-policy-active.json') -Encoding UTF8
    @{utc=$start.ToUniversalTime().ToString('o'); duration_seconds=$DurationSeconds; client_root=$clientRoot} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'ready.json') -Encoding UTF8
    "Recorder ready: $out"
    'Start the development game normally. Firewall blocks remain in place.'
    do {
        $until = Get-Date
        $events = @(Get-WinEvent -FilterHashtable @{LogName='Security'; Id=5152,5157; StartTime=$since; EndTime=$until} -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
        foreach ($err in $eventErrors) {
            if ($err.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw $err }
        }
        foreach ($event in $events) {
            if (-not $seen.Add($event.RecordId)) { continue }
            [xml]$xml = $event.ToXml()
            $fields = @{}
            foreach ($node in $xml.Event.EventData.Data) { $fields[$node.Name] = $node.InnerText }
            $app = [string]$fields['Application']
            if ($app.IndexOf($suffix,[StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
            @{utc=$event.TimeCreated.ToUniversalTime().ToString('o'); event_id=$event.Id; record_id=$event.RecordId;
              application=$app; process_id=$fields['ProcessID']; direction=$fields['Direction'];
              source_address=$fields['SourceAddress']; source_port=$fields['SourcePort'];
              destination_address=$fields['DestAddress']; destination_port=$fields['DestPort'];
              protocol=$fields['Protocol']; filter_id=$fields['FilterRTID']; layer=$fields['LayerName']} |
                ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $out 'blocked.jsonl') -Encoding UTF8
            $count++
        }
        $since = $until.AddSeconds(-2)
        Start-Sleep -Seconds 2
    } while (((Get-Date)-$start).TotalSeconds -lt $DurationSeconds -and -not (Test-Path (Join-Path $out 'stop')))
} finally {
    if ($dnsStarted) {
        $null = & logman.exe stop $dnsSession -ets
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not stop DNS trace $dnsSession; stop it manually with logman stop SESSION -ets." }
    }
    Restore-Policy $saved
    @{utc=(Get-Date).ToUniversalTime().ToString('o'); blocked_events=$count; audit_restored=$true} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'finished.json') -Encoding UTF8
}
"Recorded $count blocked dev-client events. Restored WFP failure-audit settings."
