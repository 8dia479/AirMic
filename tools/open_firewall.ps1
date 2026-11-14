# Run explicitly in an Administrator PowerShell. Never open a public/Internet port.
param(
    [Parameter(Mandatory = $true)][string]$Program,
    [ValidateRange(1, 65535)][int]$Port = 49000
)
$ErrorActionPreference = "Stop"
$resolved = (Resolve-Path $Program).Path
$ruleName = "AirMic UDP $Port (Private LocalSubnet)"
if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
    throw "Rule already exists: $ruleName. Review it in Windows Defender Firewall."
}
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
    -Protocol UDP -LocalPort $Port -Program $resolved -Profile Private `
    -RemoteAddress LocalSubnet | Format-Table DisplayName, Enabled, Profile
Write-Host "Only Private + LocalSubnet allowed. Do not configure router port forwarding."
Write-Host "Remove with: Remove-NetFirewallRule -DisplayName '$ruleName'"
