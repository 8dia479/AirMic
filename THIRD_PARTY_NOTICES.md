# Third-party components

AirMic's own source is MIT licensed (see LICENSE). Upstream notices remain intact.

| Component | Source / exact version | License | Shipped here |
|---|---|---|---|
| Oboe | https://github.com/google/oboe, 1.9.3 (`b15f5e39c01a7ada306d959e5129620b145fb8b4`) | Apache-2.0 | Gradle dependency, not vendored; linked into Android build |
| Gradle wrapper | https://github.com/gradle/gradle, v8.10.2 | Apache-2.0 | `android/gradlew`, `gradlew.bat`, `gradle/wrapper/gradle-wrapper.jar` |
| Kotlin / Android Gradle Plugin | Kotlin 2.0.21 / AGP 8.7.3 | Apache-2.0 | Build dependencies, not vendored |
| NumPy | 2.2.6, https://github.com/numpy/numpy | BSD-3-Clause + bundled dependency notices | Optional developer WAV analysis dependency, not vendored |
| VB-CABLE | https://vb-audio.com/Cable/ | VB-Audio proprietary/donationware terms | NOT included; user installs separately |

Apache-2.0 license text is in `docs/licenses/Apache-2.0.txt`. Gradle wrapper JAR SHA-256:
`2db75c40782f5e8ba1fc278a5574bab070adccb2d21ca5a6e5ed840888448046`.

The optional Linux-produced Windows diagnostic binary links Zig 0.13.0's
LLVM libc++/libc++abi/libunwind and MinGW-w64 runtime, and depends on the Windows
Universal CRT. It is not a signed or Windows-tested release. Its accompanying
`artifacts/THIRD_PARTY_CROSS_BINARY.txt` describes these runtime licenses. Normal
MSVC builds use the installed Microsoft toolchain/runtime and its redistribution terms.

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
