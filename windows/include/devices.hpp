#pragma once
#include <string>
#include <vector>

namespace airmic::win {
struct DeviceInfo { std::wstring id, name; bool vbCable{}; };
// Caller owns a COM apartment; returns ACTIVE render endpoints only.
std::vector<DeviceInfo> enumerateRenderDevices();
} // namespace airmic::win
