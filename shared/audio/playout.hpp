#pragma once

#include "audio/jitter_buffer.hpp"
#include "audio/spsc_ring.hpp"
#include <cstddef>
#include <cstdint>
#include <span>

namespace airmic {
struct IngressFrame {
    AudioPacket packet{};
    std::uint64_t arrivalUs{};
    std::uint64_t generation{};
};
using IngressQueue = SpscRing<IngressFrame, 64>;

// Render-thread endpoint. Network ingress is drained at a bounded frame boundary.
// A tiny linear ASRC (~ +/-300 ppm) prevents independent phone/PC clocks from
// accumulating a whole-frame overflow/underflow during long sessions.
class PcmPlayout {
public:
    explicit PcmPlayout(IngressQueue& ingress) noexcept : ingress_(ingress) {}
    void render(std::span<std::int16_t> destination) noexcept;
    [[nodiscard]] JitterStats stats() const noexcept { return jitter_.stats(); }
    [[nodiscard]] double correctionPpm() const noexcept { return (rate_ - 1.0) * 1e6; }
private:
    float takeSample() noexcept;
    IngressQueue& ingress_;
    JitterBuffer jitter_{};
    std::array<std::int16_t, kFrameSamples> block_{};
    std::size_t offset_{kFrameSamples};
    std::uint64_t generation_{};
    float current_{}, next_{};
    double fraction_{}, rate_{1};
    bool primed_{};
};
} // namespace airmic
