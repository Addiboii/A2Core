# Shared helpers for passive official-server captures (netsh trace + ETL conversion).

function Get-CaptureWorkspace {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

function Get-VerifiedEtlConverter {
    $workspace = Get-CaptureWorkspace
    $converter = Join-Path $workspace 'research\local\tooling\etl2pcapng-1.11.0\etl2pcapng.exe'
    $expectedHash = 'C2D03AAC43EA0F5626FC8D909E6688393FEAF71BD6FACD39AAF8A637382D9043'
    if (-not (Test-Path -LiteralPath $converter)) {
        throw "The Microsoft ETL converter is missing: $converter"
    }
    $hash = (Get-FileHash -LiteralPath $converter -Algorithm SHA256).Hash
    if ($hash -ne $expectedHash) {
        throw 'The ETL converter hash does not match the verified Microsoft etl2pcapng 1.11.0 release.'
    }
    return @{ Path = $converter; Sha256 = $expectedHash; Version = '1.11.0' }
}

function Invoke-NetshTrace([string[]]$Arguments) {
    $netsh = Join-Path $env:SystemRoot 'System32\netsh.exe'
    $lines = @(& $netsh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ($lines -join [Environment]::NewLine) }
    return ($lines -join [Environment]::NewLine)
}

function Save-CaptureSession([string]$Directory, $Metadata) {
    $Metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Directory 'session.json') -Encoding UTF8
}

function Read-CaptureSession([string]$Directory) {
    $path = Join-Path $Directory 'session.json'
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing session.json under $Directory" }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Complete-CaptureRecording {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [switch]$SkipStop
    )
    $metadata = Read-CaptureSession -Directory $Directory
    $sessionName = [string]$metadata.session_name
    $etl = Join-Path $Directory 'capture.etl'
    $pcap = Join-Path $Directory 'capture.pcapng'
    $converter = (Get-VerifiedEtlConverter).Path
    if (-not $SkipStop -and -not [bool]$metadata.capture_complete) {
        Write-Output (Invoke-NetshTrace -Arguments @('trace', 'stop', "sessionname=$sessionName"))
        $metadata.stopped_utc = [DateTime]::UtcNow.ToString('o')
        $metadata.capture_complete = $true
        Save-CaptureSession -Directory $Directory -Metadata $metadata
    }
    if (-not (Test-Path -LiteralPath $pcap)) {
        if (-not (Test-Path -LiteralPath $etl)) { throw "No capture.etl in $Directory" }
        & $converter $etl $pcap
        if ($LASTEXITCODE -ne 0) { throw "ETL conversion failed with exit code $LASTEXITCODE. The ETL is preserved at $etl" }
        $metadata = Read-CaptureSession -Directory $Directory
        $metadata.pcapng_created = $true
        Save-CaptureSession -Directory $Directory -Metadata $metadata
    }
    Write-Output "Ready for analysis: $pcap"
}

function Find-IncompleteCaptureDirectory {
    $workspace = Get-CaptureWorkspace
    $local = Join-Path $workspace 'research\local'
    if (-not (Test-Path -LiteralPath $local)) { return $null }
    return Get-ChildItem -LiteralPath $local -Directory -Filter 'capture-*' |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'session.json') } |
        ForEach-Object {
            $session = Read-CaptureSession -Directory $_.FullName
            if (-not [bool]$session.capture_complete -or
                ((Test-Path -LiteralPath (Join-Path $_.FullName 'capture.etl')) -and
                 -not (Test-Path -LiteralPath (Join-Path $_.FullName 'capture.pcapng')))) {
                [pscustomobject]@{ Directory = $_.FullName; Session = $session; Name = $_.Name }
            }
        } | Sort-Object { $_.Session.started_utc } -Descending | Select-Object -First 1
}

function Get-AionGameProcessIds {
    @(Get-Process -Name AION2 -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Id })
}

function Discover-AionServerAddresses {
    param([int]$RemotePort, [int[]]$ProcessIds)
    if ($ProcessIds.Count -eq 0) { return @() }
    $rows = @(Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
        Where-Object { $ProcessIds -contains $_.OwningProcess -and $_.RemotePort -eq $RemotePort })
    @($rows | ForEach-Object { $_.RemoteAddress } | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' } | Sort-Object -Unique)
}
