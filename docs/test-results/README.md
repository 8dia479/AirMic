# 실행 증거

2026-11-14 Linux x86_64에서 최종 공유 코어로 생성한 JSON을 보관합니다.
대형 오디오/빌드 파일은 포함하지 않습니다.

- `2026-11-14-linux-udp-600s.json`: 실제 600초 localhost UDP, 생성 PCM, null sink.
- `2026-11-14-linux-udp-impaired-10s.json`: 실제 UDP, loss/duplicate/reorder 주입.
- `2026-11-14-summary.json`: 기존 MVP 구현의 실행/미실행 범위와 빌드 결과 요약.
- `2026-11-14-dsp-review.json`: 후속 WebRTC/RNNoise 검토의 소스 revision/hash,
  canonical 대조, RNNoise public-header syntax 검사 및 기존 코어 회귀 재실행.
  **외부 라이브러리 링크/추론/성능 또는 모델 재배포 승인 증거가 아닙니다.**

두 UDP 결과의 sub-millisecond 지연은 **ACK 왕복 시간**이며, 물리 오디오/Wi-Fi/
WASAPI/VB-CABLE의 종단 간 지연이 아닙니다. shutdown 시 buffer/lookahead tail을
끝까지 drain하지 않으므로 `sent/accepted`와 `played`가 같아야 하는 테스트가 아닙니다.

전체 인수 상태는 [IMPLEMENTATION_REPORT.md](../IMPLEMENTATION_REPORT.md),
미실행 실기기 절차는 [DEVICE_TEST_PLAN.md](../DEVICE_TEST_PLAN.md)를 확인하세요.
