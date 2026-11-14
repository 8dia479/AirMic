// SPDX-License-Identifier: MIT
#include <sysvad.h>
#include "simple.h"
#include "mintopo.h"

#pragma code_seg("PAGE")
namespace {
NTSTATUS HandleJack(_In_ PPCPROPERTY_REQUEST request, bool capture) {
    PAGED_CODE();
    if (!request || !request->PropertyItem || !request->MajorTarget) return STATUS_INVALID_PARAMETER;
    if (!IsEqualGUIDAligned(*request->PropertyItem->Set, KSPROPSETID_Jack)) return STATUS_INVALID_DEVICE_REQUEST;
    KSJACK_DESCRIPTION jack = {
        KSAUDIO_SPEAKER_MONO, 0, eConnTypeUnknown, eGeoLocNotApplicable,
        eGenLocOther, ePortConnUnknown, TRUE
    };
    PKSJACK_DESCRIPTION renderJacks[] = { NULL, &jack };
    PKSJACK_DESCRIPTION captureJacks[] = { &jack, NULL };
    auto miniport = static_cast<CMiniportTopology*>(reinterpret_cast<PMINIPORTTOPOLOGY>(request->MajorTarget));
    PKSJACK_DESCRIPTION* jacks = capture ? captureJacks : renderJacks;
    if (request->PropertyItem->Id == KSPROPERTY_JACK_DESCRIPTION)
        return miniport->PropertyHandlerJackDescription(request, 2, jacks);
    if (request->PropertyItem->Id == KSPROPERTY_JACK_DESCRIPTION2)
        return miniport->PropertyHandlerJackDescription2(request, 2, jacks, 0);
    return STATUS_INVALID_DEVICE_REQUEST;
}
}
NTSTATUS PropertyHandler_AirMicRenderTopology(_In_ PPCPROPERTY_REQUEST request) { return HandleJack(request, false); }
NTSTATUS PropertyHandler_AirMicCaptureTopology(_In_ PPCPROPERTY_REQUEST request) { return HandleJack(request, true); }
#pragma code_seg()
