# 음성 처리 라이브러리 검토 및 선택 결정

**검토일:** 2026-11-14 UTC · **범위:** 기술 검토/선정, 라이브러리 통합 아님

> **검토 완료: WebRTC Audio Processing (APM)을 우선 도입 후보로 선정하고,
> RNNoise는 선택형 추가 잡음 억제 모듈로 유지한다.**
>
> 이는 실측 품질/속도 우위를 입증하거나 배포를 승인한 결정이 아니다. 현재 빌드에는
> 두 라이브러리와 RNNoise 모델이 **없다**. 기존 기본 gate를 고급 NS로 표시하지 않는다.
> 실기기 MVP 인수 및 실제 30–50 ms 지연 달성 여부도 이 검토로 바뀌지 않는다.

## 1. 요구사항과 판단 기준

| 요구사항 | 검토 결과 | 결정 |
|---|---|---|
| WebRTC Audio Processing 우선 검토 | API, 모듈, 스레드/재설정, 빌드, 라이선스 검토 완료 | 첫 외부 음성 처리 후보. 우선 HPF + NS부터 평가하며 AEC/AGC를 일괄 활성화하지 않음 |
| RNNoise를 추가 노이즈 억제 모듈로 검토 | 프레임/스케일, 상태/모델, 지연·빌드·소스 라이선스와 별도 모델 배포 위험 검토 완료 | 필요할 때 선택하는 추가 NS 모듈. WebRTC NS와 무조건 중첩하지 않음 |

판단 기준은 현재 경로와의 호환성, callback 비차단, 런타임 변경, 의존성의 출처와
재현성, 배포 조건이다. 성능·품질은 실제 측정 자료가 없으므로 선정 근거로 수치화하지 않았다.

현재 코드의 경계:

- Android `CaptureAssembler` → SPSC → **sender worker의 `DspProcessor`** → PCM16/UDP.
  입력은 48 kHz mono, **480개의 정규화 float/10 ms**이다. Windows에 같은 NS를 다시
  적용할 필요는 없다. 추가 DSP 때문에 PCM16 BE wire format을 바꿀 이유도 없다.
- 현재 HPF → 선택적 기본 gate → EQ → compressor → limiter는 자체 구현이다.
  API 선언만 추가한 가짜 backend, WebRTC/RNNoise build option·UI·가중치는 없다.
- Unprocessed 입력을 우선 요청하지만 VoiceRecognition fallback이 있어 실제 raw 입력이나
  기기 DSP의 비활성화를 보장할 수 없다. 실제 열린 preset/효과를 기록해 중복 처리를 비교해야 한다.

## 2. 검토한 소스와 근거의 범위

| 후보 | 검토 기준 | 출처 확인 |
|---|---|---|
| WebRTC APM | `3710345e71ad3a7c1f4ed4efef152fd4fc24869a` (2026-01-13 UTC) | GitHub mirror로 파일 확보 후 [공식 Gitiles commit][W1]의 tree와 API/LICENSE/PATENTS blob ID 일치 확인 |
| RNNoise | `v0.2` → `904a876dce1f9ab8860c0a5000ed151f9f6eef58` (2024-04-15 UTC) | Xiph GitHub convenience copy를 사용하고 [공식 GitLab의 동일 commit][R1], header 및 COPYING 확인 |

WebRTC의 위 revision은 **검토용 고정 스냅샷이지 최신 공식 릴리스라는 주장이 아니다**.
RNNoise도 v0.2를 기준으로 한 검토다. 두 값 모두 이번 변경에서 앱 의존성으로 고정한 값은
아니며, 도입 때 지원 버전/보안 수정/변경 API를 재확인해야 한다. 임의 배포자의 prebuilt
AAR/DLL이나 비공식 포크를 공식 배포물 대신 채택하지 않는다.

파일별 Git blob/SHA-256, canonical 대조 값, 실제 검사 명령·결과는
[검토 증거 JSON](test-results/2026-11-14-dsp-review.json)에 기록했다. 받아 본 소스는
ignored `build/dsp-review/`에만 두었으며 앱 소스나 배포 패키지에 vendoring하지 않았다.

### 비교 요약

| 항목 | WebRTC APM | RNNoise v0.2 |
|---|---|---|
| 역할 | HPF, NS, AGC, AEC 등을 선택하는 음성 처리 기반 | 신경망 기반 NS와 음성 존재 확률. AEC/범용 EQ/압축기의 대체물 아님 |
| 48 kHz / mono / 480 | `StreamConfig(48000, 1)`과 float `ProcessStream`; 기존 block 크기에 맞음 | `FRAME_SIZE=480`, 48 kHz demo; 기존 block 크기에 맞음 |
| float 범위 | planar `[-1, 1]`; mono는 채널 포인터 하나 | **PCM16 크기의 float**; 기존 정규화 float에 ×32768/÷32768 변환 필요 |
| 초기화/상태 | C++ builder, ref-counted 인스턴스, stream 초기화 | 불투명 C state, `rnnoise_get_size`/`rnnoise_init` 또는 create/destroy |
| 실시간 주의 | 내부 mutex, 모듈 초기화/재설정 시 할당. lock-free라고 부를 수 없음 | 소스의 frame 처리 경로는 상태·고정 배열 사용. 모델 로드/상태 생성은 별도. 무할당 실측은 안 함 |
| 빌드 부담 | 공식 GN/Ninja + DEPS/Chromium 도구체인. 현재 CMake와 별도 통합 필요 | v0.2 공식 Autotools, 별도 다운로드 모델/생성 header 필요. 현재 CMake에 바로 넣을 수 없음 |
| 라이선스 | BSD-3-Clause 본문 + PATENTS + 실제 링크 의존성별 조건 | 소스는 BSD-3-Clause 중심, 파일별 고지 유지. 모델 아카이브의 조건/해시는 별도 확인 필요 |
| 실제 CPU/메모리/음질/지연 | **미측정** | **미측정** |
| 판단 | 첫 도입 후보로 선정 | 선택형 추가 NS로 검토 완료, 기본 활성화하지 않음 |

## 3. WebRTC APM 우선 검토 결과

### API와 최초 모듈 정책

검토 revision의 public API는 `api/audio/audio_processing.h`이며 생성 경로는
`BuiltinAudioProcessingBuilder(config).Build(CreateEnvironment())`이다. ([API][W2], [builder][W3])
옛 `modules/audio_processing/include` 경로/오래된 factory 예제를 기준으로 통합하지 않는다.
48 kHz는 지원되는 native rate이고 `GetFrameSize(48000)`은 480이다. float API는
planar `[-1, 1]`이며 in-place도 허용한다. 외부 resampling은 이 계약 때문에 필요하지 않지만,
APM 내부 처리율·대역 분할에 따른 비용과 지연은 별도로 측정해야 한다.

**후속 도입의 첫 비교 설정** (현재 적용된 설정이 아님):

| 모듈 | 초기 비교 정책 | 이유 |
|---|---|---|
| High-pass | APM HPF 켬, 자체 HPF는 우회 | 같은 역할의 필터 중복 방지 |
| Noise suppression | 켬, `kModerate`부터 `kLow`와 비교 | 기본 gate를 고급 NS로 대체. 강도가 높을수록 좋다고 가정하지 않음 |
| 기존 gate | 끔 | 말끝/작은 음성에 중복 감쇠를 가하지 않음 |
| GainController1 / GainController2 | 둘 다 끔 | 현재 compressor/makeup과 이중 자동 이득 방지. AGC2는 별도 A/B 후 선택 가능 |
| Echo canceller | 끔 | PC 재생 reference/동기 지연 경로가 없음 |
| Transient suppression / preamplifier | 끔 | 요구 범위 밖 기능을 기본으로 확대하지 않음 |
| EQ / compressor / 최종 safety limiter | 기존 후단 정책 유지하여 비교 | 음색/출력 보호와 NS 효과를 구분. AGC를 도입하면 압축·이득 정책도 다시 조정 |

AEC는 단순히 `enabled=true`로 제공할 수 없다. APM은 reverse/render stream과
`set_stream_delay_ms` 계약을 갖는다. 현재 Android 송신기에는 PC 스피커로 나간 신호의
reference가 없고, ACK나 마이크 신호는 그 대체물이 아니다. NS만 사용하면서 AEC까지
지원한다고 표시하지 않는다. NS는 별도 de-reverb 완료 근거도 아니다.

### 실시간·런타임 변경의 제약

[공식 header][W2]는 모듈 활성화 때 할당/초기화가 생기고, 처리 중 `ApplyConfig`가
하위 모듈 reset과 음질 변화를 일으킬 수 있다고 명시한다. [구현][W4]에서도
`ProcessStream`의 capture mutex, format 변경 시 재초기화, NS 생성의 `make_unique`를
확인했다. 따라서 기존 자체 DSP의 `configure/process` 무할당 보장을 그대로 승계할 수 없다.

후속 구현 시 필요한 경계는 다음과 같다.

1. 생성·설정·`Initialize(ProcessingConfig)`·warm-up·해제는 callback 밖에서 수행한다.
   sample rate/channel 수를 48 kHz/mono로 고정하고 반환 코드를 검사한다.
2. 활성 인스턴스는 sender worker 하나만 소유한다. UI나 제어 thread가 같은 인스턴스의
   stream setter/설정을 처리와 동시에 호출하지 않게 한다. Oboe/WASAPI callback에서는
   APM을 호출하지 않는다. 내부 mutex가 있어도 경쟁을 피하는 설계이며 hard-RT 보장은 아니다.
3. 지원되는 작은 설정은 `PostRuntimeSetting`과 큐 실패 처리를 검토한다. **NS enable/level은
   이 revision의 RuntimeSetting 항목이 아니다.** 일반 런타임 설정 API로 무할당 변경된다고
   가정하지 않는다. backend/NS preset 변경은 준비된 인스턴스를 10 ms 경계에서 인계하는
   방식 등을 검토하고, 준비/폐기와 마지막 참조 해제를 hot path에 두지 않는다.
4. bypass/전환은 상태 warm-up, 경로별 지연 정렬과 crossfade까지 검증해야 한다.
   끈 객체를 매 frame 생성하거나, UI slider마다 `ApplyConfig`를 호출하지 않는다.
5. debug dump/파일 로깅은 실시간 경로에서 사용하지 않는다. 첫 호출·설정 변경·장치 재연결까지
   포함해 할당과 지연을 측정한다. 기존 자체 코어 할당 테스트로 APM을 인증하지 않는다.

### 빌드 및 유지보수

[공식 개발 문서][W5]의 경로는 depot_tools/fetch/gclient → GN → Ninja이다.
[Android 문서][W6]도 Linux에서 별도 Android checkout과 `target_cpu="arm64"` 등을 사용한다.
APM [BUILD.gn][W7]에는 `rtc_base`, `common_audio`, Abseil, AEC/AGC/NS 등 전이 의존성이
있다. 기능을 runtime에서 끄는 것과 해당 코드가 바이너리에서 제거되는 것은 다르다.

따라서 단일 header 또는 `.a` 하나만 현재 CMake에 연결하면 끝난다고 판단하지 않는다.
Android NDK27/Oboe의 `c++_shared`, exception/RTTI/ABI·최소 API·16 KB page 조건,
arm64-v8a/x86_64, Windows 도구체인/CRT와의 일치를 검증해야 한다. 공식 revision과
DEPS를 고정해 좁은 wrapper로 격리하는 편이 API 변경 영향을 줄인다. Windows에서도
지원 플랫폼이라는 사실과 이 프로젝트의 MSVC 링크/실행 성공은 별개다.

**우선 후보 선정 이유:** NS 외에도 일관된 음성 처리 기반을 선택할 수 있고 현재
48 kHz/10 ms·정규화 float 경로에 잘 맞는다. 더 빠르거나 더 좋은 음질이라는 실측
판단은 아니다. GN/ABI 통합 비용이 더 크다는 점을 수용하되 무검증 바이너리로 우회하지 않는다.

## 4. RNNoise 추가 모듈 검토 결과

### 프레임·스케일·상태

[public C API][R2]의 호출은 `rnnoise_process_frame(state, out, in)`이고 반환 float는
**음성 존재 확률**이지 처리된 한 sample이나 오류 코드가 아니다. 오디오는 `out`에 들어간다.
실제 프레임 크기는 초기화 시 `rnnoise_get_frame_size()==480`으로 확인해야 한다.
[공식 demo][R3]는 48 kHz mono PCM16을 float에 그대로 대입한다. 그러므로 기존
정규화 float를 그대로 넣으면 스케일이 맞지 않는다.

- 유한값 검사와 입력 범위 제한 후 `x_rn = x_normalized * 32768`.
  PCM16 대응 범위는 `[-32768, 32767]`이다.
- 처리 후 `y_normalized = y_rn / 32768`. 후단 limiter/PCM16 포화 정책을 유지하고
  NaN/Inf와 clipping을 검사한다. byte-order 변환은 기존 UDP 직렬화 단계의 일이다.
- 각 stream/channel에 지속 `DenoiseState`가 필요하다. 매 frame 초기화하면 안 된다.
  `rnnoise_get_size`/사전 할당/`rnnoise_init` 경로를 검토하여 메모리·초기화 실패를
  처리하고, 생성/파괴는 callback과 steady-state 처리 경로에서 분리한다.
- API에 일반적인 NS 강도 slider가 있다고 가정하지 않는다. VAD 반환값을 그대로
  gate로 쓰거나 임의의 wet/dry mix를 넣는 것은 별도 품질·지연 설계다.

[denoise.c][R4]의 frame 경로에는 지속 상태와 고정 크기 작업 배열이 있지만, 전체 링크
경로의 malloc·stack 사용량·CPU dispatch를 실행 계측하지는 않았다. 모델 로드 함수는
할당/파일 I/O를 하므로 worker 처리 중에 호출하지 않는다. loadable model과 그 buffer는
이를 참조하는 state보다 오래 살아야 한다. `rnnoise_model_from_file`을 쓰면 header의
파일 수명 계약도 따라야 하며, callback에서 파일을 여는 방식은 배제한다.

### 지연과 선택형 결합

v0.2 소스는 480-sample hop 외에 **960-sample 분석창**, analysis/synthesis memory와
이전 스펙트럼 `delayed_X`를 사용한다.[R4] 10 ms I/O 단위가 곧 0 ms 또는 10 ms의 총
추가 지연이라는 뜻이 아니다. demo의 첫 출력 block 생략도 지연 측정을 대신하지 않는다.
실제 모델로 startup/정상상태/전환/tail을 포함해 정렬 지연을 측정해야 하며, 현재는
추가 지연의 확정 수치나 AirMic 30–50 ms 목표 충족을 보고하지 않는다.

**결정:** APM 중심 설계에 선택형 RNNoise NS를 추가할 수 있게 하되, RNNoise 선택 시
WebRTC NS와 기본 gate를 끄는 정책을 우선 검토한다. RNNoise 자체 HPF도 있으므로 외부
HPF와의 중복을 피한다. 사용할 APM 모듈이 없으면 불필요한 APM 통과도 생략할 수 있다.
강한 WebRTC NS → RNNoise의 직렬 이중 억제를 기본값으로 채택하지 않는다. 말끝 손실,
금속성 아티팩트, pumping 가능성은 비교할 위험이지 이 환경에서 측정한 결과가 아니다.

### 빌드·모델 공급망

v0.2의 공식 빌드는 `autogen.sh` → `configure` → `make`이다.[R5] 해당 Git tree에는
CMakeLists와 기본 모델의 `rnnoise_data.c/.h`가 없다. `autogen.sh`는 먼저 모델을
다운로드한다. [model_version/download_model.sh][R6]가 지시하는 것은 다음 파일이다.

```text
model_version: 0b50c45
https://media.xiph.org/rnnoise/models/rnnoise_data-0b50c45.tar.gz
```

이 짧은 version 문자열은 아카이브의 SHA-256이 아니다. 스크립트에도 digest 검사가
없다. 이 검토에서는 모델 아카이브를 확보/검증하지 못했으므로 기본 모델로 라이브러리를
빌드하거나 추론하지 않았다. 무작위 가중치·더미 model로 대체해 성공으로 기록하지 않는다.

도입하려면 모델의 원본 URL, 실제 SHA-256, 배포 조건, 생성 header와 소스 revision을
함께 고정하고 offline/reproducible build가 가능해야 한다. 구형 v0.1.1의 loadable model
format과 다르고 v0.2도 모델 크기/header 호환성이 필요하므로 임의의 weights 파일을
바꿔 끼우지 않는다. 작은 모델도 같은 품질·CPU·배포 조건으로 검증해야 한다.

C API 때문에 wrapper 경계는 APM보다 단순하지만 Android NDK/MSVC 빌드 성공을 뜻하지
않는다. v0.2 Autotools를 외부 빌드로 사용할지 검토한 source list로 CMake를 유지할지
별도 결정해야 한다. ARM64 SIMD와 x86 runtime dispatch/AVX2 설정도 실제 대상별로
검증한다. 개발 PC의 `-march=native` 결과를 모든 Windows 기기에 배포하지 않는다.

## 5. 라이선스/배포 판단

이 항목은 소스 고지를 확인한 기술 검토이며 완성된 배포물의 법률 검토를 대신하지 않는다.

- **WebRTC:** 확인한 root [LICENSE][W8]는 BSD 3-clause 조건이다. source/binary의
  저작권·조건·면책 고지를 유지하고 이름을 보증/홍보에 무단 사용하지 않아야 한다.
  [PATENTS][W9]에는 범위와 특허 소송 시 종료 조건이 있는 추가 grant가 있다.
  무제한 특허 보증으로 해석하지 않는다. 실제 링크한 DEPS/third_party의 라이선스와
  고지를 별도로 수집해야 하며 root LICENSE만 붙여 재배포 승인을 끝낼 수 없다.
- **RNNoise:** 확인한 [COPYING][R7]은 BSD 3-clause이고 header/일부 소스에는 2-clause
  형식의 파일별 고지도 있다. root 조건과 각 파일의 고지를 보존해야 한다.
  **소스의 BSD 조건만으로 미확인 모델 아카이브/사용자 학습 데이터까지 같은 조건이라고
  단정하지 않는다.** 기본/추가 모델의 배포 조건·출처·해시를 확인하기 전 모델 bundling은 보류한다.
- **현재 배포 상태:** 둘 다 의존성/소스/바이너리/weights로 포함하지 않았다.
  [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md)는 검토 후보와 실제 포함물을 구분한다.
  검토용 다운로드는 ignored 경로에만 있고 기존 EXE/ZIP도 변경하지 않았다.

## 6. 실제로 확인한 것과 확인하지 않은 것

| 실행/조사 | 결과 | 증명하지 않는 것 |
|---|---|---|
| WebRTC canonical commit/tree 및 API/LICENSE/PATENTS blob 대조 | 일치; 검토 파일 15개의 blob 검증 | 최신 릴리스 여부, 전체 DEPS 검증, 실제 APM 빌드 |
| RNNoise v0.2 archive와 Git tree의 파일 blob 대조 | 78개 일치; canonical commit/header/COPYING 확인 | 별도 모델 파일의 출처/라이선스/무결성 |
| 실제 RNNoise public header의 C11/C++20 문법·호출 signature 검사 | GCC/G++ 12.2, 둘 다 PASS | RNNoise 라이브러리 링크/추론, Android/Windows ABI |
| 기존 AirMic C++ 코어 재빌드/회귀 실행 | 348,731 checks PASS | 외부 backend의 음질/성능 |
| 기존 AirMic RT 코어 재빌드/실행 | `new/new[]` 0회 | APM/RNNoise 또는 OS 전체 malloc 무할당 |
| APM/RNNoise 실제 모델 포함 빌드·실행 | **미실시** | 통합 성공이나 품질·속도 비교를 주장할 수 없음 |
| Z Flip6 / Windows / VB-CABLE E2E 검사 | **미실시** | 실제 지연/발열/배터리/10분 안정성 |

코어를 직접 GCC로 재빌드했으며 기존 test 비교식에 sign-compare warning 1개가 있었다.
오류는 없었고 두 실행 검사는 통과했다. 정확한 명령과 경고는 증거 JSON에 기록했다.

WebRTC는 GN/DEPS·플랫폼 도구체인이 준비되지 않았고, RNNoise는 별도 모델과 Autotools가
준비되지 않았다. CPU ms/frame, 메모리 MB, SNR/STOI/PESQ, 추가 지연 ms 등의 수치를
만들어 넣지 않았다. 과거 localhost ACK RTT와 자체 DSP 테스트 역시 외부 NS의 측정치가 아니다.

## 7. 별도 통합 단계의 승인 조건

아래는 **이번 검토 완료의 미충족 요구사항이 아니라**, 실제 도입을 진행할 때의 gate다.

1. 공식 revision/DEPS/모델 digest와 모든 배포 고지를 고정하고, 실제 사용할 Android
   backend를 두 ABI에서 compile/link한다. Windows receiver도 회귀 빌드하되 Windows에
   같은 NS를 추가하는 것을 요구하지 않는다. 라이선스/모델 확인이 안 되면 포함하지 않는다.
2. 실제 backend로 48 kHz/480, scale round-trip, 무음/작은 음성/full-scale/NaN/Inf,
   reset/재연결/설정 전환/종료 tail과 실패 시 안전한 bypass를 검사한다.
3. 같은 동의된 한국어 음성/clean reference와 팬·키보드·돌발 잡음 자료로 Basic,
   APM NS, 선택형 RNNoise를 동일 입력/음량/정렬 조건에서 A/B 한다. 말끝·자음·음악·
   숨소리·음성 왜곡을 청취하고, 사용할 수 있는 객관 지표의 한계도 함께 기록한다.
4. Z Flip6에서 첫 호출 및 steady-state ms/frame의 p50/p95/p99/max, heap/stack,
   allocation, queue drop/xrun, 화면 off/열/배터리를 측정한다. sender worker 전체가
   10 ms 입력 주기에 충분한 여유를 갖는지 본다. callback에 I/O/할당/대기를 추가하지 않는다.
5. backend별 정렬 지연과 전환 시 지연 차이를 측정한 후 [실제 경로 지연 절차](LATENCY.md)로
   E2E를 다시 측정한다. 실기기 600초 시험 및 vendor/소비 앱 DSP 중복 비교를 수행한다.
   결과가 나쁘면 Basic으로 돌아갈 수 있어야 하며, 측정 전 기본 활성화를 약속하지 않는다.

**최종 상태:** 요구된 두 후보의 검토와 우선순위 결정은 완료했다. 외부 라이브러리 통합,
추론 benchmark, 모델 재배포 승인, de-reverb/AEC 확장은 이번 변경의 완료 범위가 아니다.

## 공식 소스 참조

- WebRTC: [canonical commit][W1], [APM API][W2], [builder][W3], [구현][W4], [BUILD.gn][W7].
- 공식 빌드 안내: [개발 환경/GN][W5], [Android][W6].
- RNNoise: [canonical commit][R1], [C API][R2], [demo][R3], [frame 처리][R4],
  [README][R5], [모델 다운로드 스크립트][R6].
- 고지: [WebRTC LICENSE][W8] / [PATENTS][W9], [RNNoise COPYING][R7].

[W1]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a?format=JSON
[W2]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a/api/audio/audio_processing.h
[W3]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a/api/audio/builtin_audio_processing_builder.h
[W4]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a/modules/audio_processing/audio_processing_impl.cc
[W5]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a/docs/native-code/development/README.md
[W6]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a/docs/native-code/android/README.md
[W7]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a/modules/audio_processing/BUILD.gn
[W8]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a/LICENSE
[W9]: https://webrtc.googlesource.com/src/+/3710345e71ad3a7c1f4ed4efef152fd4fc24869a/PATENTS
[R1]: https://gitlab.xiph.org/xiph/rnnoise/-/commit/904a876dce1f9ab8860c0a5000ed151f9f6eef58
[R2]: https://gitlab.xiph.org/xiph/rnnoise/-/raw/904a876dce1f9ab8860c0a5000ed151f9f6eef58/include/rnnoise.h
[R3]: https://github.com/xiph/rnnoise/blob/904a876dce1f9ab8860c0a5000ed151f9f6eef58/examples/rnnoise_demo.c
[R4]: https://github.com/xiph/rnnoise/blob/904a876dce1f9ab8860c0a5000ed151f9f6eef58/src/denoise.c
[R5]: https://github.com/xiph/rnnoise/blob/904a876dce1f9ab8860c0a5000ed151f9f6eef58/README
[R6]: https://github.com/xiph/rnnoise/blob/904a876dce1f9ab8860c0a5000ed151f9f6eef58/download_model.sh
[R7]: https://gitlab.xiph.org/xiph/rnnoise/-/raw/904a876dce1f9ab8860c0a5000ed151f9f6eef58/COPYING
