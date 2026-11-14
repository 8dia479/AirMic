# AirMic 0.2 빌드·실행

**현재 앱은 Windows C# / Android Kotlin입니다.** 과거 C++ Windows 수신기, Oboe/JNI,
NDK 및 VB-CABLE 기준 안내는 0.1 구현에만 해당합니다.

## 1. 같은 저장소 받기

```powershell
git clone --branch arena/56cea872-airmic --single-branch https://github.com/8dia479/AirMic.git
cd AirMic
```

이미 받았다면 해당 브랜치에서 `git pull --ff-only`로 갱신합니다.

| 도구 | 여는 위치 | 앱 빌드에 필요 없는 것 |
|---|---|---|
| Visual Studio (.NET 10 지원 버전) | `windows/AirMic.sln` | C++ 워크로드, CMake, WDK |
| Android Studio | `android/` | NDK, CMake, Oboe |

`shared/` C++ 코어는 과거 동작 비교용 테스트에만 남겼습니다. 새 앱은 이를 링크하지 않습니다.
드라이버는 네이티브 개발 예외이며 앱 solution과 분리합니다.

## 2. Windows — C# / WPF

### 개발 환경

- .NET **10 SDK** (`dotnet --list-sdks`로 `10.*` 확인). 실행용 runtime만 설치한 경우 SDK가 필요합니다.
- Visual Studio에서 작업하려면 .NET 10을 지원하는 버전의 **.NET 데스크톱 개발** 구성 요소.
- 다른 앱·오디오 라이브러리 NuGet·C++ 컴파일러는 앱 빌드에 필요 없습니다.
- .NET SDK/Windows Desktop targeting/runtime pack의 최초 restore에는 인터넷이 필요합니다.

Visual Studio에서 solution을 열고 `AirMic.App`을 시작 프로젝트로 선택한 뒤 실행하세요.
명령줄 재현 (저장소 루트):

```powershell
dotnet build .\windows\AirMic.sln -c Release
dotnet run --project .\windows\AirMic.Tests -c Release
dotnet run --project .\windows\AirMic.App -c Release
```

GUI의 설정은 `%LOCALAPPDATA%\AirMic\settings.json`에 저장됩니다.
포트/IP/지터/출력 장치는 중지 상태에서 바꾸고, 음량은 실행 중에도 바꿀 수 있습니다.

### 단일 실행 파일과 드라이버

서명된 드라이버가 **없는 개발용** 실행 파일:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\publish_windows.ps1 -DiagnosticOnly
# artifacts\windows-diagnostic\AirMic.exe
```

이 EXE에는 .NET runtime이 포함되므로 실행 PC에 .NET을 별도 설치하지 않습니다.
단, **드라이버 없는 수신 진단 EXE는 시스템 마이크가 아닙니다.** GUI에 명시합니다.
정확히 resolve된 .NET/WPF runtime pack의 라이선스·서드파티 고지와 AirMic/SysVAD 고지를
EXE resource에 내장하고 artifact에도 복사합니다. 고지가 없는 경우 publish는 실패합니다.
배포 시 해당 고지를 제거하지 마세요.

완성된 단일 설치 패키지는 Microsoft 서명·검증된 `AirMic.inf/.cat/.sys`를 제공해야 만들 수 있습니다.
`publish_windows.ps1 -DriverPackageDirectory <서명된패키지경로>`는 이를 검증·내장하는 경로이며,
없으면 정상 제품처럼 publish하지 않고 실패합니다. 자세한 차단 상태는 [전환 보고서](MIGRATION_0_2.md).
앱은 인터넷에서 VB-CABLE/미확인 driver를 내려받거나 부팅 보안 설정을 바꾸지 않습니다.

### 정식 bundle의 설치 / 복구 / 제거 경로 (실제 OS 동작 미검증)

- GUI의 **AirMic 설치 / 복구** 또는 `AirMic.exe --install`에서만 UAC 승인을 요청합니다.
- 같은 EXE가 `%ProgramFiles%\AirMic\Versions\<EXE-SHA256>`에 앱/runtime 고지를 복사하고,
  내장 전용 driver를 Windows SetupAPI/PnP로 적용합니다. 시작 메뉴/Apps & Features와
  Private + LocalSubnet + 해당 EXE + UDP 방화벽 규칙을 등록하는 코드입니다.
- 설치 journal에 후보 파일·pending 상태를 먼저 기록하고 driver 성공 후 앱 버전을 commit합니다.
  중간 실패 시 이전 앱 경로와 복구용 EXE/driver OEM 기록을 보존합니다.
  driver를 자동 downgrade하거나 모든 PnP 변경이 원자적으로 rollback된다고 주장하지 않습니다.
- 제거는 GUI의 **AirMic 제거**, Windows Apps & Features 또는 `AirMic.exe --uninstall`입니다.
  정확한 AirMic 장치/driver만 대상으로 하고 사용 중인 package를 강제로 제거하지 않습니다.
  사용자 설정은 보존합니다. 잠긴 EXE는 재부팅 정리하며, 같은 경로에 재설치하기 전 재부팅을 요구합니다.
- 설치/제거 도중 Windows가 재부팅을 요구할 수 있습니다. 자동 재부팅이나 부팅 보안 변경은 없습니다.
  일반 수신/청취 실행에는 관리자 권한이 필요 없습니다.

이 관리자 설치/제거 경로는 **WPF build만 통과했으며 실제 Windows 설치 검증은 남았습니다**.
[대상 기기 수용 시험 계획](WINDOWS_DRIVER_VALIDATION.md)을 통과하기 전 일반 사용자용 완성 제품이 아닙니다.

## 3. Android — 순수 Kotlin / AudioRecord

고정 버전:

| 구성 요소 | 버전 |
|---|---|
| Gradle JDK | 17 |
| Gradle wrapper | 8.10.2 |
| AGP / Kotlin | 8.7.3 / 2.0.21 |
| compile / target / minimum API | 35 / 35 / 27 |
| Build Tools | 35.0.0 |

1. Android Studio **Open → `AirMic/android`**.
2. SDK Manager에서 API 35, Build Tools 35.0.0, Platform Tools 설치.
3. Settings → Build Tools → Gradle에서 **Gradle JDK 17** 선택 후 Sync.
4. 고정 버전의 초기 재현에서는 AGP/Gradle/Kotlin 자동 업그레이드를 적용하지 않습니다.
5. 휴대폰 USB 디버깅을 켜고 PC 디버깅을 허용한 뒤 **app → Run**.
6. 앱의 마이크 권한을 허용하고 PC IPv4/포트를 입력해 전송합니다.

CLI를 쓸 때는 IDE의 Gradle JDK와 별개로 JAVA_HOME도 JDK 17인지 확인합니다.

```powershell
cd android
.\gradlew.bat :app:testDebugUnitTest :dsp-host-tests:test :app:assembleDebug :app:lintDebug --stacktrace
```

APK: `android/app/build/outputs/apk/debug/app-debug.apk`.
Linux/macOS에서는 같은 위치에서 `./gradlew`를 사용합니다. SDK 경로 자동 감지가 안 되면
ignored `android/local.properties`에 `sdk.dir`를 지정합니다. NDK/JNI/ABI별 `.so`는 없습니다.
`dsp-host-tests`는 동일 production DSP의 JVM-only 메모리 계측 모듈이며 APK에 포함되지 않습니다.
실제 [5단 DSP 구현](DSP_PIPELINE.md)과 [미구현 De-reverb의 별도 설계](DE_REVERB_DESIGN.md)를 구분합니다.

## 4. 실제 연결과 ‘마이크’ 상태

1. 휴대폰과 PC를 서로 통신 가능한 같은 LAN에 둡니다. PC는 유선이어도 됩니다.
2. Windows GUI에 표시된 PC IPv4를 Android 앱에 입력합니다. UDP 포트를 일치시킵니다.
3. **전용 driver 없음:** ‘수신 진단만’으로 ACK/패킷을 확인할 수 있습니다. 다른 앱에 마이크로 전달하지 않습니다.
4. **전용 driver 및 endpoint 있음:** AirMic 재생 endpoint를 출력으로 선택하고,
   소비 앱에서 `AirMic Microphone`을 마이크로 선택합니다. 실제 전송/음질은 별도 검증합니다.
5. 일반 스피커는 명시적 청취용일 뿐 가상 마이크가 아닙니다. 하울링을 주의하세요.

방화벽이 필요하면 실행 파일에 한정한 **Private + LocalSubnet UDP** 허용을 사용합니다.
기존 `tools/open_firewall.ps1`에는 실제 `AirMic.exe` 경로를 `-Program`으로 전달하세요.
공용 프로필 전체 허용/공유기 포트 포워딩은 하지 않습니다.

연결 실패: PC IP/포트, Windows Private 방화벽, guest/AP 격리, VPN을 확인합니다.
ACK RTT/endpoint reported latency는 E2E 지연이 아닙니다. [지연 측정](LATENCY.md).

## 5. 전용 driver 유지관리자 빌드 — 앱 개발과 별개

**앱 개발자에게 C++/WDK 설치를 요구하는 단계가 아닙니다.** driver 자체를 수정/배포하는
유지관리자만 WDK-compatible Visual Studio native C++ toolchain, x64 MSBuild 및 `nuget.exe`가 필요합니다.
정확한 SDK/WDK `10.0.28000.2526`을 NuGet restore하며, restore/build 도구는 실행 PC에 배포하지 않습니다.

```powershell
.\tools\build_driver.ps1
# build\driver\unsigned\AirMic.sys / AirMic.inf / AirMic.cat
```

Windows 10 19041 native target / KMDF 1.31, x64입니다. InfVerif/API/Inf2Cat 검증을 끄지 않습니다.
CI에서 실제 compile과 unsigned catalog 생성/검증을 통과했지만 장치에 설치하지 않았습니다.
이 산출물은 **미서명**이며 normal publish의 signed-package gate를 통과하지 못하는 것이 정상입니다.
보안 기능 해제/테스트 모드로 사용하는 우회 안내는 하지 않습니다.

유지관리자는 Microsoft 정식 driver 서명·검증 및 앱 배포 서명/검토를 완료한 뒤에만:

```powershell
.\tools\publish_windows.ps1 -DriverPackageDirectory C:\signed-airmic-driver
# artifacts\windows\AirMic.exe
```

서명된 flat package의 Microsoft catalog signature와 kernel-policy INF/SYS coverage를 확인하므로
`signtool.exe`가 PATH 또는 restore된 `driver/packages`에 필요합니다. 이는 production **driver
패키징에만** 필요한 도구이며 .NET-only GUI/diagnostic build에는 필요하지 않습니다.
원본·MS-PL·native 변경: [driver/UPSTREAM.md](../driver/UPSTREAM.md).

## 6. 테스트의 구분

- `dotnet run --project windows/AirMic.Tests -c Release`: 새 managed core, 합성 ZIP 검사, portable 설치 journal, 600초 가상 drift timelines. 실제 설치/600초 기기 soak 아님.
- `:app:testDebugUnitTest`: Kotlin wire/SPSC 및 FFT·NS·HPF/EQ/compressor/limiter·pipeline 순서/상태/타임라인. 실제 AudioRecord/음성 corpus 품질은 검증하지 않음.
- `:dsp-host-tests:test`: 같은 production Kotlin 파일의 warmed JVM process/configure 할당 계측. Android API에 없는 HotSpot 관리 API는 이 host-only 모듈에만 두며 ART/AudioRecord 전체 무할당의 증거가 아님.
- WPF `--smoke-ui`: 창을 생성·닫는 테스트. 오디오/driver 검증은 아님.
- root CMake와 기존 Python 테스트: 0.1 reference/진단 도구. 새 앱의 통과 결과로 대체하지 않음.
- [전환 보고서](MIGRATION_0_2.md), [현재 DSP 검증](DSP_PIPELINE.md) 및 CI 로그에 실제 수행/미수행 범위를 구분합니다.


## 빠른 마이크 연결 — VB-CABLE 임시 경로

전용 AirMic driver의 Microsoft 서명/설치가 끝나기 전에는 VB-Audio의 **VB-CABLE**을 선택적 브리지로 사용할 수 있습니다. VB-CABLE은 별도 설치가 필요하며 AirMic이 이를 자동 다운로드하거나 설치하지 않습니다.

1. VB-Audio 공식 사이트에서 VB-CABLE을 설치하고 Windows를 재시작합니다(설치 프로그램이 요구하는 경우).
2. AirMic Windows 앱에서 출력 장치 **[가상 마이크 케이블] CABLE Input (VB-Audio Virtual Cable)** 을 선택합니다.
3. 수신 시작을 누릅니다.
4. Discord/게임의 입력 장치를 **CABLE Output (VB-Audio Virtual Cable)** 로 설정합니다.
5. Windows 녹음 장치 속성에서 CABLE Output 입력 레벨을 확인합니다.

이 경로에서는 AirMic의 출력 음량이 마이크 레벨에 영향을 줍니다. 모니터링을 위해 CABLE Output을 스피커로 재생하면 에코/하울링이 생길 수 있으니 기본적으로 모니터링을 끄세요. 장치 이름이 다르거나 CABLE endpoint가 보이지 않으면 VB-CABLE 설치 상태를 확인하세요. 실제 Flip6 → Windows → Discord 전달은 사용자 PC에서 확인해야 합니다.

공식 다운로드/설명: https://vb-audio.com/Cable/
