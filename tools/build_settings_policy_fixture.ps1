# Encrypt a local policy candidate using the observed AES-128-CBC convention.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$KeyFile,
    [Parameter(Mandatory=$true)][string]$PolicyFile,
    [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputFile) { throw 'Output already exists' }
$keyText = (Get-Content -LiteralPath $KeyFile -Raw | ConvertFrom-Json).key
if ($keyText -cnotmatch '^[\x20-\x7E]{16}$') { throw 'Expected exactly 16 printable ASCII key characters' }
$plainText = Get-Content -LiteralPath $PolicyFile -Raw
$policy = $plainText | ConvertFrom-Json
if ($policy -isnot [pscustomobject]) { throw 'Policy must be a JSON object' }
$keyBytes = [Text.Encoding]::ASCII.GetBytes($keyText)
$plainBytes = [Text.Encoding]::UTF8.GetBytes($plainText)
$aes = [Security.Cryptography.Aes]::Create()
try {
    $aes.Mode = [Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
    $aes.Key = $keyBytes
    $aes.IV = $keyBytes
    $encryptor = $aes.CreateEncryptor()
    try { $cipher = $encryptor.TransformFinalBlock($plainBytes, 0, $plainBytes.Length) }
    finally { $encryptor.Dispose() }
    $result = @{ value = [Convert]::ToBase64String($cipher) } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputFile), $result, [Text.UTF8Encoding]::new($false))
    Write-Output 'Encrypted policy candidate written; client acceptance unverified.'
} finally { $aes.Dispose() }
