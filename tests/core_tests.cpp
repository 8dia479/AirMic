#include "audio/capture_assembler.hpp"
#include "audio/jitter_buffer.hpp"
#include "audio/playout.hpp"
#include "audio/spsc_ring.hpp"
#include "dsp/processor.hpp"
#include "protocol/packet.hpp"
#include "protocol/session_gate.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <iostream>
#include <limits>
#include <numbers>
#include <random>
#include <span>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using namespace airmic;
namespace {
int checks = 0;
#define CHECK(x) do { ++checks; if (!(x)) throw std::runtime_error(std::string(__func__) + ":" + std::to_string(__LINE__) + " " #x); } while (false)
AudioPacket packet(std::uint32_t seq, std::uint64_t ts = 0) {
    AudioPacket p;
    p.sequence = seq; p.timestamp = ts ? ts : 1'000'000ULL + static_cast<std::uint64_t>(seq) * kFrameSamples;
    p.samples.fill(static_cast<std::int16_t>(1000 + seq % 100));
    return p;
}
void protocol() {
    auto p = packet(0x01020304, 0x05060708090a0b0cULL);
    p.flags = kFlagDiscontinuity;
    p.samples[0] = -32768; p.samples[1] = 32767; p.samples[2] = -1;
    std::array<std::byte, kPacketBytes> bytes{};
    CHECK(encodeAudio(p, bytes));
    const std::array<unsigned, 22> expected{1,2,3,4,5,6,7,8,9,10,11,12,1,224,0,2,128,0,127,255,255,255};
    for (std::size_t i = 0; i < expected.size(); ++i) CHECK(std::to_integer<unsigned>(bytes[i]) == expected[i]);
    AudioPacket q;
    CHECK(decodeAudio(bytes, q) == DecodeError::None);
    CHECK(q.samples == p.samples && q.sequence == p.sequence && q.timestamp == p.timestamp);
    CHECK(decodeAudio(std::span(bytes).first(kPacketBytes - 1), q) == DecodeError::WrongSize);
    auto bad = bytes; bad[12] = std::byte{0};
    CHECK(decodeAudio(bad, q) == DecodeError::SampleCount);
    bad = bytes; bad[15] = std::byte{4};
    CHECK(decodeAudio(bad, q) == DecodeError::UnknownFlags);
    bad = bytes; bad[15] = std::byte{1};
    CHECK(decodeAudio(bad, q) == DecodeError::InvalidStart);
    p.flags = kFlagStart;
    CHECK(!encodeAudio(p, bytes));
    CHECK(sequenceDelta(0, 0xffffffff) == 1);
    CHECK(sequenceDelta(0xffffffff, 0) == -1);
    CHECK(sequenceDelta(0x80000000, 0) == std::numeric_limits<std::int32_t>::min());
    CHECK(timestampMatches(0xffffffff, 1000, packet(0, 1480)));
    CHECK(!timestampMatches(4, 10, packet(0, 10)));
    CHECK(!timestampMatches(0, std::numeric_limits<std::uint64_t>::max() - 10, packet(1, 470)));
    ReceiverAck ack{23, 0x0102030405060708ULL, 456}, decoded;
    std::array<std::byte, kAckBytes> ab{};
    CHECK(encodeAck(ack, ab) && decodeAck(ab, decoded));
    CHECK(decoded.sequence == ack.sequence && decoded.timestamp == ack.timestamp && decoded.acceptedPackets == ack.acceptedPackets);
    ab[22] = std::byte{1}; CHECK(!decodeAck(ab, decoded));
    CHECK(!decodeAck(bytes, decoded));
    // Bounded malformed-datagram fuzzing; no parser may read outside its span.
    std::mt19937 rng(0x414952);
    std::array<std::byte, 1500> fuzz{};
    for (int n = 0; n < 30000; ++n) {
        const auto len = static_cast<std::size_t>(rng() % fuzz.size());
        for (std::size_t i = 0; i < len; ++i) fuzz[i] = std::byte(rng() & 255);
        const auto error = decodeAudio(std::span(fuzz).first(len), q);
        CHECK(error != DecodeError::None || len == kPacketBytes);
    }
}
void ring() {
    SpscRing<std::uint32_t, 4> q;
    std::uint32_t v = 100;
    CHECK(!q.tryPop(v)); CHECK(v == 100);
    for (std::uint32_t i = 0; i < 4; ++i) CHECK(q.tryPush(i));
    CHECK(!q.tryPush(9));
    for (std::uint32_t i = 0; i < 4; ++i) { CHECK(q.tryPop(v)); CHECK(v == i); }
    for (std::uint32_t i = 0; i < 10000; ++i) { CHECK(q.tryPush(i)); CHECK(q.tryPop(v)); CHECK(v == i); }
    SpscRing<std::uint32_t, 64> concurrent;
    std::atomic<bool> valid{true};
    constexpr std::uint32_t count = 1'000'000;
    std::thread producer([&] {
        for (std::uint32_t i = 0; i < count; ++i) while (!concurrent.tryPush(i)) std::this_thread::yield();
    });
    std::thread consumer([&] {
        for (std::uint32_t i = 0; i < count; ++i) {
            std::uint32_t x;
            while (!concurrent.tryPop(x)) std::this_thread::yield();
            if (x != i) valid.store(false);
        }
    });
    producer.join(); consumer.join();
    CHECK(valid.load()); CHECK(concurrent.sizeApprox() == 0);
}
void assembler() {
    CaptureAssembler a;
    a.reset(5000);
    std::array<float, 192> chunk{}; chunk.fill(0.5F);
    std::vector<CaptureFrame> got;
    std::uint64_t now = 1;
    for (int i = 0; i < 25; ++i) a.feed(chunk, [&](const CaptureFrame& f) noexcept {
        got.push_back(f); return true;
    }, [&] { return now++; });
    CHECK(got.size() == 10);
    for (std::size_t i = 0; i < got.size(); ++i) {
        CHECK(got[i].sequence == i && got[i].timestamp == 5000 + i * 480);
        CHECK(got[i].samples.front() == 0.5F && got[i].samples.back() == 0.5F);
        CHECK(got[i].flags == (i == 0 ? kFlagStart : 0));
    }
    std::array<float, 480> frame{};
    a.feed(frame, [](const CaptureFrame&) { return false; }, [] { return 11; });
    CaptureFrame recovered;
    a.feed(frame, [&](const CaptureFrame& f) { recovered = f; return true; }, [] { return 12; });
    CHECK(a.dropped() == 1);
    CHECK(recovered.sequence == 11 && recovered.flags == kFlagDiscontinuity);
}
void sessions() {
    const Endpoint phone{0xc0a80102, 1234}, other{0xc0a80103, 1234};
    SessionGate g(phone.ipv4);
    auto p = packet(0); p.flags = kFlagStart;
    CHECK(!g.admit(p, other, 0).accepted);
    auto a = g.admit(p, phone, 100);
    CHECK(a.accepted && a.newStream && a.generation == 1);
    CHECK(g.admit(p, phone, 200).accepted); // duplicate START is not a reset
    CHECK(g.admit(p, phone, 300).generation == 1);
    p = packet(1);
    CHECK(g.admit(p, phone, 10000).accepted);
    p.timestamp += 1; CHECK(!g.admit(p, phone, 20000).accepted);
    auto restart = packet(0, 2'000'000); restart.flags = kFlagStart;
    const Endpoint newPort{phone.ipv4, 2222};
    a = g.admit(restart, newPort, 25000);
    CHECK(a.accepted && a.newStream && a.generation == 2);
    CHECK(!g.admit(packet(2), phone, 30000).accepted);
    CHECK(g.connected(50000)); CHECK(!g.connected(3'000'000));
    CHECK(g.admit(packet(300), phone, 3'000'000).newStream);
    SessionGate wrap;
    CHECK(wrap.admit(packet(0xfffffffe, 100000), phone, 1).accepted);
    CHECK(wrap.admit(packet(0xffffffff, 100480), phone, 10001).accepted);
    CHECK(wrap.admit(packet(0, 100960), phone, 20001).accepted);
}
void jitterBasics() {
    JitterBuffer b;
    std::array<std::int16_t, kFrameSamples> out{};
    CHECK(b.pop(out) == PlayoutKind::Waiting);
    CHECK(b.insert(packet(2), 20000));
    CHECK(b.insert(packet(0), 21000));
    CHECK(b.insert(packet(1), 22000));
    CHECK(!b.insert(packet(1), 23000));
    CHECK(b.stats().duplicates == 1);
    for (std::uint32_t i = 0; i < 3; ++i) {
        CHECK(b.pop(out) == PlayoutKind::Audio);
        CHECK(b.stats().lastSequence == i);
        CHECK(out.back() == 1000 + i);
    }
    CHECK(b.pop(out) == PlayoutKind::Concealed);
    CHECK(out.back() < 1002 && out.back() > 0);
    CHECK(b.stats().lost == 1);
    auto bad = packet(4); ++bad.timestamp;
    CHECK(!b.insert(bad, 40000)); CHECK(b.stats().invalidTimeline == 1);
    CHECK(!b.insert(packet(0xffffffff, 999520), 41000));
    CHECK(b.stats().late == 1);
    b.reset();
    CHECK(b.insert(packet(0), 0)); CHECK(b.insert(packet(1), 10000));
    CHECK(b.insert(packet(3), 30000)); CHECK(b.insert(packet(4), 40000));
    CHECK(b.pop(out) == PlayoutKind::Audio);
    CHECK(b.pop(out) == PlayoutKind::Audio);
    CHECK(b.pop(out) == PlayoutKind::Concealed);
    CHECK(b.pop(out) == PlayoutKind::Audio); CHECK(b.stats().lastSequence == 3);
    for (int i = 0; i < 10; ++i) b.pop(out);
    CHECK(b.stats().underruns >= 1);
    CHECK(b.insert(packet(100), 1'000'000));
    CHECK(b.insert(packet(101), 1'010'000)); CHECK(b.insert(packet(102), 1'020'000));
    for (int i = 0; i < 10 && !b.stats().lastWasAudio; ++i) b.pop(out);
    CHECK(b.stats().lastWasAudio);
}
void jitterWrapAndAdaptive() {
    JitterBuffer b;
    std::array<std::int16_t, kFrameSamples> out{};
    CHECK(b.insert(packet(0xfffffffe, 100000), 0));
    CHECK(b.insert(packet(0xffffffff, 100480), 10000));
    CHECK(b.insert(packet(0, 100960), 20000));
    CHECK(b.insert(packet(1, 101440), 30000));
    for (std::uint32_t seq : {0xfffffffeU, 0xffffffffU, 0U, 1U}) {
        CHECK(b.pop(out) == PlayoutKind::Audio); CHECK(b.stats().lastSequence == seq);
    }
    b.reset();
    std::uint64_t arrival = 0;
    for (std::uint32_t i = 0; i < 50; ++i) {
        arrival += i % 2 ? 30000 : 1000;
        CHECK(b.insert(packet(i), arrival));
        b.pop(out);
    }
    CHECK(b.stats().target > 2 && b.stats().target <= 8);
    const auto high = b.stats().target;
    for (std::uint32_t i = 50; i < 2000; ++i) {
        arrival += 10000; (void)b.insert(packet(i), arrival); b.pop(out);
    }
    CHECK(b.stats().target < high);
    CHECK(b.stats().buffered < 20);
    CHECK(b.insert(packet(4000), arrival + 20'010'000));
    CHECK(b.stats().resyncs > 0);
}
void dsp() {
    DspProcessor processor;
    DspConfig config;
    config.ratio = 1; config.makeupDb = 0;
    processor.configure(config);
    std::array<float, kFrameSamples> samples{};
    for (int n = 0; n < 150; ++n) { samples.fill(0.5F); processor.process(samples); }
    for (float x : samples) CHECK(std::abs(x) < 0.0001F); // DC removed
    config.enabled = false; processor.configure(config);
    samples.fill(0.123F); processor.process(samples); CHECK(samples[240] == 0.123F);
    config.enabled = true; config.inputGainDb = 24; config.lowEqDb = 12;
    config.midEqDb = 12; config.highEqDb = 12; processor.configure(config);
    for (int n = 0; n < 100; ++n) {
        for (std::size_t i = 0; i < samples.size(); ++i) samples[i] = static_cast<float>(std::sin((n * 480.0 + static_cast<double>(i)) * 0.2));
        samples[5] = std::numeric_limits<float>::quiet_NaN(); samples[15] = std::numeric_limits<float>::infinity();
        processor.process(samples);
        for (float x : samples) CHECK(std::isfinite(x) && std::abs(x) <= 0.8912511F);
    }
    CHECK(DspProcessor::toPcm16(-1) == -32768);
    CHECK(DspProcessor::toPcm16(1) == 32767);
    CHECK(DspProcessor::toPcm16(std::numeric_limits<float>::quiet_NaN()) == 0);
    DspProcessor ungated, gated;
    config = {}; config.ratio = 1; config.makeupDb = 0;
    ungated.configure(config); config.noiseGate = true; gated.configure(config);
    double plainPower = 0, gatePower = 0;
    for (int n = 0; n < 200; ++n) {
        for (std::size_t i = 0; i < samples.size(); ++i) samples[i] = 0.0002F * static_cast<float>(std::sin((n * 480.0 + static_cast<double>(i)) * 0.2));
        auto other = samples; ungated.process(samples); gated.process(other);
        if (n > 150) for (std::size_t i = 0; i < samples.size(); ++i) {
            plainPower += samples[i] * samples[i]; gatePower += other[i] * other[i];
        }
    }
    CHECK(gatePower < plainPower * 0.1);
    config.highPassHz = std::numeric_limits<float>::quiet_NaN(); gated.configure(config);
    CHECK(gated.config().highPassHz == 80);
}
void playoutLongRun() {
    // Both directions, ten virtual minutes each; no hardware time is implied.
    for (const double senderRate : {0.99975, 0.9999, 1.0001, 1.00025}) {
        IngressQueue queue;
        PcmPlayout playout(queue);
        std::array<std::int16_t, kFrameSamples> out{};
        std::uint32_t sequence = 0;
        // Independent source/receiver clocks (+/-100 and +/-250 ppm).
        double produced = 3;
        std::uint64_t nonzero = 0;
        for (int n = 0; n < 60000; ++n) {
            while (static_cast<double>(sequence) < produced) {
                auto p = packet(sequence++);
                CHECK(queue.tryPush({p, static_cast<std::uint64_t>(p.sequence) * 10000, 1}));
            }
            playout.render(out);
            produced += senderRate;
            if (out[240] != 0) ++nonzero;
        }
        const auto s = playout.stats();
        if (s.underruns) std::cerr << "clock rate=" << senderRate << " underruns=" << s.underruns
            << " lost=" << s.lost << " target=" << s.target << " buffered=" << s.buffered << '\n';
        CHECK(nonzero > 59900);
        CHECK(s.underruns == 0); CHECK(s.buffered < 12);
        CHECK(std::abs(playout.correctionPpm()) <= 300.01);
        CHECK(queue.tryPush({packet(0, 100'000'000), 700'000'000, 2}));
        CHECK(queue.tryPush({packet(1, 100'000'480), 700'010'000, 2}));
        playout.render(out); CHECK(playout.stats().resyncs >= 1);
    }
}
} // namespace
int main() {
    try {
        for (auto test : {protocol, ring, assembler, sessions, jitterBasics, jitterWrapAndAdaptive, dsp, playoutLongRun}) test();
        std::cout << "PASS: " << checks << " checks (protocol/fuzz, SPSC, capture, session, jitter/PLC/wrap, DSP, 10-min virtual drift)\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "FAIL: " << e.what() << '\n'; return 1;
    }
}
