param([switch]$DiagnosticOnly, [string]$DriverPackageDirectory = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'windows/AirMic.App/AirMic.App.csproj'
if ($DiagnosticOnly -and $DriverPackageDirectory) { throw 'Choose DiagnosticOnly OR a signed driver package.' }
if (-not $DiagnosticOnly -and -not $DriverPackageDirectory) {
    throw 'No signed AirMic driver. Production publishing is BLOCKED. Use -DiagnosticOnly for a clearly labelled receiver build.'
}
$arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=embedded')
$folder = if ($DiagnosticOnly) { 'windows-diagnostic' } else { 'windows' }
if (-not $DiagnosticOnly) {
    $package = (Resolve-Path $DriverPackageDirectory).Path
    if (Get-ChildItem $package -Directory) { throw 'The AirMic driver package must be flat.' }
    foreach ($name in @('AirMic.inf', 'AirMic.cat', 'AirMic.sys')) {
        if (-not (Test-Path (Join-Path $package $name) -PathType Leaf)) { throw "Required driver file absent: $name" }
    }
    $signature = Get-AuthenticodeSignature (Join-Path $package 'AirMic.cat')
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Windows Hardware Compatibility') {
        throw 'Production package requires a valid Microsoft hardware-driver-signed catalog. Self/test signing is not accepted.'
    }
    # Maintainer's native WDK tools are needed only for a PRODUCTION driver bundle,
    # not for building the C# app or a diagnostic EXE with .NET alone.
    $signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
    if (-not $signTool) {
        $signTool = Get-ChildItem (Join-Path $root 'driver/packages') -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(ARM64|ARM)\\' } | Select-Object -ExpandProperty FullName -First 1
    }
    if (-not $signTool) { throw 'Production driver verification needs the WDK signtool on PATH (or restored driver/packages).' }
    foreach ($name in @('AirMic.inf','AirMic.sys')) {
        & $signTool verify /kp /c (Join-Path $package 'AirMic.cat') (Join-Path $package $name)
        if ($LASTEXITCODE -ne 0) { throw "Signed catalog does not validate/cover $name" }
    }
    if ((Get-Content (Join-Path $package 'AirMic.inf') -Raw) -notmatch 'Root\\AirMic') { throw 'Wrong driver hardware ID' }
    $stage = Join-Path $root 'build/driver-bundle'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem $package -File) {
        if ($file.Name -eq 'manifest.json') { continue }
        if ($file.Name -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]*$') { throw 'Unsupported package filename' }
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

# Collect notices from the EXACT resolved self-contained runtime packs, not an
# arbitrary SDK installation or an old .NET source snapshot. Keep them in the EXE
# so a single-file installation can write complete notices beside the installed app.
& dotnet restore $project -r win-x64 -p:SelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'Runtime pack restore failed.' }
$probe = & dotnet msbuild $project -t:ResolveReferences -getItem:ResolvedRuntimePack -getProperty:NuGetPackageRoot -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:Configuration=Release -nologo -verbosity:quiet
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve exact runtime-pack notice paths.' }
$resolved = ($probe -join "`n") | ConvertFrom-Json
$packs = @($resolved.Items.ResolvedRuntimePack)
if ($packs.Count -lt 2) { throw 'Both .NET and WPF runtime packs are required for redistribution notices.' }
$notices = Join-Path $root 'build/redistribution-notices'
if (Test-Path $notices) { Remove-Item $notices -Force -Recurse }
New-Item -ItemType Directory -Force $notices | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $notices 'AirMic-LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $notices
Copy-Item (Join-Path $root 'driver/sysvad/LICENSE') (Join-Path $notices 'SYSVAD-LICENSE.txt')
Copy-Item (Join-Path $root 'driver/UPSTREAM.md') (Join-Path $notices 'SYSVAD-NOTICE.md')
foreach ($pack in $packs) {
    $directory = $pack.PackageDirectory
    if (-not $directory -or -not (Test-Path $directory -PathType Container)) {
        $directory = Join-Path $resolved.Properties.NuGetPackageRoot ($pack.NuGetPackageId.ToLowerInvariant() + '/' + $pack.NuGetPackageVersion)
    }
    $legal = @(Get-ChildItem $directory -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES|THIRD_PARTY_NOTICES)(\.txt)?$' })
    if ($legal.Count -eq 0) { throw "No redistribution notices in runtime pack: $directory" }
    foreach ($file in $legal) {
        $name = $pack.NuGetPackageId + '.' + $file.Name
        if (-not [IO.Path]::GetExtension($name)) { $name += '.txt' }
        if ($name -notmatch '\.txt$') { $name += '.txt' }
        Copy-Item $file.FullName (Join-Path $notices $name)
    }
}
$noticeZip = Join-Path $root 'build/AirMic.RedistributionNotices.zip'
if (Test-Path $noticeZip) { Remove-Item $noticeZip }
Compress-Archive -Path (Join-Path $notices '*') -DestinationPath $noticeZip
$arguments += "-p:AirMicNoticesPackage=$noticeZip"
$output = Join-Path $root "artifacts/$folder"
$arguments += @('-o', $output)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
Copy-Item (Join-Path $notices '*') $output -Force
Write-Host "Published: $output/AirMic.exe"
if ($DiagnosticOnly) { Write-Warning 'NO DRIVER: this executable is NOT a system microphone. Installation is disabled.' }
else { Write-Warning 'Signed driver bundle present. Actual hardware/upgrade/uninstall/Secure Boot tests and application signing remain release gates.' }
