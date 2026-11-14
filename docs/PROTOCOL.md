# AirMic Audio Protocol v1

## 전송 규약

- IPv4 unicast UDP, 기본 수신 port **49000**, 앱에서 변경 가능.
- 양쪽 모두 **48,000 Hz / mono / PCM16 / 480 samples / 10 ms** 고정.
- audio datagram 길이는 **정확히 976 bytes**. 16-byte header + 960-byte PCM payload.
- **모든 다중 바이트 필드는 network byte order (big-endian)**.
  **PCM sample도 big-endian signed 16-bit two's-complement**.
- 100 packets/s, UDP payload 97,600 bytes/s (780,800 bits/s).
  IPv4/UDP 기본 header를 포함하면 100,400 bytes/s이며 Wi-Fi/L2 overhead는 별도입니다.
- IPv4/UDP 포함 한 패킷 약 1,004 bytes, 통상 MTU 1,500 아래.
- 구조체를 `memcpy`하거나 C++ packing/host endianness에 의존하지 않습니다.
- Opus, stereo, 다른 sample count/rate, fragmentation 조립을 지원하지 않습니다.

v1 audio header에는 magic/version/codec/stream-ID 필드가 없습니다. v1을 사용하는
endpoint/port라는 out-of-band 전제가 있습니다. 아래 optional ACK는 길이와 magic이
완전히 다릅니다. v1 제약을 해결하려고 알려지지 않은 flags를 임의 수용하지 않습니다.

## Audio datagram

| Offset | Bytes | Type | 의미 |
|---:|---:|---|---|
| 0 | 4 | uint32 BE | sequence number |
| 4 | 8 | uint64 BE | source monotonic sample timestamp |
| 12 | 2 | uint16 BE | sample count, 반드시 480 (`01 E0`) |
| 14 | 2 | uint16 BE | flags |
| 16 | 960 | int16[480] BE | mono PCM payload |

### Sequence

각 10 ms capture frame마다 1 증가, uint32 modulo wrap. UDP send 성공 여부와
무관하게 진행됩니다. 100 packets/s에서 약 497일에 wrap하며 signed modulo delta로
비교합니다. 양의/음의 방향은 거리가 2^31 미만인 window에서만 정의합니다.

실제 버퍼 window는 64 frames입니다. 수신 즉시 sequence gap을 확정 손실로 간주하지
않고 재생 deadline까지 재정렬을 기다립니다. 같은 sequence는 중복/late로 버립니다.
오디오 재전송은 하지 않습니다.

### Timestamp

- 단위는 **1/48,000초인 sample tick**. 각 frame의 첫 sample을 식별하는 타임라인.
- 송신 시작 때 source monotonic clock에서 임의 base를 정한 후 480씩 증가합니다.
- 기준 clock epoch를 Windows와 공유하지 않으며 UTC/Unix milliseconds가 아닙니다.
- 현재 Kotlin 앱의 base는 AudioRecord capture 시작 부근의 단조 시각입니다.
  ADC hardware timestamp 정밀도나 실제 첫 sample 물리 포착 시각을 보장하지 않습니다.
- 현행 DSP는 고정 **480-sample / 10 ms** WOLA 정렬 지연을 갖습니다. sender는
  capture timestamp에서 이 값을 뺀 값을 payload의 source timeline으로 사용합니다.
  NS/전체 DSP ON·OFF에도 같은 offset을 유지해 인접 packet의 관계를 바꾸지 않습니다.
  startup/gap reset의 첫 hop은 무음, 종료 시 마지막 buffered hop은 폐기합니다.
  [DSP 지연·불연속 계약](DSP_PIPELINE.md)을 참조하세요.
- source timestamp와 Windows clock을 빼서 one-way/E2E latency라고 계산하면 안 됩니다.
- 정상 세션은 다음 관계를 만족합니다:

```text
timestamp_B = timestamp_A + signed_sequence_delta(B, A) * 480
```

순서 뒤바뀜/유실도 이 관계를 바꾸지 않습니다. 수신측은 overflow/underflow를 검사하고
이 관계가 틀리면 거부합니다. 최근 forward packet으로 anchor를 이동시켜 sequence
wrap 뒤에도 연속 타임라인을 유지합니다.

### Flags

| Bit / value | 이름 | 의미 |
|---|---|---|
| 0 / `0x0001` | START | 새 capture 세션의 sequence=0인 첫 프레임 |
| 1 / `0x0002` | DISCONTINUITY | 직전 로컬 capture queue drop 등 불연속 힌트 |
| 2–15 | reserved | 반드시 0; 아니면 거부 |

START는 sequence=0에서만 허용합니다. sequence가 자연스럽게 0으로 wrap했다고
START를 다시 붙이지 않습니다. DISCONTINUITY는 timestamp/sequence 진행 규칙을
바꾸지 않으며 손실을 감추지 않습니다. 송신 첫 패킷이 유실돼도 수신은 첫 정상
패킷을 anchor로 동작할 수 있습니다.

## 독립 golden vector

```text
sequence  = 0x01020304
timestamp = 0x05060708090A0B0C
count     = 480
flags     = 0x0002
samples   = [-32768, 32767, -1, ...]

01 02 03 04  05 06 07 08 09 0A 0B 0C  01 E0  00 02  80 00 7F FF FF FF ...
```

C++ serializer/parser와 독립 Python `struct.Struct("!IQHH480h")` 검사 양쪽에
이 golden vector가 있습니다. C++ int16/uint16 변환은 bit_cast로 부호 bit를 보존합니다.

## Optional Receiver ACK (별도 24-byte datagram)

PC는 마지막으로 수락한 audio source의 **동일 UDP source port**로 응답합니다.
보통 250 ms마다 최대 한 번, 새로운 세션 수락 시 즉시 보냅니다. ACK 때문에 audio
패킷을 기다리거나 재전송하지 않습니다. audio만 보내는 v1 송신기도 PC에서 재생되지만
AirMic 앱의 PC 응답 확인/RTT 표시는 이 확장을 사용하는 PC가 필요합니다.

| Offset | Bytes | Type | 값 |
|---:|---:|---|---|
| 0 | 4 | ASCII | `AMAK` (`41 4D 41 4B`) |
| 4 | 4 | uint32 BE | ACK 발생 audio sequence |
| 8 | 8 | uint64 BE | 해당 audio timestamp 그대로 echo |
| 16 | 4 | uint32 BE | 수락 datagram 누계, modulo uint32 |
| 20 | 2 | uint16 BE | protocol version = 1 |
| 22 | 2 | uint16 BE | reserved = 0 |

Python 대응 형식은 `!4sIQIHH`.

Android는 선택한 PC IP/port에 connected UDP socket을 사용하고, 최근 1,024개
성공 송신 record의 sequence와 timestamp를 모두 대조합니다. 5초 넘은 RTT sample은
버립니다. 이 RTT는 UDP send→receiver validate/ACK→sender receive 시간이며,
오디오 capture/DSP/jitter/WASAPI/AirMic driver/게임 입력 지연을 재는 것이 아닙니다.
UI는 RTT를 7/8 이전 + 1/8 현재 sample로 평활합니다. 2초 ACK timeout은 오디오
중단의 확정 증거가 아니라 **PC 응답 미확인**입니다. 역방향 방화벽/혼잡으로도 발생합니다.

`acceptedPackets`는 validation/source gate를 통과한 datagram 수입니다. duplicates와
render-queue drop도 포함할 수 있어 **재생 성공 ACK가 아닙니다**. 향후 구현에서 render
통계를 제어 채널로 내보내려면 새 제어 규약이 필요합니다.

## 수신 validation / admission 순서

1. UDP datagram의 정확한 길이 976. 잘린/붙은/빈/초과 패킷 거부.
2. sample count 480, known flag mask, START 규칙 검사.
3. `--allow IPv4` 설정 시 source IP 검사.
4. 첫 정상 source를 IPv4+port로 pin. 유효 세션 중 다른 송신자는 거부.
5. 기존 source이면 sequence↔timestamp 관계 검사.
6. network thread에서 generation/arrival과 함께 SPSC에 enqueue.
7. render thread에서 session reset, window/duplicate/late 검사, 재정렬, playout.

소켓 receive buffer보다 큰 datagram도 WSAEMSGSIZE로 거부합니다. header 통과 후
payload에는 임의의 int16 값이 가능하므로 v1에 별도 음성 plausibility 검사는 없습니다.

### 재시작

- 같은 phone IP의 START가 이전 최고 timestamp보다 크면 새 generation으로 전환.
  새 ephemeral port도 이 경우 수용합니다.
- 늦게 도착한 과거 START는 stream을 reset하지 않습니다.
- 정상 수락 패킷이 2초 없으면 기존 pin을 대체할 수 있습니다. phone reboot 등으로
  monotonic clock이 작아진 경우도 이 timeout 뒤 복구할 수 있습니다.
- START가 유실되거나 재부팅/장기 클럭 오차가 있는 경우 자동 복구까지 timeout이
  걸릴 수 있습니다. receiver의 `--allow`는 계속 적용됩니다.

## 보안 경계 / 후속 v2

UDP source 주소와 monotonic timestamp는 인증이 아닙니다. 암호화, MAC, nonce,
key exchange, replay protection, NAT traversal, discovery는 없습니다. 신뢰하는 LAN,
Private firewall + LocalSubnet + 필요 시 `--allow`만 사용하세요. 공용망/port forwarding
사용 금지. 같은 LAN 공격자가 오디오를 주입하거나 source pin을 가로챌 수 있습니다.

후속 v2는 식별 magic/version, session ID, negotiated format/codec, 인증된 제어 채널,
기기 페어링과 replay protection을 함께 설계합니다. 지금의 16-byte v1 header에
임의 필드를 덧붙이거나 Opus를 PCM16인 것처럼 보내지 않습니다.
