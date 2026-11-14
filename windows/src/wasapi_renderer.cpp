#include "wasapi_renderer.hpp"

#include <audioclient.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <algorithm>
#include <exception>

namespace airmic::win {
namespace {
class Mmcss {
public:
    Mmcss() { DWORD task = 0; handle_ = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task); if (handle_) AvSetMmThreadPriority(handle_, AVRT_PRIORITY_HIGH); }
    ~Mmcss() { if (handle_) AvRevertMmThreadCharacteristics(handle_); }
    bool active() const noexcept { return handle_ != nullptr; }
private:
    HANDLE handle_{};
};
} // namespace
WasapiRenderer::WasapiRenderer(IngressQueue& q, RenderStats& s)
    : queue_(q), stats_(s), stopEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
    if (!stopEvent_.get()) throw std::runtime_error("Create stop event failed");
}
void WasapiRenderer::start(std::wstring id, unsigned bufferMs) {
    if (thread_.joinable()) throw std::runtime_error("WASAPI renderer already running");
    ResetEvent(stopEvent_.get()); stats_.error.store(0);
    std::promise<void> ready; auto future = ready.get_future();
    thread_ = std::thread(&WasapiRenderer::run, this, std::move(id), bufferMs, std::move(ready));
    try { future.get(); } catch (...) { stop(); throw; }
}
void WasapiRenderer::stop() noexcept {
    SetEvent(stopEvent_.get());
    if (thread_.joinable()) thread_.join();
}
void WasapiRenderer::run(std::wstring id, unsigned bufferMs, std::promise<void> ready) noexcept {
    bool announced = false;
    try {
        ComApartment com;
        ComPtr<IMMDeviceEnumerator> enumerator;
        require(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(enumerator.put())), "Create WASAPI enumerator");
        ComPtr<IMMDevice> device;
        require(enumerator->GetDevice(id.c_str(), device.put()), "Open selected render endpoint");
        ComPtr<IAudioClient> client;
        require(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.put())), "Activate WASAPI client");
        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_PCM; format.nChannels = 1; format.nSamplesPerSec = kSampleRate;
        format.wBitsPerSample = 16; format.nBlockAlign = 2; format.nAvgBytesPerSec = kSampleRate * 2;
        // WASAPI converts mono PCM16/48k into the endpoint's mix format. The render
        // buffer contract remains mono PCM16 regardless of VB-CABLE's channel layout.
        const DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                            | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        require(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags,
            static_cast<REFERENCE_TIME>(bufferMs) * 10'000, 0, &format, nullptr), "Initialize shared WASAPI (48k mono PCM16)");
        Handle audioEvent(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        if (!audioEvent.get()) throw HResultError(HRESULT_FROM_WIN32(GetLastError()), "Create audio event");
        require(client->SetEventHandle(audioEvent.get()), "Set WASAPI event");
        UINT32 capacity = 0; require(client->GetBufferSize(&capacity), "Get WASAPI capacity");
        stats_.bufferFrames.store(capacity);
        REFERENCE_TIME latency = 0;
        if (SUCCEEDED(client->GetStreamLatency(&latency))) stats_.endpointLatencyMs.store(static_cast<double>(latency) / 10'000.0);
        ComPtr<IAudioRenderClient> render;
        require(client->GetService(__uuidof(IAudioRenderClient), reinterpret_cast<void**>(render.put())), "Get render service");
        BYTE* initial = nullptr;
        require(render->GetBuffer(capacity, &initial), "Prime WASAPI");
        require(render->ReleaseBuffer(capacity, AUDCLNT_BUFFERFLAGS_SILENT), "Release prime buffer");
        PcmPlayout playout(queue_);
        Mmcss mmcss; stats_.mmcss.store(mmcss.active());
        require(client->Start(), "Start WASAPI");
        stats_.running.store(true); ready.set_value(); announced = true;
        const HANDLE events[]{stopEvent_.get(), audioEvent.get()};
        HRESULT failure = S_OK;
        while (true) {
            const DWORD wait = WaitForMultipleObjects(2, events, FALSE, 2000);
            if (wait == WAIT_OBJECT_0) break;
            if (wait != WAIT_OBJECT_0 + 1) { failure = wait == WAIT_FAILED ? HRESULT_FROM_WIN32(GetLastError()) : AUDCLNT_E_DEVICE_INVALIDATED; break; }
            UINT32 padding = 0;
            failure = client->GetCurrentPadding(&padding); if (FAILED(failure)) break;
            if (padding > capacity) { failure = E_UNEXPECTED; break; }
            const UINT32 available = capacity - padding;
            if (!available) continue;
            BYTE* data = nullptr;
            failure = render->GetBuffer(available, &data); if (FAILED(failure)) break;
            playout.render(std::span(reinterpret_cast<std::int16_t*>(data), available));
            failure = render->ReleaseBuffer(available, 0); if (FAILED(failure)) break;
            stats_.publish(playout.stats(), playout.correctionPpm());
        }
        client->Stop();
        if (FAILED(failure)) stats_.error.store(failure);
    } catch (const HResultError& e) {
        stats_.error.store(e.code);
        if (!announced) ready.set_exception(std::current_exception());
    } catch (...) {
        stats_.error.store(E_FAIL);
        if (!announced) ready.set_exception(std::current_exception());
    }
    stats_.running.store(false);
}
} // namespace airmic::win
