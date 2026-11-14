// SPDX-License-Identifier: MIT
// AirMic's allocation-free, externally synchronized PCM16 broadcast history.
// This exact header is compiled by both the kernel adapter and host regression tests.
#pragma once
// No CRT headers: mixing host MSVC stdint.h with kernel CRT headers is unsafe.

namespace airmic {
using Pcm16 = short;
using FrameCount = unsigned int;
using SamplePosition = unsigned long long;
static_assert(sizeof(Pcm16) == 2 && sizeof(FrameCount) == 4 && sizeof(SamplePosition) == 8, "Unsupported PCM/platform integer widths");
struct CaptureCursor {
    SamplePosition position = 0;
    SamplePosition generation = 0;
    bool ready = false;
};

class AudioHistory final {
public:
    static constexpr FrameCount Capacity = 4096;       // 85.3 ms at 48 kHz, power of two.
    static constexpr FrameCount MaximumLag = 1440;     // Trim stalled readers after 30 ms.
    static constexpr FrameCount MaximumTransfer = 480; // <= 960 bytes per kernel spin-lock hold.

    void Initialize() noexcept {
        written = 0; generation = 1; running = false; powered = true;
    }
    void SetRunning(bool value) noexcept {
        if (running != value) {
            running = value;
            written = 0;
            ++generation; // Invalidates all readers without accessing their stream objects.
        }
    }
    void SetPowered(bool value) noexcept {
        if (powered != value) {
            powered = value;
            written = 0;
            ++generation;
        }
    }
    void Discontinuity() noexcept {
        written = 0;
        ++generation;
    }
    void Write(const Pcm16* source, FrameCount count) noexcept {
        if (!running || !powered) return;
        // The kernel wrapper splits larger DMA regions into bounded transfers.
        // Keep this defensive bound as well; never walk an arbitrary caller length.
        if (count > MaximumTransfer) count = MaximumTransfer;
        for (FrameCount i = 0; i < count; ++i) {
            samples[written & (Capacity - 1)] = source[i];
            ++written;
        }
    }
    FrameCount Read(CaptureCursor& cursor, Pcm16* destination, FrameCount count) noexcept {
        if (count > MaximumTransfer) count = MaximumTransfer;
        if (count == 0) return 0;
        if (!running || !powered) {
            cursor.ready = false;
            for (FrameCount i = 0; i < count; ++i) destination[i] = 0;
            return 0;
        }
        if (!cursor.ready || cursor.generation != generation) {
            // A new microphone listener starts at current audio, never at an old buffered tail.
            cursor.generation = generation;
            cursor.position = written > count ? written - count : 0;
            cursor.ready = true;
        }
        SamplePosition available = written - cursor.position;
        if (available > MaximumLag) {
            cursor.position = written > count ? written - count : 0;
            available = written - cursor.position;
        }
        const auto copied = static_cast<FrameCount>(available < count ? available : count);
        for (FrameCount i = 0; i < copied; ++i)
            destination[i] = samples[(cursor.position + i) & (Capacity - 1)];
        cursor.position += copied;
        // Underflow is silence, not a repeating recording or a sample tone.
        // Do not advance into the future: the next render write becomes immediately readable.
        for (FrameCount i = copied; i < count; ++i) destination[i] = 0;
        return copied;
    }
private:
    Pcm16 samples[Capacity] = {};
    SamplePosition written = 0;
    SamplePosition generation = 1;
    bool running = false;
    bool powered = true;
};
} // namespace airmic
