#include "udp_receiver.hpp"
#include "audio/clock.hpp"
#include "protocol/packet.hpp"
#include "protocol/session_gate.hpp"
#include <mswsock.h>
#include <array>
#include <stdexcept>

namespace airmic::win {
void UdpReceiver::start(const std::string& bindIp, std::uint16_t port, std::uint32_t allowedIp) {
    if (thread_.joinable()) throw std::runtime_error("UDP receiver already running");
    socket_ = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_ == INVALID_SOCKET) throw std::runtime_error("UDP socket: " + std::to_string(WSAGetLastError()));
    try {
        sockaddr_in address{}; address.sin_family = AF_INET; address.sin_port = htons(port);
        if (InetPtonA(AF_INET, bindIp.c_str(), &address.sin_addr) != 1) throw std::runtime_error("--bind must be numeric IPv4");
        // Do not use SO_REUSEADDR on Windows: it can allow another receiver to steal this port.
        BOOL exclusive = TRUE;
        if (setsockopt(socket_, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive)) == SOCKET_ERROR)
            throw std::runtime_error("SO_EXCLUSIVEADDRUSE failed");
        const int size = 256 * 1024;
        setsockopt(socket_, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&size), sizeof(size));
        if (::bind(socket_, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR)
            throw std::runtime_error("UDP bind failed: " + std::to_string(WSAGetLastError()));
        u_long nonblocking = 1;
        if (ioctlsocket(socket_, FIONBIO, &nonblocking) == SOCKET_ERROR) throw std::runtime_error("FIONBIO failed");
        BOOL reset = FALSE; DWORD returned = 0;
        WSAIoctl(socket_, SIO_UDP_CONNRESET, &reset, sizeof(reset), nullptr, 0, &returned, nullptr, nullptr);
        stopping_.store(false); stats_.running.store(true);
        thread_ = std::thread(&UdpReceiver::run, this, allowedIp);
    } catch (...) { stats_.running.store(false); closesocket(socket_); socket_ = INVALID_SOCKET; throw; }
}
void UdpReceiver::stop() noexcept {
    stopping_.store(true);
    if (thread_.joinable()) thread_.join(); // select wakes at least every 50 ms
    if (socket_ != INVALID_SOCKET) { closesocket(socket_); socket_ = INVALID_SOCKET; }
}
void UdpReceiver::run(std::uint32_t allowedIp) noexcept {
    SessionGate gate(allowedIp);
    std::array<std::byte, 2048> bytes{};
    std::uint64_t lastAckUs = 0;
    while (!stopping_.load(std::memory_order_relaxed)) {
        fd_set readSet; FD_ZERO(&readSet); FD_SET(socket_, &readSet);
        timeval timeout{0, 50'000};
        const int ready = select(0, &readSet, nullptr, nullptr, &timeout);
        if (ready == 0) continue;
        if (ready == SOCKET_ERROR) { ++stats_.socketErrors; break; }
        // Bounded batch: control/stop requests cannot be starved by hostile traffic.
        for (int batch = 0; batch < 64; ++batch) {
            sockaddr_in from{}; int length = sizeof(from);
            const int count = recvfrom(socket_, reinterpret_cast<char*>(bytes.data()), static_cast<int>(bytes.size()), 0,
                                       reinterpret_cast<sockaddr*>(&from), &length);
            if (count == SOCKET_ERROR) {
                const int error = WSAGetLastError();
                if (error == WSAEWOULDBLOCK) break;
                if (error == WSAEMSGSIZE) ++stats_.invalid;
                else ++stats_.socketErrors;
                continue;
            }
            AudioPacket packet;
            if (decodeAudio(std::span(bytes).first(static_cast<std::size_t>(count)), packet) != DecodeError::None) { ++stats_.invalid; continue; }
            const auto now = steadyMicros();
            const Endpoint endpoint{ntohl(from.sin_addr.s_addr), ntohs(from.sin_port)};
            const auto admitted = gate.admit(packet, endpoint, now);
            if (!admitted.accepted) {
                if (admitted.reason == RejectReason::Timeline) ++stats_.timelineRejected;
                else ++stats_.sourceRejected;
                continue;
            }
            const auto accepted = ++stats_.accepted;
            stats_.bytes.fetch_add(static_cast<std::uint64_t>(count), std::memory_order_relaxed);
            stats_.lastPacketUs.store(now, std::memory_order_relaxed);
            stats_.peerIp.store(endpoint.ipv4, std::memory_order_relaxed);
            stats_.peerPort.store(endpoint.port, std::memory_order_relaxed);
            if (!queue_.tryPush({packet, now, admitted.generation})) ++stats_.queueDrops;
            if (admitted.newStream || now - lastAckUs >= 250'000) {
                std::array<std::byte, kAckBytes> ack{};
                (void)encodeAck({packet.sequence, packet.timestamp, static_cast<std::uint32_t>(accepted)}, ack);
                if (sendto(socket_, reinterpret_cast<const char*>(ack.data()), static_cast<int>(ack.size()), 0,
                           reinterpret_cast<const sockaddr*>(&from), length) == static_cast<int>(ack.size())) ++stats_.acks;
                lastAckUs = now;
            }
        }
    }
    stats_.running.store(false);
}
} // namespace airmic::win
