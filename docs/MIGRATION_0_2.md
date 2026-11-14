# 0.2 C# / Kotlin / 단일 설치 전환 보고서

작성일: 2026-11-14 UTC · 작업 브랜치: `arena/56cea872-airmic`

## 사용자 결정

- Windows 앱은 C#/.NET GUI로 전환.
- Android 앱의 C++/Oboe/JNI도 제거하고 Kotlin AudioRecord 사용.
- 다른 앱을 별도 설치하는 대신 전용 가상 오디오 driver를 AirMic 설치에 포함.
  driver 자체는 네이티브 개발 예외이며 관리자 승인·서명·검증이 필요함을 확인.

## 구현 중 / 현재 경계

- C# Core: 976-byte BE codec, ACK, session/source/timeline gate, 고정 SPSC,
  적응형 2–8 frame jitter/PLC, ±300 ppm 선형 ASRC, 실제 UDP receiver.
- WPF GUI: IP 표시, 포트/허용 IP/지터/출력/음량/시작/중지, 통계, 설정 저장,
  기본 스피커 자동 선택 방지. WASAPI COM을 직접 사용하며 NAudio 등 런타임 앱은 필요 없음.
- Kotlin: AudioRecord Float 우선/PCM16 fallback, Unprocessed/VoiceRecognition fallback,
  capture thread → 고정 SPSC → Kotlin DSP/UDP worker, ACK 검증·수명주기/재시도.
  앱 Gradle에서 externalNativeBuild/NDK/Oboe를 제거하고 JNI source도 제거.
- 단일 설치 경로: publish 시 driver zip을 resource로 내장하고 동일 EXE의 명시적 UAC
  설치 경로가 hash/하드웨어 ID 검증 후 Windows PnP API를 사용함. 인터넷 다운로드,
  서명 강제 해제, 테스트 모드 자동 설정, VB-CABLE 자동 설치는 없음.

## 아직 완료하지 않은 핵심 항목

**전용 가상 driver의 실제 build·서명·장치 설치·오디오 전달 검증은 완료하지 않았습니다.**
따라서 ‘앱 하나 설치하면 Discord/게임 마이크로 바로 사용’ 요구는 아직 BLOCKED입니다.
샘플 SysVAD의 사인파 출력 또는 일반 스피커 청취를 그 요구의 완료로 간주하지 않습니다.
서명된 package가 없는 publish는 명시적인 `-DiagnosticOnly`만 허용하며 GUI도 미준비 상태를 표시합니다.

공식 SysVAD의 제한된 소스만 MS-PL 고지와 함께 `driver/sysvad/`에 분리했습니다.
샘플 시험음/파일 녹음을 제거하고 `AirMic Input`→고정 PCM history→각 `AirMic Microphone`
stream cursor 전달 코드, underrun 무음·지연 tail 폐기·정지/전원 generation 리셋,
48 kHz mono PCM16 고정 형식, 전용 INF/WDK 빌드 경로를 작성했습니다.
**이 코드는 아직 WDK/커널 장치 검증 전**입니다. 원본·수정 범위: [driver/UPSTREAM.md](../driver/UPSTREAM.md).
Windows 10/11 실제 stream/power/장치 제거·Secure Boot 정책 및 형식 변환/동시 접근 검증이 필요합니다.
정식 배포는 Microsoft driver signing 절차와 파일별 배포 고지를 통과해야 합니다.

## 빌드/검사 상태

이 Linux 작업 공간에는 dotnet/JDK/Android SDK/Windows/WDK가 없어 실제 대상 빌드는
GitHub Actions에서 수행했습니다. 첫 Android SDK 준비 실패 후 runner의 SDK를 사용하도록
고쳐 **앱 CI 전체 통과**를 확인했습니다.

- 검증 commit: `a1912b269ce3173c5a985b31d0381e30927d489b`
- 실행: <https://github.com/8dia479/AirMic/actions/runs/43589817494>
- C# 코어 Windows/Linux 각각 통과; Windows 결과 **116,436 checks**, 코어 2,000 cycle
  steady-state allocation guard **0 bytes** (CLR 전체/WASAPI까지 무할당이라는 뜻 아님).
- WPF Release solution build, 실제 창 생성·닫기 smoke, self-contained 단일 diagnostic EXE publish 통과.
- Android 순수 Kotlin JVM tests / debug APK / lint tasks 통과. AudioRecord 실제 실행은 미검증.
- `AudioHistory.h` driver 코어는 이 Linux 환경에서 G++ Release와 ASan+UBSan 각각
  **21,551 host checks 통과**. 이는 kernel/DDI/IRQL/endpoint 설치 검증이 아닙니다.
- 별도 native-driver CI는 새 driver source build/Inf2Cat 검증을 위해 구성했고 결과 대기 중입니다.

기존 C++ 테스트 결과를 새 코드 결과로 복사하지 않습니다.

| 검사 | 상태 |
|---|---|
| C# Core 실제 build/test | Windows / Linux CI 통과 |
| Windows WPF build / 실제 창 smoke | CI 통과 (오디오 장치 아님) |
| Kotlin JVM test / APK / lint | CI 통과 (휴대폰 실행 아님) |
| 전용 driver build / 설치 / 실제 PCM 전달 | 미검증 |
| 실제 Z Flip6 / Windows / Wi-Fi / 소비 앱 600초 | 미실시 |
| 새 경로 E2E·GC·발열·배터리 측정 | 미실시 |

## 런타임/성능 계약

수신/render/UI와 capture/DSP/network를 분리합니다. 오디오 처리에 파일/네트워크/사용자
mutex를 넣지 않습니다. C# core/Kotlin loop의 저장소는 사전 할당하지만 CLR/ART의 GC,
JIT, COM/드라이버 내부까지 hard-RT 또는 무할당이라고 주장하지 않습니다.
30–50 ms는 계속 개발 목표이며 AudioRecord 전환 후의 실제 지연은 다시 측정해야 합니다.
기본 gate는 고급 NS가 아닙니다. WebRTC/RNNoise 후보 검토는 유지하되 새 앱에 통합하지 않았습니다.

과거 0.1 결과는 [IMPLEMENTATION_REPORT.md](IMPLEMENTATION_REPORT.md),
현재 빌드 절차는 [BUILD.md](BUILD.md), protocol v1은 [PROTOCOL.md](PROTOCOL.md).
