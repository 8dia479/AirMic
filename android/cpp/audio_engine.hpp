#pragma once

#include "audio/capture_assembler.hpp"
#include "audio/spsc_ring.hpp"
#include "dsp/processor.hpp"
#include <oboe/AudioStream.h>
#include <oboe/AudioStreamBuilder.h>
#include <oboe/Utilities.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <memory>
#include <string>
#include <thread>

namespace airmic::android {
class AudioEngine;
class CaptureCallbacks final : public oboe::AudioStreamDataCallback, public oboe::AudioStreamErrorCallback {
public:
    CaptureCallbacks(AudioEngine& owner, std::uint64_t generation) noexcept
        : owner_(owner), generation_(generation) {}
    oboe::DataCallbackResult onAudioReady(oboe::AudioStream*, void*, std::int32_t) override;
    void onErrorAfterClose(oboe::AudioStream*, oboe::Result) override;
    int error() const noexcept { return error_.load(std::memory_order_relaxed); }
private:
    AudioEngine& owner_; // Engine outlives all capture callbacks.
    const std::uint64_t generation_;
    std::atomic<int> error_{}; // Per-open state: a late old error cannot poison a new stream.
};

// Public control methods are serialized by jni_bridge, never called on audio thread.
class AudioEngine {
public:
    AudioEngine();
    ~AudioEngine();
    AudioEngine(const AudioEngine&) = delete;
    AudioEngine& operator=(const AudioEngine&) = delete;
    std::string start(const std::string& numericIpv4, std::uint16_t port);
    void stop() noexcept;
    bool configure(DspConfig) noexcept;
    std::array<std::int64_t, 17> snapshot() noexcept;
    std::string lastError() const;
private:
    friend class CaptureCallbacks;
    oboe::DataCallbackResult capture(void*, std::int32_t, std::uint64_t generation) noexcept;
    void senderLoop(DspConfig initial) noexcept;
    void closeSocket() noexcept;
    std::shared_ptr<CaptureCallbacks> callbacks_;
    std::shared_ptr<oboe::AudioStream> stream_;
    CaptureAssembler assembler_{};
    SpscRing<CaptureFrame, 16> captureQueue_{};
    SpscRing<DspConfig, 16> configQueue_{};
    DspConfig config_{};
    std::thread sender_;
    int socket_{-1};
    std::string startError_;
    std::atomic<bool> stopping_{true}, running_{};
    std::atomic<int> state_{}, networkError_{};
    std::atomic<std::uint64_t> callbackGeneration_{};
    std::atomic<std::uint64_t> captured_{}, queueDrops_{}, sent_{}, sendErrors_{}, staleDrops_{};
    std::atomic<std::uint64_t> lastAckUs_{}, rttUs_{}, receiverAccepted_{}, peakMilli_{};
    std::uint32_t burstFrames_{}, bufferFrames_{}, sharingMode_{};
};
static_assert(std::atomic<std::uint64_t>::is_always_lock_free);
} // namespace airmic::android
