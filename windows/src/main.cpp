#include "win_util.hpp"
#include "devices.hpp"
#include "statistics.hpp"
#include "udp_receiver.hpp"
#include "wasapi_renderer.hpp"
#include "audio/clock.hpp"
#include "protocol/session_gate.hpp"

#include <shellapi.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <string>
#include <thread>
#include <vector>

using namespace airmic;
using namespace airmic::win;
namespace {
std::atomic<bool> interrupted{false};
BOOL WINAPI consoleControl(DWORD type) {
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT || type == CTRL_CLOSE_EVENT) {
        interrupted.store(true, std::memory_order_relaxed); return TRUE;
    }
    return FALSE;
}
struct Winsock {
    Winsock() { WSADATA data{}; const int e = WSAStartup(MAKEWORD(2, 2), &data); if (e) throw std::runtime_error("WSAStartup: " + std::to_string(e)); }
    ~Winsock() { WSACleanup(); }
};
std::uint32_t parseIp(const std::wstring& text) {
    IN_ADDR address{};
    if (InetPtonW(AF_INET, text.c_str(), &address) != 1) throw std::runtime_error("Expected numeric IPv4 address");
    return ntohl(address.s_addr);
}
unsigned integer(const std::wstring& text, unsigned low, unsigned high) {
    std::size_t used = 0;
    const auto value = std::stoul(text, &used);
    if (used != text.size() || text.empty() || text.front() == L'-' || value < low || value > high)
        throw std::runtime_error("Numeric argument outside allowed range");
    return static_cast<unsigned>(value);
}
void usage() {
    std::cout << "AirMic receiver 0.1.0 (Windows 10/11, WASAPI shared)\n"
        "  --list-devices             List active RENDER endpoints\n"
        "  --device-index N            Select listed endpoint explicitly\n"
        "  --device-id ID              Select a persistent endpoint ID\n"
        "  --bind IPv4                 Listen address (default 0.0.0.0)\n"
        "  --port 1..65535             UDP port (default 49000)\n"
        "  --allow IPv4                Only accept this phone's IPv4\n"
        "  --buffer-ms 10..200         WASAPI requested buffer (default 10)\n"
        "  --duration 1..86400         Stop automatically after N seconds\n"
        "  --stats-json PATH           Write final diagnostic counters\n\n"
        "With no endpoint argument, exactly one VB-Audio CABLE Input must exist.\n"
        "Select CABLE Output as the microphone in Discord/games. Ctrl+C stops.\n"
        "No encryption/authentication: use a trusted private LAN, not the Internet.\n";
}
void writeStats(const std::filesystem::path& path, const NetworkStats& n, const RenderStats& r, double elapsed) {
    std::ofstream f(path);
    if (!f) throw std::runtime_error("Cannot write --stats-json");
    f << "{\n  \"duration_seconds\": " << elapsed
      << ",\n  \"accepted_packets\": " << n.accepted.load()
      << ",\n  \"invalid_packets\": " << n.invalid.load()
      << ",\n  \"source_rejected\": " << n.sourceRejected.load()
      << ",\n  \"timeline_rejected\": " << n.timelineRejected.load()
      << ",\n  \"network_queue_drops\": " << n.queueDrops.load()
      << ",\n  \"socket_errors\": " << n.socketErrors.load()
      << ",\n  \"acks_sent\": " << n.acks.load()
      << ",\n  \"played_packets\": " << r.played.load()
      << ",\n  \"lost_packets\": " << r.lost.load()
      << ",\n  \"plc_frames\": " << r.plc.load()
      << ",\n  \"duplicates\": " << r.duplicates.load()
      << ",\n  \"late_packets\": " << r.late.load()
      << ",\n  \"underruns\": " << r.underruns.load()
      << ",\n  \"resyncs\": " << r.resyncs.load()
      << ",\n  \"latency_trimmed_packets\": " << r.latencyDrops.load()
      << ",\n  \"adaptive_holds\": " << r.holds.load()
      << ",\n  \"jitter_estimate_ms\": " << r.jitterMs.load()
      << ",\n  \"target_frames\": " << r.target.load()
      << ",\n  \"wasapi_buffer_frames\": " << r.bufferFrames.load()
      << ",\n  \"wasapi_reported_latency_ms\": " << r.endpointLatencyMs.load()
      << ",\n  \"wasapi_error\": " << r.error.load()
      << ",\n  \"hardware_end_to_end_latency_ms\": null\n}\n";
}
int run(const std::vector<std::wstring>& args) {
    bool list = false;
    int deviceIndex = -1;
    std::wstring deviceId, bindIp = L"0.0.0.0";
    std::uint32_t allowed = 0;
    std::uint16_t port = kDefaultPort;
    unsigned bufferMs = 10, duration = 0;
    std::filesystem::path statsPath;
    for (std::size_t i = 1; i < args.size(); ++i) {
        const auto& a = args[i];
        const auto value = [&]() -> const std::wstring& {
            if (++i >= args.size()) throw std::runtime_error("Missing option argument"); return args[i];
        };
        if (a == L"--help" || a == L"-h") { usage(); return 0; }
        if (a == L"--list-devices") list = true;
        else if (a == L"--device-index") deviceIndex = static_cast<int>(integer(value(), 0, 10000));
        else if (a == L"--device-id") deviceId = value();
        else if (a == L"--bind") { bindIp = value(); (void)parseIp(bindIp); }
        else if (a == L"--port") port = static_cast<std::uint16_t>(integer(value(), 1, 65535));
        else if (a == L"--allow") { allowed = parseIp(value()); if (!allowed) throw std::runtime_error("--allow cannot be 0.0.0.0"); }
        else if (a == L"--buffer-ms") bufferMs = integer(value(), 10, 200);
        else if (a == L"--duration") duration = integer(value(), 1, 86400);
        else if (a == L"--stats-json") statsPath = value();
        else throw std::runtime_error("Unknown option: " + utf8(a));
    }
    if (deviceIndex >= 0 && !deviceId.empty()) throw std::runtime_error("Choose either --device-index or --device-id");
    ComApartment com;
    const auto devices = enumerateRenderDevices();
    std::cout << "Active render endpoints:\n";
    for (std::size_t i = 0; i < devices.size(); ++i) {
        std::cout << "  [" << i << "] " << utf8(devices[i].name) << (devices[i].vbCable ? " [VB-CABLE]" : "")
                  << "\n      " << utf8(devices[i].id) << '\n';
    }
    if (list) return 0;
    if (devices.empty()) throw std::runtime_error("No active render endpoints. Install VB-CABLE and reboot first.");
    if (deviceIndex >= 0) {
        if (static_cast<std::size_t>(deviceIndex) >= devices.size()) throw std::runtime_error("Invalid --device-index");
        deviceId = devices[static_cast<std::size_t>(deviceIndex)].id;
    }
    if (deviceId.empty()) {
        const auto count = std::count_if(devices.begin(), devices.end(), [](const DeviceInfo& d) { return d.vbCable; });
        if (count != 1) throw std::runtime_error("Expected exactly one VB-CABLE Input. Install it, or select --device-index explicitly (speakers can cause feedback).");
        deviceId = std::find_if(devices.begin(), devices.end(), [](const DeviceInfo& d) { return d.vbCable; })->id;
    }
    auto selected = std::find_if(devices.begin(), devices.end(), [&](const DeviceInfo& d) { return d.id == deviceId; });
    if (selected == devices.end()) throw std::runtime_error("Selected endpoint is no longer active");
    std::cout << "Rendering to: " << utf8(selected->name) << '\n';
    if (!selected->vbCable) std::cout << "WARNING: explicitly selected a non-VB-CABLE endpoint. Avoid acoustic feedback.\n";
    IngressQueue queue;
    NetworkStats network;
    RenderStats render;
    WasapiRenderer renderer(queue, render);
    UdpReceiver receiver(queue, network);
    renderer.start(deviceId, bufferMs);
    receiver.start(utf8(bindIp), port, allowed);
    std::cout << "Listening " << utf8(bindIp) << ':' << port
        << " | WASAPI buffer " << render.bufferFrames.load() << " frames ("
        << render.bufferFrames.load() / 48.0 << " ms)"
        << " | endpoint reports " << render.endpointLatencyMs.load() << " ms (NOT E2E)"
        << " | MMCSS " << (render.mmcss.load() ? "on" : "unavailable") << '\n';
    std::cout << "Use this PC's LAN IPv4 in Android. Allow UDP " << port << " on the PRIVATE firewall profile.\n";
    SetConsoleCtrlHandler(consoleControl, TRUE);
    const auto start = std::chrono::steady_clock::now();
    auto nextReport = start;
    std::uint64_t previousBytes = 0;
    bool failed = false;
    while (!interrupted.load(std::memory_order_relaxed)) {
        const auto now = std::chrono::steady_clock::now();
        const auto elapsed = std::chrono::duration<double>(now - start).count();
        if (duration && elapsed >= duration) break;
        if (!render.running.load()) { failed = true; std::cerr << "WASAPI stopped: " << hresultText(static_cast<HRESULT>(render.error.load())) << ". Check/reselect the endpoint and restart.\n"; break; }
        if (!network.running.load()) { failed = true; std::cerr << "UDP receiver stopped after a socket error. Restart AirMic.\n"; break; }
        if (now >= nextReport) {
            const auto last = network.lastPacketUs.load();
            const bool live = last && steadyMicros() - last < SessionGate::kIdleUs;
            const auto bytes = network.bytes.load();
            std::cout << std::fixed << std::setprecision(2)
                << (live ? (render.playing.load() ? "STREAMING" : "BUFFERING") : "WAITING")
                << " rx=" << network.accepted.load() << " rate~" << (bytes - previousBytes) / 1000.0 << "kB/s"
                << " invalid=" << network.invalid.load() << " rejected=" << network.sourceRejected.load() + network.timelineRejected.load()
                << " qdrop=" << network.queueDrops.load() << " lost=" << render.lost.load()
                << " dup=" << render.duplicates.load() << " late=" << render.late.load()
                << " plc=" << render.plc.load() << " underrun=" << render.underruns.load()
                << " jb=" << render.buffered.load() << '/' << render.target.load() << "x10ms"
                << " jitter=" << render.jitterMs.load() << "ms drift=" << render.correctionPpm.load() << "ppm\n";
            previousBytes = bytes; nextReport = now + std::chrono::seconds(1);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    receiver.stop(); renderer.stop();
    SetConsoleCtrlHandler(consoleControl, FALSE);
    if (!statsPath.empty()) writeStats(statsPath, network, render, std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count());
    return failed ? 1 : 0;
}
} // namespace
int main() {
    SetConsoleOutputCP(CP_UTF8);
    try {
        Winsock winsock;
        int argc = 0;
        LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        if (!argv) throw std::runtime_error("CommandLineToArgvW failed");
        std::vector<std::wstring> args;
        for (int i = 0; i < argc; ++i) args.emplace_back(argv[i]);
        LocalFree(argv);
        return run(args);
    } catch (const HResultError& e) { std::cerr << "AirMic: " << e.what() << " (" << hresultText(e.code) << ")\n"; return 1; }
      catch (const std::exception& e) { std::cerr << "AirMic: " << e.what() << "\nRun --help for usage.\n"; return 1; }
}
