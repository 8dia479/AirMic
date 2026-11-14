// Actual localhost UDP + independent receive/render threads. Generated PCM and
// a null sink: this is NOT an Android/Windows/audio-device latency measurement.
#include "audio/playout.hpp"
#include "dsp/processor.hpp"
#include "protocol/packet.hpp"
#include "protocol/session_gate.hpp"

#include <arpa/inet.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <numbers>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using namespace airmic;
using Clock = std::chrono::steady_clock;
namespace {
std::uint64_t micros() {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count());
}
struct Socket {
    int fd{-1};
    Socket() : fd(::socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, 0)) { if (fd < 0) throw std::runtime_error("socket failed"); }
    ~Socket() { if (fd >= 0) ::close(fd); }
    Socket(const Socket&) = delete;
};
struct Counters {
    std::atomic<std::uint64_t> accepted{}, malformed{}, rejected{}, queueDrops{}, ackSent{}, renderCalls{}, renderNonzero{}, renderStalls{};
};
double percentile(std::vector<double> v, double p) {
    if (v.empty()) return 0;
    std::sort(v.begin(), v.end()); return v[static_cast<std::size_t>((v.size() - 1) * p)];
}
} // namespace

int main(int argc, char** argv) {
    try {
        int seconds = 5; bool impair = false; std::string jsonPath;
        for (int i = 1; i < argc; ++i) {
            const std::string arg = argv[i];
            if (arg == "--seconds" && i + 1 < argc) seconds = std::stoi(argv[++i]);
            else if (arg == "--json" && i + 1 < argc) jsonPath = argv[++i];
            else if (arg == "--impair") impair = true;
            else throw std::runtime_error("usage: airmic_loopback [--seconds 600] [--impair] [--json result.json]");
        }
        if (seconds < 1 || seconds > 86400) throw std::runtime_error("seconds must be 1..86400");
        Socket receiver, sender, foreign;
        sockaddr_in local{}; local.sin_family = AF_INET; local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (::bind(receiver.fd, reinterpret_cast<sockaddr*>(&local), sizeof(local)) < 0) throw std::runtime_error("bind failed");
        socklen_t localLen = sizeof(local);
        if (getsockname(receiver.fd, reinterpret_cast<sockaddr*>(&local), &localLen) < 0) throw std::runtime_error("getsockname failed");
        if (::connect(sender.fd, reinterpret_cast<sockaddr*>(&local), sizeof(local)) < 0) throw std::runtime_error("connect failed");
        int bufferSize = 256 * 1024;
        setsockopt(receiver.fd, SOL_SOCKET, SO_RCVBUF, &bufferSize, sizeof(bufferSize));
        IngressQueue queue;
        PcmPlayout playout(queue);
        Counters counts;
        std::atomic<bool> stop{false};
        const auto start = Clock::now() + std::chrono::milliseconds(100);
        const auto beginUs = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(start.time_since_epoch()).count());
        std::thread receiving([&] {
            SessionGate gate;
            std::array<std::byte, 2048> bytes{};
            std::uint64_t lastAck = 0;
            while (!stop.load(std::memory_order_relaxed)) {
                pollfd fds{receiver.fd, POLLIN, 0};
                if (::poll(&fds, 1, 20) <= 0) continue;
                sockaddr_in peer{}; socklen_t len = sizeof(peer);
                const auto n = ::recvfrom(receiver.fd, bytes.data(), bytes.size(), 0, reinterpret_cast<sockaddr*>(&peer), &len);
                if (n < 0) continue;
                AudioPacket p;
                if (decodeAudio(std::span(bytes).first(static_cast<std::size_t>(n)), p) != DecodeError::None) { ++counts.malformed; continue; }
                const auto now = micros();
                const auto admission = gate.admit(p, {ntohl(peer.sin_addr.s_addr), ntohs(peer.sin_port)}, now);
                if (!admission.accepted) { ++counts.rejected; continue; }
                const auto accepted = ++counts.accepted;
                if (!queue.tryPush({p, now, admission.generation})) ++counts.queueDrops;
                if (now - lastAck >= 250000) {
                    std::array<std::byte, kAckBytes> ack{};
                    (void)encodeAck({p.sequence, p.timestamp, static_cast<std::uint32_t>(accepted)}, ack);
                    if (::sendto(receiver.fd, ack.data(), ack.size(), 0, reinterpret_cast<sockaddr*>(&peer), len) == static_cast<ssize_t>(ack.size())) ++counts.ackSent;
                    lastAck = now;
                }
            }
        });
        std::thread rendering([&] {
            auto deadline = start + std::chrono::milliseconds(10);
            std::array<std::int16_t, kFrameSamples> output{};
            while (!stop.load(std::memory_order_relaxed)) {
                std::this_thread::sleep_until(deadline);
                if (stop.load(std::memory_order_relaxed)) break;
                if (Clock::now() - deadline > std::chrono::milliseconds(100)) { ++counts.renderStalls; deadline = Clock::now(); }
                playout.render(output); ++counts.renderCalls;
                if (std::any_of(output.begin(), output.end(), [](auto s) { return s != 0; })) ++counts.renderNonzero;
                deadline += std::chrono::milliseconds(10);
            }
        });
        // Invalid traffic cannot acquire the source pin.
        std::array<std::byte, 31> invalid{};
        for (int i = 0; i < 5; ++i) ::sendto(foreign.fd, invalid.data(), invalid.size(), 0, reinterpret_cast<sockaddr*>(&local), sizeof(local));
        DspProcessor dsp;
        std::array<float, kFrameSamples> samples{};
        std::array<std::byte, kPacketBytes> bytes{}, deferred{};
        bool hasDeferred = false;
        struct Sent { std::uint32_t seq{}; std::uint64_t stamp{}, time{}; bool valid{}; };
        std::array<Sent, 1024> sent{};
        std::vector<double> rtts; rtts.reserve(static_cast<std::size_t>(seconds) * 5 + 10);
        std::uint64_t emitted = 0, intentionallyLost = 0;
        const auto frames = static_cast<std::uint32_t>(seconds * 100);
        for (std::uint32_t seq = 0; seq < frames; ++seq) {
            std::this_thread::sleep_until(start + std::chrono::milliseconds(static_cast<std::int64_t>(seq) * 10));
            AudioPacket p; p.sequence = seq; p.timestamp = beginUs * 48 / 1000 + static_cast<std::uint64_t>(seq) * kFrameSamples;
            p.flags = seq == 0 ? kFlagStart : 0;
            for (std::size_t i = 0; i < samples.size(); ++i)
                samples[i] = 0.2F * static_cast<float>(std::sin(2 * std::numbers::pi * 997.0 * (static_cast<double>(seq) * 480 + static_cast<double>(i)) / kSampleRate));
            dsp.process(samples);
            for (std::size_t i = 0; i < samples.size(); ++i) p.samples[i] = DspProcessor::toPcm16(samples[i]);
            (void)encodeAudio(p, bytes);
            sent[seq % sent.size()] = {seq, p.timestamp, micros(), true};
            const auto sendPacket = [&](const auto& data) {
                if (::send(sender.fd, data.data(), data.size(), 0) == static_cast<ssize_t>(data.size())) ++emitted;
            };
            if (impair && seq % 997 == 1) ++intentionallyLost;
            else if (impair && seq % 197 == 100 && !hasDeferred) { deferred = bytes; hasDeferred = true; }
            else {
                sendPacket(bytes);
                if (hasDeferred) { sendPacket(deferred); hasDeferred = false; }
                if (impair && seq % 331 == 200) sendPacket(bytes);
            }
            if (seq == 50) { // structurally valid packet from an unrelated source port
                ::sendto(foreign.fd, bytes.data(), bytes.size(), 0, reinterpret_cast<sockaddr*>(&local), sizeof(local));
            }
            pollfd fd{sender.fd, POLLIN, 0};
            if (::poll(&fd, 1, 8) > 0) {
                std::array<std::byte, 256> reply{};
                const auto n = ::recv(sender.fd, reply.data(), reply.size(), MSG_DONTWAIT);
                ReceiverAck ack;
                if (n >= 0 && decodeAck(std::span(reply).first(static_cast<std::size_t>(n)), ack)) {
                    const auto& s = sent[ack.sequence % sent.size()];
                    if (s.valid && s.seq == ack.sequence && s.stamp == ack.timestamp)
                        rtts.push_back(static_cast<double>(micros() - s.time) / 1000.0);
                }
            }
            if (seq && seq % 1000 == 0) std::cout << "progress " << seq / 100 << "s sent=" << emitted << " accepted=" << counts.accepted.load() << " queue_drops=" << counts.queueDrops.load() << std::endl;
        }
        std::this_thread::sleep_until(start + std::chrono::seconds(seconds));
        stop.store(true); receiving.join(); rendering.join();
        const auto s = playout.stats();
        const bool passed = counts.accepted > static_cast<std::uint64_t>(frames) * 95 / 100
            && counts.queueDrops == 0 && counts.renderNonzero > static_cast<std::uint64_t>(frames) * 95 / 100
            && counts.malformed == 5 && counts.rejected >= 1 && !rtts.empty() && counts.renderStalls == 0
            && (impair || s.underruns == 0);
        std::string json = "{\n  \"test\": \"Linux localhost UDP, generated PCM, null sink (NOT hardware E2E)\",\n";
        const auto number = [&](const char* name, auto value) { json += "  \"" + std::string(name) + "\": " + std::to_string(value) + ",\n"; };
        number("duration_seconds", seconds); number("impairment_enabled", impair ? 1 : 0);
        number("sent_datagrams", emitted); number("accepted_datagrams", counts.accepted.load());
        number("intentional_packet_losses", intentionallyLost); number("malformed_rejected", counts.malformed.load());
        number("foreign_source_rejected", counts.rejected.load()); number("ingress_queue_drops", counts.queueDrops.load());
        number("render_calls", counts.renderCalls.load()); number("nonzero_render_blocks", counts.renderNonzero.load());
        number("render_scheduler_stalls_over_100ms", counts.renderStalls.load());
        number("played_packets", s.played); number("detected_losses", s.lost); number("plc_frames", s.plc);
        number("duplicates_removed", s.duplicates); number("late_packets", s.late); number("underruns", s.underruns);
        number("adaptive_target_frames", s.target); number("jitter_estimate_ms", s.jitterMs);
        number("adaptive_holds", s.holds); number("latency_trimmed_packets", s.latencyDrops);
        number("ack_samples", rtts.size()); number("udp_ack_rtt_p50_ms", percentile(rtts, 0.5)); number("udp_ack_rtt_p95_ms", percentile(rtts, 0.95));
        json += std::string("  \"passed\": ") + (passed ? "true" : "false") + "\n}\n";
        std::cout << json;
        if (!jsonPath.empty()) { std::ofstream file(jsonPath); if (!file) throw std::runtime_error("cannot write JSON result"); file << json; }
        return passed ? 0 : 1;
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 2; }
}
