@echo off
setlocal EnableExtensions
title AirMic - Android Install + Windows Build

set "ROOT=%~dp0"
set "ANDROID_DIR=%ROOT%android"
set "APK=%ANDROID_DIR%\app\build\outputs\apk\debug\app-debug.apk"
set "ADB_EXE=adb"

echo.
echo ==========================================
echo   AirMic Android Build / Device Install
echo   + Windows Build
echo ==========================================
echo.

rem Check Java
where java >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Java not found. Install JDK 17 and add it to PATH or set JAVA_HOME.
    goto :fail
)

rem Find ADB from Android SDK or PATH
if defined ANDROID_HOME if exist "%ANDROID_HOME%\platform-tools\adb.exe" set "ADB_EXE=%ANDROID_HOME%\platform-tools\adb.exe"
if defined ANDROID_SDK_ROOT if exist "%ANDROID_SDK_ROOT%\platform-tools\adb.exe" set "ADB_EXE=%ANDROID_SDK_ROOT%\platform-tools\adb.exe"
if "%ADB_EXE%"=="adb" if exist "%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" set "ADB_EXE=%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe"
if "%ADB_EXE%"=="adb" (
    where adb >nul 2>&1
    if errorlevel 1 (
        echo [ERROR] ADB not found. Install Android SDK Platform-Tools.
        goto :fail
    )
)

rem Check .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found. Install the .NET 10 SDK.
    goto :fail
)

rem Require one available ADB target
echo [1/4] Checking connected Android device...
"%ADB_EXE%" start-server
if errorlevel 1 goto :fail
"%ADB_EXE%" get-state >nul 2>&1
if errorlevel 1 (
    echo [ERROR] No single usable Android device found.
    echo Connect the phone, enable USB debugging, accept the RSA prompt,
    echo and disconnect extra devices/emulators.
    "%ADB_EXE%" devices
    goto :fail
)

rem Build and test Android
echo.
echo [2/4] Building and testing Android...
pushd "%ANDROID_DIR%"
call gradlew.bat :app:testDebugUnitTest :dsp-host-tests:test :app:assembleDebug :app:lintDebug
if errorlevel 1 (
    popd
    echo [ERROR] Android build/test failed.
    goto :fail
)
popd

if not exist "%APK%" (
    echo [ERROR] APK not found: "%APK%"
    goto :fail
)

rem Install APK on the connected phone
echo.
echo [3/4] Installing AirMic on Android...
"%ADB_EXE%" install -r "%APK%"
if errorlevel 1 (
    echo [ERROR] APK installation failed.
    goto :fail
)

rem Build Windows app and supporting projects
echo.
echo [4/4] Building Windows solution...
dotnet build "%ROOT%windows\AirMic.sln" -c Release
if errorlevel 1 (
    echo [ERROR] Windows build failed. Android installation already succeeded.
    goto :fail
)

echo.
echo ==========================================
echo SUCCESS - Android installed, Windows built.
echo ==========================================
echo APK: %APK%
echo Windows output: %ROOT%windows\AirMic.App\bin\Release\net10.0-windows\
echo.
pause
exit /b 0

:fail
echo.
echo Build process stopped. Fix the error above and run this BAT again.
echo.
pause
exit /b 1
