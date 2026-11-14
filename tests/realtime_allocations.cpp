#include "audio/capture_assembler.hpp"
#include "audio/playout.hpp"
#include "dsp/processor.hpp"
#include "protocol/packet.hpp"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdlib>
#include <iostream>
#include <new>
#ifdef _WIN32
#include <malloc.h>
#endif

namespace {
std::atomic<bool> watching{false};
std::atomic<std::size_t> allocations{0};
void count() noexcept { if (watching.load(std::memory_order_relaxed)) allocations.fetch_add(1, std::memory_order_relaxed); }
}
void* operator new(std::size_t size) {
    count(); if (void* p = std::malloc(size ? size : 1)) return p; throw std::bad_alloc();
}
void* operator new[](std::size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }
void operator delete[](void* p, std::size_t) noexcept { std::free(p); }
void* operator new(std::size_t size, std::align_val_t alignment) {
    count();
    const auto align = static_cast<std::size_t>(alignment);
#ifdef _WIN32
    if (void* p = _aligned_malloc(size ? size : 1, align)) return p;
#else
    void* p = nullptr;
    if (posix_memalign(&p, align, size ? size : 1) == 0) return p;
#endif
    throw std::bad_alloc();
}
void* operator new[](std::size_t n, std::align_val_t a) { return ::operator new(n, a); }
void operator delete(void* p, std::align_val_t) noexcept {
#ifdef _WIN32
    _aligned_free(p);
#else
    std::free(p);
#endif
}
void operator delete[](void* p, std::align_val_t a) noexcept { ::operator delete(p, a); }
void operator delete(void* p, std::size_t, std::align_val_t a) noexcept { ::operator delete(p, a); }
void operator delete[](void* p, std::size_t, std::align_val_t a) noexcept { ::operator delete(p, a); }

int main() {
    using namespace airmic;
    IngressQueue ingress;
    PcmPlayout playout(ingress);
    SpscRing<CaptureFrame, 4> captured;
    CaptureAssembler assembler; assembler.reset(100'000);
    DspProcessor dsp;
    DspConfig config;
    std::array<float, kFrameSamples> input{}; input.fill(0.1F);
    std::array<std::int16_t, kFrameSamples> output{};
    std::array<std::byte, kPacketBytes> wire{};
    bool ok = true;
    watching.store(true);
    for (int n = 0; n < 2000; ++n) {
        if (n % 37 == 0) { config.lowEqDb = n % 2 ? 2.0F : -2.0F; config.noiseGate = n % 3 == 0; dsp.configure(config); }
        assembler.feed(input, [&](const CaptureFrame& f) { return captured.tryPush(f); }, [n] { return static_cast<std::uint64_t>(n) * 10000; });
        CaptureFrame f;
        if (!captured.tryPop(f)) { ok = false; break; }
        dsp.process(f.samples);
        AudioPacket p; p.sequence = f.sequence; p.timestamp = f.timestamp; p.flags = f.flags;
        for (std::size_t i = 0; i < kFrameSamples; ++i) p.samples[i] = DspProcessor::toPcm16(f.samples[i]);
        ok = ok && encodeAudio(p, wire);
        AudioPacket decoded;
        ok = ok && decodeAudio(wire, decoded) == DecodeError::None;
        ok = ok && ingress.tryPush({decoded, static_cast<std::uint64_t>(n) * 10000, 1});
        playout.render(output);
    }
    watching.store(false);
    std::cout << "RT core new/new[] allocations: " << allocations.load() << '\n';
    return ok && allocations.load() == 0 ? 0 : 1;
}
