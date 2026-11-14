# 0.2 C# / Kotlin / 단일 설치 전환 보고서

작성일: 2026-11-14 UTC · 작업 브랜치: `arena/56cea872-airmic`

## 사용자 결정과 현재 결론

- Windows 앱은 **C# / .NET 10 / WPF GUI**로 전환했습니다. 앱 solution은 C++/WDK를 사용하지 않습니다.
- Android C++/Oboe/JNI를 제거하고 **순수 Kotlin / AudioRecord**로 전환했습니다. NDK가 필요 없습니다.
- 다른 오디오 앱을 별도 설치하는 방식 대신 **전용 가상 audio driver를 동일 EXE 설치에 포함**하는 구조입니다.
  driver만 native WDK 예외이며 관리자 승인·서명·장치 검증이 필요합니다.

**C#/Kotlin 앱 빌드·테스트, WPF 창 smoke/단일 EXE publish, 실제 native driver의 WDK build와
미서명 catalog 생성/검증까지 통과했습니다.** 다만 Microsoft 정식 driver 서명과 실제 Windows
설치·오디오 전달 검증이 없어 **‘앱 하나 설치하면 Discord/게임 마이크로 사용’의 최종 수용은 아직 BLOCKED**입니다.
일반 스피커 청취나 ACK, sample 사인파를 시스템 마이크 성공으로 간주하지 않습니다.

## 구현 범위

### Windows / Android 앱

- C# Core: 976-byte BE codec, ACK, session/source/timeline gate, 고정 SPSC,
  적응형 2–8 frame jitter/PLC, ±300 ppm 선형 ASRC, 실제 UDP receiver.
- WPF GUI: IP 표시·복사, 포트/허용 IP/지터/출력/음량/시작/중지, 통계, 사용자 설정 저장.
  WASAPI COM/MMCSS를 직접 사용하며 NAudio·VB-CABLE·별도 audio helper 앱은 쓰지 않습니다.
- 일반 스피커는 자동 선택하지 않고 청취 경고를 표시합니다. 전용 경로 식별은 편집 가능한 이름이 아니라
  endpoint의 PnP ancestry에서 **정확한 `Root\AirMic` hardware ID**를 확인하며, render 시작 시 재확인합니다.
  이 식별 코드의 실제 대상 장치 동작은 추가 검증 대상입니다.
- Kotlin: AudioRecord Float/PCM16, Unprocessed/VoiceRecognition fallback,
  capture thread → 고정 SPSC → DSP/UDP worker, ACK 검증·foreground service·재시도.
  Gradle에서 externalNativeBuild/NDK/Oboe/JNI를 제거했습니다.

### 단일 EXE 설치 / 복구 / 제거

다음은 **구현 후 WPF 빌드에 포함된 코드**입니다. 관리자 파일/PnP/registry/방화벽 설치를
CI에서 실행하거나 대상 Windows에서 설치·제거한 것은 아닙니다.

- self-contained EXE에 signed-driver ZIP과 **정확히 resolve된 .NET/WPF runtime pack의 배포 고지**를 내장.
- 명시적 GUI 설치/복구 또는 `AirMic.exe --install`에서만 UAC 요청. 일반 수신은 비관리자로 실행.
- ZIP의 flat 경로·중복·예약 파일명·크기·schema·hardware ID·SHA-256을 먼저 검사.
  SHA-256 자체가 서명 검증을 대체하지는 않으며 Windows PnP가 catalog/signature 정책을 적용합니다.
- `%ProgramFiles%\AirMic\Versions\<EXE-SHA256>`에 설치하고 시작 메뉴/Apps & Features 등록,
  해당 EXE에 **Private + LocalSubnet + UDP**만 허용. 인터넷 다운로드나 보안 정책 변경 없음.
- 파일 및 pending 설치 journal을 먼저 보존하고 driver를 적용한 뒤 앱 버전을 commit.
  중간 실패 시 이전 앱 경로와 후보 파일을 보존하여 복구/제거할 수 있게 설계했습니다.
  driver 변경을 파일 작업과 원자적으로 rollback하거나 조용히 downgrade한다고 주장하지 않습니다.
- 설치/제거 경쟁은 named mutex로 직렬화. OEM INF 이름을 별도 기록하여 장치 제거 후 package 제거 실패도 재시도.
- GUI 제거 또는 Apps & Features에서 정확한 AirMic 장치/연결·기록된 OEM package만 제거하고 `/force` 미사용.
  알려진 소유 파일만 삭제하며 사용자 설정/임의 파일은 보존합니다. 잠긴 EXE의 재부팅 삭제와
  같은 경로에 대한 재부팅 전 재설치 차단 marker를 작성했습니다. 실제 동작 검증은 남아 있습니다.
- normal publish는 Microsoft hardware-driver-signed catalog 및 `signtool /kp /c` INF/SYS coverage를 요구합니다.
  서명된 package가 없으면 **명시적 `-DiagnosticOnly`**만 허용하고 GUI 설치 버튼도 비활성화합니다.

### 전용 native driver 예외

공식 SysVAD의 제한된 source만 MS-PL 고지와 함께 `driver/sysvad/`에 분리했습니다.
샘플 시험음/파일 녹음을 제거하고 다음 transport를 작성했습니다.

`AirMic Input (render)` → adapter-owned 고정 PCM history → 독립적인 `AirMic Microphone (capture)` cursor

- 48 kHz mono PCM16, 4096-sample history, <=480-sample bridge lock transfer, 지연 reader trim/underrun 무음.
- stop/pause/power/discontinuity의 generation invalidation, 소비된 render DMA zeroing,
  보호 콘텐츠 muting, timer quiescence와 stream의 adapter 수명 참조.
- Windows 10 19041 native target / KMDF 1.31로 고정하고 그 이후 SDK 타입에 의존하는
  사용하지 않는 jack-description-3/posture/hardware-resource-group sample handler를 제외했습니다.
- render는 console/multimedia/communications 기본 speaker로 선택되지 않도록 표시합니다.
  해당 역할에서 수동 기본 장치 선택도 제한되며, GUI에서 명시적으로 render를 선택합니다.
- **WDK compile/INF·API validation/Inf2Cat 성공은 커널 안전성·장치 설치·실제 호환성 인증이 아닙니다.**
  원본 pin·수정·MS-PL 조건은 [driver/UPSTREAM.md](../driver/UPSTREAM.md)에 기록했습니다.

## 실제 빌드/검사 결과

이 Linux 작업 공간에는 dotnet/JDK/Android SDK/Windows/WDK가 없어 대상 빌드는
GitHub Actions에서 수행했습니다. C++ history host tests는 이 환경에서도 직접 실행했습니다.

현재 기록된 검증 source: `a025aa8ae60a890e51520936ea8538f9646f4c35`
실행: <https://github.com/8dia479/AirMic/actions/runs/43592745698> — **7개 job 전체 성공**.
이후 변경은 해당 commit의 검증으로 대신하지 않으며 최종 검증 source/기록은 아래 결과 JSON에 남깁니다.

| 검사 | 실제 결과 / 범위 |
|---|---|
| C# Core, Windows / Linux | 각각 통과. 기본 harness **116,436 checks**, warmed core 2,000 cycle allocation guard **0 bytes**. CLR/COM/WASAPI 전체 무할당이라는 뜻 아님. |
| ZIP package 검사 | 합성 ZIP acceptance/rejection 통과. 실제 driver bytes·서명·설치 검사가 아님. |
| 설치 journal | 최초 설치/commit, pending upgrade/이전 버전 보존, 동일 버전 복구, unsafe path 거절 통과. OS 통합 설치/rollback 검사가 아님. |
| managed drift | −200/0/+200 ppm 각각 600초 **가상 시간** playout 통과. 재생 프레임 59,987 / 59,999 / 60,010; peak buffered 2 / 2 / 3, PLC/underrun 0. 물리적 600초 soak/E2E 아님. |
| Windows WPF | Release solution build, 실제 창 생성·닫기 smoke, self-contained 단일 diagnostic EXE + runtime 고지 publish 통과. 실제 audio 장치/PnP 미검증. |
| Android | 순수 Kotlin JVM **3 tests, failures/errors/skipped 0**, debug APK, lint 통과. 실제 AudioRecord/휴대폰 미검증. |
| driver history | 실제 헤더를 G++ Release와 ASan+UBSan 각각 **21,559 host checks** 통과; CI sanitizer 통과. DDI/IRQL/커널 기기 검사가 아님. |
| 전용 driver 빌드 | x64 MSBuild / pinned SDK·WDK 10.0.28000.2526, Win10 19041 target compile, INF/API validation, Inf2Cat `10_VB_X64,10_NI_X64` 통과. **미서명** SYS/INF/CAT 생성. |
| reference | 별도 C++/Python 0.1 회귀 CI 통과. 새 앱의 실기기 결과로 재사용하지 않음. |
| Microsoft driver signing / 실제 설치·제거·WASAPI 전달 | **미수행 / 차단** |
| 실제 Z Flip6 / Windows / Wi-Fi / 소비 앱 600초 및 E2E·GC·발열·배터리 | **미수행** |

이전 앱 검증 기록: `docs/test-results/2026-11-14-managed-kotlin-ci.json`.
새 driver/setup 검증 기록: `docs/test-results/2026-11-14-driver-setup-ci.json`.
미서명 artifact 이름은 `AirMic-driver-UNSIGNED-NOT-A-RELEASE`이며 일반 사용자 설치 제품이 아닙니다.
`AirMic-windows-x64-DIAGNOSTIC-NO-DRIVER`도 시스템 마이크가 아닙니다.

## 남은 수용 단계 / 성능 계약

1. 유지관리자의 Microsoft 정식 driver 서명 및 배포 EXE 서명·출처/라이선스 검토.
2. 지원 보안 설정을 유지한 실제 Windows 10/11 x64의 설치·업그레이드·제거·전원·장치·소비 앱 검증.
3. Z Flip6 실기기 및 실제 audio-path 지연·600초 안정성 측정.

세부 **미수행 체크리스트**: [Windows driver 검증 계획](WINDOWS_DRIVER_VALIDATION.md).
보안 부팅·메모리 무결성·BitLocker·서명 강제 정책을 끄는 우회는 구현하지 않습니다.

수신/render/UI와 capture/DSP/network를 분리합니다. 오디오 처리에 파일/네트워크/사용자 mutex를
넣지 않습니다. 처리 저장소는 사전 할당하지만 CLR/ART의 GC/JIT/COM/드라이버 내부까지
hard-RT 또는 무할당이라고 주장하지 않습니다. **30–50 ms는 계속 미검증 목표**입니다.
기본 gate는 고급 NS가 아닙니다. WebRTC/RNNoise는 후보 검토만 유지하고 통합하지 않았습니다.

과거 0.1 보고서는 [IMPLEMENTATION_REPORT.md](IMPLEMENTATION_REPORT.md),
현재 빌드는 [BUILD.md](BUILD.md), protocol은 [PROTOCOL.md](PROTOCOL.md)를 참조하세요.
