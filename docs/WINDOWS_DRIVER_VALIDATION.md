# Windows 단일 설치 / 전용 driver 릴리스 검증 계획

이 문서는 **아직 수행하지 않은 대상 Windows 검증의 체크리스트**입니다.
CI에서 WPF 창을 연 것, portable ZIP/설치 journal 테스트, host history sanitizer,
WDK compile/InfVerif/API validation/Inf2Cat가 통과해도 아래 항목이 통과한 것은 아닙니다.

## 현재 릴리스 차단 조건

1. Microsoft 정식 서명·카탈로그 검증된 AirMic driver package가 없음.
2. 실제 Windows 10/11의 설치·장치·WASAPI·PnP·전원·제거·업그레이드 검증 미수행.
3. Galaxy Z Flip6 → 실제 LAN → Windows 소비 앱의 600초 안정성/E2E 지연 미측정.
4. 배포 EXE의 Authenticode 서명·출처/라이선스·배포 검토 미완료.

이 조건이 해결되기 전에는 미서명 CI driver를 일반 사용자용 설치 파일로 배포하지 않습니다.
Secure Boot, 메모리 무결성, BitLocker, 서명 강제 정책을 끄거나 테스트 모드를 켜는 스크립트는 없습니다.
자격증명·인증서 private key를 저장소나 채팅에 넣지 않습니다. 정식 서명은 유지관리자의
Microsoft driver 제출/서명 절차에서 진행해야 합니다.

## 대상과 준비

- Windows 10 x64 / Windows 11 x64, Secure Boot 및 지원되는 보안 기능을 유지한 테스트 PC/VM.
- native target 최소값은 19041 / KMDF 1.31. 이는 실행 호환성 인증이 아닙니다.
  .NET 10 자체의 OS 지원 범위도 별도로 확인합니다. ARM64는 지원하지 않습니다.
- Microsoft 서명된 `AirMic.inf`, `AirMic.cat`, `AirMic.sys`로
  `tools/publish_windows.ps1 -DriverPackageDirectory <경로>`를 실행한 self-contained EXE.
  publish는 Microsoft catalog 서명뿐 아니라 `signtool verify /kp /c`의 INF/SYS coverage도 요구합니다.
- 깨끗한 snapshot/복구 경로를 가진 테스트 환경에서만 PnP 실패/강제 종료/Driver Verifier 검증.
  Verifier는 시스템을 중단시킬 수 있으므로 일반 사용자 PC에서 자동 실행하지 않습니다.

## 수동 수용 시험 — 모두 미수행

| 영역 | 시험 및 통과 조건 |
|---|---|
| 다른 앱 없는 설치 | .NET runtime·C++ 도구·VB-CABLE 없는 PC에서 EXE 하나로 명시적 UAC 설치. 실행에 외부 오디오 앱/런타임 설치가 없어야 함. Android에는 AirMic APK를 별도로 설치함. |
| UAC / 범위 | UAC 거절 시 관리자 단계가 실행되지 않음. 일반 수신은 비관리자로 실행 가능. C# 개발 solution은 WDK/C++ 없이 .NET만으로 빌드됨. |
| 파일 / 고지 | `%ProgramFiles%\AirMic\Versions\<EXE-SHA256>`의 EXE 해시, 정확한 .NET/WPF runtime pack 고지, SysVAD MS-PL 및 AirMic 고지를 확인. reparse point는 거절. 사용자 임의 파일을 재귀 삭제하지 않음. |
| OS 등록 | 시작 메뉴, Apps & Features, 제거 문자열이 실제 설치 버전을 가리킴. Private + LocalSubnet + 해당 EXE + UDP만 firewall 허용. Public/Internet/TCP/공유기 포트 개방 없음. |
| 장치 식별 | 정확한 `Root\AirMic`의 AirMic Input(render) / AirMic Microphone(capture) 열거. 일반 장치를 ‘AirMic’으로 이름 변경해도 검증된 경로로 표시/자동 선택하지 않음. |
| 기본 장치 | AirMic Input을 console/multimedia/communications 기본 스피커로 설정하지 않음(`NeverSetAsDefaultEndpoint=0x107`; 수동 선택도 해당 역할에서 제한). GUI가 render를 명시적으로 선택함. |
| 실제 PCM | 알려진 비주기 PCM/음성을 AirMic Input에 재생하고 Microphone에서 캡처하여 채널·표본율·극성·크기·순서/누락/무음·클리핑을 검사. sample 사인파가 없어야 함. 휴대폰 중지 후 오래된 말소리가 재생되지 않아야 함. |
| 소비 앱 | Discord/게임/Windows 녹음기에서 **AirMic Microphone** 선택 후 실제 Android 음성 확인. ACK 수신만으로 통과 처리하지 않음. |
| 형식 / 동시 접근 | 공유 모드, 소비 앱의 44.1/48 kHz·mono/stereo 형식 변환, 복수 소비자, 최대 capture pin, 독점 모드 요청의 정상 거절/동작을 확인. 한 소비자의 지연/정지가 다른 소비자의 cursor를 빼앗지 않음. |
| underrun / 제한 | 입력 단절, reader 지연, 재생 정지/재시작, DMA wrap/일정 지연 후 무음 또는 최신 오디오로 회복. 무제한 queue/오래된 tail/반복 음성 없음. |
| 전원 / PnP | 실제 sleep/resume/hibernate, 장치 disable/enable, active stream 중 제거/재설치, 부팅·재부팅. BSOD/hang/해제 후 DPC/오래된 오디오 없음. KS 상태가 유지되는 resume도 검증. |
| 보호 콘텐츠 | 지원하지 않는 DRM 콘텐츠가 가상 마이크로 노출되지 않음. DRM 상태 변경과 DPC copy의 동시성을 Verifier/검토로 확인. 이 driver는 DRM-compliant 전달 경로가 아님. |
| 설치 경쟁 | 두 installer/uninstaller 동시 실행 시 한 쪽만 진행. 설치 복구의 동일 버전 반복은 중복 root 장치를 만들지 않음. |
| 업그레이드 / 실패 | 후보 EXE 파일을 journal에 기록한 뒤 driver 적용, 최종 commit 전까지 이전 앱 유지. 중간 실패 시 pending 상태·이전 경로·후보 파일·OEM INF 기록을 유지하고 복구/제거 가능. driver를 조용히 downgrade하지 않음. |
| 제거 / 재시도 | 정확한 AirMic root 장치와 기록/연결된 OEM INF만 제거. 사용 중이면 `/force` 없이 실패와 재시도/재부팅 안내. device가 이미 제거된 후에도 OEM package 제거를 재시도할 수 있어야 함. 다른 오디오 driver는 보존. |
| 잠긴 파일 / 재설치 | 실행 중 EXE는 정확한 소유 경로만 재부팅 삭제 예약. 예약된 동일 경로에 즉시 재설치하지 못하도록 marker 유지. Windows 재부팅 후 파일/marker 정리와 재설치를 확인. |
| 사용자 데이터 | `%LOCALAPPDATA%\AirMic\settings.json`은 제거 후 보존. 임의 설치 폴더 내 알 수 없는 사용자 파일은 보존. |
| 실제 600초 / E2E | Z Flip6 화면 off/FGS·실제 Wi-Fi·Windows 소비 앱 캡처로 600초. 누락/PLC/underrun/stall·CPU/GC·발열·배터리·정확한 audio-path E2E p50/p95 기록. **30–50 ms는 목표일 뿐**이며 ACK RTT/endpoint reported latency와 구분. |

## 이미 자동화된 검사와 한계

- C# codec/session/SPSC/jitter/PLC/ASRC/UDP 및 core allocation guard.
- 합성 ZIP의 경로·중복·SHA-256·schema/장치 ID 검증. 실제 driver/signature 검증이 아님.
- portable app journal의 최초 설치·업그레이드·중간 pending 상태·동일 버전 복구와 경로 거절.
  Windows 파일/registry/firewall/PnP에 대한 실패 주입 시험은 아님.
- ±200/0 ppm, 각 600초 **가상 시간** managed playout. 실제 600초 기기 soak가 아님.
- 실제 kernel history 헤더의 host sanitizer·독립 cursor·power generation 테스트.
  DDI/IRQL/lifetime 검증이 아님.
- WDK/INF/API/Inf2Cat 검증. production signing·설치·KS/WASAPI 동작 확인이 아님.

실행한 결과만 [전환 보고서](MIGRATION_0_2.md) 및 `docs/test-results/`에 기록합니다.
