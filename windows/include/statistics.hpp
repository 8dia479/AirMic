#pragma once
#include "audio/jitter_buffer.hpp"
#include <atomic>
#include <cstdint>

namespace airmic::win {
struct NetworkStats {
    std::atomic<std::uint64_t> accepted{}, invalid{}, sourceRejected{}, timelineRejected{}, queueDrops{}, socketErrors{}, acks{};
    std::atomic<std::uint64_t> bytes{}, lastPacketUs{};
    std::atomic<std::uint32_t> peerIp{};
    std::atomic<std::uint16_t> peerPort{};
    std::atomic<bool> running{};
};
struct RenderStats {
    std::atomic<std::uint64_t> played{}, lost{}, plc{}, duplicates{}, late{}, underruns{}, resyncs{}, latencyDrops{}, holds{};
    std::atomic<std::uint32_t> buffered{}, target{2}, bufferFrames{};
    std::atomic<double> jitterMs{}, correctionPpm{}, endpointLatencyMs{};
    std::atomic<bool> playing{}, mmcss{}, running{};
    std::atomic<long> error{0};
    void publish(const JitterStats& s, double ppm) noexcept {
        played.store(s.played, std::memory_order_relaxed); lost.store(s.lost, std::memory_order_relaxed);
        plc.store(s.plc, std::memory_order_relaxed); duplicates.store(s.duplicates, std::memory_order_relaxed);
        late.store(s.late, std::memory_order_relaxed); underruns.store(s.underruns, std::memory_order_relaxed);
        resyncs.store(s.resyncs, std::memory_order_relaxed); latencyDrops.store(s.latencyDrops, std::memory_order_relaxed);
        holds.store(s.holds, std::memory_order_relaxed); buffered.store(s.buffered, std::memory_order_relaxed);
        target.store(s.target, std::memory_order_relaxed); jitterMs.store(s.jitterMs, std::memory_order_relaxed);
        correctionPpm.store(ppm, std::memory_order_relaxed); playing.store(s.playing, std::memory_order_relaxed);
    }
};
static_assert(std::atomic<std::uint64_t>::is_always_lock_free);
static_assert(std::atomic<double>::is_always_lock_free);
} // namespace airmic::win
