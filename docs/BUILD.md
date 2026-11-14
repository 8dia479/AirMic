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
저작권/런타임 고지는 배포 시 함께 보존해야 합니다.

완성된 단일 설치 패키지는 Microsoft 서명·검증된 `AirMic.inf/.cat/.sys`를 제공해야 만들 수 있습니다.
`publish_windows.ps1 -DriverPackageDirectory <서명된패키지경로>`는 이를 검증·내장하는 경로이며,
없으면 정상 제품처럼 publish하지 않고 실패합니다. 자세한 차단 상태는 [전환 보고서](MIGRATION_0_2.md).
앱은 인터넷에서 VB-CABLE/미확인 driver를 내려받거나 부팅 보안 설정을 바꾸지 않습니다.

GUI에서 내장 driver 설치를 선택할 때만 UAC 승인을 요청합니다. 설치 도중 Windows가
재부팅을 요구할 수 있습니다. 일반 수신/청취 실행에는 관리자 권한이 필요 없습니다.

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
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:lintDebug --stacktrace
```

APK: `android/app/build/outputs/apk/debug/app-debug.apk`.
Linux/macOS에서는 같은 위치에서 `./gradlew`를 사용합니다. SDK 경로 자동 감지가 안 되면
ignored `android/local.properties`에 `sdk.dir`를 지정합니다. NDK/JNI/ABI별 `.so`는 없습니다.

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

## 5. 테스트의 구분

- `dotnet run --project windows/AirMic.Tests -c Release`: 새 managed core.
- `:app:testDebugUnitTest`: 새 Kotlin wire/DSP/SPSC. 실제 AudioRecord는 검증하지 않음.
- WPF `--smoke-ui`: 창을 생성·닫는 테스트. 오디오/driver 검증은 아님.
- root CMake와 기존 Python 테스트: 0.1 reference/진단 도구. 새 앱의 통과 결과로 대체하지 않음.
- [전환 보고서](MIGRATION_0_2.md) 및 CI 로그에 실제 수행/미수행 범위를 구분합니다.
