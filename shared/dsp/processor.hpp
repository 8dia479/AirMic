#pragma once

#include "protocol/packet.hpp"
#include <array>
#include <cstdint>
#include <span>

namespace airmic {
struct DspConfig {
    bool enabled{true};
    bool noiseGate{false}; // Basic adaptive downward expander, NOT WebRTC/RNNoise.
    float inputGainDb{0.0F};
    float highPassHz{80.0F};
    float lowEqDb{0.0F};
    float midEqDb{0.0F};
    float highEqDb{0.0F};
    float thresholdDb{-18.0F};
    float ratio{3.0F};
    float makeupDb{2.0F};
    float ceilingDb{-1.0F};
};

// One worker owns this object; submit config changes via an SPSC control queue.
// Persistent storage only: process() and configure() never allocate or block.
class DspProcessor {
public:
    DspProcessor() noexcept;
    void configure(DspConfig) noexcept;
    void reset() noexcept;
    void process(std::span<float, kFrameSamples>) noexcept;
    static std::int16_t toPcm16(float) noexcept;
    [[nodiscard]] const DspConfig& config() const noexcept { return config_; }
private:
    struct Biquad {
        double b0{1}, b1{}, b2{}, a1{}, a2{}, z1{}, z2{};
        float tick(float x) noexcept;
        void clear() noexcept { z1 = z2 = 0; }
    };
    static Biquad highPass(float hz) noexcept;
    static Biquad peak(float hz, float q, float db) noexcept;
    static Biquad shelf(float hz, float db, bool high) noexcept;
    DspConfig config_{};
    Biquad hp_{}, low_{}, mid_{}, high_{};
    float gain_{1}, makeup_{1}, ceiling_{0.89125094F};
    float detector_{}, compressorGain_{1}, gateGain_{1};
    float noisePower_{0.000001F};
};
} // namespace airmic
