param([Parameter(Mandatory=$true)][string]$Config)
$ErrorActionPreference = 'Stop'
$directory = Split-Path -Parent $Config
$exitCode = 1
try {
    $settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
    Remove-Item -LiteralPath $Config
    # Remove inherited application settings before applying the launch configuration.
    Get-ChildItem Env: | Where-Object { $_.Name -match '^(Aion__|ConnectionStrings__)' } |
        ForEach-Object { [Environment]::SetEnvironmentVariable($_.Name, $null, 'Process') }
    foreach ($property in $settings.environment.PSObject.Properties) {
        [Environment]::SetEnvironmentVariable($property.Name, [string]$property.Value, 'Process')
    }
    Set-Location -LiteralPath (Split-Path -Parent $settings.dll)
    $server = Start-Process dotnet -ArgumentList ('"' + $settings.dll + '"') -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $directory 'host.log') -RedirectStandardError (Join-Path $directory 'host.stderr.log')
    $exitCode = $server.ExitCode
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $directory 'host-start-error.log')
} finally {
    if (Test-Path -LiteralPath $Config) { Remove-Item -LiteralPath $Config }
    @{exit_code=$exitCode} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $directory 'dotnet-exit.tmp')
    Move-Item -LiteralPath (Join-Path $directory 'dotnet-exit.tmp') -Destination (Join-Path $directory 'dotnet-exit.json') -Force
}
exit $exitCode
