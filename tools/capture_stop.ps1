# Stop an orphaned netsh trace and convert capture.etl to capture.pcapng.
[CmdletBinding()]
param([string]$CaptureDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'capture_session.ps1')

if (-not $CaptureDirectory) {
    $candidate = Find-IncompleteCaptureDirectory
    if (-not $candidate) { throw 'No incomplete capture-* directory under research/local (or pass -CaptureDirectory).' }
    $CaptureDirectory = $candidate.Directory
    Write-Output ("Using " + $candidate.Name)
} else {
    $CaptureDirectory = [IO.Path]::GetFullPath($CaptureDirectory)
}
Complete-CaptureRecording -Directory $CaptureDirectory
