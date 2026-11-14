# De-reverb — 별도 기능 설계

상태: **설계 완료 / 알고리즘·backend·UI 미구현**.
현재 앱에는 동작하는 척하는 De-reverb toggle, 더미 processor, 숨겨진 잔향 제거 모드가 없습니다.
이 문서는 이번 요구의 ‘별도 기능으로 설계’ 범위이며, 아래 후보의 품질/속도/지연을 검증했다는 뜻이 아닙니다.

## 1. 목적과 비목적

**목적:** 단일 휴대폰 마이크의 방 잔향(late reverberation)을 선택적으로 완화한다.
직접음/음성 명료도와 말끝을 보호하고, 기존 5단 DSP와 독립적으로 켜고 끌 수 있는 기능으로 설계한다.

다음과 같지 않다:

- Noise Suppression: 팬·정상 배경 잡음의 주파수별 power를 추정해 낮추는 기능.
- noise gate/expander: 낮은 level의 전체 신호를 줄이는 기능.
- AEC: 별도 재생 reference와 지연 추정이 필요한 음향 echo 제거. 현재 reference 경로는 없다.
- EQ/고역 강조: 잔향이 줄어든 듯한 음색만으로 De-reverb 완료를 판정하지 않는다.
- 다른 화자/음악 제거, 원래 무향실 음성의 완벽 복원, 장거리 마이크 보정.

WebRTC/RNNoise의 기존 **NS 후보 검토**는 그대로 둔다. 그 검토나 이번 자체 spectral NS는
De-reverb backend 선정/통합의 대체물이 아니다. 이번 변경에서 native/JNI/모델 의존성을 추가하지 않는다.

## 2. 모듈과 경로의 분리

현재 구현 경로는 변경하지 않는다:

```text
AudioRecord -> capture SPSC -> DspFrameProcessor
                              HPF -> NS -> EQ -> Compressor -> Limiter
                           -> PCM16/UDP
```

미래에 별도 backend를 구현/검증한 경우에만 다음 optional route를 제공한다:

```text
                          DeReverbSlot (별도 소유 상태/설정/실패 상태)
                         /  aligned dry                    \
Capture SPSC -> router --                                   -> 5-stage DspProcessor -> UDP
                         \  de-reverb wet -> aligned mix   /
```

- De-reverb는 **5단 pipeline에 여섯 번째 필수 단계로 숨겨 넣지 않는다.**
  `DspProcessor`가 De-reverb 상태·설정·factory에 의존하지 않게 한다.
- optional slot을 NS 앞에 둔다. NS의 시간 가변 spectral gain으로 바뀌기 전 입력에서
  잔향 상관관계를 추정하려는 설계이며, 실제 순서의 우열은 후속 A/B가 필요하다.
- 현재 Kotlin sender worker 하나가 처리 graph를 소유하는 구조를 유지한다.
  Windows 수신기에서 동일 기능을 다시 적용하지 않는다.
- 공유 가능한 FFT 최적화는 후속 성능 검증 대상이다. 서로의 state/reset/지연/disable 계약을
  묶는 ‘일단 같은 FFT 쓰기’는 최초 설계에 요구하지 않는다.

## 3. I/O 및 수명 계약

| 항목 | 계약 |
|---|---|
| 입력/출력 | 48,000 Hz, mono, 정규화 float, 정확히 480 samples/frame. 매 호출 정확히 480개를 출력. |
| 상태 | capture 세션별 독립 인스턴스. 다른 사용자·이전 세션의 음성/history를 재사용하지 않음. |
| 제어 | immutable 설정을 제어 thread에서 준비, worker의 10 ms 경계에서 적용. 같은 인스턴스의 process와 configure를 다른 thread에서 동시에 호출하지 않음. |
| prepare/close | controller가 graph 시작/정지 또는 안전한 인계 경계에서 메모리·table·workspace를 사전 준비/해제. 오디오 read/process 경로의 파일/네트워크/heap/mutex 대기 금지. ART 전체 hard-RT 보장은 별개. |
| process | 사전 할당된 buffer를 쓰며 enum/int 상태를 반환. 프레임별 결과 객체/배열/복소수 객체 생성 없음. |
| reset | 새 세션, capture/queue sequence gap, device/route 변경에서 predictor·STFT·overlap·wet/dry history를 모두 무효화. 오래된 speech tail을 새 프레임에 섞지 않음. |
| failure | NaN/Inf·안정성 실패·과도한 처리 시간은 상태에 기록하고 정렬된 dry로 전환. 음소거/오래된 audio 반복/latency 은닉으로 성공한 척하지 않음. 로깅과 재준비는 controller에서. |
| 종료 | 무제한 tail drain이나 종료 후 UDP 전송 없음. 보관된 잔여 오디오는 폐기. |

개념상 backend는 `prepare(format, limits)`, `latencySamples`, `process(in, out)`,
`reset(generation)`, `close()` 계약을 가진다. 이는 **설계 계약이지 현재 앱에 붙인 stub API가 아니다**.
새 backend가 없는 빌드의 지원 상태는 `UNAVAILABLE`이며 현재는 이 상태뿐이다.

## 4. 후보 알고리즘과 안정성 설계

첫 연구 후보는 **단일 채널 online WPE(weighted prediction error)**다.
검증 없이 backend로 확정하거나 신경망 모델/외부 바이너리를 내려받지 않는다.
C#/순수 Kotlin 앱 범위를 유지할 수 있는 구현을 우선 검토하고, native 의존성이 필요하면
합의된 ‘driver만 native 예외’와 충돌하므로 별도 범위 결정 전 도입하지 않는다.

주파수 bin `k`에 대해 과거 STFT frame만으로 늦은 반사 성분을 예측한다:

```text
phi_t(k) = [x_(t-D)(k), ..., x_(t-D-P+1)(k)]
y_t(k)   = x_t(k) - g(k)^H phi_t(k)
```

- 후보 시작점: 20 ms 분석창, 10 ms hop, prediction delay `D=3`, order `P=12`.
  이는 **실험용 설계값**이며 동작/품질 검증된 preset이 아니다.
- `D`는 직접음과 초기 반사를 예측기에 바로 먹이지 않으려는 상관 지연이다.
  **30 ms prediction delay가 곧 30 ms 추가 lookahead라는 뜻은 아니다.**
  STFT alignment/buffering과 실제 backend `latencySamples`는 따로 측정한다.
- complex weighted least-squares/RLS의 covariance와 inverse/workspace를 고정 크기로 준비한다.
  power floor, diagonal regularization, forgetting factor 범위, coefficient norm bound,
  singular/ill-conditioned matrix 감지와 reset을 설계에 포함한다.
- 무음/너무 낮은 SNR에서는 업데이트를 freeze하고 confidence가 낮으면 dry를 유지한다.
  startup에 context가 쌓일 때까지 출력을 기다려 큐를 늘리지 않는다.
- 첫 버전의 상한은 `P<=16`, 고정 bin 수, 대략 수 MB의 bounded workspace를 검토한다.
  정확한 메모리/연산량·10 ms deadline 여유는 구현 후 계측해야 한다.
- 단일 채널은 직접음·배경 잡음·반사의 분리가 불확정적이다. 긴 잔향과 비정상 잡음,
  음악/다른 화자에서는 실패하거나 왜곡할 수 있다. dry보다 나쁘면 기본 OFF를 유지한다.

## 5. 지연, bypass 및 wire timeline

현재 5단 DSP는 NS ON/OFF와 전체 bypass 모두 **480-sample / 10 ms 정렬 지연**을 가진다.
De-reverb를 켜며 이 값을 세션 중 몰래 바꾸면 capture↔wire timestamp 의미와 receiver의
sequence/timestamp 검사를 깨뜨릴 수 있다.

따라서 미래 router의 profile을 구분한다:

1. **Low-latency 5-stage:** 현재 경로. De-reverb slot과 그 지연이 없음.
2. **De-reverb capable:** 세션 시작 전에 backend/지연 정렬 slot을 준비.
   이 profile 안에서 OFF/WARMING/ACTIVE/FAILED_BYPASSED는 모두 같은 고정 지연을 가짐.
   enable/강도 변경은 정렬된 wet/dry를 최소 한 hop crossfade하며 추가 할당 없이 적용.

profile 간 지연이 달라지는 변경은 세션을 명시적으로 재시작해 새 START/timeline을 만든다.
동일 profile의 runtime 조절은 세션/sequence를 리셋하지 않는다.

- 실제 route의 출력 timestamp는 원래 capture timeline에서 **보고된 전체 정렬 sample 수**만큼 보정.
- startup/reset은 필요한 길이의 무음 warm-up을 표시하고, 임의 과거 음성을 채우지 않음.
- De-reverb의 추가 정렬 예산은 우선 20 ms 이하를 실험 목표로 삼되 **달성값이 아니다**.
  현행 NS 지연과 합산하고 GUI/통계에 공개한다. 더 오래 걸리는 backend는 자동 선택하지 않는다.
- **30–50 ms E2E 목표 충족은 보장하지 않는다.** ADC/AudioRecord·DSP·Wi-Fi·jitter·WASAPI·driver·
  소비 앱 전체를 재측정해야 한다. ACK RTT로 대신하지 않는다.

## 6. 독립 UI / 설정 / 지원 상태

후속 구현 시 ‘잔향 완화(De-reverb)’ 카드를 NS 설정과 분리한다.

- master enable, wet/강도, profile/재시작 필요 여부, 고정 추가 정렬 지연 표시.
- 기본 OFF. `UNAVAILABLE`, `DISABLED`, `WARMING`, `ACTIVE`, `FAILED_BYPASSED`를 구분.
- NS를 끈다고 De-reverb가 자동으로 켜지거나, 그 반대가 되지 않음.
- backend 미포함 빌드는 ‘미제공’ 표시만 하며 조작 가능한 가짜 toggle을 노출하지 않음.
- 설정은 별도 versioned preference namespace(`dereverb.*`)를 사용하고 기존 `ns`/EQ 값을 재해석하지 않음.
- dry/실패 전환에서도 기존 limiter가 최후단에 있으므로 bounded output 계약을 유지.

현재 UI에는 이 기능이 있다는 오해를 막기 위한 **미제공 안내만** 있고 실제 제어는 없다.

## 7. 후속 구현의 수용 시험 — 아직 수행하지 않음

1. **독립성:** De-reverb 미지원/비활성 경로가 같은 route latency로 정렬된 dry와 일치.
   기본 5단 pipeline/설정/NS 상태가 변하지 않음. 기존 단위/할당/프로토콜 테스트 회귀.
2. **수학/안정성:** impulse·무음·DC·full-scale·NaN/Inf·singular covariance,
   긴 RIR·power 변화·장시간 반복·reset 후 old-tail 부재, 출력 finite/bounded.
3. **잔향 구분:** 출처/동의 확인한 clean speech와 알려진 synthetic/measured RIR을 사용.
   무잔향 입력의 direct sound/말끝/자음 손실도 평가. 잡음 제거량이나 EQ 고역 증가만으로 통과시키지 않음.
4. **품질:** 정렬된 dry/wet의 청취, SI-SDR/STOI 등 적합한 지표를 clean reference 조건에서 기록.
   reference 없는 실제 녹음에서 지표를 ground-truth 복원 성능처럼 표시하지 않음.
5. **성능:** 실제 Z Flip6에서 graph 전체 first/warmed p50/p95/p99/max ms/frame,
   ART allocation/GC·queue drop·화면 off·발열/배터리·600초 동작과 실제 E2E 지연.
6. **실패/전환:** background noise 급변·말 중/무음 중 enable/disable·profile 재시작,
   queue gap·Wi-Fi/device 재연결 시 무제한 buffering/클릭/old-tail/타임라인 위반 없음.

**이번 완료 범위:** 위 기능 경계·후보·I/O·상태·지연·bypass·UI·검증 설계.
실제 De-reverb 알고리즘 구현/실측은 완료했다고 보고하지 않는다.
