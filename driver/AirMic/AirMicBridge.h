// SPDX-License-Identifier: MIT
#pragma once
#include "AudioHistory.h"

// Adapter-owned NONPAGED history. One render hardware pin, independent capture
// cursors; PortCls/WASAPI mixes/shares user-mode clients. No user-mode IOCTL or
// pointer, file/network I/O, work-item allocation, waits or floating point here.
// All callers hold at most their own stream position lock, then this short lock.
// Do not inline lock-taking bodies into pageable control-path callers.
#pragma code_seg(push)
#pragma code_seg()
class AirMicBridge final {
public:
    void Initialize() noexcept {
        KeInitializeSpinLock(&lock);
        history.Initialize();
    }
    _IRQL_requires_max_(DISPATCH_LEVEL)
    __declspec(noinline) void SetRunning(bool running) noexcept {
        KIRQL previous;
        KeAcquireSpinLock(&lock, &previous);
        history.SetRunning(running);
        KeReleaseSpinLock(&lock, previous);
    }
    _IRQL_requires_max_(DISPATCH_LEVEL)
    __declspec(noinline) void SetPowered(bool powered) noexcept {
        KIRQL previous;
        KeAcquireSpinLock(&lock, &previous);
        history.SetPowered(powered);
        KeReleaseSpinLock(&lock, previous);
    }
    _IRQL_requires_max_(DISPATCH_LEVEL)
    __declspec(noinline) void Discontinuity() noexcept {
        KIRQL previous;
        KeAcquireSpinLock(&lock, &previous);
        history.Discontinuity();
        KeReleaseSpinLock(&lock, previous);
    }
    _IRQL_requires_max_(DISPATCH_LEVEL)
    __declspec(noinline) void Write(_In_reads_(count) const SHORT* data, ULONG count) noexcept {
        NT_ASSERT(count <= airmic::AudioHistory::MaximumTransfer);
        KIRQL previous;
        KeAcquireSpinLock(&lock, &previous);
        history.Write(data, count);
        KeReleaseSpinLock(&lock, previous);
    }
    _IRQL_requires_max_(DISPATCH_LEVEL)
    __declspec(noinline) void Read(airmic::CaptureCursor& cursor, _Out_writes_(count) SHORT* data, ULONG count) noexcept {
        NT_ASSERT(count <= airmic::AudioHistory::MaximumTransfer);
        KIRQL previous;
        KeAcquireSpinLock(&lock, &previous);
        history.Read(cursor, data, count);
        KeReleaseSpinLock(&lock, previous);
    }
private:
    KSPIN_LOCK lock = {};
    airmic::AudioHistory history;
};
#pragma code_seg(pop)
