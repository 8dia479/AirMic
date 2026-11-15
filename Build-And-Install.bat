@echo off
setlocal EnableExtensions
title AirMic - Windows Build Only

set "ROOT=%~dp0"
set "SOLUTION=%ROOT%windows\AirMic.sln"
set "OUTPUT=%ROOT%windows\AirMic.App\bin\Release\net10.0-windows"
set "EXE=%OUTPUT%\AirMic.exe"

echo.
echo ==========================================
echo          AirMic Windows Build
echo ==========================================
echo.

rem Check .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found.
    echo Install the .NET 10 SDK and run this file again.
    goto :fail
)

echo [INFO] Installed .NET SDKs:
dotnet --list-sdks
echo.

rem Check solution
if not exist "%SOLUTION%" (
    echo [ERROR] Solution not found:
    echo "%SOLUTION%"
    goto :fail
)

echo [1/1] Building AirMic Windows solution...
echo [INFO] Configuration: Release / x64
echo.
dotnet build "%SOLUTION%" --configuration Release --arch x64
if errorlevel 1 (
    echo.
    echo [ERROR] Windows build failed.
    goto :fail
)

echo.
echo ==========================================
echo          BUILD COMPLETED
echo ==========================================
echo.
echo Build output directory:
echo "%OUTPUT%"
echo.
if exist "%EXE%" (
    echo Main application:
    echo "%EXE%"
) else (
    echo [WARNING] Build succeeded, but AirMic.exe was not found at the expected path.
    echo Check the output directory above.
)
echo.
pause
exit /b 0

:fail
echo.
echo Build process stopped. Fix the error above and run this BAT again.
echo.
pause
exit /b 1
