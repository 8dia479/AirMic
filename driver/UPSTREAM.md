# AirMic native driver source provenance

This is the **explicit native-driver exception**, not a return to a C++ desktop or
Android app. `windows/AirMic.sln` and the Android Gradle build do not import it.

## Official upstream subset / source licence

`sysvad/` contains a selected, maintained subset of Microsoft's official SysVAD
sample, from `microsoft/Windows-driver-samples` at commit
`d49bb7d72f3651952b2b6e4ad28d14fe2247eb69`:
<https://github.com/microsoft/Windows-driver-samples/tree/d49bb7d72f3651952b2b6e4ad28d14fe2247eb69/audio/sysvad>.

**The upstream repository's licence is MS-PL, not MIT.** The complete licence is
preserved at `sysvad/LICENSE`; original file copyright notices remain. Source
redistribution of this fork is under MS-PL. `upstream.json` records selected files
and their upstream hashes after UTF-8/LF normalization. The application repository's
MIT licence does not replace the sample's licence.

## AirMic modifications to the selected sample

- `common.h/.cpp`: adapter-owned nonpaged transport, private interface accessor,
  power-down invalidation; no SaveData class or static recording object.
- `adapter.cpp`: only two AirMic endpoints selected by `AirMic/minipairs.h`;
  sample tone/data-file configuration removed; Bluetooth/USB/A2DP sideband is not compiled.
- `EndpointsCommon/minwavertstream.h/.cpp`: replace tone generation and recording
  with bounded render→broadcast-history→per-capture-cursor PCM transfer; fixed
  48 kHz/mono/PCM16 validation, <=200 ms DMA allocations, bounded catch-up,
  consumed render-buffer zeroing, protected-content muting, page-map failure checks,
  timer quiescence before object release, 64-bit displacement calculation.
- `EndpointsCommon/minwavert.cpp` / `MiniportStreamAudioEngineNode.cpp`: remove
  remaining sample recording/tone references; no advertised sample loopback protection.
- `sysvad.h`: AirMic diagnostic prefix, removed data-file switch declaration.
- `MiniportAudioEngineNode.cpp`: remove unguarded sample sideband branches and add runtime channel bounds.
- AirMic history uses explicit width-checked primitive types without pulling host CRT headers into kernel sources.
- Case-only include normalization, SPDX/provenance comments and UTF-8/LF normalization.
- No sample APO, keyword-detector adapter DLL, sideband device, HDMI/SPDIF,
  sound-detection endpoint, sine generator or file recorder is packaged/registered.
  Some unused generic PortCls helper code remains in the selected sample files.

`AirMic/` and `tests/` are AirMic-authored glue, endpoint configuration, bounded
history and host tests under MIT (see each file). The independent glue does not
change the MS-PL source redistribution conditions of `sysvad/`.

## Actual transport

The managed app writes shared-mode audio to **AirMic Input**. The WaveRT render
owner publishes already-consumed 48 kHz mono PCM16 to a fixed 4096-sample history.
Each **AirMic Microphone** capture hardware stream has its own cursor, so a slow
or new consumer cannot steal samples from another consumer. Reads are zero-filled
on underflow; a reader more than 30 ms behind is trimmed to current audio. Render
stop/pause, discontinuity, power-down and a new render generation invalidate old
speech. One adapter is supported; one kernel render pin is exposed. Windows Audio
Engine handles shared clients and consumer sample-format conversion.

All copy operations under the adapter spin lock are <=480 frames (960 bytes).
There is no file/network I/O, waits, user pointer/IOCTL, dynamic allocation or
floating point in the AirMic bridge transfer. These design properties are not a
hard-real-time, timing, kernel-safety or hardware-certification claim.

## Build / signing / validation boundary

`tools/build_driver.ps1` restores the exact SDK/WDK package versions used by the
pinned official sample (`10.0.28000.2526`) and uses a compatible **native C++**
toolchain. This is driver-maintainer tooling; end users should receive the final
signed package through AirMic, not install development tools or another audio app.
The intended minimum device OS is Windows 10 2004 (19041), x64, because this subset
uses `ExAllocatePool2`; Windows 10/11 runtime compatibility needs actual testing.

A successful compile, host history tests or Inf2Cat run is **not** a kernel/device
acceptance test. Unsigned CI output is **not a normal installable release**.
Production requires Windows driver signing/submission, a validated catalog covering
all packaged files, device/format/power/removal/concurrency tests, and an upgrade /
rollback / uninstall review. No signing key or certificate is committed. No script
installs this development output, enables test signing or changes Secure Boot.
