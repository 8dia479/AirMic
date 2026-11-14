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

rem Gradle 8.10.2 supports running on Java 8 through 23.
rem Android Gradle Plugin 8.7.3 requires JDK 17 or newer.
rem Prefer a compatible JDK 17-23; Java 25 is not supported by this wrapper.
set "JAVA_MAJOR="
for /f "tokens=3" %%V in ('java -XshowSettings:properties -version 2^>^&1 ^| findstr /c:"java.specification.version"') do set "JAVA_MAJOR=%%V"

if not defined JAVA_MAJOR (
    echo [ERROR] Could not determine the installed Java version.
    goto :fail
)

echo [INFO] Detected Java %JAVA_MAJOR%.
if %JAVA_MAJOR% GEQ 17 if %JAVA_MAJOR% LEQ 23 goto :java_ok

echo [INFO] Java %JAVA_MAJOR% is incompatible with the bundled Gradle 8.10.2.
echo [INFO] Searching for Android Studio JBR or JDK 17/21...

rem Prefer Android Studio's bundled JBR (commonly Java 21).
if defined ProgramFiles if exist "%ProgramFiles%\Android\Android Studio\jbr\bin\java.exe" (
    set "JAVA_HOME=%ProgramFiles%\Android\Android Studio\jbr"
    goto :use_fallback_java
)
if exist "C:\Program Files\Android\Android Studio\jbr\bin\java.exe" (
    set "JAVA_HOME=C:\Program Files\Android\Android Studio\jbr"
    goto :use_fallback_java
)
if exist "C:\Program Files\Java\jdk-21\bin\java.exe" (
    set "JAVA_HOME=C:\Program Files\Java\jdk-21"
    goto :use_fallback_java
)
if exist "C:\Program Files\Java\jdk-17\bin\java.exe" (
    set "JAVA_HOME=C:\Program Files\Java\jdk-17"
    goto :use_fallback_java
)
if defined LOCALAPPDATA if exist "%LOCALAPPDATA%\Programs\Eclipse Adoptium\jdk-21\bin\java.exe" (
    set "JAVA_HOME=%LOCALAPPDATA%\Programs\Eclipse Adoptium\jdk-21"
    goto :use_fallback_java
)

echo [ERROR] No compatible JDK found.
echo Install JDK 17 or 21, or install Android Studio (includes a compatible JBR).
echo Then rerun this file. Your current Java %JAVA_MAJOR% cannot run Gradle 8.10.2.
goto :fail

:use_fallback_java
set "PATH=%JAVA_HOME%\bin;%PATH%"
set "JAVA_MAJOR="
for /f "tokens=3" %%V in ('"%JAVA_HOME%\bin\java.exe" -XshowSettings:properties -version 2^>^&1 ^| findstr /c:"java.specification.version"') do set "JAVA_MAJOR=%%V"
if not defined JAVA_MAJOR goto :java_version_error
if %JAVA_MAJOR% LSS 17 goto :java_version_error
if %JAVA_MAJOR% GTR 23 goto :java_version_error
echo [INFO] Using compatible Java %JAVA_MAJOR% from "%JAVA_HOME%".
goto :java_ok

:java_version_error
echo [ERROR] The discovered JDK is not compatible. Install JDK 17 or 21.
goto :fail

:java_ok
echo [OK] Java %JAVA_MAJOR% is compatible.

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
echo.
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
