# 빌드·설치·실행

정확한 검증 현황은 [IMPLEMENTATION_REPORT.md](IMPLEMENTATION_REPORT.md)를 참고하세요.
아래는 **재현 명령**이지 이 환경에서 모든 플랫폼 빌드에 성공했다는 선언이 아닙니다.

## 0. 저장소 받기 / 두 IDE에서 여는 위치

**한 저장소의 같은 브랜치에 Windows와 Android 소스가 모두 있습니다.**
각 플랫폼 브랜치를 따로 받거나 `shared/`를 복사하지 않습니다.

```powershell
git clone --branch arena/56cea872-airmic --single-branch https://github.com/8dia479/AirMic.git
cd AirMic
```

이미 clone했다면 해당 브랜치를 선택한 상태에서 `git pull --ff-only`로 갱신합니다.

```text
AirMic/                  Visual Studio에서 이 루트 폴더를 열기 (CMakeLists.txt)
  windows/               Windows 수신기 (C++20 / Winsock / WASAPI)
  android/               Android Studio에서 이 폴더를 열기 (settings.gradle.kts)
    app/                 Kotlin UI / service
    cpp/                 Oboe / JNI 캡처·DSP·송신
  shared/                양쪽 빌드가 참조하는 공통 C++ 코어
  tools/                 Windows 빌드·방화벽·진단 스크립트
```

- Visual Studio용 `.sln`을 저장소에 넣은 구조가 아니라 **CMake 프로젝트**입니다.
- Android Studio에서는 `AirMic/`나 `android/app/`가 아닌 **`AirMic/android/`**를 엽니다.
- `android/`만 따로 복사하면 상위의 `shared/`를 찾을 수 없습니다. 저장소 전체를 유지하세요.
- 두 IDE를 동시에 열어도 됩니다. Windows는 `build/windows/`, Android는 Gradle의
  `android/app/build/` 등에 별도로 빌드합니다. SDK 경로/IDE 캐시/생성 바이너리는 커밋하지 않습니다.

## 1. 공통 코어 (Linux / Windows / macOS host)

필수: CMake ≥3.22, C++20 compiler, build tool. 외부 테스트 프레임워크 다운로드 없음.

```sh
cmake -S . -B build/host -DCMAKE_BUILD_TYPE=RelWithDebInfo
cmake --build build/host --parallel 2
ctest --test-dir build/host --output-on-failure
```

Windows multi-config generator라면 build에 `--config Release`, ctest에 `-C Release`를
추가합니다. macOS에서는 코어/테스트의 이식성을 의도했지만 미검증이며 UDP loopback 도구는
Linux에서만 빌드합니다. Windows 프로그램은 WIN32에서만, Android는 독립 Gradle/CMake graph로 빌드합니다.

### Sanitizer (GCC/Clang Linux)

```sh
cmake -S . -B build/asan -DCMAKE_BUILD_TYPE=Debug -DAIRMIC_SANITIZER=address
cmake --build build/asan --parallel 2
ctest --test-dir build/asan --output-on-failure
# address 옵션은 AddressSanitizer + UndefinedBehaviorSanitizer 활성화

cmake -S . -B build/tsan -DCMAKE_BUILD_TYPE=Debug -DAIRMIC_SANITIZER=thread
cmake --build build/tsan --parallel 2
ctest --test-dir build/tsan --output-on-failure
```

두 sanitizer는 별도 build directory를 사용합니다. 이 옵션으로 Windows/Android
sanitizer 동작까지 보장하지는 않습니다.

### 실제 UDP headless soak (Linux)

```sh
mkdir -p artifacts
./build/host/airmic_loopback --seconds 600 --json artifacts/linux-soak.json
./build/host/airmic_loopback --seconds 10 --impair --json artifacts/linux-impair.json
```

독립 sender/receiver/render 타이밍, 실제 IPv4 loopback socket, 생성 sine PCM,
DSP/프로토콜/source pin/SPSC/jitter/PLC/ASRC, null sink를 사용합니다. malformed 및
다른 source port 패킷도 주입합니다. `--impair`는 결정적 loss/duplicate/reordering을
주입합니다. 마이크/Wi-Fi/WASAPI/VB-CABLE은 사용하지 않습니다.

## 2. Android (Galaxy Z Flip6)

### 고정한 도구 버전

| 항목 | 버전 |
|---|---|
| JDK | 17 |
| Gradle wrapper | 8.10.2 |
| Android Gradle Plugin | 8.7.3 |
| Kotlin | 2.0.21 |
| compile/target SDK | 35 / 35 |
| min SDK | 27 |
| NDK | 27.2.12479018 |
| CMake (SDK component) | 3.22.1 |
| Oboe (official Prefab AAR) | `com.google.oboe:oboe:1.9.3` |
| ABI | arm64-v8a, x86_64 |

Oboe 공식 source: https://github.com/google/oboe . 1.9.3 tag는
`b15f5e39c01a7ada306d959e5129620b145fb8b4`입니다. Google Maven/Maven Central,
Gradle distribution 다운로드가 필요합니다. 임의의 mutable fork는 사용하지 않습니다.

### Android Studio에서 열고 실행 (Windows PC 기준)

1. **Open** → `AirMic/android`를 선택합니다. 기존 프로젝트 화면에서는
   **File → Open**을 사용합니다. 처음에는 Gradle/Oboe 등을 받으므로 인터넷이 필요합니다.
2. **Tools → SDK Manager**에서 다음을 확인합니다. **Show Package Details**를 켜야
   정확한 NDK/CMake/Build Tools 버전을 고를 수 있습니다.
   - SDK Platforms: **Android 15 / API 35**
   - SDK Tools: **Android SDK Build-Tools 35.0.0**, **Android SDK Platform-Tools**,
     **NDK (Side by side) 27.2.12479018**, **CMake 3.22.1**
3. **File → Settings → Build, Execution, Deployment → Build Tools → Gradle**에서
   **Gradle JDK를 17**로 지정합니다. 설치된 JDK 17이 없으면 선택 창의 Download JDK를
   사용합니다. IDE에 포함된 JDK가 항상 17인 것은 아닙니다.
4. Gradle Sync를 실행합니다. 초기 재현에서는 AGP/Kotlin/Gradle upgrade 제안을 적용하지
   말고 저장소의 고정 버전을 유지합니다. SDK 경로는 IDE가 `local.properties`로 관리합니다.
5. Z Flip6에서 개발자 옵션/USB 디버깅을 켜고 USB로 연결한 뒤 PC 디버깅 허용을 승인합니다.
   실행 구성을 **app**, 장치를 휴대폰으로 선택하고 **Run ▶** 합니다. 앱의 마이크 권한을
   허용해야 캡처를 시작할 수 있습니다.
6. APK만 만들려면 Build 메뉴의 **Build APK(s)**를 사용합니다 (IDE 버전에 따라
   Generate/Build App Bundle(s) or APK(s) 아래에 있음). 생성 경로:
   `AirMic/android/app/build/outputs/apk/debug/app-debug.apk`.

**IDE의 Gradle JDK 선택과 터미널의 JAVA_HOME은 별개일 수 있습니다.** 터미널 빌드를
쓸 때는 `java -version`과 JAVA_HOME이 JDK 17을 가리키는지 확인하세요. PowerShell에서
`AirMic/android`를 작업 디렉터리로 하여:

```powershell
.\gradlew.bat :app:assembleDebug :app:lintDebug --stacktrace
```

### SDK command-line tools로 준비/빌드 (선택)

```sh
sdkmanager --licenses
sdkmanager "platforms;android-35" "build-tools;35.0.0" "ndk;27.2.12479018" "cmake;3.22.1"
# JAVA_HOME=JDK17, ANDROID_HOME=/absolute/path/to/Android/Sdk 설정
cd android
./gradlew :app:assembleDebug :app:lintDebug --stacktrace
# Windows host: gradlew.bat :app:assembleDebug :app:lintDebug
```

SDK 자동 탐지가 안 되면 Git ignore 대상인 `android/local.properties`에
`sdk.dir=/absolute/path/to/Android/Sdk`를 기록합니다. 비밀정보는 넣지 마세요.
repo root에서 설치:

```sh
adb devices
adb install -r android/app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n io.airmic/.MainActivity
adb logcat -b crash
```

정상 빌드 후 APK 위치는 `android/app/build/outputs/apk/debug/app-debug.apk`입니다.
**이 개발 환경에서는 APK가 생성되지 않았습니다.** release signing 키/keystore는
포함하지 않습니다. 배포 전 모든 native .so의 16 KB 페이지 호환성도 검사해야 합니다.
Oboe source와 자체 link에 16 KB max-page-size 설정이 있지만 실제 APK는 아직 미검사입니다.

### 앱 조작

1. 휴대폰을 PC와 서로 통신 가능한 Wi-Fi에 연결합니다. PC는 같은 LAN의 유선
   Ethernet 연결이어도 됩니다.
2. Windows receiver를 실행하고 Private firewall 설정을 확인합니다.
3. 앱에서 **PC의 LAN IPv4**와 UDP port를 입력합니다.
4. 마이크 권한을 허용합니다. 알림 권한도 요청하지만, 거부 시에도 마이크 권한이
   있으면 OS 규칙에 따라 foreground service를 사용할 수 있습니다. 이 경우 앱/OS에서 중지합니다.
5. `PC 응답 확인됨`은 최근 PC validation ACK 수신 상태입니다.
   `전송 중 · PC 응답 없음`은 UDP 송신 처리만 진행 중인 상태입니다.
6. 앱 버튼/알림 action으로 중지합니다. Activity 화면을 닫는 것만으로는 중지되지 않습니다.

서비스 범위에서 자동 재접속을 시도합니다. OS force-stop 후에는 사용자가 앱을 열어
명시적으로 재개해야 합니다. 부팅 자동 시작이나 숨겨진 백그라운드 마이크 시작은 없습니다.

## 3. Windows 10/11 x64

### Visual Studio에서 준비 / 권장 native build

Visual Studio가 설치돼 있어도 C++ 구성 요소가 없을 수 있습니다. **Visual Studio
Installer → Modify**에서 아래를 확인하세요 (Visual Studio 2022 기준).

- **Desktop development with C++ / C++를 사용한 데스크톱 개발** 워크로드
- MSVC v143 x64/x86 C++ build tools, Windows 10 또는 11 SDK
- **C++ CMake tools for Windows** (CMake ≥3.22)

Visual Studio의 **File → Open → Folder**로 `AirMic` 루트를 열 수 있습니다. CMake
구성 완료 후 x64 구성을 선택해 `airmic_receiver` 또는 전체를 빌드하세요. IDE의 기본
Ninja 구성은 출력 경로가 다를 수 있으므로 아래의 재현 가능한 스크립트 경로를 권장합니다.

**Tools → Command Line → Developer PowerShell** 또는 시작 메뉴의 VS 2022 Developer
PowerShell을 열고 `AirMic` 루트로 이동합니다. 일반 터미널에서 cmake를 못 찾으면 해당
개발자 터미널을 사용하고 CMake 구성 요소 설치 여부를 확인하세요.

VB-CABLE은 **컴파일에는 필요 없고 가상 마이크 실행 때 별도 설치**합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build_windows.ps1
.\artifacts\airmic_receiver.exe --help
.\artifacts\airmic_receiver.exe --list-devices
```

스크립트 없이 repo root에서 코어 테스트까지 빌드:

```powershell
cmake -S . -B build/windows -A x64
cmake --build build/windows --config Release --parallel 2
ctest --test-dir build/windows -C Release --output-on-failure
.\build\windows\windows\Release\airmic_receiver.exe --list-devices
```

수신기만 standalone으로 빌드할 수도 있습니다:

```powershell
cmake -S windows -B windows/build -A x64
cmake --build windows/build --config Release
.\windows\build\Release\airmic_receiver.exe --list-devices
```

여러 가상 장치가 있거나 자동 탐색할 이름이 다른 경우:

```powershell
.\artifacts\airmic_receiver.exe --device-index 2 --port 49000 --allow 192.168.0.20
```

`2`는 예시입니다. **반드시 --list-devices의 실제 index**로 바꿉니다. 영구 ID가 필요하면
같은 목록의 실제 ID를 복사하여 `--device-id`에 전달합니다. 자동 선택은 `CABLE Input`과
`VB-Audio`가 이름에 있는 render endpoint가 정확히 하나인 경우만 가능합니다.
일반 스피커를 기본값으로 몰래 선택하지 않습니다. 명시적으로 스피커를 고르면
하울링과 음량에 주의하세요.

### 가상 마이크 연결

1. https://vb-audio.com/Cable/ 에서 공식 설치 파일을 받아 설치합니다.
2. Windows sound settings에서 CABLE Input (재생) / CABLE Output (녹음)을 활성화합니다.
3. 가능하면 둘 다 default format을 48 kHz로 맞춥니다. WASAPI shared autoconvert가
   실제 mix format으로 변환하므로 mono PCM16 요청과 stereo endpoint 차이는 처리합니다.
4. AirMic의 출력은 **CABLE Input**.
5. Discord/게임/녹음 프로그램의 마이크 입력은 **CABLE Output**.
6. 비교 측정 때 소비 앱의 추가 noise suppression/AGC는 우선 꺼 이중 DSP를 피합니다.

`--buffer-ms 10`은 WASAPI 요청값이지 실제 버퍼 크기 보장이 아닙니다. 시작할 때 실제
capacity와 endpoint reported latency를 출력합니다. VB-CABLE/소비 앱의 추가 버퍼도
있으므로 저지연 달성 여부는 경로 전체를 측정해서 판단합니다.

### Private firewall

필요할 때만 관리자 PowerShell에서:

```powershell
.\tools\open_firewall.ps1 -Program .\artifacts\airmic_receiver.exe -Port 49000
```

Private + LocalSubnet + 실행 파일 경로에 한정한 수신 UDP 허용입니다. Public 전체
허용/인터넷 공개/공유기 port forwarding은 하지 마세요. NIC가 Private profile인지
확인합니다. 규칙 삭제 명령은 스크립트 실행 결과에 표시합니다.

### 진단 및 10분 수집

```powershell
.\artifacts\airmic_receiver.exe --device-index 2 --duration 600 --stats-json artifacts\receiver.json
.\tools\soak_windows.ps1 -Program .\artifacts\airmic_receiver.exe -PhoneIp 192.168.0.20 -Seconds 600
```

스크립트는 통계 조건만 검사합니다. 실제 음질, Discord 입력, 휴대폰 동작/지연은
[DEVICE_TEST_PLAN.md](DEVICE_TEST_PLAN.md)에 따라 별도로 판정합니다.

휴대폰 없이 수신기의 wire/output 경로를 진단하는 생성/사전 녹음 source:

```powershell
python tools\udp_sender.py --host 192.168.0.10 --seconds 10
python tools\udp_sender.py --host 192.168.0.10 --wav speech.wav --seconds 30
```

진단 source는 **Android 실기기 캡처 검증을 대체하지 않습니다**.

### Linux → Windows compile-only check

이번 Linux 환경에서는 PyPI `ziglang==0.13.0` compiler bundle로 PE x64를 생성했습니다.
**Windows 실행 또는 MSVC 검증이 아닙니다.** 선택적 재현 절차:

```sh
python -m pip install ziglang==0.13.0 cmake==3.31.10 ninja
ZIG=$(python -c 'import pathlib,ziglang;print(pathlib.Path(ziglang.__file__).parent / "zig")')
cmake -S . -B build/win-cross -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE=cmake/zig-windows.cmake -DZIG_EXECUTABLE="$ZIG" \
  -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF
cmake --build build/win-cross --parallel 1
# build/win-cross/windows/airmic_receiver.exe
```

교차 생성한 파일은 unsigned 개발용입니다. Windows 표준 UCRT 등 시스템 DLL이
필요합니다. MSVC native build 및 실기기 시험을 release gate로 유지하세요.

## 4. 문제 해결

| 증상 | 확인 |
|---|---|
| Android build 시작 실패 | JAVA_HOME/JDK17, SDK/NDK 버전, Google Maven/Gradle 다운로드 가능 여부 |
| Oboe open/start error | 마이크 권한, 다른 앱의 독점 녹음, 시스템 마이크 privacy switch |
| PC ACK 없음 | PC LAN IPv4/port, Private firewall, guest/AP 격리, VPN |
| valid rx는 증가하나 무음 | CABLE Input 출력/CABLE Output 녹음 meter, mute/volume, 게임 입력 선택 |
| --allow 사용 시 거부 | 지정한 IP가 PC가 아닌 phone IP인지, DHCP로 바뀌었는지 |
| 다른 sender 거부 | source pin. 이전 sender 정지 후 2초 대기 또는 receiver 재시작 |
| WASAPI invalidated/error | 장치 disable/제거, 드라이버 설치 후 재부팅, 장치 재선택. 자동 re-open은 미구현 |
| 오래 쓰면 끊김 | qdrop/late/plc/underrun/xrun을 구분해 수집. 절전/전파 혼잡/VB-CABLE buffer 확인 |
| 화면 off에서 중지 | FGS 유지 여부, Samsung 배터리 정책, 녹음 경쟁. 실기기 검증 필요 |
| RTT는 짧은데 Discord 지연 | RTT≠E2E. jitter/WASAPI/virtual cable/app buffer까지 전체 측정 |

이 환경에는 JDK/Android SDK/NDK/Windows runtime이 없습니다. Windows는 cross-link,
Android native engine은 공식 Oboe header에 대한 **host syntax 검사**까지 수행했습니다.
Kotlin/AGP/Prefab/NDK 빌드나 PowerShell 스크립트를 여기서 실행한 것으로 간주하지 않습니다.
