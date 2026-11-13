<#
.SYNOPSIS
  Database dump of the A2Core server (scheduled task "A2Core nightly backup"; update.ps1 also runs it before a deploy).

.DESCRIPTION
  1. pg_dump -Fc of the "aion" database into <Root>\backups\aion-<UTC time>.dump (written as .partial first, so a broken run
     never looks like a finished dump), then read back with pg_restore --list (the archive must list table data).
  2. Keeps about 14 days: every dump of the last 48 hours, then the newest dump of each day, nothing older than 14 days
     (the 3 newest are always kept).
  3. Writes <Root>\backups\backup-status.json (the staff "Server health" page reads it) and, on failure, appends a RED line
     to the alert log (Aion__Ops__AlertLog of <Root>\aion.env, else <Root>\logs\alerts.log). Exit code 1 on failure.
  Read-only for the live database (pg_dump takes a snapshot).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File G:\A2Core\backup.ps1
#>
param(
    [string] $Root = $PSScriptRoot,
    [string] $AlertLog = "",
    [int] $KeepDays = 14
)
$ErrorActionPreference = "Stop"
$backups = Join-Path $Root "backups"
$statusFile = Join-Path $backups "backup-status.json"
$envFile = Join-Path $Root "aion.env"
function Setting($name) {
    if (Test-Path $envFile) { foreach ($line in Get-Content $envFile) { if ($line.StartsWith("$name=")) { return $line.Substring($name.Length + 1).Trim() } } }
    return $null
}
if (-not $AlertLog) { $AlertLog = Setting "Aion__Ops__AlertLog" }
if (-not $AlertLog) { $AlertLog = Join-Path $Root "logs\alerts.log" }
$port = 55432
$connection = Setting "ConnectionStrings__Aion"
if ($connection -and $connection -match 'Port=(\d+)') { $port = [int]$Matches[1] }
$pgBin = Join-Path $Root "pgsql\bin"
$utf8 = New-Object Text.UTF8Encoding $false

function Native([scriptblock] $block) {
    # pg tools write notices to stderr; under PowerShell 5.1 with Stop that would be fatal: judge by the exit code instead.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $out = & $block 2>&1 | ForEach-Object { "$_" }; return @{ Code = $LASTEXITCODE; Text = ($out -join "`n") } }
    finally { $ErrorActionPreference = $previous }
}
function Alert($text) {
    try {
        [void][IO.Directory]::CreateDirectory((Split-Path $AlertLog))
        [IO.File]::AppendAllText($AlertLog, (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") + "Z A2Core backup $text`r`n", $utf8)
    } catch { Write-Warning "alert not written: $_" }
}
function Status($fields) {
    $fields["at"] = (Get-Date).ToUniversalTime().ToString("o")
    [IO.File]::WriteAllText($statusFile, ($fields | ConvertTo-Json -Depth 4), $utf8)
}

$started = Get-Date
$partial = $null
try {
    New-Item -ItemType Directory -Force $backups | Out-Null
    $env:PGPASSWORD = (Get-Content (Join-Path $Root "postgres\credentials.json") -Raw | ConvertFrom-Json).aion_admin
    $name = "aion-" + (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss") + ".dump"
    $out = Join-Path $backups $name
    $partial = "$out.partial"
    $dump = Native { & (Join-Path $pgBin "pg_dump.exe") -h 127.0.0.1 -p $port -U aion_admin -Fc -f $partial aion }
    if ($dump.Code -ne 0) { throw "pg_dump failed (exit $($dump.Code)): $($dump.Text)" }
    $toc = Native { & (Join-Path $pgBin "pg_restore.exe") --list $partial }
    if ($toc.Code -ne 0 -or $toc.Text -notmatch "TABLE DATA") { throw "the new dump cannot be read back (pg_restore --list exit $($toc.Code))" }
    Move-Item $partial $out
    $partial = $null

    # Retention: all of the last 48 h, then the newest per day, up to $KeepDays days; the 3 newest always stay.
    $now = (Get-Date).ToUniversalTime()
    $days = @{}
    $removed = 0
    $index = 0
    foreach ($file in Get-ChildItem $backups -Filter "aion-*.dump" | Sort-Object LastWriteTimeUtc -Descending) {
        $age = $now - $file.LastWriteTimeUtc
        $day = $file.LastWriteTimeUtc.ToString("yyyyMMdd")
        $drop = $index -ge 3 -and ($age.TotalDays -gt $KeepDays -or ($age.TotalHours -gt 48 -and $days.ContainsKey($day)))
        if ($drop) { Remove-Item $file.FullName -Force; $removed++ } else { $days[$day] = $true }
        $index++
    }
    Get-ChildItem $backups -Filter "*.partial" | Where-Object { $_.LastWriteTimeUtc -lt $now.AddDays(-1) } | Remove-Item -Force
    $kept = @(Get-ChildItem $backups -Filter "aion-*.dump").Count
    $bytes = (Get-Item $out).Length
    Status ([ordered]@{ ok = $true; file = $name; bytes = $bytes; seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1); kept = $kept; removed = $removed; error = $null })
    Write-Host "Backup $name ($([math]::Round($bytes / 1MB, 1)) MB); $kept dumps kept, $removed old ones removed"
} catch {
    $message = "$_" -replace "\s+", " "
    if ($message.Length -gt 300) { $message = $message.Substring(0, 300) }
    try { Status ([ordered]@{ ok = $false; file = $null; seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1); error = $message }) } catch { }
    Alert "RED backup: the database backup FAILED: $message"
    Write-Error $message -ErrorAction Continue
    exit 1
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    if ($partial -and (Test-Path $partial)) { Remove-Item $partial -Force -ErrorAction SilentlyContinue }
}
