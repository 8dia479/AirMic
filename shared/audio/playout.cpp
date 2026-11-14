#include "audio/playout.hpp"

#include <algorithm>
#include <cmath>

namespace airmic {
float PcmPlayout::takeSample() noexcept {
    if (offset_ == kFrameSamples) {
        jitter_.pop(block_); offset_ = 0;
    }
    return static_cast<float>(block_[offset_++]);
}
void PcmPlayout::render(std::span<std::int16_t> destination) noexcept {
    IngressFrame frame;
    for (std::size_t i = 0; i < IngressQueue::capacity() && ingress_.tryPop(frame); ++i) {
        if (frame.generation != generation_) {
            jitter_.reset(generation_ != 0); generation_ = frame.generation;
            offset_ = kFrameSamples; primed_ = false; fraction_ = 0; rate_ = 1;
        }
        (void)jitter_.insert(frame.packet, frame.arrivalUs);
    }
    const auto state = jitter_.stats();
    const double error = static_cast<double>(state.buffered) - static_cast<double>(state.target);
    // One frame of deficit must command enough correction before the reserve
    // is exhausted. A weaker 80 ppm/frame controller underruns at -250 ppm.
    const double desired = 1.0 + std::clamp(error * 0.0003, -0.0003, 0.0003);
    rate_ += 0.02 * (desired - rate_);
    if (!primed_) { current_ = takeSample(); next_ = takeSample(); primed_ = true; }
    for (auto& sample : destination) {
        const auto value = std::lround(static_cast<double>(current_)
                                       + (static_cast<double>(next_) - current_) * fraction_);
        sample = static_cast<std::int16_t>(std::clamp(value, -32768L, 32767L));
        fraction_ += rate_;
        while (fraction_ >= 1.0) {
            fraction_ -= 1.0; current_ = next_; next_ = takeSample();
        }
    }
}
} // namespace airmic
