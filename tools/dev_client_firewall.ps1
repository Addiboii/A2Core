# Keep the development client on our own servers: Windows Firewall rules that block
# every non-loopback address, in and out, for each program in the dev client copy
# (research/local/dev-client). Loopback (127.0.0.1 / ::1) stays allowed, so our login,
# game, bootstrap and account servers keep working. The official Steam install is a
# different path and is not affected. Run from an Administrator PowerShell:
#
#   powershell -ExecutionPolicy Bypass -File tools\dev_client_firewall.ps1           # add/refresh
#   powershell -ExecutionPolicy Bypass -File tools\dev_client_firewall.ps1 -Remove   # remove
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$group = 'AION2 dev client - local only'
$root = Join-Path $PSScriptRoot '..\research\local\dev-client' | Resolve-Path
Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($Remove) { "Removed firewall group '$group'."; return }
# Everything except 127.0.0.0/8 and ::1 (and the unspecified ::).
$remote = @('0.0.0.0-126.255.255.255', '128.0.0.0-255.255.255.255', '::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')
$programs = Get-ChildItem -Path $root -Recurse -Filter *.exe | Select-Object -ExpandProperty FullName
foreach ($program in $programs) {
    foreach ($direction in 'Outbound', 'Inbound') {
        New-NetFirewallRule -Group $group -DisplayName "$group ($direction): $(Split-Path $program -Leaf)" `
            -Direction $direction -Program $program -RemoteAddress $remote -Action Block -Profile Any | Out-Null
    }
}
"Blocked non-loopback traffic for $($programs.Count) dev client programs (group '$group'):"
$programs
