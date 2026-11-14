#pragma once

#include "protocol/packet.hpp"
#include <cstdint>

namespace airmic {
struct Endpoint {
    std::uint32_t ipv4{}; // host order
    std::uint16_t port{};
    bool operator==(const Endpoint&) const = default;
};
enum class RejectReason { None, Source, Timeline };
struct Admission {
    bool accepted{};
    bool newStream{};
    std::uint64_t generation{};
    RejectReason reason{RejectReason::None};
};

// Network-thread owned. Not authentication: use only on a trusted LAN.
class SessionGate {
public:
    explicit SessionGate(std::uint32_t allowedIp = 0) noexcept : allowedIp_(allowedIp) {}
    [[nodiscard]] Admission admit(const AudioPacket& p, Endpoint from, std::uint64_t nowUs) noexcept {
        if (allowedIp_ && allowedIp_ != from.ipv4) return reject(RejectReason::Source);
        const bool idle = !active_ || nowUs - lastArrivalUs_ >= kIdleUs;
        // A new capture has a later monotonic base. Old delayed STARTs cannot reset it.
        const bool freshStart = active_ && from.ipv4 == peer_.ipv4 && (p.flags & kFlagStart)
                                && p.timestamp > highestTimestamp_;
        if (idle || freshStart) {
            peer_ = from; active_ = true; ++generation_;
            anchorSequence_ = p.sequence; anchorTimestamp_ = p.timestamp;
            highestTimestamp_ = p.timestamp; lastArrivalUs_ = nowUs;
            return {true, true, generation_, RejectReason::None};
        }
        if (!(from == peer_)) return reject(RejectReason::Source);
        if (!timestampMatches(anchorSequence_, anchorTimestamp_, p)) return reject(RejectReason::Timeline);
        if (p.timestamp > highestTimestamp_) {
            highestTimestamp_ = p.timestamp;
            // Roll the anchor forward so continuous streams survive sequence wrap indefinitely.
            anchorSequence_ = p.sequence; anchorTimestamp_ = p.timestamp;
        }
        lastArrivalUs_ = nowUs;
        return {true, false, generation_, RejectReason::None};
    }
    [[nodiscard]] bool connected(std::uint64_t nowUs) const noexcept {
        return active_ && nowUs - lastArrivalUs_ < kIdleUs;
    }
    static constexpr std::uint64_t kIdleUs = 2'000'000;
private:
    Admission reject(RejectReason reason) const noexcept { return {false, false, generation_, reason}; }
    std::uint32_t allowedIp_{};
    bool active_{};
    Endpoint peer_{};
    std::uint64_t lastArrivalUs_{}, highestTimestamp_{}, generation_{}, anchorTimestamp_{};
    std::uint32_t anchorSequence_{};
};
} // namespace airmic
