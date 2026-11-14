#pragma once
#include "win_util.hpp"
#include "audio/playout.hpp"
#include "statistics.hpp"
#include <future>
#include <string>
#include <thread>

namespace airmic::win {
class WasapiRenderer {
public:
    WasapiRenderer(IngressQueue& queue, RenderStats& stats);
    ~WasapiRenderer() { stop(); }
    WasapiRenderer(const WasapiRenderer&) = delete;
    WasapiRenderer& operator=(const WasapiRenderer&) = delete;
    void start(std::wstring endpointId, unsigned bufferMs);
    void stop() noexcept;
private:
    void run(std::wstring endpointId, unsigned bufferMs, std::promise<void> ready) noexcept;
    IngressQueue& queue_;
    RenderStats& stats_;
    Handle stopEvent_;
    std::thread thread_;
};
} // namespace airmic::win
