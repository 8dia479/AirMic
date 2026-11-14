# AirMic MVP 0.1 개발 보고서

**작성일:** 2026-11-14 UTC

**작업 브랜치:** `arena/56cea872-airmic`

**시작 상태:** README만 있는 저장소. 기존 엔진/앱 코드는 없었음.

> ## 최종 판정: 구현 후보 + 호스트 검증 완료 / 실기기 MVP 인수는 BLOCKED
>
> 실제 Galaxy Z Flip6·Windows·VB-CABLE이 없고 Android SDK/NDK/JDK도 없어
> 사용자가 제시한 전체 MVP 완료 조건은 아직 충족하지 못했습니다.
> **Android 실동작, Windows 오디오 실동작, 실기기 10분 안정성, 30–50 ms 달성을
> 완료했다고 주장하지 않습니다.** 가능한 빌드/검사를 수행하고 그 경계를 아래에 기록합니다.

## 1. 구현된 기능 (코드 기준)

### Android — Kotlin UI / foreground service / NDK 엔진

- 마이크·알림 권한 요청, PC IPv4/port 입력과 검증, 상태/통계/입력 meter.
- 공식 Oboe 1.9.3 / AAudio, low-latency, mono float 48 kHz 캡처 요청.
- Unprocessed preset 및 Exclusive 우선, Shared/VoiceRecognition 열기 fallback.
- arbitrary callback 크기 → 480 samples 조립 → SPSC → worker DSP/PCM16/UDP.
- callback에 네트워크·파일 I/O·사용자 mutex·heap allocation 없음.
- 비차단 UDP, 오래된 frame 폐기, 송신/큐 손실 통계.
- PC ACK 검증과 UDP RTT; ACK 없이 허위 연결 성공으로 표시하지 않음.
- microphone foreground service/알림 중지, Wake/Wi-Fi lock, Wi-Fi 전용 process binding.
- Wi-Fi 변경/녹음 오류 재시도, service 간 전역 제어 직렬화, capture generation 분리.
- 런타임 DSP bypass/gate/input gain/3-band EQ 조절 및 preferences.

**주의:** 위 Android 항목은 실제 구현 소스이며, APK/기기 동작 검증은 아직 아닙니다.

### Windows — C++20 receiver

- Winsock2 IPv4 UDP 서버, 길이/count/flags/START/source/timestamp 엄격 검증.
- 단일 source pin, 선택적 `--allow`, timeout/new-session generation.
- fixed SPSC ingress → jitter/reorder/duplicate 제거/PLC/재생률 보정.
- 실제 render endpoint 열거, VB-CABLE 자동 탐색/명시적 index·ID 선택.
- 이벤트 기반 WASAPI shared PCM16 mono 48 kHz 출력, endpoint format 자동 변환 요청.
- MMCSS, 실제 buffer capacity/endpoint reported latency, 콘솔 상태/통계/JSON.
- Ctrl+C/기간 종료 정리 및 device invalidation 오류 표시.

**주의:** Windows x64 코드와 테스트는 교차 컴파일/링크됐지만 Windows에서 미실행입니다.

### 공통 / 개발 도구

- 16-byte header + 960-byte PCM payload의 정확한 976-byte v1 serialization.
- header와 PCM16 모두 BE, uint32 sequence wrap, monotonic sample timestamp.
- lock-free SPSC, bounded reorder, 감쇠/반복 PLC, 2–8 frame adaptive jitter.
- 약 ±300 ppm 선형 ASRC로 clock drift 완화.
- HPF → 선택적 기본 adaptive noise gate → EQ → compressor → safety limiter.
- 실제 UDP headless soak, 독립 Python wire 진단 source, 실제 2채널 WAV 지연 분석기.
- 단위/fuzz/동시성/무할당/ASan+UBSan/TSan 검사와 빌드/인수 시험 문서.
- Windows/Android/Linux CI 정의 추가 (**원격 CI 실행 결과는 없음**).

## 2. 구현되지 않은 기능

- WebRTC Audio Processing / RNNoise 실제 통합, 말하는 동안의 고급 배경 잡음 제거.
  기본 noise gate는 이 기능들의 대체 완료물이 아님.
- de-reverb, AEC (PC 재생 reference 채널 없음), Opus.
- 자체 Windows virtual microphone driver (현재 VB-CABLE 외부 설치 필요).
- Windows GUI/자동 endpoint re-open, 자동 검색/페어링, IPv6/USB/Bluetooth.
- 암호화·인증·replay 방어·인터넷/NAT 전송.
- 실제 물리 경로 지연 자동 취득. WAV 분석 도구는 구현됐지만 실제 녹음이 필요함.
- release signing, installer, Play/Store 배포.

### 별도 요구사항: 음성 처리 라이브러리 검토 완료

2026-11-14 [검토·선정 문서](DSP_EVALUATION.md)에서 **WebRTC APM 우선 검토 및
RNNoise 선택형 추가 NS 검토를 완료**했습니다. 공식 revision/API·모듈, float scale,
실시간/재설정, GN/Autotools, 소스 라이선스와 모델 배포 조건을 구분해 판단했습니다.
WebRTC를 첫 외부 도입 후보로, RNNoise를 이중 억제를 기본으로 하지 않는 추가 모듈로
선정했습니다. 이 요구는 검토 범위이며 위 목록의 **실제 통합은 여전히 미구현**입니다.

검토 중 기존 코어를 GCC 12.2로 다시 빌드해 348,731 checks와 RT new/new[] 0회를
확인했고, 실제 RNNoise public header의 C11/C++20 signature 검사를 통과했습니다.
이는 두 외부 라이브러리의 빌드/추론/성능 검증이 아닙니다. 모델 아카이브의 배포 조건과
해시는 미확인이며 모델 bundling은 보류합니다. 상세 증거:
[2026-11-14-dsp-review.json](test-results/2026-11-14-dsp-review.json).
이번 검토에서 EXE/ZIP을 새로 빌드하지 않았으며, 프로토콜·런타임 DSP와 실기기 인수
차단 상태를 바꾸지 않았습니다.

## 3. Android 빌드 결과

**실패/환경 차단 — APK 없음.** 실제 시도:

```text
./android/gradlew -p android :app:assembleDebug
exit 1
ERROR: JAVA_HOME is not set and no 'java' command could be found in your PATH.
```

SDK/NDK도 설치돼 있지 않습니다. Gradle launcher 이전 단계 실패이므로 Kotlin/AGP/
Prefab/NDK 성공을 추론할 수 없습니다.

대체로 수행한 검사:

- 공식 Oboe tag/commit 및 Gradle wrapper JAR SHA-256 확인.
- 공식 Oboe 1.9.3 headers로 `audio_engine.cpp`의 GCC C++20 host syntax 검사 통과.
  이는 Linux POSIX headers를 사용한 검사이지 NDK cross-build/link가 아닙니다.
- Kotlin/Gradle Kotlin DSL 7개 파일 tree-sitter 문법 parsing 통과.
  **Kotlin compiler/type checking이 아닙니다.**
- Android manifest 및 resource XML parsing 통과.
- 공식 Prefab 안내에 맞춰 `c++_shared`, NDK27 flexible page-size opt-in 및
  build-tools 35.0.0을 명시. 실제 Gradle/NDK 검증은 여전히 필요.

재현 도구 버전/설치 절차는 [BUILD.md](BUILD.md), 실제 기기 시험은
[DEVICE_TEST_PLAN.md](DEVICE_TEST_PLAN.md).

## 4. Windows 빌드 결과

**Linux → Windows x64 교차 컴파일/링크 성공. MSVC native build/실행은 미검증.**

- Zig 0.13.0 / Clang 18.1.6, `x86_64-windows-gnu`, C++20 Release.
- `airmic_receiver.exe`, `airmic_core_tests.exe`, `airmic_rt_tests.exe` 생성.
- objdump로 `pei-x86-64` / executable / Win32 DLL imports 확인.
- WASAPI/COM/Winsock2/AVRT 호출을 실제 Windows header/import libraries에 링크.
- Windows runtime, device enumeration, VB-CABLE 및 PowerShell scripts는 실행하지 못함.
- 교차 빌드 바이너리는 unsigned 진단용이지 Windows-tested release가 아님.

## 5. 실행 테스트 결과

### 환경

Linux x86_64 / kernel 6.1.158, GCC 12.2.0, CMake 3.31.10,
Ninja 1.13.2, Python 3.11.2, NumPy 2.2.6.

| 실행한 검사 | 결과 | 범위 |
|---|---|---|
| C++ Release CTest | 2/2 PASS | core + realtime allocations |
| core checks | 348,731 PASS | wire/golden/fuzz/SPSC/capture/session/jitter/PLC/DSP/drift |
| RT allocation guard | new/new[] 0회 | aligned 포함, 자체 코어 steady-state |
| ASan + UBSan CTest | 2/2 PASS | 메모리/정의되지 않은 동작 검사 |
| ThreadSanitizer CTest | 2/2 PASS | host 동시성 검사 포함 |
| Python 도구 검사 | 7/7 PASS | 독립 wire/실제 UDP ACK/WAV 분석 |
| ±100/±250 ppm drift | 각 가상 시간 10분 PASS | 물리 시간/장치 soak 아님 |
| 실제 시간 600초 UDP (최종 코어) | PASS | Linux localhost + 생성 PCM + null sink |
| 10초 loss/duplicate/reorder 주입 | PASS | 같은 headless 경로 |
| Galaxy/Windows/VB-CABLE 실행 | 미실행 | 기기/OS 부재 |

### 최종 600초 UDP 결과

원본: [2026-11-14-linux-udp-600s.json](test-results/2026-11-14-linux-udp-600s.json)

- 송신 60,000 / 정상 수락 60,000 datagrams.
- ingress queue drops **0**, 검출 deadline loss/PLC **0**, underruns **0**.
- malformed 5개 및 다른 source port 1개 거부.
- render calls 60,000, nonzero output blocks 59,998, played packets 59,999.
- 100 ms 초과 render scheduling stall **0**.
- 종료 시 남는 buffer/lookahead tail과 초기 waiting은 전송 유실이 아님.
- adaptive target 종료값 2 frames; jitter 추정 0.029233 ms.
- ACK sample 2,349개, localhost UDP RTT p50 **0.122 ms**, p95 **0.173 ms**.

이 결과는 실제 소켓/독립 스레드를 사용하는 **소프트웨어 전송 경로 검사**입니다.
Wi-Fi·마이크·스피커·Windows 오디오 드라이버를 포함하지 않습니다. 낮은 실제 장치 CPU나
하드웨어 E2E 지연을 증명하지 않습니다.

### 최종 10초 장애 주입 결과

원본: [2026-11-14-linux-udp-impaired-10s.json](test-results/2026-11-14-linux-udp-impaired-10s.json)

- 의도적 loss 2개, duplicate 3개 및 sequence pair 재정렬 주입.
- 수락 1,001 datagrams, duplicate 제거 3개, queue drops 0, underruns 0.
- 종료 전 playout 범위에서 loss/PLC 1개. 나머지 주입 loss는 마지막 sequence 998로
  종료 시 아직 소비하지 않은 tail에 있어 deadline loss 집계 전임.
- 올바른 재정렬/손실 복구 순서는 별도 core unit test에서도 확인.

## 6. 실제 측정된 지연 시간

**Galaxy→Wi-Fi→Windows→VB-CABLE 종단 간 지연: 미측정.**

측정된 값은 위 Linux localhost **UDP ACK RTT**뿐입니다. RTT/2나 WASAPI 보고값을
종단 간 지연으로 바꾸어 적지 않았습니다. 30–50 ms 목표 달성 여부는 알 수 없습니다.

실제 경로 측정용 `tools/measure_latency.py`를 구현했고 알려진 합성 offset/45 ms WAV로
계산 회귀 검사를 했습니다. 이 45 ms는 **합성 fixture 값**이며 AirMic 성능이 아닙니다.
실제 공통 클럭 녹음/보정 절차: [LATENCY.md](LATENCY.md).

## 7. 발견한 문제와 해결 내역

1. **느린 source clock에서 언더런 (실제 unit test 실패로 재현).**
   - 기존 80 ppm/frame 보정으로 -250 ppm source를 따라가지 못함.
   - 300 ppm/frame 피드백/±300 ppm cap으로 수정.
   - ±100/±250 ppm × 각 10분 가상 클럭, sanitizer, 최종 실제 600초 UDP 재검사 통과.
2. **Windows 교차 빌드 컴파일 오류: SIO_UDP_CONNRESET 미정의.**
   - 올바른 `mswsock.h` include로 수정, 재빌드 통과.
3. **Windows 링크 오류: PKEY_Device_FriendlyName 미해결.**
   - device TU에서 `initguid.h`로 PROPERTYKEY 인스턴스 정의, 링크 통과.
4. **Zig toolchain try_compile에서 compiler path 유실 / Win32 macro 재정의.**
   - CMAKE_TRY_COMPILE_PLATFORM_VARIABLES 전달 및 MinGW macro override 순서 수정.
5. **Oboe umbrella header의 host syntax 검사 include 의존 문제.**
   - 필요한 AudioStream/Builder/Utilities header를 직접 사용, syntax 검사 통과.
6. **리뷰로 발견한 Android lifecycle 경쟁 가능성 (실기기 재현 아님).**
   - old service cleanup이 새 엔진을 중지할 수 있어 process-wide control executor 도입.
   - 늦은 이전 Oboe error가 새 stream 상태를 덮지 않도록 per-open callback/error와
     generation 분리. 실패 직후 재열기 반복은 안정 동작 30초 후 backoff reset.
   - host syntax/문법 검사 통과, 실제 Android 오류/재접속 시험은 여전히 필요.

## 8. 남은 문제 / 인수 차단 조건

가장 큰 차단 요인은 **Android 빌드 및 실제 두 장치 오디오 경로 검증 부재**입니다.
코드 수준에서 고쳐야 할 기기별 문제가 남아 있을 수 있습니다.

- Galaxy Z Flip6의 실제 input format, burst/xrun, Unprocessed 지원, 화면 off,
  권한/통화 경쟁, Samsung 절전/발열을 검증해야 함.
- Android JNI/AGP/NDK/Prefab 및 16 KB APK 검증 필요. 파싱은 빌드 대체물이 아님.
- Windows 10/11 native 빌드, 실제 WASAPI autoconvert/버퍼와 VB-CABLE 입력/Discord
  경로 확인. 장치 무효화 시 자동 re-open은 아직 없음.
- 큰 EQ 변경/PLC/target 조정의 청감 아티팩트, ±300 ppm 밖의 드리프트,
  다양한 실제 WASAPI callback 크기와 장시간 물리 clock 시험 필요.
- noise gate는 speech mixed noise를 제거하지 못함. WebRTC/RNNoise 후속 통합 필요.
- v1 plaintext/source pin은 인증이 아니며 Internet 노출에 부적합.
- 실측 지연, 실제 기기 CPU/메모리/배터리, 실기기 10분 안정성 자료 없음.

## 9. APK 및 Windows 실행 파일

### 현재 실제로 생성한 산출물

- Windows x64 진단 실행 파일:
  `artifacts/airmic_receiver-0.1.0-windows-x64-cross.exe`
- runtime licenses/사용 안내 포함 묶음:
  `artifacts/AirMic-0.1.0-windows-x64-cross.zip`
- 크기: 실행 파일 **801,792 bytes**.
- SHA-256:
  `eecb0144b478d2f2f5a2fd5859baa29dacd46dc7b0a8f87d5e9771b56b10bc40`
- **Windows에서 실행하지 않은 unsigned cross binary**. VB-CABLE driver/Android APK 없음.
- generated binaries는 Git ignore 대상 `artifacts/`에 둠. source/dependency tree에
  대형 빌드 산출물을 체크인하지 않음.

### 아직 없는 산출물

- **Android APK: 없음.** 정상 build 후 예상 경로는
  `android/app/build/outputs/apk/debug/app-debug.apk`.
- Windows MSVC build 후 script가 복사하는 경로는 `artifacts/airmic_receiver.exe`.
  현재 cross 파일과 native 검증 파일을 혼동하지 않도록 이름을 분리함.

## 10. 다음 개발 단계 (우선순위)

1. JDK17/Android SDK·NDK가 있는 환경에서 pinned build + lint, 실제 Z Flip6 설치.
2. Windows MSVC build 후 CABLE Input 선택 → CABLE Output 녹음/Discord 확인.
3. [실기기 시험표](DEVICE_TEST_PLAN.md)의 정상/권한/재접속/화면 off/600초 연속 테스트 수행.
4. 공통 clock 2채널 녹음으로 실제 경로 median/p95 측정 및 latency budget 조정.
5. 고급 NS 도입을 별도 진행할 경우 [완료된 검토 결정](DSP_EVALUATION.md)에 따라
   WebRTC APM부터 통합/실측하고 RNNoise는 선택형 추가 모듈로 비교. 실제 CPU/열/배터리·
   품질·지연 검증 전 기본 활성화를 약속하지 않음. PLC/ASRC 개선도 별도 검토.
6. 인증/페어링/session ID 기반 후속 프로토콜, Opus, 별도 de-reverb, Windows GUI/
   endpoint recovery 및 향후 자체 virtual driver를 단계적으로 추진.

사용자 승인을 기다리며 구현 단계를 멈춘 것은 아닙니다. 이 환경에서 가능한 코딩/
빌드/재테스트는 수행했으며, 전체 MVP 완료 표시는 실제 장치/플랫폼 인수 증거가
갖춰질 때까지 보류합니다.
