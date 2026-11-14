#pragma once

#include "protocol/packet.hpp"
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace airmic {
struct JitterStats {
    std::uint64_t inserted{}, duplicates{}, late{}, invalidTimeline{};
    std::uint64_t played{}, lost{}, plc{}, underruns{}, resyncs{}, latencyDrops{}, holds{};
    std::uint32_t buffered{}, target{2}, lastSequence{};
    double jitterMs{};
    bool playing{}, lastWasAudio{};
};
enum class PlayoutKind { Waiting, Audio, Concealed, AdaptiveHold };

// Owned exclusively by the render thread. Bounded work and fixed storage.
class JitterBuffer {
public:
    static constexpr std::size_t kCapacity = 64;
    static constexpr std::uint32_t kMinTarget = 2, kMaxTarget = 8;
    void reset(bool countResync = false) noexcept;
    [[nodiscard]] bool insert(const AudioPacket&, std::uint64_t arrivalUs) noexcept;
    PlayoutKind pop(std::span<std::int16_t, kFrameSamples>) noexcept;
    [[nodiscard]] JitterStats stats() const noexcept;
private:
    struct Slot { AudioPacket packet{}; bool used{}; };
    struct Seen { std::uint32_t sequence{}; bool valid{}; };
    void clearSlots() noexcept;
    void updateJitter(const AudioPacket&, std::uint64_t) noexcept;
    void conceal(std::span<std::int16_t, kFrameSamples>, bool hold) noexcept;
    bool discardNext() noexcept;
    std::array<Slot, kCapacity> slots_{};
    std::array<Seen, kCapacity> seen_{};
    std::array<std::int16_t, kFrameSamples> history_{};
    JitterStats counts_{};
    std::uint32_t next_{}, anchorSequence_{}, buffered_{}, target_{kMinTarget};
    std::uint32_t lastArrivalSequence_{}, stableUpdates_{}, expansion_{}, shrink_{};
    std::uint64_t anchorTimestamp_{}, lastArrivalUs_{};
    double jitterUs_{};
    std::uint32_t consecutiveLoss_{};
    float concealGain_{1};
    std::int16_t lastSample_{};
    bool anchored_{}, started_{}, arrivalKnown_{}, reanchoring_{}, crossfade_{};
};
} // namespace airmic
