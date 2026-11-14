#include "audio/jitter_buffer.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace airmic {
void JitterBuffer::clearSlots() noexcept {
    for (auto& s : slots_) s.used = false;
    buffered_ = 0;
}
void JitterBuffer::reset(bool countResync) noexcept {
    clearSlots();
    for (auto& s : seen_) s.valid = false;
    history_.fill(0);
    if (countResync) ++counts_.resyncs;
    anchored_ = started_ = arrivalKnown_ = reanchoring_ = crossfade_ = false;
    target_ = kMinTarget; jitterUs_ = 0; stableUpdates_ = expansion_ = shrink_ = 0;
    consecutiveLoss_ = 0; concealGain_ = 1; lastSample_ = 0;
}
void JitterBuffer::updateJitter(const AudioPacket& p, std::uint64_t arrivalUs) noexcept {
    if (!arrivalKnown_) {
        arrivalKnown_ = true; lastArrivalSequence_ = p.sequence; lastArrivalUs_ = arrivalUs;
        return;
    }
    const auto delta = sequenceDelta(p.sequence, lastArrivalSequence_);
    if (delta <= 0) return; // reordered packets do not poison the estimator
    const double spacing = static_cast<double>(delta) * 10'000.0;
    const double actual = static_cast<double>(arrivalUs) - static_cast<double>(lastArrivalUs_);
    jitterUs_ += (std::abs(actual - spacing) - jitterUs_) / 16.0;
    lastArrivalSequence_ = p.sequence; lastArrivalUs_ = arrivalUs;
    const auto desired = static_cast<std::uint32_t>(std::clamp(
        1.0 + std::ceil(4.0 * jitterUs_ / 10'000.0),
        static_cast<double>(kMinTarget), static_cast<double>(kMaxTarget)));
    if (desired > target_) {
        if (started_) expansion_ += desired - target_;
        target_ = desired; stableUpdates_ = 0;
    } else if (desired < target_) {
        if (++stableUpdates_ >= 200) { --target_; ++shrink_; stableUpdates_ = 0; }
    } else stableUpdates_ = 0;
}

bool JitterBuffer::insert(const AudioPacket& p, std::uint64_t arrivalUs) noexcept {
    if (!anchored_) {
        anchored_ = true; anchorSequence_ = next_ = p.sequence; anchorTimestamp_ = p.timestamp;
    }
    if (!timestampMatches(anchorSequence_, anchorTimestamp_, p)) {
        ++counts_.invalidTimeline; return false;
    }
    auto& seen = seen_[p.sequence & (kCapacity - 1)];
    if (seen.valid && seen.sequence == p.sequence) { ++counts_.duplicates; return false; }
    if (reanchoring_ && buffered_ == 0) { next_ = p.sequence; reanchoring_ = false; }
    auto delta = sequenceDelta(p.sequence, next_);
    if (delta < 0) {
        if (!started_ && delta > -static_cast<std::int32_t>(kCapacity)) next_ = p.sequence;
        else { ++counts_.late; return false; }
    } else if (delta >= static_cast<std::int32_t>(kCapacity)) {
        clearSlots(); next_ = p.sequence; started_ = false;
        consecutiveLoss_ = expansion_ = 0; crossfade_ = true; ++counts_.resyncs;
    }
    // Roll forward the timeline anchor to keep wraparound comparisons local.
    if (sequenceDelta(p.sequence, anchorSequence_) > 0) {
        anchorSequence_ = p.sequence; anchorTimestamp_ = p.timestamp;
    }
    auto& slot = slots_[p.sequence & (kCapacity - 1)];
    if (slot.used) { // only possible at a window edge before initial playout
        slot.used = false; --buffered_; ++counts_.latencyDrops;
    }
    slot.packet = p; slot.used = true; ++buffered_;
    seen = {p.sequence, true}; ++counts_.inserted;
    updateJitter(p, arrivalUs);
    return true;
}

bool JitterBuffer::discardNext() noexcept {
    auto& slot = slots_[next_ & (kCapacity - 1)];
    if (!slot.used || slot.packet.sequence != next_) return false;
    slot.used = false; --buffered_; ++next_; ++counts_.latencyDrops;
    crossfade_ = true;
    return true;
}
void JitterBuffer::conceal(std::span<std::int16_t, kFrameSamples> out, bool hold) noexcept {
    const float endGain = hold ? concealGain_ * 0.98F
        : (consecutiveLoss_ >= 5 ? 0.0F : concealGain_ * 0.75F);
    for (std::size_t i = 0; i < kFrameSamples; ++i) {
        const float t = static_cast<float>(i + 1) / static_cast<float>(kFrameSamples);
        float value = static_cast<float>(history_[i]) * (concealGain_ + t * (endGain - concealGain_));
        if (i < 48) {
            const float mix = static_cast<float>(i + 1) / 48.0F;
            value = static_cast<float>(lastSample_) * (1 - mix) + value * mix;
        }
        out[i] = static_cast<std::int16_t>(std::clamp(value, -32768.0F, 32767.0F));
    }
    concealGain_ = endGain; lastSample_ = out.back(); crossfade_ = true;
}

PlayoutKind JitterBuffer::pop(std::span<std::int16_t, kFrameSamples> out) noexcept {
    counts_.lastWasAudio = false;
    if (!started_) {
        if (!anchored_ || buffered_ < target_) {
            std::fill(out.begin(), out.end(), 0); return PlayoutKind::Waiting;
        }
        started_ = true; reanchoring_ = false;
    }
    if (expansion_) {
        if (buffered_ < target_ && consecutiveLoss_ == 0) {
            --expansion_; conceal(out, true); ++counts_.holds; return PlayoutKind::AdaptiveHold;
        }
        expansion_ = 0;
    }
    // Bound accumulated latency after scheduler/network stalls. Recovery is audible
    // but preferable to retaining hundreds of milliseconds of stale microphone audio.
    while (buffered_ > target_ + 4 && discardNext()) {}
    if (shrink_ && buffered_ > target_ + 1 && discardNext()) --shrink_;
    auto& slot = slots_[next_ & (kCapacity - 1)];
    if (slot.used && slot.packet.sequence == next_) {
        std::copy(slot.packet.samples.begin(), slot.packet.samples.end(), out.begin());
        slot.used = false; --buffered_;
        if (crossfade_) {
            for (std::size_t i = 0; i < 48; ++i) {
                const float t = static_cast<float>(i + 1) / 48.0F;
                out[i] = static_cast<std::int16_t>(static_cast<float>(lastSample_) * (1 - t)
                                                  + static_cast<float>(out[i]) * t);
            }
        }
        std::copy(out.begin(), out.end(), history_.begin());
        lastSample_ = out.back(); concealGain_ = 1; crossfade_ = false; consecutiveLoss_ = 0;
        counts_.lastSequence = next_++; ++counts_.played; counts_.lastWasAudio = true;
        return PlayoutKind::Audio;
    }
    ++counts_.lost; ++counts_.plc; ++consecutiveLoss_; ++next_;
    conceal(out, false);
    if (consecutiveLoss_ >= 6) {
        ++counts_.underruns;
        if (target_ < kMaxTarget) ++target_;
        started_ = false; reanchoring_ = true; consecutiveLoss_ = 0;
        // Pick the earliest available future packet, or reanchor on the next arrival.
        std::int32_t nearest = std::numeric_limits<std::int32_t>::max();
        for (auto& s : slots_) {
            if (!s.used) continue;
            const auto d = sequenceDelta(s.packet.sequence, next_);
            if (d < 0) { s.used = false; --buffered_; }
            else if (d < nearest) nearest = d;
        }
        if (nearest != std::numeric_limits<std::int32_t>::max()) {
            next_ += static_cast<std::uint32_t>(nearest); reanchoring_ = false;
        }
    }
    return PlayoutKind::Concealed;
}
JitterStats JitterBuffer::stats() const noexcept {
    auto s = counts_;
    s.buffered = buffered_; s.target = target_; s.jitterMs = jitterUs_ / 1000.0;
    s.playing = started_;
    return s;
}
} // namespace airmic
