# 실시간 음성 DSP — Kotlin 5단 파이프라인

범위: **High-pass Filter → Noise Suppression → EQ → Compressor → Limiter 구현**,
**De-reverb는 별도 기능으로 설계**. Windows/C#·순수 Kotlin/no-NDK 구조를 유지합니다.
WebRTC/RNNoise/AEC/AGC/신경망 모델 통합으로 범위를 확대하지 않습니다.

소스: `android/app/src/main/java/io/airmic/{DspProcessor,DspStages,NoiseSuppressor,Radix2Fft}.kt`.
실제 sender worker는 `DspFrameProcessor`를 호출합니다. 과거 `shared/dsp/`의 C++ 코드는
비교용 reference일 뿐 앱의 활성 DSP가 아닙니다.

## 1. 실제 처리 순서와 설정

```text
AudioRecord float/PCM16 -> capture SPSC (480 floats, 48 kHz mono)
  -> sanitize / input trim
  -> HighPassFilter
  -> NoiseSuppressor
  -> VoiceEqualizer
  -> VoiceCompressor
  -> PeakLimiter
  -> PCM16 BE / UDP
```

| 순서 | 구현 / 기본값 | 런타임 설정 |
|---|---|---|
| HPF | 2차 Butterworth, 80 Hz | cutoff 40–200 Hz. DC/저주파 rumble을 감쇠. |
| NS | 자체 per-bin noise-PSD 추정 + decision-directed Wiener spectral gain, 기본 ON | 독립 ON/OFF, 최대 bin 감쇠 0–24 dB (UI 6–24 dB, 기본 12). 전체 신호에 같은 gain을 거는 gate가 아님. |
| EQ | low shelf 180 Hz → peak 1.5 kHz/Q=0.8 → high shelf 4.5 kHz | 각 −12…+12 dB. 기본 0 dB. |
| Compressor | feed-forward/hard knee, threshold −18 dBFS, 3:1, makeup +2 dB | threshold −36…−6 dBFS, ratio 1:1…10:1. Detector attack/release 5/100 ms, gain smoothing 2/80 ms. |
| Limiter | 마지막의 zero-lookahead **sample-peak safety clamp**, −1 dBFS | ceiling −12…−0.1 dBFS (UI −12…−1). true-peak/loudness/lookahead limiter는 아님. 과부하에서 clipping 왜곡 가능. |

입력 trim은 HPF 앞에서 −24…+24 dB를 조정하며 한 hop에 걸쳐 이동합니다.
NaN/Inf를 제거하고 설정을 유한 범위로 정규화합니다. 기존 `gate` preference를 NS 설정으로
재해석하지 않습니다. 새로운 `ns`/`ns_reduction` 설정을 사용하고 gate 단계/제어는 제거했습니다.

## 2. Noise Suppression 알고리즘과 한계

- 960-sample(20 ms) periodic sqrt-Hann 분석/합성창, 480-sample(10 ms) hop.
  64개 zero padding 후 1024-point radix-2 FFT. plan/table/workspace는 생성 시 준비합니다.
- bin별 input power와 평활 power, 8×100 ms minimum-statistics history를 유지합니다.
  noise-like bin의 power 증감에는 대칭 update를 사용해 추정치의 하향 편향을 피하고,
  coherent 성분이나 두 floor 추정치를 모두 넘는 speech-like rise의 상승 학습은 느리게 합니다.
- 좁은 coherent 성분은 보수적으로 보호하여 세션 시작의 유성 성분을 곧바로 noise로
  학습하지 않도록 합니다. 같은 이유로 **tonal fan/hum도 남을 수 있습니다**.
- 이전 clean-power 추정과 posterior SNR로 decision-directed prior SNR `xi`를 만들고,
  Wiener **amplitude gain `xi/(1+xi)`**을 bin마다 계산합니다. 주파수 및 비대칭 시간 smoothing과
  gain floor를 적용합니다. 최대 bin 감쇠 설정은 모든 시간/주파수에서 그만큼 감쇠한다는 보장이 아닙니다.
- inverse FFT와 weighted overlap-add로 파형을 복원합니다. 모든 gain이 1이면 sqrt-Hann의
  50% overlap 합이 1이며 단일 hop 지연을 제외하고 입력을 복원합니다.
- 음성이 존재할 때도 noise-dominated bin을 낮춥니다. 무음일 때만 전체 신호를 끄는 gate가 아닙니다.

**의도한 대상은 정상/느리게 변하는 배경 잡음**입니다. 다른 화자, 충격음/키보드,
음악, 긴 잔향, 기기 자체 DSP와의 중복 처리에 대한 제거/명료도 보장은 없습니다.
초기 noise 모델이 안정화하는 시간과 환경/입력 gain 변화 후 재적응이 필요합니다.
실제 발화의 자음·말끝·숨소리, pumping/musical-noise와 더 복잡한 noise는 기기/음성 corpus A/B가 필요합니다.
NS가 켜져 있다는 사실은 De-reverb·AEC가 제공된다는 뜻이 아닙니다.

## 3. 지연 / bypass / 타임라인

- NS의 nominal alignment는 **480 samples = 10 ms**입니다.
  이것은 처리 CPU 시간/AudioRecord buffer/종단 간 지연의 측정값이 아닙니다.
- NS OFF도 동일한 1-hop delayed dry를 사용합니다. 내부 분석/추정을 계속 warm하게 유지하여
  enable/disable 때 다른 지연의 경로로 바뀌지 않습니다. wet/dry는 1 hop에 걸쳐 crossfade.
- 전체 DSP bypass도 입력의 지연 정렬된 dry를 사용합니다. **최종 safety limiter는 bypass에서도 유지**합니다.
  일반 설정 변경은 state reset/새 인스턴스/stream 재시작을 만들지 않습니다.
- 새 stream/queue sequence gap/DISCONTINUITY에서는 HPF/NS/EQ/compressor와 dry history를 모두 reset.
  첫 output hop은 무음으로 채우고 이전 speech tail을 재사용하지 않습니다.
- `DspFrameProcessor`는 capture timestamp에서 고정 480 ticks를 뺀 뒤 encode합니다.
  sequence는 원래 capture progression을 유지하므로 인접 정상 패킷의 timestamp 차이는 여전히 480입니다.
  DSP ON/OFF로 이 offset을 바꾸지 않습니다. gap으로 폐기된 tail은 복구한 것처럼 송신하지 않습니다.
- 종료 때 마지막 buffered hop은 폐기합니다. 종료 후 drain/재전송으로 지연을 늘리지 않습니다.

**30–50 ms E2E 목표는 아직 미검증**입니다. 현행 jitter·AudioRecord·WASAPI·전용 driver·
소비 앱을 포함하여 다시 측정해야 합니다. NS의 10 ms 정렬 비용을 숨기지 않습니다.

## 4. 실행 소유권 / 실시간 경계

- capture thread는 capture/프레임 조립/SPSC만 담당. FFT/DSP·socket·file을 AudioRecord read 경로에 넣지 않음.
- 제어 executor는 immutable `DspConfig`를 준비하고 `AtomicReference`로 게시.
  sender worker 하나가 480-sample 경계에서 configure/process/reset을 소유합니다.
- FFT reverse/twiddle, 960/1024 sample workspace, PSD/minima/gain, WOLA/dry history와 filter state는 사전 할당.
  process/configure/reset 내부에 파일·네트워크·동기 mutex 대기·프레임별 객체/배열 생성이 없습니다.
- Android framework/ART·네트워크 루프 전체까지 hard-RT/무할당이라는 뜻은 아닙니다.
  host JVM allocation guard와 실제 Z Flip6 ART/GC 검증을 구분합니다.
- runtime EQ/coefficient 변경은 bounded output과 상태 연속성을 유지하지만 mastering급 무클릭/투명한
  자동화라고 주장하지 않습니다. 입력 trim 및 wet/dry 이외의 모든 파라미터를 sample-wise ramp하지는 않습니다.

## 5. 자동 검사와 재현

```sh
cd android
./gradlew :app:testDebugUnitTest :dsp-host-tests:test :app:assembleDebug :app:lintDebug
```

Windows에서는 `gradlew.bat`를 사용합니다. JDK 17/기존 Android SDK만 필요하고 NDK는 필요 없습니다.
`dsp-host-tests`는 APK 의존성이 아닙니다. Android API에 없는 HotSpot management 계측만
별도 JVM 모듈에 두며 **production Kotlin 파일 자체**를 다시 컴파일합니다. DSP 사본/다른 언어 모형을 테스트하지 않습니다.

검사 범위:

- FFT round-trip/bin, NS OFF/0 dB WOLA 복원과 정확한 480-sample identity delay.
- seeded stationary-noise 감쇠, 활성 three-tone 신호와 noise 혼합의 SNR/신호 보존, 증가한 noise floor 적응.
  **합성 신호 fixture이며 실제 사람 음성 corpus 품질/휴대폰 실측이 아님**.
- HPF 20 Hz/1 kHz 응답, EQ neutral/1.5 kHz gain, compressor 정적 ratio와 release, limiter 양·음/비유한값.
- production processor와 **명시적인 HPF→NS→EQ→compressor→limiter 조합**의 sample 비교.
- runtime 변경·NaN/Inf·bypass·reset, queue gap/flags/timestamp/uint32 wrap와 old-tail 부재.
- warmed core process + runtime configure의 JVM allocation guard. ART/AudioRecord/network 무할당 시험은 아님.
- 기존 Kotlin codec/ACK/DSP/SPSC, C#·WPF 및 native-driver/reference CI 회귀.

### 실제 수행 결과 (2026-11-14 UTC)

검증 source: `733df700ccf28ad03da4c37c58088d9a30aa74b1` ·
[CI run 43595090569](https://github.com/8dia479/AirMic/actions/runs/43595090569): **7개 job 전체 성공**.
이 환경에는 JDK/Android SDK/Windows가 없어 대상 빌드는 GitHub hosted runner에서 수행했습니다.

| 검사 | 관측 결과 / 해석 |
|---|---|
| Kotlin 단위/할당 테스트 | **18 tests, failures/errors/skipped 0**. Android app 17 + host-only 1. |
| Android 빌드 | debug APK + lint 통과. 실제 휴대폰 실행 아님. |
| FFT / WOLA / bypass | round-trip, unity 재구성, 정확한 480-sample delayed dry 통과. |
| 합성 정상 잡음 구간 | 감쇠 **8.27 dB**. 실제 방/음성 녹음 수치 아님. |
| 합성 three-tone + noise 구간 | 정렬된 error 기준 SNR **16.28 → 22.14 dB**, signal projection gain **0.941**. 실제 speech intelligibility 점수 아님. |
| 상승한 noise floor | 합성 noise fixture에서 재적응 후 **11.78 dB** 감쇠. |
| 시작부터 활성인 신호 | 무음 calibration 없이 three-tone proxy projection gain **0.867**. 임의 발화 보호를 보증하지 않음. |
| HPF | 20 Hz **−24.10 dB**, 1 kHz **−0.00018 dB** (기본 cutoff 80 Hz). |
| warmed Kotlin core | configure 포함 **2,000 frames × 3개 모든 batch에서 0 allocated bytes**. HotSpot JVM의 warmed core만 측정했으며 ART/framework 무할당이나 초기 생성 무할당 아님. |
| 나머지 회귀 | managed Windows/Linux 각 116,436 checks, WPF build/창 smoke/diagnostic publish, unsigned WDK/driver-history 및 reference CI 통과. 실제 driver 설치/서명/오디오 수용은 별도. |

검사에서 발견한 약한 잡음 감쇠를 실제 코드에서 수정했으며 기준을 낮춰 통과시키지 않았습니다.
이전 실패와 수정, raw CI annotations와 수치는
[`test-results/2026-11-14-dsp-pipeline-ci.json`](test-results/2026-11-14-dsp-pipeline-ci.json)에 기록했습니다.
실기기 음질·성능·GC/발열/배터리·600초·E2E는 **별도의 미실시 수용 시험**입니다.

## 6. De-reverb의 분리

[De-reverb 설계](DE_REVERB_DESIGN.md)에 독립 graph slot, 단일 채널 online-WPE 연구 후보,
상태/실패·wet/dry·지연/profile·UI·추후 검증 조건을 정의했습니다.
**현재 backend/기능은 미구현이며 NS나 EQ를 De-reverb로 이름만 바꿔 노출하지 않습니다.**
기존 [WebRTC/RNNoise 검토](DSP_EVALUATION.md)의 후보 우선순위와 통합 미실시 범위도 그대로 유지합니다.
