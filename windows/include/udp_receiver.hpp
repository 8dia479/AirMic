#pragma once
#include "win_util.hpp"
#include "audio/playout.hpp"
#include "statistics.hpp"
#include <atomic>
#include <cstdint>
#include <string>
#include <thread>

namespace airmic::win {
class UdpReceiver {
public:
    UdpReceiver(IngressQueue& q, NetworkStats& s) : queue_(q), stats_(s) {}
    ~UdpReceiver() { stop(); }
    UdpReceiver(const UdpReceiver&) = delete;
    UdpReceiver& operator=(const UdpReceiver&) = delete;
    void start(const std::string& bindIp, std::uint16_t port, std::uint32_t allowedIp);
    void stop() noexcept;
private:
    void run(std::uint32_t allowedIp) noexcept;
    IngressQueue& queue_;
    NetworkStats& stats_;
    SOCKET socket_{INVALID_SOCKET};
    std::atomic<bool> stopping_{};
    std::thread thread_;
};
} // namespace airmic::win
