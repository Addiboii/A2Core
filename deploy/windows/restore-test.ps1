<#
.SYNOPSIS
  Monthly restore test of the A2Core database backups (scheduled task "A2Core monthly restore test").

.DESCRIPTION
  Restores the newest dump of <Root>\backups into the scratch database "aion_restore_test" on the same PostgreSQL, then
  compares it with the live database: every table of the dump must exist after the restore, and the row counts are compared
  with the live ones (the game kept running since the dump, so small differences are normal; a table that is empty after the
  restore while the live one has rows, or less than half of all rows, fails the test). The scratch database is dropped
  afterwards (-Keep leaves it). The live database is only read (a read-only session counting rows).
  Result: <Root>\backups\restore-test.json (shown on the staff "Server health" page); a failure also appends a RED line to the
  alert log (Aion__Ops__AlertLog of <Root>\aion.env, else <Root>\logs\alerts.log). Exit code 1 on failure.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File G:\A2Core\restore-test.ps1
#>
param(
    [string] $Root = $PSScriptRoot,
    [string] $Dump = "",
    [string] $AlertLog = "",
    [switch] $Keep
)
$ErrorActionPreference = "Stop"
$Scratch = "aion_restore_test"   # the only database this script creates or drops
$backups = Join-Path $Root "backups"
$resultFile = Join-Path $backups "restore-test.json"
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
$psql = Join-Path $pgBin "psql.exe"
$utf8 = New-Object Text.UTF8Encoding $false

function Native([scriptblock] $block) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $out = & $block 2>&1 | ForEach-Object { "$_" }; return @{ Code = $LASTEXITCODE; Lines = @($out); Text = ($out -join "`n") } }
    finally { $ErrorActionPreference = $previous }
}
function Sql($db, $text) {
    if ($db -ne $Scratch -and $db -ne "postgres" -and -not $env:PGOPTIONS) { throw "refusing to run on $db without a read-only session" }
    $r = Native { & $psql -h 127.0.0.1 -p $port -U aion_admin -d $db -v ON_ERROR_STOP=1 -qAtX -c $text }
    if ($r.Code -ne 0) { throw "psql on $db failed: $($r.Text)" }
    return $r.Lines
}
# Exact row count of every table (partitioned parents included), one "schema.table|count" line each.
$countSql = "SELECT n.nspname || '.' || c.relname, (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', n.nspname, c.relname), false, true, '')))[1]::text " +
    "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema') " +
    "AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp%' ORDER BY 1"
function Counts($lines) {
    $map = @{}
    foreach ($line in $lines) { $p = "$line".Split("|"); if ($p.Count -eq 2) { $map[$p[0]] = [long]$p[1] } }
    return $map
}
function Alert($text) {
    try {
        [void][IO.Directory]::CreateDirectory((Split-Path $AlertLog))
        [IO.File]::AppendAllText($AlertLog, (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") + "Z A2Core restore-test $text`r`n", $utf8)
    } catch { Write-Warning "alert not written: $_" }
}

$started = Get-Date
$result = [ordered]@{ at = $null; ok = $false; dump = $null; dumpAt = $null; seconds = 0; tables = 0; missing = @(); emptied = @(); rowsRestored = 0; rowsLive = 0;
    differences = @(); summary = ""; error = $null }
try {
    $env:PGPASSWORD = (Get-Content (Join-Path $Root "postgres\credentials.json") -Raw | ConvertFrom-Json).aion_admin
    $file = if ($Dump) { Get-Item $Dump } else { Get-ChildItem $backups -Filter "aion-*.dump" | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 }
    if (-not $file) { throw "no dump in $backups" }
    $result.dump = $file.Name
    $result.dumpAt = $file.LastWriteTimeUtc.ToString("o")

    # Tables the dump holds (its table of contents).
    $toc = Native { & (Join-Path $pgBin "pg_restore.exe") --list $file.FullName }
    if ($toc.Code -ne 0) { throw "the dump cannot be read (pg_restore --list exit $($toc.Code))" }
    # "TABLE schema name owner" lines (not "TABLE DATA schema name owner", the rows of the same table).
    $dumped = @($toc.Lines | ForEach-Object { if ($_ -match '^\d+; \d+ \d+ TABLE (?!DATA )(\S+) (\S+) ') { "$($Matches[1]).$($Matches[2])" } } | Sort-Object -Unique)

    # Fresh scratch database, restore (owners and grants as dumped: the roles exist on this server).
    Sql "postgres" "DROP DATABASE IF EXISTS $Scratch WITH (FORCE)" | Out-Null
    Sql "postgres" "CREATE DATABASE $Scratch" | Out-Null
    $restore = Native { & (Join-Path $pgBin "pg_restore.exe") -h 127.0.0.1 -p $port -U aion_admin -d $Scratch --no-password -j 2 $file.FullName }
    if ($restore.Code -ne 0) {
        $errors = @($restore.Lines | Where-Object { $_ -match "error" } | Select-Object -First 3) -join " | "
        throw "pg_restore reported errors (exit $($restore.Code)): $errors"
    }

    $restored = Counts (Sql $Scratch $countSql)
    $env:PGOPTIONS = "-c default_transaction_read_only=on"
    try { $live = Counts (Sql "aion" $countSql) } finally { Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue }

    $missing = @($dumped | Where-Object { -not $restored.ContainsKey($_) })
    $emptied = @($restored.Keys | Where-Object { $restored[$_] -eq 0 -and $live.ContainsKey($_) -and $live[$_] -ge 50 } | Sort-Object)
    $rowsRestored = ($restored.Values | Measure-Object -Sum).Sum
    $rowsLiveShared = ($restored.Keys | Where-Object { $live.ContainsKey($_) } | ForEach-Object { $live[$_] } | Measure-Object -Sum).Sum
    $diffs = @($restored.Keys | Where-Object { $live.ContainsKey($_) -and $live[$_] -ne $restored[$_] } |
        Sort-Object { [math]::Abs($live[$_] - $restored[$_]) } -Descending | Select-Object -First 15 |
        ForEach-Object { [ordered]@{ table = $_; restored = $restored[$_]; live = $live[$_] } })
    $result.tables = $restored.Count
    $result.missing = $missing
    $result.emptied = $emptied
    $result.rowsRestored = [long]$rowsRestored
    $result.rowsLive = [long]$rowsLiveShared
    $result.differences = $diffs
    $problems = @()
    if ($dumped.Count -eq 0) { $problems += "the dump lists no tables" }
    if ($missing.Count -gt 0) { $problems += "tables missing after the restore: $($missing -join ', ')" }
    if ($emptied.Count -gt 0) { $problems += "tables empty after the restore but not live: $($emptied -join ', ')" }
    if ($rowsLiveShared -gt 1000 -and $rowsRestored -lt $rowsLiveShared / 2) { $problems += "only $rowsRestored of $rowsLiveShared rows came back" }
    $result.ok = $problems.Count -eq 0
    $result.seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    $result.summary = "$($file.Name): $($restored.Count) tables, $('{0:N0}' -f $rowsRestored) rows restored (live now $('{0:N0}' -f $rowsLiveShared), $($diffs.Count) tables differ) in $($result.seconds) s"
    if (-not $result.ok) { $result.error = $problems -join "; "; $result.summary += " - " + $result.error }
} catch {
    $result.ok = $false
    $result.error = ("$_" -replace "\s+", " ")
    $result.summary = "restore test failed: $($result.error)"
} finally {
    if (-not $Keep) { try { Sql "postgres" "DROP DATABASE IF EXISTS $Scratch WITH (FORCE)" | Out-Null } catch { Write-Warning "scratch database not dropped: $_" } }
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
$result.at = (Get-Date).ToUniversalTime().ToString("o")
$result.seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
try { [IO.File]::WriteAllText($resultFile, ($result | ConvertTo-Json -Depth 5), $utf8) } catch { Write-Warning "result not written: $_" }
if ($result.ok) {
    Write-Host "Restore test passed: $($result.summary)"
    exit 0
}
Alert "RED restore: the monthly restore test FAILED: $($result.summary)"
Write-Host "Restore test FAILED: $($result.summary)"
exit 1
