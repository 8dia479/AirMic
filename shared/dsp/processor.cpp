#include "dsp/processor.hpp"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace airmic {
namespace {
float finiteClamp(float v, float lo, float hi, float fallback) noexcept {
    return std::isfinite(v) ? std::clamp(v, lo, hi) : fallback;
}
float linear(float db) noexcept { return std::pow(10.0F, db / 20.0F); }
} // namespace

float DspProcessor::Biquad::tick(float x) noexcept {
    const double y = b0 * x + z1;
    z1 = b1 * x - a1 * y + z2;
    z2 = b2 * x - a2 * y;
    // Prevent denormal tails without altering audible samples.
    if (std::abs(z1) < 1e-24) z1 = 0;
    if (std::abs(z2) < 1e-24) z2 = 0;
    return static_cast<float>(y);
}

DspProcessor::Biquad DspProcessor::highPass(float hz) noexcept {
    const double w = 2.0 * std::numbers::pi * hz / kSampleRate;
    const double c = std::cos(w), alpha = std::sin(w) / std::sqrt(2.0), a0 = 1 + alpha;
    return {(1 + c) / (2 * a0), -(1 + c) / a0, (1 + c) / (2 * a0),
            -2 * c / a0, (1 - alpha) / a0, 0, 0};
}
DspProcessor::Biquad DspProcessor::peak(float hz, float q, float db) noexcept {
    const double a = std::pow(10.0, db / 40.0);
    const double w = 2.0 * std::numbers::pi * hz / kSampleRate;
    const double c = std::cos(w), alpha = std::sin(w) / (2 * q), a0 = 1 + alpha / a;
    return {(1 + alpha * a) / a0, -2 * c / a0, (1 - alpha * a) / a0,
            -2 * c / a0, (1 - alpha / a) / a0, 0, 0};
}
DspProcessor::Biquad DspProcessor::shelf(float hz, float db, bool high) noexcept {
    const double a = std::pow(10.0, db / 40.0);
    const double w = 2.0 * std::numbers::pi * hz / kSampleRate;
    const double c = std::cos(w), s = std::sin(w);
    const double beta = std::sqrt(2 * a) * s;
    if (high) {
        const double a0 = (a + 1) - (a - 1) * c + beta;
        return {a * ((a + 1) + (a - 1) * c + beta) / a0,
                -2 * a * ((a - 1) + (a + 1) * c) / a0,
                a * ((a + 1) + (a - 1) * c - beta) / a0,
                2 * ((a - 1) - (a + 1) * c) / a0,
                ((a + 1) - (a - 1) * c - beta) / a0, 0, 0};
    }
    const double a0 = (a + 1) + (a - 1) * c + beta;
    return {a * ((a + 1) - (a - 1) * c + beta) / a0,
            2 * a * ((a - 1) - (a + 1) * c) / a0,
            a * ((a + 1) - (a - 1) * c - beta) / a0,
            -2 * ((a - 1) + (a + 1) * c) / a0,
            ((a + 1) + (a - 1) * c - beta) / a0, 0, 0};
}

DspProcessor::DspProcessor() noexcept { configure({}); }
void DspProcessor::configure(DspConfig c) noexcept {
    c.inputGainDb = finiteClamp(c.inputGainDb, -24, 24, 0);
    c.highPassHz = finiteClamp(c.highPassHz, 20, 300, 80);
    c.lowEqDb = finiteClamp(c.lowEqDb, -12, 12, 0);
    c.midEqDb = finiteClamp(c.midEqDb, -12, 12, 0);
    c.highEqDb = finiteClamp(c.highEqDb, -12, 12, 0);
    c.thresholdDb = finiteClamp(c.thresholdDb, -48, 0, -18);
    c.ratio = finiteClamp(c.ratio, 1, 12, 3);
    c.makeupDb = finiteClamp(c.makeupDb, 0, 12, 2);
    c.ceilingDb = finiteClamp(c.ceilingDb, -12, 0, -1);
    // Preserve delay elements when changing coefficients at a frame boundary.
    const auto replace = [](Biquad& dst, Biquad src) {
        src.z1 = dst.z1; src.z2 = dst.z2; dst = src;
    };
    replace(hp_, highPass(c.highPassHz));
    replace(low_, shelf(180, c.lowEqDb, false));
    replace(mid_, peak(1500, 0.8F, c.midEqDb));
    replace(high_, shelf(4500, c.highEqDb, true));
    if (c.enabled != config_.enabled) reset();
    config_ = c; gain_ = linear(c.inputGainDb);
    makeup_ = linear(c.makeupDb); ceiling_ = linear(c.ceilingDb);
}
void DspProcessor::reset() noexcept {
    hp_.clear(); low_.clear(); mid_.clear(); high_.clear();
    detector_ = 0; compressorGain_ = gateGain_ = 1; noisePower_ = 0.000001F;
}
void DspProcessor::process(std::span<float, kFrameSamples> samples) noexcept {
    float power = 0;
    for (float& x : samples) {
        x = std::isfinite(x) ? std::clamp(x, -8.0F, 8.0F) : 0;
        if (config_.enabled) x = hp_.tick(x * gain_);
        power += x * x;
    }
    if (!config_.enabled) return;
    power /= static_cast<float>(kFrameSamples);
    // Basic noise-floor tracking + downward expansion. This suppresses pauses,
    // not stationary noise mixed with speech; it is intentionally labelled a gate.
    const float floorRate = power < noisePower_ ? 0.1F : 0.0005F;
    noisePower_ += floorRate * (power - noisePower_);
    const float threshold = std::max(0.000004F, noisePower_ * 3.0F);
    const float gateTarget = config_.noiseGate
        ? std::clamp(power / threshold, 0.04F, 1.0F) : 1.0F;
    // Coefficients: 5 ms detector attack, 100 ms release; 2/80 ms gain smoothing.
    constexpr float attack = 0.995842F, release = 0.999792F;
    constexpr float gainAttack = 0.989637F, gainRelease = 0.999740F;
    const float thresholdLinear = linear(config_.thresholdDb);
    const float exponent = 1.0F / config_.ratio - 1.0F;
    for (float& x : samples) {
        gateGain_ += (gateTarget > gateGain_ ? 0.01F : 0.0005F) * (gateTarget - gateGain_);
        x = high_.tick(mid_.tick(low_.tick(x * gateGain_)));
        const float level = std::abs(x);
        const float coefficient = level > detector_ ? attack : release;
        detector_ = coefficient * detector_ + (1 - coefficient) * level;
        const float desired = detector_ > thresholdLinear
            ? std::pow(detector_ / thresholdLinear, exponent) : 1.0F;
        const float smooth = desired < compressorGain_ ? gainAttack : gainRelease;
        compressorGain_ = smooth * compressorGain_ + (1 - smooth) * desired;
        // Zero-lookahead safety limiter, not a mastering lookahead limiter.
        const float y = x * compressorGain_ * makeup_;
        x = std::isfinite(y) ? std::clamp(y, -ceiling_, ceiling_) : 0;
    }
}
std::int16_t DspProcessor::toPcm16(float x) noexcept {
    if (!std::isfinite(x)) return 0;
    const auto scaled = std::lround(std::clamp(x, -1.0F, 1.0F) * 32768.0F);
    return static_cast<std::int16_t>(std::clamp(scaled, -32768L, 32767L));
}
} // namespace airmic
