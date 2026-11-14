#pragma once
// Include Winsock before windows.h in EVERY translation unit.
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <objbase.h>

#include <stdexcept>
#include <string>
#include <utility>

namespace airmic::win {
template<class T> class ComPtr {
public:
    ComPtr() = default;
    ~ComPtr() { reset(); }
    ComPtr(const ComPtr&) = delete;
    ComPtr& operator=(const ComPtr&) = delete;
    ComPtr(ComPtr&& other) noexcept : p_(std::exchange(other.p_, nullptr)) {}
    ComPtr& operator=(ComPtr&& other) noexcept { if (this != &other) { reset(); p_ = std::exchange(other.p_, nullptr); } return *this; }
    T* get() const noexcept { return p_; }
    T* operator->() const noexcept { return p_; }
    T** put() noexcept { reset(); return &p_; }
    void reset() noexcept { if (p_) { p_->Release(); p_ = nullptr; } }
private:
    T* p_{};
};
class Handle {
public:
    explicit Handle(HANDLE h = nullptr) noexcept : h_(h) {}
    ~Handle() { if (h_ && h_ != INVALID_HANDLE_VALUE) CloseHandle(h_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE get() const noexcept { return h_; }
private:
    HANDLE h_{};
};
class ComApartment {
public:
    ComApartment() : hr_(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {
        if (FAILED(hr_)) throw std::runtime_error("CoInitializeEx failed");
    }
    ~ComApartment() { if (SUCCEEDED(hr_)) CoUninitialize(); }
private:
    HRESULT hr_;
};
struct HResultError : std::runtime_error {
    HRESULT code;
    HResultError(HRESULT hr, const char* what) : std::runtime_error(what), code(hr) {}
};
inline void require(HRESULT hr, const char* operation) { if (FAILED(hr)) throw HResultError(hr, operation); }
inline std::string utf8(const std::wstring& s) {
    if (s.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), result.data(), count, nullptr, nullptr);
    return result;
}
inline std::string hresultText(HRESULT hr) {
    char code[16]{}; wsprintfA(code, "0x%08lX", static_cast<unsigned long>(hr));
    return code;
}
} // namespace airmic::win
