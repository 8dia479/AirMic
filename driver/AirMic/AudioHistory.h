// SPDX-License-Identifier: MIT
// AirMic's allocation-free, externally synchronized PCM16 broadcast history.
// This exact header is compiled by both the kernel adapter and host regression tests.
#pragma once
#include <stdint.h>

namespace airmic {
struct CaptureCursor {
    uint64_t position = 0;
    uint64_t generation = 0;
    bool ready = false;
};

class AudioHistory final {
public:
    static constexpr uint32_t Capacity = 4096;       // 85.3 ms at 48 kHz, power of two.
    static constexpr uint32_t MaximumLag = 1440;     // Trim stalled readers after 30 ms.
    static constexpr uint32_t MaximumTransfer = 480; // <= 960 bytes per kernel spin-lock hold.

    void Initialize() noexcept {
        written = 0; generation = 1; running = false;
    }
    void SetRunning(bool value) noexcept {
        if (running != value) {
            running = value;
            written = 0;
            ++generation; // Invalidates all readers without accessing their stream objects.
        }
    }
    void Discontinuity() noexcept {
        written = 0;
        ++generation;
    }
    void Write(const int16_t* source, uint32_t count) noexcept {
        if (!running) return;
        // The kernel wrapper splits larger DMA regions into bounded transfers.
        // Keep this defensive bound as well; never walk an arbitrary caller length.
        if (count > MaximumTransfer) count = MaximumTransfer;
        for (uint32_t i = 0; i < count; ++i) {
            samples[written & (Capacity - 1)] = source[i];
            ++written;
        }
    }
    uint32_t Read(CaptureCursor& cursor, int16_t* destination, uint32_t count) noexcept {
        if (count > MaximumTransfer) count = MaximumTransfer;
        if (count == 0) return 0;
        if (!running) {
            cursor.ready = false;
            for (uint32_t i = 0; i < count; ++i) destination[i] = 0;
            return 0;
        }
        if (!cursor.ready || cursor.generation != generation) {
            // A new microphone listener starts at current audio, never at an old buffered tail.
            cursor.generation = generation;
            cursor.position = written > count ? written - count : 0;
            cursor.ready = true;
        }
        uint64_t available = written - cursor.position;
        if (available > MaximumLag) {
            cursor.position = written > count ? written - count : 0;
            available = written - cursor.position;
        }
        const auto copied = static_cast<uint32_t>(available < count ? available : count);
        for (uint32_t i = 0; i < copied; ++i)
            destination[i] = samples[(cursor.position + i) & (Capacity - 1)];
        cursor.position += copied;
        // Underflow is silence, not a repeating recording or a sample tone.
        // Do not advance into the future: the next render write becomes immediately readable.
        for (uint32_t i = copied; i < count; ++i) destination[i] = 0;
        return copied;
    }
private:
    int16_t samples[Capacity] = {};
    uint64_t written = 0;
    uint64_t generation = 1;
    bool running = false;
};
} // namespace airmic
