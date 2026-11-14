# MVP 0.1 실기기 인수 시험 — 아직 실행하지 않음

**대상:** Galaxy Z Flip6 + Windows 10/11 x64 + VB-CABLE + 동일 LAN.
이 문서의 체크박스는 절차/판정 기준입니다. 체크되지 않은 항목은 미검증이며,
Linux headless soak/가상 클럭 테스트 결과로 대신 체크하지 않습니다.

## 환경 기록 (실제 시험 시 채우기)

- phone 모델/OS/One UI/build number:
- APK version/hash/ABI:
- PC CPU/Windows edition/build/audio driver:
- receiver version/hash/compiler/configuration:
- VB-CABLE version, CABLE Input/Output format 및 buffer 설정:
- AP/router 모델, band/channel, 거리/RSSI, guest isolation 여부:
- PC 연결 방식 (Ethernet/Wi-Fi), VPN/보안 프로그램:
- DSP 설정, Discord/게임 버전 및 추가 DSP 설정:
- 시작/종료 UTC 시각, foreground/화면 off/충전/발열 조건:
- logs/통계/녹음/측정 JSON의 위치:

## A. 빌드·설치 gate

- [ ] JDK17 + pinned SDK/NDK에서 `assembleDebug`와 `lintDebug` 성공.
- [ ] 실제 Z Flip6 arm64에 APK 설치/실행, JNI library 로딩 성공.
- [ ] native .so들 및 APK 16 KB 페이지 호환성 검사 (해당 환경에서).
- [ ] Windows MSVC x64 Release build + CTest 성공.
- [ ] 실제 Windows에서 `--help`, `--list-devices` 실행 및 endpoint 이름 확인.
- [ ] VB-CABLE 공식 driver 설치, CABLE Input/Output 사용 가능.

## B. 권한·명시적 녹음·종료

- [ ] 최초 마이크 권한 거부: 앱이 권한 오류를 표시하고 캡처/FGS가 시작되지 않음.
- [ ] 권한 허용: 시스템 마이크 indicator/foreground service가 표시됨.
- [ ] 알림 허용/거부 각각 UI와 종료 동작 확인.
- [ ] 앱/알림 중지 후 indicator 소멸, 패킷 송신 중단, Wake/Wi-Fi lock 해제.
- [ ] 화면 회전/Activity 재생성 중 서비스와 destination이 의도대로 유지됨.
- [ ] 빠른 중지→시작 20회: 예전 service cleanup이 새 엔진을 중지하지 않음.
- [ ] OS force-stop 후 자동 마이크 재시작 없음; 사용자 동작으로 재개 가능.

## C. PC 연결과 실제 소리

- [ ] PC private profile + 제한된 UDP firewall, phone과 같은 통신 가능한 LAN.
- [ ] PC IP/port 오입력: 송신 중/응답 없음 표시. 허위 연결 성공 표시 없음.
- [ ] 정상 설정: PC valid rx와 Android PC accepted/ACK 갱신.
- [ ] Android 입력 meter가 말소리에 반응하고 CABLE Output 녹음 meter도 반응.
- [ ] CABLE Output으로 실제 음성을 녹음하여 intelligibility/클리핑/지속 끊김 청취 확인.
- [ ] Discord 입력을 CABLE Output으로 선택, 실제 통화 상대가 음성을 확인.
- [ ] 대상 게임에서 마이크 입력 사용 가능.
- [ ] 명시적으로 물리 render endpoint 선택 시에만 스피커 재생 (낮은 음량/피드백 주의).

## D. 오류/네트워크 복구

- [ ] 잘못된 길이/count/flags/START/timestamp 패킷 거부, crash 없음.
- [ ] 중복/순서 변경/누락 시 counters 구분 및 오디오 PLC/복귀 동작 확인.
- [ ] 다른 source IP/port가 활성 stream을 섞지 않음; --allow 동작 확인.
- [ ] phone Wi-Fi 끄기→10초→켜기: cellular 경로로 전송하지 않고 자동 재개.
- [ ] AP 변경/새 DHCP IP/PC 재시작 후 문서화된 조건에서 복구.
- [ ] Android 다른 녹음 앱/통화/오디오 route 오류 후 재시도 또는 명확한 오류.
- [ ] Windows endpoint disable/제거 시 조용한 성공 대신 오류 표시/종료.
  현재 수신기는 자동 장치 re-open이 없으므로 재선택 후 재시작 필요.

## E. DSP 런타임

- [ ] 송신 중 bypass/gate/gain/EQ 변경, 앱/수신기 crash 없음.
- [ ] 입력 게인 과대 시 limiter 동작, PCM overflow/wrap distortion 없음.
- [ ] gate의 음절 누락 여부와 말하는 동안의 배경 잡음 한계를 청취.
- [ ] OS VoiceRecognition fallback / 소비 앱 DSP 중복 영향을 기록.
- [ ] EQ의 큰 변화에 클릭/펌핑 등 아티팩트가 있으면 버그로 기록.

## F. 연속 10분 gate

연결 완료 후 **추가 600초 동안** 일정한 음성/간헐적 broadband test signal을 송출하고
수신기를 녹음합니다. 녹음 종료/JSON 저장은 render thread가 아닌 외부 앱/제어 경로에서.

```powershell
.\tools\soak_windows.ps1 -Program .\artifacts\airmic_receiver.exe `
  -PhoneIp 192.168.0.20 -Seconds 600
```

script는 startup allowance를 둔 counter smoke 검사일 뿐 full gate 판정이 아닙니다.
안정적으로 연결된 이후의 별도 600초 관측 구간을 확보합니다.

- [ ] 10분 동안 Android/Windows 프로세스 crash, ANR, hang 없음.
- [ ] 실제 녹음/청취에서 지속적 오디오 끊김 없음.
- [ ] 화면 켜짐 10분 + 화면 꺼짐 조건 시험, Samsung 배터리 정책 기록.
- [ ] captured/sent/accepted/played, qdrop, invalid, lost/plc, late/duplicate,
  rebuffer/trim, Oboe xrun, WASAPI errors를 시작/종료 delta로 보관.
- [ ] CPU, 메모리 증가, 발열, 배터리 사용을 실제 장치에서 측정.
- [ ] 재생 종료 후 생긴 PLC를 송신 중 IP 유실률로 오인하지 않음.

## G. 지연 측정 gate

- [ ] [LATENCY.md](LATENCY.md)의 공통 클럭 2채널 녹음 절차 수행.
- [ ] 여러 broadband 이벤트/구간의 median/p95/range 및 fixture 지연 기록.
- [ ] 30–50 ms 목표와 비교. 미달 시에도 실제 수치를 그대로 기록.
- [ ] RTT/endpoint reported latency/가상 fixture 값을 E2E로 기록하지 않음.

## 최종 판정

아래가 모두 확인되기 전에는 **MVP 완료**라고 표시하지 않습니다:

1. 실제 Galaxy 캡처 및 실제 Windows 출력
2. 같은 LAN을 통한 실제 음성
3. VB-CABLE을 통한 마이크 입력/사용 앱 연결
4. 실기기 10분 안정성
5. 실제 지연 측정 결과 (목표 달성 여부와 무관하게 정직하게 보고)

**현재 판정: BLOCKED — 이 개발 환경에 실제 phone/Windows/Android SDK가 없음.**
호스트 코어·프로토콜·실제 loopback UDP 시험은 개발 보고서에 별도 기록합니다.
