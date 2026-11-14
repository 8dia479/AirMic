# AirMic 0.2 preview

**Android 마이크 → Wi-Fi UDP → C# Windows GUI 수신기**

## 이번 구조 변경

- **Windows:** C# / .NET 10 / WPF. C++ 데스크톱 워크로드·CMake·NAudio·VB-CABLE에 의존하지 않습니다.
- **Android:** Kotlin / `AudioRecord`. 앱 빌드에서 C++·JNI·NDK·Oboe를 제거했습니다.
- 한 저장소·같은 브랜치에 두 앱을 유지합니다. 48 kHz mono PCM16 BE / 480 samples / 10 ms
  프로토콜은 유지해 기존 진단 송신기와 호환합니다.
- Windows GUI에서 PC IPv4, 포트, 휴대폰 IP 제한, 지터 버퍼, 출력 장치, 음량,
  시작/중지, 패킷·오디오 통계를 조절/확인합니다. 설정은 사용자 LocalAppData에 저장합니다.

> **중요 — ‘다른 앱 없이 시스템 마이크’는 아직 완료되지 않았습니다.**
>
> 선택한 최종 구조는 **C# 앱 + AirMic 전용 가상 오디오 드라이버를 포함한 단일 설치**입니다.
> C# 앱이나 스피커 출력만으로는 Discord/게임이 선택하는 Windows 마이크 장치가 생기지 않습니다.
> 정식 서명·검증된 드라이버가 없는 개발 빌드는 **수신 진단/명시적 청취용**이며 시스템 마이크가 아닙니다.
> 앱은 이 상태를 GUI에 표시하고 드라이버 설치 버튼을 비활성화합니다. VB-CABLE 설치를 대신 요구하지 않습니다.
> 진행 상태와 차단 조건: [전환 보고서](docs/MIGRATION_0_2.md).

## 저장소 구조

```text
windows/AirMic.sln       C# solution — Visual Studio에서 열기
  AirMic.App/           WPF GUI / Windows WASAPI / 내장 드라이버 설치 경로
  AirMic.Core/          C# wire codec / 세션 / SPSC / 지터·PLC·ASRC / UDP
  AirMic.Tests/         별도 테스트 패키지 없는 .NET console tests
android/                Android Studio에서 열기
  app/src/main/         Kotlin UI / FGS / AudioRecord / DSP / UDP
  app/src/test/         Kotlin wire / DSP / SPSC 테스트
driver/                 전용 native WaveRT driver 예외 (WDK/서명/장치 검증은 별도)
shared/, tests/         0.1 C++ reference 회귀·Python 진단 (앱 빌드에는 사용하지 않음)
docs/                   현재 빌드 안내, 전환 보고서, 프로토콜 및 과거 결과
```

## Windows 빌드

**.NET 10 SDK**와 .NET 데스크톱 개발 도구가 필요합니다. Visual Studio는 .NET 10을 지원하는
버전을 사용하거나 `dotnet` CLI로 빌드하세요. C++/WDK는 **앱 빌드에 필요하지 않습니다**.

```powershell
dotnet build .\windows\AirMic.sln -c Release
dotnet run --project .\windows\AirMic.Tests -c Release
dotnet run --project .\windows\AirMic.App -c Release
```

.NET 런타임까지 포함한 개발용 단일 EXE (드라이버 없음):

```powershell
.\tools\publish_windows.ps1 -DiagnosticOnly
# artifacts\windows-diagnostic\AirMic.exe
```

정식 드라이버가 없는 경우 GUI의 **수신 진단만**을 선택합니다. 다른 일반 출력 장치는
경고에 동의한 경우에만 청취용으로 사용할 수 있습니다. 일반 스피커를 자동 선택하지 않습니다.

## Android 빌드

Android Studio에서 **`AirMic/android`**를 엽니다. JDK 17, Android SDK API 35,
Build Tools 35.0.0이 필요합니다. **NDK / CMake 설치는 더 이상 필요 없습니다.**

```powershell
cd android
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:lintDebug
```

APK: `android/app/build/outputs/apk/debug/app-debug.apk`.
USB 디버깅으로 휴대폰을 연결하고 Android Studio의 **app → Run**으로 설치할 수도 있습니다.
PC와 같은 LAN에 연결해 GUI에 표시된 PC IPv4와 같은 포트를 입력하고 마이크 권한을 허용하세요.

## 검증과 한계

- 새 C#/Kotlin 코드의 실행 결과는 [전환 보고서](docs/MIGRATION_0_2.md)에 별도로 기록합니다.
  과거 C++ 348,731 checks/localhost soak를 새 코드의 통과 결과로 재사용하지 않습니다.
- `AudioRecord`의 실제 형식/버퍼·기기 DSP, .NET GC/COM, WASAPI, 드라이버,
  실제 Wi-Fi·화면 off·발열·배터리·600초 안정성과 E2E 지연은 대상 기기 검증이 필요합니다.
- **30–50 ms는 계속 미검증 목표**입니다. ACK는 수신 확인일 뿐 마이크 출력/인증/오디오 지연 측정이 아닙니다.
- Android HPF → 선택적 기본 gate → EQ → compressor → limiter는 Kotlin으로 옮겼습니다.
  **WebRTC/RNNoise 통합·de-reverb·AEC는 없습니다.** [기존 후보 검토](docs/DSP_EVALUATION.md).
- v1은 암호화/인증이 없는 신뢰 LAN 전용입니다. 공용 Wi-Fi·인터넷·포트 포워딩에 노출하지 마세요.

[빌드·실행 안내](docs/BUILD.md) · [프로토콜](docs/PROTOCOL.md) ·
[지연 측정](docs/LATENCY.md) · [서드파티 고지](THIRD_PARTY_NOTICES.md)
