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

실제 드라이버에는 전용 render→capture 전달, 제한된 버퍼/손실 정책, stream/power/장치 제거,
형식 변환과 동시 접근, Windows 10/11 및 Secure Boot 정책 검증이 필요합니다.
정식 배포는 Microsoft driver signing 절차와 파일별 배포 고지를 통과해야 합니다.

## 빌드/검사 상태

이 Linux 작업 공간에는 dotnet/JDK/Android SDK/Windows/WDK가 없습니다.
새 managed/Kotlin source의 CI를 구성했지만 **아직 그 성공을 기록하지 않습니다**.
확인된 CI 결과를 받은 뒤 아래를 갱신합니다. 기존 C++ 테스트 결과를 새 코드 결과로 복사하지 않습니다.

| 검사 | 상태 |
|---|---|
| C# Core 실제 build/test | 대기 |
| Windows WPF build / 실제 창 smoke | 대기 |
| Kotlin JVM test / APK / lint | 대기 |
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
