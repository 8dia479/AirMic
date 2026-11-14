# AirMic 0.1

**Android 스마트폰 → Wi-Fi UDP → Windows WASAPI → VB-CABLE 무선 마이크**

첫 지원 대상: **Samsung Galaxy Z Flip6 (arm64)** / **Windows 10·11 x64**.
48 kHz, mono, PCM16, 480 samples / 10 ms, 976-byte UDP 패킷.

> **현재 상태: MVP 구현 코드 + 호스트 검증, 실기기 인수 테스트 대기.**
> Android SDK/NDK·JDK 및 Windows/실제 휴대폰이 없는 Linux에서 개발했습니다.
> Android APK 빌드 성공, Galaxy 실동작, WASAPI/VB-CABLE 실행, 실제 종단 간
> 30–50 ms 달성, 실기기 10분 안정성을 **주장하지 않습니다**.
> Windows x64 실행 파일은 교차 컴파일되었지만 Windows에서는 미실행입니다.
> 상세한 증거와 남은 조건: [개발 보고서](docs/IMPLEMENTATION_REPORT.md).

## 구현

- **Android:** 마이크/알림 권한, Kotlin UI, Oboe/AAudio low-latency float 캡처,
  arbitrary callback-size → 480-sample 조립, PCM16 변환, 비차단 UDP 송신.
- 마이크 foreground service, 알림에서 중지, Wake/Wi-Fi lock, Wi-Fi 네트워크
  바인딩, 연결 변경/오디오 장치 오류 재시도. PC ACK가 와야 응답 확인 상태 표시.
- **Windows:** Winsock2 UDP 서버, 엄격한 패킷/타임라인/송신자 검사,
  이벤트 기반 WASAPI shared 출력, 실제 render 장치 열거/선택, VB-CABLE 자동 탐색,
  콘솔 상태·통계와 JSON 저장.
- **공통:** lock-free SPSC, sequence wrap/reordering/중복 제거,
  감쇠 PLC, 2–8 프레임 적응형 지터 버퍼, 약 ±300 ppm 재생률 보정.
- **DSP:** HPF → 선택적 기본 noise gate → 3-band EQ → compressor → safety limiter.
  런타임 설정 가능. **WebRTC/RNNoise급 잡음 억제는 미구현**이며 기본 gate와 다릅니다.
  [라이브러리 검토 완료](docs/DSP_EVALUATION.md): WebRTC APM 우선,
  RNNoise 선택형 추가 NS. 실제 통합·모델 포함·성능 검증을 완료했다는 뜻은 아닙니다.
- UDP 진단 송신기, 실제 소켓 loopback soak 도구, 동기화된 2채널 WAV 지연 분석기,
  단위/메모리/스레드/실시간 코어 할당 검사, CI 구성.

## 빠른 시작

**Windows와 Android 코드는 한 저장소의 같은 브랜치에 있습니다.** Visual Studio에서는
저장소 루트(CMake)를, Android Studio에서는 `android/`(Gradle)를 엽니다. `shared/`를
함께 참조하므로 저장소 전체를 받으세요. 브랜치 받기와 두 IDE의 설치/열기 순서는
[빌드 안내](docs/BUILD.md#0-저장소-받기--두-ide에서-여는-위치)를 참고하세요.

### 1. Windows

1. [VB-CABLE 공식 배포](https://vb-audio.com/Cable/)를 직접 설치하고 필요하면 재부팅합니다.
   드라이버는 이 저장소에 포함되지 않습니다.
2. Visual Studio 2022의 **Desktop development with C++**, Windows SDK, CMake 설치 후:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build_windows.ps1
.\artifacts\airmic_receiver.exe --list-devices
.\artifacts\airmic_receiver.exe --port 49000 --allow 192.168.0.20
```

`192.168.0.20`은 **휴대폰 IPv4**로 변경하거나 `--allow`를 생략합니다.
VB-CABLE render endpoint가 정확히 하나이면 자동 선택됩니다. 직접 선택하려면
`--device-index N` 또는 `--device-id ID`를 사용하세요. 일반 스피커는 명시적으로
선택한 경우에만 사용합니다. 피드백에 주의하세요.

필요 시 관리자 PowerShell에서 **Private + LocalSubnet 전용** 방화벽 규칙 생성:

```powershell
.\tools\open_firewall.ps1 -Program .\artifacts\airmic_receiver.exe -Port 49000
```

### 2. Android

JDK 17, SDK 35, NDK `27.2.12479018`, CMake `3.22.1` 준비 후:

```sh
cd android
./gradlew :app:assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

앱에서 **PC의 LAN IPv4**와 포트를 입력하고 권한을 허용한 뒤 전송 시작.
휴대폰과 PC는 서로 통신 가능한 같은 LAN에 있어야 합니다. AP/게스트 격리와
VPN 정책을 확인하세요. **UDP 송신 성공은 PC 연결 성공이 아닙니다.**

### 3. 마이크로 사용

- AirMic PC 출력: **CABLE Input (VB-Audio Virtual Cable)** — render 장치
- Discord/게임의 마이크 입력: **CABLE Output (VB-Audio Virtual Cable)** — capture 장치

이름의 Input/Output 방향이 반대처럼 보이는 것은 가상 케이블의 정상 구조입니다.
30–50 ms는 개발 목표이며, 실제 경로의 지연은 별도로 측정해야 합니다.

## 호스트 테스트

Android/Windows SDK 없이 공통 코어를 빌드할 수 있습니다 (CMake ≥3.22, C++20):

```sh
cmake -S . -B build/host -DCMAKE_BUILD_TYPE=RelWithDebInfo
cmake --build build/host --parallel 2
ctest --test-dir build/host --output-on-failure
./build/host/airmic_loopback --seconds 600
./build/host/airmic_loopback --seconds 10 --impair

python -m pip install -r tools/requirements-latency.txt
python -m unittest discover -s tests -p 'python_test_*.py' -v
```

Linux loopback은 **실제 UDP + 생성 PCM + null sink** 검사입니다. 실제 마이크,
무선 LAN, Windows 오디오 또는 종단 간 지연을 검증하지 않습니다.

## 문서

- [구조·스레딩·DSP 설계](docs/ARCHITECTURE.md)
- [WebRTC APM 우선 / RNNoise 추가 NS 검토·선정](docs/DSP_EVALUATION.md)
- [Audio Protocol v1 / ACK / 바이트 순서](docs/PROTOCOL.md)
- [빌드·실행·방화벽·트러블슈팅](docs/BUILD.md)
- [Galaxy Z Flip6 + Windows 실기기 인수 시험](docs/DEVICE_TEST_PLAN.md)
- [실제 경로 지연 측정 방법](docs/LATENCY.md)
- [구현·미구현·실행 증거·산출물 보고서](docs/IMPLEMENTATION_REPORT.md)
- [서드파티 라이선스](THIRD_PARTY_NOTICES.md)

```text
android/     Kotlin UI/service + Oboe/JNI capture/sender engine
windows/     C++20 Winsock receiver + WASAPI render/device selection
shared/      protocol, SPSC, capture assembler, jitter/PLC/ASRC, DSP
tests/       C++ and Python tests
tools/       UDP diagnostics, soak, WAV latency analysis, Windows build/firewall
docs/        specifications, build/test instructions, recorded results
```

LAN 전용 v1은 **암호화·인증이 없습니다**. 송신자 pinning은 인증이 아닙니다.
공용 Wi-Fi/인터넷에 노출하거나 공유기 포트 포워딩을 하지 마세요.

AirMic 자체 코드는 [MIT](LICENSE). Oboe/Gradle 및 Windows 진단 바이너리 런타임의
별도 라이선스는 notices를 확인하세요.
