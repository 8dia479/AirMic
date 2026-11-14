param([switch]$DiagnosticOnly, [string]$DriverPackageDirectory = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ($DiagnosticOnly -and $DriverPackageDirectory) { throw 'Choose DiagnosticOnly OR a signed driver package.' }
if (-not $DiagnosticOnly -and -not $DriverPackageDirectory) {
    throw 'No signed AirMic driver. Production publishing is BLOCKED. Use -DiagnosticOnly for a clearly labelled receiver build.'
}
$arguments = @('publish', (Join-Path $root 'windows/AirMic.App'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=embedded')
$folder = if ($DiagnosticOnly) { 'windows-diagnostic' } else { 'windows' }
if (-not $DiagnosticOnly) {
    $package = (Resolve-Path $DriverPackageDirectory).Path
    foreach ($name in @('AirMic.inf', 'AirMic.cat', 'AirMic.sys')) {
        if (-not (Test-Path (Join-Path $package $name) -PathType Leaf)) { throw "Required driver file absent: $name" }
    }
    $signature = Get-AuthenticodeSignature (Join-Path $package 'AirMic.cat')
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Windows Hardware Compatibility') {
        throw 'Production package requires a valid Microsoft hardware-driver-signed catalog. Self/test signing is not accepted.'
    }
    if ((Get-Content (Join-Path $package 'AirMic.inf') -Raw) -notmatch 'Root\\AirMic') { throw 'Wrong driver hardware ID' }
    $stage = Join-Path $root 'build/driver-bundle'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem $package -File) {
        if ($file.Name -eq 'manifest.json') { continue }
        if ($file.Name -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Unsupported package filename' }
        Copy-Item $file.FullName (Join-Path $stage $file.Name)
        $hashes[$file.Name] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    }
    @{schemaVersion=1; hardwareId='Root\AirMic'; inf='AirMic.inf'; catalog='AirMic.cat'; files=$hashes} |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'manifest.json') -Encoding utf8
    $zip = Join-Path $root 'build/AirMic.DriverPackage.zip'
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    $arguments += "-p:AirMicDriverPackage=$zip"
}
$output = Join-Path $root "artifacts/$folder"
$arguments += @('-o', $output)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
Copy-Item (Join-Path $root 'LICENSE') $output
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $output
Write-Host "Published: $output/AirMic.exe"
if ($DiagnosticOnly) { Write-Warning 'NO DRIVER: this executable is NOT a system microphone.' }
else { Write-Warning 'Driver bundle present. Hardware/upgrade/uninstall/Secure Boot tests and application signing are still release gates.' }
