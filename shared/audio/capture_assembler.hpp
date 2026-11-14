#pragma once

#include "protocol/packet.hpp"
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace airmic {
struct CaptureFrame {
    std::array<float, kFrameSamples> samples{};
    std::uint64_t timestamp{};
    std::uint64_t readyUs{};
    std::uint32_t sequence{};
    std::uint16_t flags{};
};

// Accepts arbitrary Oboe callback sizes; no allocations, locks, or I/O.
class CaptureAssembler {
public:
    void reset(std::uint64_t monotonicSampleBase) noexcept {
        frame_ = {}; frame_.timestamp = monotonicSampleBase;
        frame_.flags = kFlagStart; used_ = 0; dropped_ = 0;
    }
    // now() is called once per complete 10 ms block and must itself be RT-safe.
    template<class Sink, class Clock>
    void feed(std::span<const float> samples, Sink&& sink, Clock&& now) noexcept {
        for (float sample : samples) {
            frame_.samples[used_++] = sample;
            if (used_ == kFrameSamples) {
                frame_.readyUs = now();
                const bool accepted = sink(frame_);
                if (!accepted) ++dropped_;
                ++frame_.sequence;
                frame_.timestamp += kFrameSamples;
                frame_.flags = accepted ? 0 : kFlagDiscontinuity;
                used_ = 0;
            }
        }
    }
    [[nodiscard]] std::uint64_t dropped() const noexcept { return dropped_; }
private:
    CaptureFrame frame_{};
    std::size_t used_{};
    std::uint64_t dropped_{};
};
} // namespace airmic
