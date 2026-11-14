# Native driver exception only. Does NOT change the .NET application's requirements.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & nuget restore driver/packages.config -PackagesDirectory driver/packages -NonInteractive
    if ($LASTEXITCODE -ne 0) { throw "WDK NuGet restore failed: $LASTEXITCODE" }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'Native driver builds need a WDK-compatible Visual Studio C++ toolchain. App builds need only .NET.' }
    & $msbuild driver/AirMic/AirMic.vcxproj /m /v:minimal /p:Configuration=$Configuration /p:Platform=x64 /p:SignMode=Off
    if ($LASTEXITCODE -ne 0) { throw "Driver compile failed: $LASTEXITCODE" }
    $output = Join-Path $root 'build/driver/unsigned'
    New-Item -ItemType Directory -Force $output | Out-Null
    $sys = Get-ChildItem "build/driver/x64/$Configuration" -Filter AirMic.sys -Recurse | Select-Object -First 1
    $inf = Get-ChildItem "build/driver/x64/$Configuration" -Filter AirMic.inf -Recurse | Select-Object -First 1
    if (-not $sys -or -not $inf) { throw 'WDK did not produce the expected AirMic.sys / stamped AirMic.inf.' }
    Copy-Item $sys.FullName $output -Force
    Copy-Item $inf.FullName $output -Force
    # No test/production signing and no installation on the build host.
    $inf2cat = Get-ChildItem driver/packages -Filter Inf2Cat.exe -Recurse |
        Sort-Object FullName | Select-Object -First 1
    if (-not $inf2cat) { throw 'Inf2Cat not found in pinned WDK packages.' }
    & $inf2cat.FullName "/driver:$output" '/os:10_VB_X64,10_NI_X64' /uselocaltime
    if ($LASTEXITCODE -ne 0) { throw "Driver catalog validation failed: $LASTEXITCODE" }
    Copy-Item driver/sysvad/LICENSE (Join-Path $output 'SYSVAD-LICENSE.txt') -Force
    Copy-Item driver/UPSTREAM.md (Join-Path $output 'SYSVAD-NOTICE.txt') -Force
    Copy-Item LICENSE (Join-Path $output 'AIRMIC-LICENSE.txt') -Force
    Write-Warning "UNSIGNED driver package: $output. NOT installable as a normal Secure Boot product. Do not disable system security to use it."
} finally { Pop-Location }
