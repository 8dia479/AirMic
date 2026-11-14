// SPDX-License-Identifier: MIT
#include "../AirMic/AudioHistory.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <mutex>
#include <thread>

namespace {
unsigned checks = 0;
void check(bool value) {
    ++checks;
    if (!value) { std::cerr << "AudioHistory failure at check " << checks << '\n'; std::abort(); }
}
using Block = std::array<int16_t, 480>;
void silence_and_broadcast() {
    airmic::AudioHistory history; history.Initialize();
    airmic::CaptureCursor a{}, b{};
    Block input{}, output{}, second{};
    input.fill(1234); output.fill(-1);
    history.Write(input.data(), 480); // A stopped render must not store a future recording.
    check(history.Read(a, output.data(), 480) == 0);
    check(std::all_of(output.begin(), output.end(), [](int16_t x) { return x == 0; }));
    history.SetRunning(true); history.Write(input.data(), 480);
    check(history.Read(a, output.data(), 480) == 480); check(output == input);
    check(history.Read(b, second.data(), 480) == 480); check(second == input); // Independent reader.
    check(history.Read(a, output.data(), 480) == 0);
    check(std::all_of(output.begin(), output.end(), [](int16_t x) { return x == 0; }));
    input.fill(-32768); history.Write(input.data(), 480);
    check(history.Read(a, output.data(), 480) == 480); check(output == input);
    history.SetRunning(false);
    check(history.Read(b, output.data(), 480) == 0); // Old tail must disappear even for lagging readers.
    check(std::all_of(output.begin(), output.end(), [](int16_t x) { return x == 0; }));
    history.SetRunning(true);
    check(history.Read(a, output.data(), 480) == 0);
    input.fill(32767); history.Write(input.data(), 137);
    check(history.Read(a, output.data(), 480) == 137);
    for (unsigned i = 0; i < output.size(); ++i) check(output[i] == (i < 137 ? 32767 : 0));
    history.Discontinuity();
    check(history.Read(a, output.data(), 480) == 0);
    check(history.Read(b, output.data(), 0) == 0);
}
void ring_wrap_and_stalled_reader() {
    airmic::AudioHistory history; history.Initialize(); history.SetRunning(true);
    airmic::CaptureCursor fast{}, slow{};
    Block input{}, output{};
    for (int frame = 0; frame < 10000; ++frame) {
        for (unsigned i = 0; i < input.size(); ++i) input[i] = static_cast<int16_t>(frame * 480 + i);
        history.Write(input.data(), 480);
        check(history.Read(fast, output.data(), 480) == 480); check(output == input);
        if (frame % 19 == 0) {
            check(history.Read(slow, output.data(), 480) == 480);
            check(output == input); // >30ms lag is trimmed to current audio, even through capacity wraps.
        }
    }
}
void externally_synchronized_readers() {
    // Mirrors the adapter's lock discipline; not a substitute for kernel/IRQL tests.
    airmic::AudioHistory history; history.Initialize(); history.SetRunning(true);
    std::mutex lock;
    std::atomic<bool> done{false}, valid{true};
    auto reader = [&] {
        airmic::CaptureCursor cursor{}; Block data{};
        while (!done.load()) {
            std::lock_guard guard(lock);
            const auto copied = history.Read(cursor, data.data(), 480);
            for (uint32_t i = 1; i < copied; ++i)
                if (data[i] != data[0]) valid.store(false);
            for (uint32_t i = copied; i < data.size(); ++i)
                if (data[i] != 0) valid.store(false);
        }
    };
    std::thread first(reader), second(reader);
    Block data{};
    for (int frame = 1; frame < 20000; ++frame) {
        data.fill(static_cast<int16_t>(frame));
        std::lock_guard guard(lock);
        history.Write(data.data(), 480);
        if (frame % 101 == 0) { history.SetRunning(false); history.SetRunning(true); }
    }
    done.store(true); first.join(); second.join(); check(valid.load());
}
}
int main() {
    silence_and_broadcast(); ring_wrap_and_stalled_reader(); externally_synchronized_readers();
    std::cout << "PASS: " << checks << " AirMic driver history checks (host only, not kernel/device acceptance)\n";
}
