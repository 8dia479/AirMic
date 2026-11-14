#include "win_util.hpp"
#include "devices.hpp"

#include <mmdeviceapi.h>
#include <initguid.h> // Instantiate PROPERTYKEYs in this translation unit only.
#include <functiondiscoverykeys_devpkey.h>
#include <propvarutil.h>
#include <algorithm>
#include <cwctype>

namespace airmic::win {
std::vector<DeviceInfo> enumerateRenderDevices() {
    ComPtr<IMMDeviceEnumerator> enumerator;
    require(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(enumerator.put())), "Create device enumerator");
    ComPtr<IMMDeviceCollection> collection;
    require(enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, collection.put()), "Enumerate render endpoints");
    UINT count = 0; require(collection->GetCount(&count), "Get endpoint count");
    std::vector<DeviceInfo> devices;
    devices.reserve(count);
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> device;
        require(collection->Item(i, device.put()), "Get endpoint");
        LPWSTR id = nullptr;
        require(device->GetId(&id), "Get endpoint ID");
        DeviceInfo info;
        info.id = id; CoTaskMemFree(id);
        ComPtr<IPropertyStore> properties;
        require(device->OpenPropertyStore(STGM_READ, properties.put()), "Open endpoint properties");
        PROPVARIANT value; PropVariantInit(&value);
        const auto hr = properties->GetValue(PKEY_Device_FriendlyName, &value);
        if (SUCCEEDED(hr) && value.vt == VT_LPWSTR && value.pwszVal) info.name = value.pwszVal;
        PropVariantClear(&value);
        if (info.name.empty()) info.name = info.id;
        auto lower = info.name;
        std::transform(lower.begin(), lower.end(), lower.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
        info.vbCable = lower.find(L"cable input") != std::wstring::npos && lower.find(L"vb-audio") != std::wstring::npos;
        devices.push_back(std::move(info));
    }
    return devices;
}
} // namespace airmic::win
