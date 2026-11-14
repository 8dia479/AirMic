#include "protocol/packet.hpp"

#include <bit>
#include <limits>

namespace airmic {
namespace {
void put16(std::byte* p, std::uint16_t v) noexcept {
    p[0] = std::byte(v >> 8); p[1] = std::byte(v & 0xff);
}
void put32(std::byte* p, std::uint32_t v) noexcept {
    for (unsigned i = 0; i < 4; ++i) p[i] = std::byte((v >> (24 - 8 * i)) & 0xff);
}
void put64(std::byte* p, std::uint64_t v) noexcept {
    for (unsigned i = 0; i < 8; ++i) p[i] = std::byte((v >> (56 - 8 * i)) & 0xff);
}
std::uint16_t get16(const std::byte* p) noexcept {
    return static_cast<std::uint16_t>((std::to_integer<unsigned>(p[0]) << 8)
                                      | std::to_integer<unsigned>(p[1]));
}
std::uint32_t get32(const std::byte* p) noexcept {
    std::uint32_t v = 0;
    for (unsigned i = 0; i < 4; ++i) v = (v << 8) | std::to_integer<unsigned>(p[i]);
    return v;
}
std::uint64_t get64(const std::byte* p) noexcept {
    std::uint64_t v = 0;
    for (unsigned i = 0; i < 8; ++i) v = (v << 8) | std::to_integer<unsigned>(p[i]);
    return v;
}
bool validHeader(const AudioPacket& p) noexcept {
    return (p.flags & ~kKnownFlags) == 0 && (!(p.flags & kFlagStart) || p.sequence == 0);
}
} // namespace

bool encodeAudio(const AudioPacket& p, std::span<std::byte> bytes) noexcept {
    if (bytes.size() != kPacketBytes || !validHeader(p)) return false;
    auto* b = bytes.data();
    put32(b, p.sequence); put64(b + 4, p.timestamp);
    put16(b + 12, static_cast<std::uint16_t>(kFrameSamples)); put16(b + 14, p.flags);
    for (std::size_t i = 0; i < kFrameSamples; ++i)
        put16(b + kHeaderBytes + 2 * i, std::bit_cast<std::uint16_t>(p.samples[i]));
    return true;
}

DecodeError decodeAudio(std::span<const std::byte> bytes, AudioPacket& p) noexcept {
    if (bytes.size() != kPacketBytes) return DecodeError::WrongSize;
    const auto* b = bytes.data();
    if (get16(b + 12) != kFrameSamples) return DecodeError::SampleCount;
    const auto flags = get16(b + 14);
    if ((flags & ~kKnownFlags) != 0) return DecodeError::UnknownFlags;
    const auto sequence = get32(b);
    if ((flags & kFlagStart) && sequence != 0) return DecodeError::InvalidStart;
    p.sequence = sequence; p.timestamp = get64(b + 4); p.flags = flags;
    for (std::size_t i = 0; i < kFrameSamples; ++i)
        p.samples[i] = std::bit_cast<std::int16_t>(get16(b + kHeaderBytes + 2 * i));
    return DecodeError::None;
}

const char* decodeErrorName(DecodeError e) noexcept {
    switch (e) {
    case DecodeError::None: return "ok";
    case DecodeError::WrongSize: return "wrong datagram length";
    case DecodeError::SampleCount: return "sample count is not 480";
    case DecodeError::UnknownFlags: return "unknown flags";
    case DecodeError::InvalidStart: return "START requires sequence zero";
    }
    return "unknown";
}

bool timestampMatches(std::uint32_t seq, std::uint64_t ts, const AudioPacket& p) noexcept {
    const auto ticks = static_cast<std::int64_t>(sequenceDelta(p.sequence, seq))
                       * static_cast<std::int64_t>(kFrameSamples);
    if (ticks >= 0) {
        const auto delta = static_cast<std::uint64_t>(ticks);
        return ts <= std::numeric_limits<std::uint64_t>::max() - delta && p.timestamp == ts + delta;
    }
    const auto delta = static_cast<std::uint64_t>(-ticks);
    return ts >= delta && p.timestamp == ts - delta;
}

bool encodeAck(const ReceiverAck& a, std::span<std::byte> b) noexcept {
    if (b.size() != kAckBytes) return false;
    b[0] = std::byte{'A'}; b[1] = std::byte{'M'}; b[2] = std::byte{'A'}; b[3] = std::byte{'K'};
    put32(b.data() + 4, a.sequence); put64(b.data() + 8, a.timestamp);
    put32(b.data() + 16, a.acceptedPackets); put16(b.data() + 20, 1); put16(b.data() + 22, 0);
    return true;
}

bool decodeAck(std::span<const std::byte> b, ReceiverAck& a) noexcept {
    if (b.size() != kAckBytes || b[0] != std::byte{'A'} || b[1] != std::byte{'M'}
        || b[2] != std::byte{'A'} || b[3] != std::byte{'K'}
        || get16(b.data() + 20) != 1 || get16(b.data() + 22) != 0) return false;
    a.sequence = get32(b.data() + 4); a.timestamp = get64(b.data() + 8);
    a.acceptedPackets = get32(b.data() + 16);
    return true;
}
} // namespace airmic
