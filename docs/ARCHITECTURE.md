# AirMic MVP 0.1 Architecture

## 1. 처리 경로와 소유권

```text
ANDROID
 Kotlin Activity --(intent/preferences)--> microphone ForegroundService
                                            |
                                 process-wide control executor
                              start/stop / Wi-Fi bind / retry / JNI
                                            |
 Oboe/AAudio callback                    sender worker
 float mono 48k --> CaptureAssembler --> SPSC<CaptureFrame,16>
                     480 samples             |
                                        DSP (480 floats)
                                             |
                                       PCM16 + BE encode
                                             |
                                  nonblocking UDP send / ACK recv

WINDOWS
 Winsock nonblocking socket + select             WASAPI event thread (MMCSS)
        |                                                 |
 exact-size decode --> SessionGate --> SPSC<IngressFrame,64>
                                                          |
                                              adaptive jitter (64 fixed slots)
                                                          |
                                                fade/repeat PLC
                                                          |
                                              small clock-drift ASRC
                                                          |
                                           PCM16 mono 48k shared render
                                                          |
                                                  CABLE Input
                                                          |
                                                  CABLE Output
                                                          |
                                                 Discord / game
```

수신과 오디오 재생은 서로 직접 호출하지 않습니다. 네트워크가 멈춰도 WASAPI는
PLC/무음으로 계속 채웁니다. 송신이 지연돼도 Oboe callback은 네트워크를 기다리지
않고 큐에 넣거나 해당 프레임을 버립니다. Windows UI는 콘솔 CLI이며 GUI는 없습니다.

## 2. 실시간 제약

| 경로 | 허용하는 작업 | 금지/배제한 작업 |
|---|---|---|
| Oboe callback | float 복사, 480-frame 조립, 단조 시계 조회, lock-free push/atomic 통계 | 소켓/JNI, 파일/로그, heap, mutex, 조건변수 대기 |
| Android worker | fixed-block DSP, endian encode, nonblocking send/recv, 제한된 poll 대기 | 오디오 callback 대기, 누적 재전송 |
| WASAPI 재생 루프 | 오디오 이벤트 대기, GetBuffer/ReleaseBuffer, bounded ingress drain, jitter/PLC/ASRC, atomic 통계 | 네트워크/파일/콘솔, 사용자 mutex, heap |
| 제어/UI | 장치 열기/닫기, COM/AAudio 초기화, 스레드 생성·join, preferences, 알림, JSON | callback에서 호출되지 않음 |

오디오 이벤트를 기다리는 `WaitForMultipleObjects`는 장치가 구동하는 정상 스케줄링
대기입니다. 오디오 데이터 처리가 다른 스레드의 mutex나 소켓을 기다리지는 않습니다.
초기화/종료의 장치 API 비용과 드라이버 내부 동작까지 무할당이라고 주장하지 않습니다.
호스트의 `airmic_rt_tests`는 **자체 코어** steady-state의 new/new[] (aligned 포함)
할당 0회를 실제 검사합니다. OS/드라이버 내부 malloc 전체를 후킹하는 검사는 아닙니다.

## 3. 유한 버퍼와 지연 상한 정책

- `SpscRing<T,N>`: power-of-two 저장소, acquire/release 인덱스, 정확히 1 producer /
  1 consumer. 만원 시 unread 프레임을 덮어쓰지 않고 push 실패를 반환합니다.
- Android capture queue: 16 × 10 ms 저장 용량. 이는 **목표 지연이 아닙니다**.
  worker가 완료된 지 60 ms 넘은 프레임을 버려 오래된 오디오를 몰아서 송신하지 않습니다.
- callback에서 송신 여부와 관계없이 sequence/timestamp를 계속 진행시켜 로컬 손실도
  수신측에서 gap으로 드러납니다. 다음 성공 프레임은 discontinuity flag를 붙입니다.
- Windows ingress: 64 frames, fixed capacity. 가득 차면 network queue-drop 카운터 증가.
- jitter: 64 fixed sequence 슬롯, 최근 64 sequence 태그로 duplicate/late 구별.
  오래된 중복도 재생하지 않으며 history 밖이면 late로 집계할 수 있습니다.
- 소비 시 목표보다 4프레임 넘게 쌓인 경우 오래된 연속 오디오를 잘라 지연을 제한합니다.
  이는 청감상 변화를 일으킬 수 있어 별도 `latencyDrops`로 공개합니다.
- 큐를 reset하는 시점은 생산자/소비자가 모두 정지한 후로 제한합니다.

## 4. 지터·손실·클럭 드리프트

### Adaptive jitter

forward packet의 `abs(arrivalDelta - sequenceDelta * 10ms)`를 1/16 EWMA로 추정합니다.
목표는 `clamp(1 + ceil(4*jitter/10ms), 2, 8)` 프레임입니다. 나빠지면 즉시 늘리고,
좋아진 상태가 200 forward packets 유지될 때 한 단계씩 줄입니다.

초기/rebuffer 때 목표 수의 패킷이 모여야 시작합니다. 목표 확장 때는 sequence를
소비하지 않는 감쇠 hold로 여유를 만들 수 있습니다. 축소/과다 backlog는 제한된
프레임 trim + 1 ms crossfade로 복구합니다. 2–8프레임은 20–80 ms의 **명목 버퍼
예산**이며, 실제 대기는 arrival/render 위상과 현재 block을 포함하므로 그 값과
종단 간 지연을 동일시하지 않습니다.

### PLC

재생 시점의 expected sequence가 없으면 마지막 정상 10 ms block을 반복하되 프레임
당 0.75 배 감쇠하고 처음 48 samples를 연결합니다. 다섯 번째 손실부터 무음으로
감쇠하며 여섯 번 연속이면 rebuffer 및 target 증가를 수행합니다. 복귀도 48-sample
crossfade입니다. pitch 추정이나 neural PLC는 아닙니다.

`lost/plc`는 **재생 deadline에 없었던 프레임 수**입니다. 네트워크 유실뿐 아니라
늦은 도착, 송신/수신 큐 손실, 정상 송신 종료도 포함될 수 있습니다. 이를 실제 IP
패킷 손실률이라고 표시하지 않습니다. startup waiting / adaptive hold는 별도입니다.

### Independent clocks

phone ADC와 PC 오디오 클럭은 같지 않습니다. PcmPlayout은 선형 보간으로 소비율을
최대 약 ±300 ppm 조정합니다. 큐가 목표보다 한 프레임 부족/초과할 때 충분히
보정하도록 300 ppm/frame 피드백과 완만한 smoothing을 사용합니다. 초기의 80 ppm/
frame 설정은 -250 ppm 테스트에서 언더런을 일으켜 수정했습니다.

±100 및 ±250 ppm 각각 **가상 시간 10분** 회귀 테스트가 있습니다. 더 큰 드리프트,
다양한 실제 WASAPI event size, 장시간 물리 클럭은 별도 장비 검증 대상입니다.
이 선형 ASRC는 범용 고품질 sample-rate converter의 대체품이 아닙니다.

## 5. 연결·세션·종료

- UDP `connect()`는 연결 성공 확인이 아닙니다. PC가 24-byte ACK를 보내고 Android가
  최근 송신 sequence/timestamp와 대조한 경우만 `PC 응답 확인됨`을 표시합니다.
- ACK는 보통 250 ms마다, 새 stream은 즉시. 2초 이상 없으면 응답 없음. audio나 ACK
  재전송은 하지 않습니다. 무음 입력도 정상적인 100 packets/s 연결 확인 역할을 합니다.
- Windows는 첫 정상 송신자의 IPv4+port를 pin하고, `--allow`로 phone IPv4를 제한할
  수 있습니다. 잘못된 길이/flag/timestamp는 pin을 만들거나 갱신하지 않습니다.
- 새 START+더 큰 monotonic timestamp는 같은 phone의 새 세션으로 보고 generation을
  올립니다. 늦게 도착한 과거 START는 현재 stream을 초기화하지 않습니다.
- 정상 패킷이 2초 끊기면 다른 source/reboot 세션도 수용합니다. v1에는 stream ID나
  인증이 없어 idle 후의 replay를 완벽히 구별하지 못합니다.
- generation은 network→render 큐 항목마다 전달하여 초기 START 항목이 큐에서
  누락되더라도 다음 항목으로 jitter reset이 전달됩니다.
- Windows Ctrl+C/기간 종료: receive stop/join 후 render event stop/join, 그 다음 COM/
  Winsock 종료. 장치 무효화 등 WASAPI 오류는 숨기지 않고 오류 코드와 함께 종료합니다.
  Windows endpoint 자동 재선택은 아직 없습니다.

### Android 수명주기

- 사용자 동작에서 foreground microphone service 시작, 시스템 recording indicator,
  알림 중지 action, START_NOT_STICKY. 앱이 강제 종료된 후 몰래 마이크를 다시 켜지 않습니다.
- process-wide 단일 control executor가 JNI start/stop, Wi-Fi binding, cleanup을
  순서대로 실행합니다. 이전 Service의 비동기 cleanup이 새 Service 엔진을 중지하는
  경쟁을 방지합니다. 화면 Activity는 snapshot만 읽습니다.
- Wi-Fi Network로 process를 바인딩한 후 socket 생성. Wi-Fi가 없으면 캡처/송신 중지,
  복구하면 재시도. 셀룰러 데이터 경로로 자동 전송하지 않습니다. 이 앱 범위의 Wi-Fi
  binding은 VPN 우회를 요청할 수 있으므로 lockdown VPN 정책에서는 실패할 수 있습니다.
- capture device 오류는 해당 open 전용 callback의 atomic 상태에만 기록하고 control 쪽에서
  닫은 뒤 1/2/4/8/16/30초 backoff 재시도. 30초 안정 동작 후 backoff를 초기화합니다.
  generation 검사와 per-open error 상태로 이전 stream의 늦은 callback이 새 stream을
  오염시키지 않게 합니다. 네트워크 인터페이스 변경도 새 socket/stream을 생성합니다.
- session 동안 Partial WakeLock 및 low-latency Wi-Fi lock (구형 OS high-perf)을
  요청하고 종료 때 해제합니다. Samsung 배터리 정책/화면 꺼짐 효과는 미측정입니다.

## 6. DSP: 구현과 차후 통합의 경계

현재 native block processor는 다음을 구현합니다:

1. 2차 Butterworth HPF (기본 80 Hz)
2. **선택적 adaptive downward expander/noise gate** (기본 꺼짐)
3. low shelf 180 Hz / peak 1.5 kHz / high shelf 4.5 kHz, 각 ±12 dB
4. envelope 기반 feed-forward compressor, 기본 -18 dB / 3:1 / makeup +2 dB
5. -1 dBFS 기본 ceiling의 zero-lookahead safety clamp limiter

NaN/Inf를 제거하고 PCM16 포화 변환을 합니다. compressor/게이트는 지속 상태를
유지합니다. 설정은 fixed SPSC control queue로 보내 10 ms boundary에서 갱신합니다.
큰 EQ 변경은 mastering급 무클릭 전환을 보장하지 않습니다. limiter는 loudness
normalization이나 lookahead limiter가 아닙니다.

**Noise gate는 말하는 동안의 배경 잡음 제거가 아닙니다.** WebRTC/RNNoise가
포함됐다고 주장하지 않습니다. 입력 preset은 우선 Unprocessed, 열기 실패 시
VoiceRecognition을 시도하므로 기기 fallback에서 시스템 DSP가 중복될 수 있습니다.

### 검토 완료: WebRTC 우선 / RNNoise 선택형 추가 NS

2026-11-14에 공식 소스의 API·빌드·실시간 제약·라이선스와 도입 정책을 검토했습니다.
근거 revision, 비교표, 실제 검사와 미검증 항목은 [DSP_EVALUATION.md](DSP_EVALUATION.md).
이 결정은 **검토 완료**이며 라이브러리 통합이나 배포/성능 검증 완료가 아닙니다.

- **WebRTC APM을 첫 외부 처리 후보로 선정.** 검토 revision의 public API는
  `api/audio/audio_processing.h` + `BuiltinAudioProcessingBuilder`입니다.
  48 kHz/480개 정규화 float와 맞습니다. 첫 비교는 HPF + moderate NS, 자체 HPF/gate
  우회, AGC/AEC 끄기를 기준으로 합니다. AEC는 PC 재생 reference가 없어 제공하지 않습니다.
- APM은 GN/DEPS 통합과 ABI 정합 검증이 필요하고 내부 mutex/재초기화 할당이 있습니다.
  Oboe callback에 넣지 않으며 sender worker 단일 소유와 사전 준비를 전제로 합니다.
  NS enable/level은 검토 API의 RuntimeSetting 항목이 아니므로 기존 무할당 configure와
  동일하다고 간주하지 않습니다.
- **RNNoise v0.2를 선택형 추가 NS로 검토.** 48 kHz/480 frame이지만 입력 float가
  PCM16 크기여서 ×32768/÷32768 변환이 필요합니다. 분석창/이전 스펙트럼 버퍼 때문에
  10 ms 호출 단위를 실제 추가 지연으로 해석하지 않습니다. 모델 파일의 출처·해시·배포
  조건은 실제 도입 전에 확인해야 합니다.
- RNNoise 선택 시 WebRTC NS/기본 gate를 동시에 켜지 않는 정책을 우선합니다.
  RNNoise 내부 HPF와 외부 필터의 중복도 피합니다. 실제 ARM64 CPU·품질·지연은 미측정이며
  어느 후보도 현재 의존성/가중치/UI/가짜 통합 함수로 추가하지 않았습니다.

### 별도 범위: de-reverb / Opus

De-reverb는 noise suppression과 구분하여 별도 backend·지연 budget·잔향 데이터셋을
설계합니다. 이번 버전에 동작하는 de-reverb는 없습니다. Opus 또한 v1 PCM payload를
몰래 바꾸지 않고 stream ID/format negotiation 등을 갖춘 후속 프로토콜로 설계합니다.

## 7. 지연 budget과 관측 한계

목표 30–50 ms = 입력/10ms frame 조립 + worker + Wi-Fi + jitter + WASAPI + VB-CABLE +
소비 앱의 실제 버퍼입니다. 합의 각 항이 측정되지 않아 목표 달성을 선언하지 않습니다.

ACK RTT는 송신 syscall 직전의 단조 시각에서 해당 ACK 수신까지입니다. 오디오 캡처,
PC jitter/render, 가상 마이크 소비 지연은 포함하지 않습니다. WASAPI GetStreamLatency도
endpoint 자체 보고값일 뿐입니다. 실제 검증은 [LATENCY.md](LATENCY.md)를 따릅니다.
