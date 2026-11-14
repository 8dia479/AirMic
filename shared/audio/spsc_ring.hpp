#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include <type_traits>

namespace airmic {
// Exactly ONE producer and ONE consumer. Never reset while either is running.
// The producer never overwrites unread data, including on counter wraparound.
template<class T, std::size_t Capacity>
class SpscRing {
    static_assert(Capacity >= 2 && (Capacity & (Capacity - 1)) == 0);
    static_assert(std::is_trivially_copyable_v<T>);
    static_assert(std::atomic<std::size_t>::is_always_lock_free);
public:
    [[nodiscard]] bool tryPush(const T& value) noexcept {
        const auto h = head_.load(std::memory_order_relaxed);
        if (h - tail_.load(std::memory_order_acquire) == Capacity) return false;
        slots_[h & (Capacity - 1)] = value;
        head_.store(h + 1, std::memory_order_release);
        return true;
    }
    [[nodiscard]] bool tryPop(T& value) noexcept {
        const auto t = tail_.load(std::memory_order_relaxed);
        if (t == head_.load(std::memory_order_acquire)) return false;
        value = slots_[t & (Capacity - 1)];
        tail_.store(t + 1, std::memory_order_release);
        return true;
    }
    [[nodiscard]] std::size_t sizeApprox() const noexcept {
        const auto t = tail_.load(std::memory_order_acquire);
        const auto n = head_.load(std::memory_order_acquire) - t;
        return n > Capacity ? Capacity : n;
    }
    void resetQuiescent() noexcept { head_.store(0); tail_.store(0); }
    static constexpr std::size_t capacity() noexcept { return Capacity; }
private:
    alignas(64) std::array<T, Capacity> slots_{};
    alignas(64) std::atomic<std::size_t> head_{0};
    alignas(64) std::atomic<std::size_t> tail_{0};
};
} // namespace airmic
