#pragma once
#include <chrono>
#include <cstdint>

namespace airmic {
inline std::uint64_t steadyMicros() noexcept {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count());
}
inline std::uint64_t sampleTicksFromMicros(std::uint64_t us) noexcept {
    return (us / 1'000'000) * 48'000 + (us % 1'000'000) * 48 / 1000;
}
} // namespace airmic
