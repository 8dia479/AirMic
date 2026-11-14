# Third-party components

AirMic-authored application/glue source is MIT licensed (see LICENSE). The selected
Microsoft SysVAD driver fork is **MS-PL**, not MIT; upstream notices and its complete
licence are preserved. See [driver provenance](driver/UPSTREAM.md).

| Component | Source / exact version | License | Shipped here |
|---|---|---|---|
| Gradle wrapper | https://github.com/gradle/gradle, v8.10.2 | Apache-2.0 | `android/gradlew`, `gradlew.bat`, `gradle/wrapper/gradle-wrapper.jar` |
| Kotlin / Android Gradle Plugin | Kotlin 2.0.21 / AGP 8.7.3 | Apache-2.0 | Build dependencies, not vendored |
| NumPy | 2.2.6, https://github.com/numpy/numpy | BSD-3-Clause + bundled dependency notices | Optional developer WAV analysis dependency, not vendored |
| .NET / WPF | .NET 10, https://github.com/dotnet/runtime and https://github.com/dotnet/wpf | MIT + runtime third-party notices | SDK/framework references; runtime bundled only in self-contained Windows publish |
| SysVAD driver subset | https://github.com/microsoft/Windows-driver-samples, `d49bb7d72f3651952b2b6e4ad28d14fe2247eb69` | MS-PL | Selected source fork in `driver/sysvad/`; **native driver exception only**, not linked by C#/Kotlin app builds |
| JUnit | 4.13.2, https://github.com/junit-team/junit4 | EPL-1.0 | Android JVM tests only; not bundled in APK |

Apache-2.0 license text is in `docs/licenses/Apache-2.0.txt`. Gradle wrapper JAR SHA-256:
`2db75c40782f5e8ba1fc278a5574bab070adccb2d21ca5a6e5ed840888448046`.

The 0.2 production app build no longer includes Oboe, Android JNI/NDK, the old
native Windows CLI or the historical Zig/MinGW cross-built executable. No
VB-CABLE driver/app is bundled or required as a substitute for the planned AirMic
driver. The retained `shared/` C++ code is a regression reference, not an app dependency.

Self-contained Windows distributions must preserve .NET's accompanying license
and third-party notices. `tools/publish_windows.ps1` copies the project's notices;
release packaging must also include the notices supplied with its exact runtime.
The MIT .NET runtime licensing does not cover Windows itself, the proprietary
Android/Windows SDK/WDK tools or third-party driver signing services.

## Reviewed candidates — NOT integrated or bundled

The [DSP evaluation](docs/DSP_EVALUATION.md) completed the WebRTC APM priority review
and the RNNoise optional additional noise-suppression review on 2026-11-14. These
are selection decisions, not active dependencies or redistribution approvals.

| Candidate / review snapshot | License review | Distribution status |
|---|---|---|
| WebRTC APM `3710345e71ad3a7c1f4ed4efef152fd4fc24869a` | Official root LICENSE: BSD-3-Clause; separate PATENTS grant. Actual linked third-party dependencies need their own notices. | No source/library bundled; no APM in the build graph. This is a review snapshot, not a claim of the latest official release. |
| RNNoise v0.2 `904a876dce1f9ab8860c0a5000ed151f9f6eef58` | Official COPYING: BSD-3-Clause; preserve per-file notices too. The separate model archive's terms and digest have NOT been verified. | No library or model bundled. Model redistribution remains on hold pending provenance/license/hash verification. |

Sources were inspected in ignored local build storage, not vendored. The linked
evaluation records canonical source references and the limits of the review.
Opus is also NOT integrated or bundled and was outside this evaluation. No VB-CABLE
driver, Android SDK/NDK, JDK, or whole third-party source tree is checked into this repository.
