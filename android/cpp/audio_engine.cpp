#include "audio_engine.hpp"
#include "audio/clock.hpp"
#include "protocol/packet.hpp"

#include <arpa/inet.h>
#include <fcntl.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>
#include <algorithm>
#include <array>
#include <cerrno>
#include <cmath>
#include <cstring>
#include <exception>
#include <span>
#include <stdexcept>

namespace airmic::android {
AudioEngine::AudioEngine() = default;
AudioEngine::~AudioEngine() { stop(); }
oboe::DataCallbackResult CaptureCallbacks::onAudioReady(oboe::AudioStream*, void* data, std::int32_t frames) {
    return owner_.capture(data, frames, generation_);
}
void CaptureCallbacks::onErrorAfterClose(oboe::AudioStream*, oboe::Result error) {
    // Error delivery may outlive close(). Only touch this open's callback state.
    error_.store(static_cast<int>(error), std::memory_order_relaxed);
}

std::string AudioEngine::start(const std::string& host, std::uint16_t port) {
    stop(); state_.store(1); startError_.clear(); networkError_.store(0);
    captured_ = 0; queueDrops_ = 0; sent_ = 0; sendErrors_ = 0; staleDrops_ = 0;
    lastAckUs_ = 0; rttUs_ = 0; receiverAccepted_ = 0; peakMilli_ = 0;
    captureQueue_.resetQuiescent(); configQueue_.resetQuiescent();
    try {
        const auto generation = callbackGeneration_.fetch_add(1) + 1;
        callbacks_ = std::make_shared<CaptureCallbacks>(*this, generation);
        sockaddr_in remote{}; remote.sin_family = AF_INET; remote.sin_port = htons(port);
        if (port == 0 || inet_pton(AF_INET, host.c_str(), &remote.sin_addr) != 1)
            throw std::runtime_error("PC address must be numeric IPv4 and port 1..65535");
        socket_ = ::socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, IPPROTO_UDP);
        if (socket_ < 0) throw std::runtime_error("Cannot create UDP socket");
        const int flags = fcntl(socket_, F_GETFL, 0);
        if (flags < 0 || fcntl(socket_, F_SETFL, flags | O_NONBLOCK) < 0)
            throw std::runtime_error("Cannot make UDP socket nonblocking");
        int sendBuffer = 64 * 1024;
        setsockopt(socket_, SOL_SOCKET, SO_SNDBUF, &sendBuffer, sizeof(sendBuffer));
        // A connected UDP socket filters incoming ACKs by the selected PC IP/port.
        // ConnectivityManager binds this process to Wi-Fi BEFORE this socket is made.
        if (::connect(socket_, reinterpret_cast<const sockaddr*>(&remote), sizeof(remote)) < 0)
            throw std::runtime_error("UDP connect failed (check Wi-Fi route)");
        oboe::Result opened = oboe::Result::ErrorInternal;
        for (auto preset : {oboe::InputPreset::Unprocessed, oboe::InputPreset::VoiceRecognition}) {
            for (auto sharing : {oboe::SharingMode::Exclusive, oboe::SharingMode::Shared}) {
                oboe::AudioStreamBuilder builder;
                builder.setDirection(oboe::Direction::Input)
                    ->setAudioApi(oboe::AudioApi::AAudio)
                    ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
                    ->setSharingMode(sharing)
                    ->setInputPreset(preset)
                    ->setPrivacySensitiveMode(oboe::PrivacySensitiveMode::Enabled)
                    ->setSampleRate(kSampleRate)
                    ->setChannelCount(1)
                    ->setFormat(oboe::AudioFormat::Float)
                    ->setChannelConversionAllowed(true)
                    ->setFormatConversionAllowed(true)
                    ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
                    ->setDataCallback(callbacks_)
                    ->setErrorCallback(callbacks_);
                opened = builder.openStream(stream_);
                if (opened == oboe::Result::OK) break;
            }
            if (opened == oboe::Result::OK) break;
        }
        if (opened != oboe::Result::OK)
            throw std::runtime_error(std::string("Oboe open: ") + oboe::convertToText(opened));
        if (stream_->getSampleRate() != static_cast<std::int32_t>(kSampleRate)
            || stream_->getChannelCount() != 1 || stream_->getFormat() != oboe::AudioFormat::Float)
            throw std::runtime_error("Oboe did not provide 48 kHz mono float capture");
        burstFrames_ = static_cast<std::uint32_t>(std::max(1, stream_->getFramesPerBurst()));
        (void)stream_->setBufferSizeInFrames(static_cast<std::int32_t>(burstFrames_ * 2));
        bufferFrames_ = static_cast<std::uint32_t>(stream_->getBufferSizeInFrames());
        sharingMode_ = stream_->getSharingMode() == oboe::SharingMode::Exclusive ? 1U : 0U;
        assembler_.reset(sampleTicksFromMicros(steadyMicros()));
        stopping_.store(false);
        sender_ = std::thread(&AudioEngine::senderLoop, this, config_);
        const auto started = stream_->requestStart();
        if (started != oboe::Result::OK)
            throw std::runtime_error(std::string("Oboe start: ") + oboe::convertToText(started));
        running_.store(true); state_.store(2);
        return {};
    } catch (const std::exception& e) {
        startError_ = e.what(); stop(); state_.store(3); return startError_;
    }
}
void AudioEngine::closeSocket() noexcept { if (socket_ >= 0) { ::close(socket_); socket_ = -1; } }
void AudioEngine::stop() noexcept {
    stopping_.store(true, std::memory_order_release);
    callbackGeneration_.fetch_add(1); // Invalidate stale data callbacks before reopening.
    if (stream_) {
        (void)stream_->requestStop();
        (void)stream_->close(); // Quiesce data callbacks; error delivery can still be asynchronous.
        stream_.reset();
    }
    if (sender_.joinable()) sender_.join();
    closeSocket(); running_.store(false); state_.store(0);
}
bool AudioEngine::configure(DspConfig config) noexcept {
    config_ = config;
    return !running_.load() || configQueue_.tryPush(config);
}
oboe::DataCallbackResult AudioEngine::capture(void* data, std::int32_t frames, std::uint64_t generation) noexcept {
    if (generation != callbackGeneration_.load(std::memory_order_acquire)
        || stopping_.load(std::memory_order_acquire)) return oboe::DataCallbackResult::Stop;
    if (!data || frames <= 0) return oboe::DataCallbackResult::Continue;
    assembler_.feed(std::span(static_cast<const float*>(data), static_cast<std::size_t>(frames)),
        [this](const CaptureFrame& frame) noexcept {
            captured_.fetch_add(1, std::memory_order_relaxed);
            if (captureQueue_.tryPush(frame)) return true;
            queueDrops_.fetch_add(1, std::memory_order_relaxed); return false;
        }, []() noexcept { return steadyMicros(); });
    return oboe::DataCallbackResult::Continue;
}
void AudioEngine::senderLoop(DspConfig initial) noexcept {
    DspProcessor dsp; dsp.configure(initial);
    struct SentStamp { std::uint32_t sequence{}; std::uint64_t timestamp{}, sentUs{}; bool valid{}; };
    std::array<SentStamp, 1024> stamps{};
    std::array<std::byte, kPacketBytes> bytes{};
    std::array<std::byte, 256> reply{};
    while (!stopping_.load(std::memory_order_relaxed)) {
        DspConfig update;
        for (int i = 0; i < 16 && configQueue_.tryPop(update); ++i) dsp.configure(update);
        CaptureFrame frame;
        for (int i = 0; i < 8 && captureQueue_.tryPop(frame); ++i) {
            if (steadyMicros() - frame.readyUs > 60'000) { ++staleDrops_; continue; }
            dsp.process(frame.samples);
            AudioPacket packet;
            packet.sequence = frame.sequence; packet.timestamp = frame.timestamp; packet.flags = frame.flags;
            float peak = 0;
            for (std::size_t j = 0; j < kFrameSamples; ++j) {
                peak = std::max(peak, std::abs(frame.samples[j]));
                packet.samples[j] = DspProcessor::toPcm16(frame.samples[j]);
            }
            peakMilli_.store(static_cast<std::uint64_t>(std::min(peak, 1.0F) * 1000), std::memory_order_relaxed);
            (void)encodeAudio(packet, bytes);
            const auto now = steadyMicros();
            if (::send(socket_, bytes.data(), bytes.size(), MSG_DONTWAIT | MSG_NOSIGNAL) == static_cast<ssize_t>(bytes.size())) {
                ++sent_; networkError_.store(0, std::memory_order_relaxed);
                stamps[packet.sequence % stamps.size()] = {packet.sequence, packet.timestamp, now, true};
            } else { ++sendErrors_; networkError_.store(errno, std::memory_order_relaxed); }
        }
        pollfd fd{socket_, POLLIN, 0};
        const int polled = ::poll(&fd, 1, captureQueue_.sizeApprox() ? 0 : 2);
        if (polled > 0) {
            // recv also clears asynchronous ICMP socket errors; an offline PC is not fatal.
            for (int i = 0; i < 8; ++i) {
                const auto n = ::recv(socket_, reply.data(), reply.size(), MSG_DONTWAIT);
                if (n < 0) break;
                ReceiverAck ack;
                if (!decodeAck(std::span(reply).first(static_cast<std::size_t>(n)), ack)) continue;
                const auto& stamp = stamps[ack.sequence % stamps.size()];
                if (!stamp.valid || stamp.sequence != ack.sequence || stamp.timestamp != ack.timestamp) continue;
                const auto now = steadyMicros();
                const auto sample = now - stamp.sentUs;
                if (sample > 5'000'000) continue;
                const auto previous = rttUs_.load(std::memory_order_relaxed);
                rttUs_.store(previous ? (previous * 7 + sample) / 8 : sample, std::memory_order_relaxed);
                lastAckUs_.store(now, std::memory_order_relaxed);
                receiverAccepted_.store(ack.acceptedPackets, std::memory_order_relaxed);
            }
        }
    }
}
std::array<std::int64_t, 17> AudioEngine::snapshot() noexcept {
    const int audioError = callbacks_ ? callbacks_->error() : 0;
    const int currentState = state_.load();
    std::int64_t xruns = -1;
    if (stream_ && !audioError) {
        const auto result = stream_->getXRunCount(); if (result) xruns = result.value();
    }
    const auto ack = lastAckUs_.load(std::memory_order_relaxed);
    return {currentState == 2 && audioError ? 3 : currentState, static_cast<std::int64_t>(captured_.load()), static_cast<std::int64_t>(queueDrops_.load()),
        static_cast<std::int64_t>(sent_.load()), static_cast<std::int64_t>(sendErrors_.load()), static_cast<std::int64_t>(staleDrops_.load()),
        ack ? static_cast<std::int64_t>((steadyMicros() - ack) / 1000) : -1,
        static_cast<std::int64_t>(rttUs_.load()), xruns, burstFrames_, bufferFrames_, sharingMode_,
        static_cast<std::int64_t>(peakMilli_.load()), audioError, networkError_.load(),
        static_cast<std::int64_t>(receiverAccepted_.load()), static_cast<std::int64_t>(captureQueue_.sizeApprox())};
}
std::string AudioEngine::lastError() const {
    const int audio = callbacks_ ? callbacks_->error() : 0;
    if (audio) return std::string("Oboe: ") + oboe::convertToText(static_cast<oboe::Result>(audio));
    return startError_;
}
} // namespace airmic::android
