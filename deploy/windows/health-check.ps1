<#
.SYNOPSIS
  Post-deploy health check of the A2Core server (update.ps1 runs it after the restart). Report only: it changes nothing.

.DESCRIPTION
  Checks, then watches the first -WatchSeconds (60) after the start:
  - the A2Core service is running and its process stays the same (no crash / restart while watched);
  - the game ports are listening (login 3700, account site 8780, game 13328 by default);
  - the account site answers /health/live over HTTPS;
  - login probe: the login and game ports answer the client's first step (a greeting with an RSA key) with their handshake
    reply within 5 s (no account is used, nothing is logged in);
  - the database answers a read-only "SELECT 1";
  - no error burst: errors logged by the host in the watch window (the host's loopback /health/ops when it has it, else the
    Windows Application log, source A2Core); -MaxErrors (default 5) or more is a failure. The host's own red checks are listed.
  Prints a short report, writes it as JSON (-Json, default <InstallDir>\logs\health-check.json), appends a line to the alert
  log when something failed, and exits 0 (healthy) or 1 (problems). Deciding on a rollback is left to the caller.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File deploy\windows\health-check.ps1
  powershell -ExecutionPolicy Bypass -File deploy\windows\health-check.ps1 -WatchSeconds 0     # quick check, no watching
#>
[CmdletBinding()]
param(
    [string] $InstallDir = "",
    [int[]] $Ports = @(3700, 8780, 13328),
    [int] $WatchSeconds = 60,
    [int] $MaxErrors = 5,
    [string] $Json = "",
    [string] $AlertLog = ""
)
$ErrorActionPreference = "Stop"
if (-not $InstallDir) {
    $InstallDir = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\A2Core" -ErrorAction SilentlyContinue).ImagePath -replace '^"?(.*)\\host\\Aion\.Host\.exe"?$', '$1'
}
$envFile = Join-Path $InstallDir "aion.env"
function Setting($name) {
    if (Test-Path $envFile) { foreach ($line in Get-Content $envFile) { if ($line.StartsWith("$name=")) { return $line.Substring($name.Length + 1).Trim() } } }
    return $null
}
if (-not $Json) { $Json = Join-Path $InstallDir "logs\health-check.json" }
if (-not $AlertLog) { $AlertLog = Setting "Aion__Ops__AlertLog" }
if (-not $AlertLog) { $AlertLog = Join-Path $InstallDir "logs\alerts.log" }
$accountPort = 8780
if (Setting "Aion__LocalPort") { $accountPort = [int](Setting "Aion__LocalPort") }
$loginPort = $(if (Setting "Aion__Native__LoginPort") { [int](Setting "Aion__Native__LoginPort") } else { 3700 })
$gamePort = $(if (Setting "Aion__Native__GamePort") { [int](Setting "Aion__Native__GamePort") } else { 13328 })
$results = New-Object System.Collections.ArrayList
function Result($name, [bool] $ok, $detail) {
    [void]$results.Add([ordered]@{ check = $name; ok = $ok; detail = "$detail" })
    $mark = $(if ($ok) { "ok  " } else { "FAIL" })
    Write-Host ("  [{0}] {1}: {2}" -f $mark, $name, $detail) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
}
# Self-signed certificate on the account site: accept it for this local probe only.
if (-not ("A2CoreTrustAll" -as [type])) {
    Add-Type @"
using System.Net.Security; using System.Security.Cryptography.X509Certificates;
public static class A2CoreTrustAll {
    static bool Accept(object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; }
    public static RemoteCertificateValidationCallback Callback { get { return Accept; } }
}
"@
}
[Net.ServicePointManager]::ServerCertificateValidationCallback = [A2CoreTrustAll]::Callback
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
function Get-Url($path) {
    foreach ($scheme in "https", "http") {
        try {
            $request = [Net.WebRequest]::Create("${scheme}://127.0.0.1:$accountPort$path")
            $request.Timeout = 5000
            $response = $request.GetResponse()
            $reader = New-Object IO.StreamReader($response.GetResponseStream())
            $body = $reader.ReadToEnd(); $response.Close()
            return @{ Status = 200; Body = $body }
        } catch [Net.WebException] {
            if ($_.Exception.Response) { return @{ Status = [int]$_.Exception.Response.StatusCode; Body = "" } }
        }
    }
    return @{ Status = 0; Body = "" }
}
# The client's first step: a greeting frame with an RSA public key (0x3900 login / 0x3610 game); the server must answer with its
# handshake reply. A throwaway 1024-bit modulus (2^1023 + 1, e = 3) is enough: nothing is logged in and the reply is discarded.
function Probe-Greeting($port, [int] $tag) {
    $modulus = [byte[]](@(0x80) + @(0) * 126 + @(0x01))
    $der = [byte[]](@(0x30, 0x81, 0x87, 0x02, 0x81, 0x81, 0x00) + $modulus + @(0x02, 0x01, 0x03))
    $body = [byte[]](@(($tag -band 0xFF), ($tag -shr 8), 0x8A, 0x01) + $der + @(0) * 11)
    $frame = [byte[]](@(0x9D, 0x01) + $body)
    $client = New-Object Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect("127.0.0.1", $port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(5000)) { return "no connection within 5 s" }
        $client.EndConnect($connect)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 5000
        $stream.Write($frame, 0, $frame.Length)
        $buffer = New-Object byte[] 1024
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -gt 0) { return $null }
        return "the server closed the connection without a handshake reply"
    } catch { return "no handshake reply: " + $_.Exception.GetBaseException().Message }
    finally { $client.Close() }
}
function Host-Process { Get-CimInstance Win32_Service -Filter "Name='A2Core'" -ErrorAction SilentlyContinue }

Write-Host "==> Health check of A2Core ($InstallDir)" -ForegroundColor Cyan
$started = Get-Date
$service = Host-Process
$pid0 = $(if ($service) { $service.ProcessId } else { 0 })
Result "service" ($service -and $service.State -eq "Running") $(if ($service) { "$($service.State), process $pid0" } else { "the A2Core service is not installed" })
$processStart = $(try { (Get-Process -Id $pid0 -ErrorAction Stop).StartTime } catch { $started })

# Ports (the host may still be starting: up to 60 s).
$missing = $Ports
for ($i = 0; $i -lt 30; $i++) {
    $up = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $Ports -contains $_.LocalPort } | Select-Object -ExpandProperty LocalPort -Unique)
    $missing = @($Ports | Where-Object { $up -notcontains $_ })
    if ($missing.Count -eq 0) { break }
    Start-Sleep -Seconds 2
}
Result "ports" ($missing.Count -eq 0) $(if ($missing.Count -eq 0) { "listening on $($Ports -join ', ')" } else { "not listening: $($missing -join ', ')" })

$live = Get-Url "/health/live"
Result "account site" ($live.Status -eq 200) $(if ($live.Status -eq 200) { "/health/live answers" } else { "/health/live: status $($live.Status)" })

foreach ($probe in @(@{ Name = "login port"; Port = $loginPort; Tag = 0x3900 }, @{ Name = "game port"; Port = $gamePort; Tag = 0x3610 })) {
    if ($Ports -notcontains $probe.Port) { continue }
    $problem = Probe-Greeting $probe.Port $probe.Tag
    Result "$($probe.Name) probe" ($null -eq $problem) $(if ($null -eq $problem) { "port $($probe.Port) answers the client's handshake" } else { "port $($probe.Port): $problem" })
}

$psql = Join-Path $InstallDir "pgsql\bin\psql.exe"
$credentials = Join-Path $InstallDir "postgres\credentials.json"
if ((Test-Path $psql) -and (Test-Path $credentials)) {
    $dbPort = 55432
    if ((Setting "ConnectionStrings__Aion") -match 'Port=(\d+)') { $dbPort = [int]$Matches[1] }
    $env:PGPASSWORD = (Get-Content $credentials -Raw | ConvertFrom-Json).aion_admin
    $env:PGOPTIONS = "-c default_transaction_read_only=on"
    $previous = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { $answer = & $psql -h 127.0.0.1 -p $dbPort -U aion_admin -d aion -qAtX -c "SELECT 1" 2>&1 | ForEach-Object { "$_" }; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous; Remove-Item Env:PGPASSWORD, Env:PGOPTIONS -ErrorAction SilentlyContinue }
    Result "database" ($code -eq 0 -and "$answer".Trim() -eq "1") $(if ($code -eq 0) { "answers on port $dbPort" } else { "psql: $answer" })
} else { Result "database" $true "not checked (no psql or credentials here)" }

# Watch: same process, errors in the window.
$until = $processStart.AddSeconds($WatchSeconds)
if ((Get-Date) -lt $until) { Write-Host "   watching until $($until.ToString('HH:mm:ss')) for crashes and errors..." }
while ((Get-Date) -lt $until) {
    Start-Sleep -Seconds 5
    $now = Host-Process
    if (-not $now -or $now.ProcessId -ne $pid0 -or $now.State -ne "Running") { break }
}
$now = Host-Process
Result "stays up" ($now -and $now.ProcessId -eq $pid0 -and $now.State -eq "Running") $(if ($now -and $now.ProcessId -eq $pid0) { "same process for $([int]((Get-Date) - $processStart).TotalSeconds) s" } else { "the host stopped or restarted while watched" })

$ops = Get-Url "/health/ops"
$redChecks = @()
if ($ops.Status -eq 200) {
    $report = $ops.Body | ConvertFrom-Json
    $errors = [int]$report.errorsLast5Minutes
    $source = "host log"
    $redChecks = @($report.checks | Where-Object { $_.state -eq "bad" } | ForEach-Object { "$($_.label): $($_.value)" })
    $sample = @($report.lastErrors | Select-Object -First 3 | ForEach-Object { $_.message }) -join " | "
} else {
    $since = $processStart.AddSeconds(-5)
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = "Application"; ProviderName = "A2Core"; Level = 1, 2; StartTime = $since } -ErrorAction SilentlyContinue)
    $errors = $events.Count
    $source = "Windows Application log"
    $sample = @($events | Select-Object -First 3 | ForEach-Object { ($_.Message -split "`n")[0] }) -join " | "
}
Result "error burst" ($errors -lt $MaxErrors) ("$errors errors since the start ($source)" + $(if ($sample) { ": $sample" } else { "" }))
if ($redChecks.Count -gt 0) { Write-Host "   host health page red: $($redChecks -join '; ')" -ForegroundColor Yellow }

$failed = @($results | Where-Object { -not $_.ok })
$summary = $(if ($failed.Count -eq 0) { "healthy" } else { "PROBLEMS: " + (($failed | ForEach-Object { "$($_.check) ($($_.detail))" }) -join "; ") })
$out = [ordered]@{ at = (Get-Date).ToUniversalTime().ToString("o"); ok = ($failed.Count -eq 0); summary = $summary; hostRed = $redChecks; checks = $results }
try {
    [void][IO.Directory]::CreateDirectory((Split-Path $Json))
    [IO.File]::WriteAllText($Json, ($out | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
} catch { Write-Warning "report not written: $_" }
if ($failed.Count -gt 0) {
    try {
        [void][IO.Directory]::CreateDirectory((Split-Path $AlertLog))
        [IO.File]::AppendAllText($AlertLog, (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") + "Z A2Core health-check RED deploy: $summary`r`n", (New-Object Text.UTF8Encoding $false))
    } catch { }
    Write-Host "Health check: $summary" -ForegroundColor Red
    exit 1
}
Write-Host "Health check: healthy" -ForegroundColor Green
exit 0
