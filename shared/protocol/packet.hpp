#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace airmic {
inline constexpr std::uint32_t kSampleRate = 48'000;
inline constexpr std::size_t kFrameSamples = 480;
inline constexpr std::size_t kHeaderBytes = 16;
inline constexpr std::size_t kPacketBytes = kHeaderBytes + kFrameSamples * 2;
inline constexpr std::uint16_t kDefaultPort = 49'000;
inline constexpr std::uint16_t kFlagStart = 0x0001;
inline constexpr std::uint16_t kFlagDiscontinuity = 0x0002;
inline constexpr std::uint16_t kKnownFlags = kFlagStart | kFlagDiscontinuity;

// Timestamp is a SOURCE monotonic sample clock (48,000 ticks/second), NOT UTC.
// Every multi-byte wire field, including signed PCM16, is big-endian.
struct AudioPacket {
    std::uint32_t sequence{};
    std::uint64_t timestamp{};
    std::uint16_t flags{};
    std::array<std::int16_t, kFrameSamples> samples{};
};

enum class DecodeError { None, WrongSize, SampleCount, UnknownFlags, InvalidStart };
[[nodiscard]] bool encodeAudio(const AudioPacket&, std::span<std::byte>) noexcept;
[[nodiscard]] DecodeError decodeAudio(std::span<const std::byte>, AudioPacket&) noexcept;
[[nodiscard]] const char* decodeErrorName(DecodeError) noexcept;

// Signed modulo-2^32 difference. Only distances < 2^31 are unambiguous.
[[nodiscard]] constexpr std::int32_t sequenceDelta(std::uint32_t a, std::uint32_t b) noexcept {
    const auto d = a - b;
    return d <= 0x7fffffffU ? static_cast<std::int32_t>(d)
        : static_cast<std::int32_t>(-static_cast<std::int64_t>(0x1'0000'0000ULL - d));
}
[[nodiscard]] bool timestampMatches(std::uint32_t anchorSequence, std::uint64_t anchorTimestamp,
                                    const AudioPacket&) noexcept;

// Separate optional control datagram; it can NEVER be interpreted as audio.
inline constexpr std::size_t kAckBytes = 24;
struct ReceiverAck {
    std::uint32_t sequence{};
    std::uint64_t timestamp{};
    std::uint32_t acceptedPackets{};
};
[[nodiscard]] bool encodeAck(const ReceiverAck&, std::span<std::byte>) noexcept;
[[nodiscard]] bool decodeAck(std::span<const std::byte>, ReceiverAck&) noexcept;
} // namespace airmic
