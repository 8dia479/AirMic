# 지연 측정: RTT와 오디오 E2E를 구분하기

**30–50 ms는 목표입니다. 실제 Galaxy→Windows→VB-CABLE 경로 지연은 아직 미측정입니다.**

## 서로 다른 값

| 값 | 재는 범위 | E2E 오디오 지연인가? |
|---|---|---|
| Android UDP ACK RTT | UDP send 직전→PC validation ACK→앱 recv | 아니오 |
| Windows GetStreamLatency | WASAPI endpoint가 보고하는 자체 latency | 아니오 |
| jitter target × 10 ms | 명목 playout buffering budget | 아니오 |
| virtual 10-minute test | 알고리즘에서 진행시킨 sample 수/모의 클럭 | 아니오 |
| synthetic WAV의 알려진 offset | 분석기 회귀 정확도 | 아니오 |
| 실제 동기 2채널 녹음 | reference와 실제 경로가 출력된 신호의 차이 | fixture를 포함하는 실제 경로 측정 |

서로 다른 장치의 monotonic timestamp를 단순히 빼면 clock offset/drift가 섞입니다.
RTT/2를 one-way 오디오 지연이라고 부르지 않습니다. 정상 ACK도 render 성공 증거는 아닙니다.

## 분석 도구

```sh
python -m pip install -r tools/requirements-latency.txt
python tools/measure_latency.py measured-stereo.wav --max-ms 500 --json artifacts/latency.json
```

입력 조건:

- 실제 동기화된 **48 kHz / stereo / PCM16 WAV**.
- channel 0: 직접 reference, channel 1: AirMic 경로를 거친 결과.
- 두 channel은 **같은 녹음 클럭**에서 얻어야 합니다.
- 충분한 레벨의 broadband 클릭/음성/잡음 burst를 여러 번 사용. 순수 정현파는
  주기마다 같은 상관 peak가 있어 지연을 유일하게 정할 수 없으므로 피합니다.
- 기본 2초 window마다 최대 500 ms의 양의 lag를 검사합니다. 입력은 window+max lag
  이상 길어야 하며, 최소 30초 이상 여러 구간 수집을 권장합니다.

도구는 FFT normalized cross-correlation, 무음/약한 신호 제외, 경쟁 peak 비교를 합니다.
confidence <0.2 또는 peak ratio <1.02인 구간은 제외합니다. 결과는 구간별 지연,
median, nearest-rank p95, min/max/range를 JSON으로 기록합니다. 한 sample의 해상도는
48 kHz에서 약 0.02083 ms이지만 fixture/드라이버/상관 peak의 불확실성은 그보다 큽니다.
분석 결과의 소수점 자릿수가 물리 측정 정확도를 뜻하지 않습니다.

## 재현 가능한 실제 fixture 예

가능한 한 단순한 루프로 시작하고 모든 추가 장치/버퍼를 기록합니다.

1. 같은 클럭을 공유하는 **2입력 오디오 인터페이스**와 DAW/녹음기를 준비합니다.
2. 테스트 출력 클릭/잡음 burst를 분기해 인터페이스 input 1에 직접 reference로 연결하고,
   동시에 작은 스피커로 휴대폰 마이크에 입력합니다. 스피커-폰 거리도 기록합니다.
3. AirMic으로 수신한 **CABLE Output**을 모니터 경로를 통해 다른 인터페이스 출력으로
   보낸 뒤 input 2에 loopback합니다. 두 입력을 하나의 stereo track으로 동시에 녹음합니다.
4. 낮은 음량으로 피드백/클리핑을 막고 적어도 여러 개의 비주기적인 이벤트를 수집합니다.
5. stereo PCM16/48 kHz로 export한 뒤 분석기를 실행합니다.
6. reference 직접 경로/추가 DAC/ADC/모니터링 버퍼/음향 이동 시간을 별도 보정 측정합니다.

이 fixture의 원시 결과에는 **AirMic 외의 speaker·거리·monitor·DAC/ADC 지연**도
포함됩니다. 이를 보정하지 않고 순수 AirMic 지연이라고 부르지 마세요. DAW에서
서로 다른 capture device를 합칠 때도 clock synchronization/resampling이 보장되는지
확인해야 합니다. 독립 녹음을 임의로 맞붙이거나 앱의 시작 시각으로 정렬하면 안 됩니다.

더 정밀한 장비/공통 clock capture가 있으면 그 경로를 사용하고 calibration 근거를 함께
보관합니다. 단순 손뼉/스마트폰 동영상은 대략적 확인에는 쓸 수 있어도 30–50 ms 목표의
정밀 인수 근거로 사용하지 않습니다.

## 결과 기록 양식

- 실제 recording 파일/hash, 녹음 시작 시각
- phone/OS/AP/PC/VB-CABLE/DAW 버전
- DSP, jitter target 범위, WASAPI 실제 capacity와 reported latency
- 공통 클럭 확보 방법, 전체 배선/라우팅, 거리
- fixture baseline 및 뺀 값의 근거 (원시 측정값도 유지)
- matched/skipped windows, median/p95/range, confidence/ambiguity
- 30–50 ms 목표와 비교 및 끊김/지연 튐 관찰

## 자동 회귀 검증의 의미

`tests/python_test_tools.py`는 알려진 1,733-sample offset, **합성 45 ms WAV**, 무음,
주기적 tone, 잘못된 파일 형식을 검사합니다. 이는 측정 계산의 정확성 검사이며
실제 하드웨어가 45 ms를 달성했다는 주장이 아닙니다.

`airmic_loopback` JSON의 sub-millisecond 값은 **Linux localhost UDP ACK RTT**입니다.
그 숫자를 README의 종단 간 성능 달성값으로 사용하지 않습니다.
