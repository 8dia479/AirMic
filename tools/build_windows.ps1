param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root "build/windows"
cmake -S $root -B $build -A x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cmake --build $build --config $Configuration --parallel 2
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
ctest --test-dir $build -C $Configuration --output-on-failure
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$exe = Join-Path $build "windows/$Configuration/airmic_receiver.exe"
$destination = Join-Path $artifacts "airmic_receiver.exe"
Copy-Item $exe $destination -Force
Write-Host "Unsigned Windows x64 executable: $destination"
Write-Host "This build does not validate WASAPI/VB-CABLE or a phone connection."
