param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build windows/AirMic.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Managed build failed: $LASTEXITCODE" }
    dotnet run --project windows/AirMic.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Managed tests failed: $LASTEXITCODE" }
    Write-Host 'Run the GUI: dotnet run --project windows/AirMic.App -c Release'
    Write-Host 'This is not a completed virtual microphone without an installed, verified AirMic driver.'
} finally { Pop-Location }
