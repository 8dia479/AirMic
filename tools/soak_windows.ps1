# Counter-based smoke/soak collection; it CANNOT certify audible quality or E2E latency.
param(
    [Parameter(Mandatory = $true)][string]$Program,
    [Parameter(Mandatory = $true)][string]$PhoneIp,
    [int]$DeviceIndex = -1,
    [ValidateRange(10, 86400)][int]$Seconds = 600,
    [ValidateRange(1, 65535)][int]$Port = 49000,
    [string]$OutputDirectory = "artifacts/windows-soak"
)
$ErrorActionPreference = "Stop"
$resolved = (Resolve-Path $Program).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
$statsPath = Join-Path $output "receiver-stats.json"
$arguments = @("--allow", $PhoneIp, "--port", "$Port", "--duration", "$Seconds", "--stats-json", $statsPath)
if ($DeviceIndex -ge 0) { $arguments += @("--device-index", "$DeviceIndex") }
Write-Host "Start the Galaxy Z Flip6 transmitter now. Record device/OS/router and listen throughout."
Write-Host "The first valid source is pinned. Ctrl+C to stop."
& $resolved @arguments
if ($LASTEXITCODE -ne 0) { throw "Receiver failed with exit code $LASTEXITCODE; inspect its console error." }
$stats = Get-Content $statsPath -Raw | ConvertFrom-Json
$minimumPackets = [Math]::Floor($Seconds * 100 * 0.8)
if ($stats.accepted_packets -lt $minimumPackets) { throw "Insufficient incoming audio: $($stats.accepted_packets) packets." }
if ($stats.network_queue_drops -ne 0 -or $stats.socket_errors -ne 0 -or $stats.wasapi_error -ne 0) {
    throw "Transport/render errors observed. See $statsPath"
}
if ($stats.underruns -ne 0) { Write-Warning "Jitter underruns observed: $($stats.underruns). Inspect continuous audio and Wi-Fi conditions." }
Write-Host "Counters collected: $statsPath"
Write-Host "NOT an MVP acceptance by itself: complete docs/DEVICE_TEST_PLAN.md, audio recording and measured E2E delay."
