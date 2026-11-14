// SPDX-License-Identifier: MIT
// AirMic's two endpoints. Only 48 kHz / mono / PCM16 is accepted in the kernel;
// shared-mode Windows Audio Engine converts consumer formats outside the driver.
#pragma once
#include "mintopo.h"

NTSTATUS CreateMiniportWaveRTSYSVAD(_Out_ PUNKNOWN*, _In_ REFCLSID, _In_opt_ PUNKNOWN,
    _In_ POOL_FLAGS, _In_ PUNKNOWN, _In_opt_ PVOID, _In_ PENDPOINT_MINIPAIR);
NTSTATUS CreateMiniportTopologySYSVAD(_Out_ PUNKNOWN*, _In_ REFCLSID, _In_opt_ PUNKNOWN,
    _In_ POOL_FLAGS, _In_ PUNKNOWN, _In_opt_ PVOID, _In_ PENDPOINT_MINIPAIR);
NTSTATUS PropertyHandler_AirMicRenderTopology(_In_ PPCPROPERTY_REQUEST request);
NTSTATUS PropertyHandler_AirMicCaptureTopology(_In_ PPCPROPERTY_REQUEST request);

static KSDATAFORMAT_WAVEFORMATEXTENSIBLE AirMicFormats[] = {
    {
        { sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE), 0, 0, 0,
          STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO), STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
          STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX) },
        { { WAVE_FORMAT_EXTENSIBLE, 1, 48000, 96000, 2, 16,
            sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX) },
          16, KSAUDIO_SPEAKER_MONO, STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM) }
    }
};
static MODE_AND_DEFAULT_FORMAT AirMicModes[] = {
    { STATIC_AUDIO_SIGNALPROCESSINGMODE_RAW, &AirMicFormats[0].DataFormat },
    { STATIC_AUDIO_SIGNALPROCESSINGMODE_DEFAULT, &AirMicFormats[0].DataFormat },
    { STATIC_AUDIO_SIGNALPROCESSINGMODE_COMMUNICATIONS, &AirMicFormats[0].DataFormat },
    { STATIC_AUDIO_SIGNALPROCESSINGMODE_SPEECH, &AirMicFormats[0].DataFormat },
};
static PIN_DEVICE_FORMATS_AND_MODES AirMicRenderFormats[] = {
    { SystemRenderPin, AirMicFormats, ARRAYSIZE(AirMicFormats), AirMicModes, ARRAYSIZE(AirMicModes) },
    { BridgePin, NULL, 0, NULL, 0 },
};
static PIN_DEVICE_FORMATS_AND_MODES AirMicCaptureFormats[] = {
    { BridgePin, NULL, 0, NULL, 0 },
    { SystemCapturePin, AirMicFormats, ARRAYSIZE(AirMicFormats), AirMicModes, ARRAYSIZE(AirMicModes) },
};
static KSDATARANGE_AUDIO AirMicStreamRange = {
    { sizeof(KSDATARANGE_AUDIO), KSDATARANGE_ATTRIBUTES, 0, 0,
      STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO), STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
      STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX) },
    1, 16, 16, 48000, 48000
};
static PKSDATARANGE AirMicStreamRanges[] = {
    PKSDATARANGE(&AirMicStreamRange), PKSDATARANGE(&PinDataRangeAttributeList)
};
static KSDATARANGE AirMicAnalogRange = {
    sizeof(KSDATARANGE), 0, 0, 0, STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
    STATICGUIDOF(KSDATAFORMAT_SUBTYPE_ANALOG), STATICGUIDOF(KSDATAFORMAT_SPECIFIER_NONE)
};
static PKSDATARANGE AirMicAnalogRanges[] = { &AirMicAnalogRange };

// No fake hardware volume/mute/DSP nodes: Windows applies its software controls.
static PCPIN_DESCRIPTOR AirMicRenderWavePins[] = {
    { 1, 1, 0, NULL, { 0, NULL, 0, NULL, ARRAYSIZE(AirMicStreamRanges), AirMicStreamRanges,
      KSPIN_DATAFLOW_IN, KSPIN_COMMUNICATION_SINK, &KSCATEGORY_AUDIO, NULL, 0 } },
    { 0, 0, 0, NULL, { 0, NULL, 0, NULL, ARRAYSIZE(AirMicAnalogRanges), AirMicAnalogRanges,
      KSPIN_DATAFLOW_OUT, KSPIN_COMMUNICATION_NONE, &KSCATEGORY_AUDIO, NULL, 0 } },
};
static PCPIN_DESCRIPTOR AirMicCaptureWavePins[] = {
    { 0, 0, 0, NULL, { 0, NULL, 0, NULL, ARRAYSIZE(AirMicAnalogRanges), AirMicAnalogRanges,
      KSPIN_DATAFLOW_IN, KSPIN_COMMUNICATION_NONE, &KSCATEGORY_AUDIO, NULL, 0 } },
    { 5, 5, 0, NULL, { 0, NULL, 0, NULL, ARRAYSIZE(AirMicStreamRanges), AirMicStreamRanges,
      KSPIN_DATAFLOW_OUT, KSPIN_COMMUNICATION_SINK, &KSCATEGORY_AUDIO, &KSAUDFNAME_RECORDING_CONTROL, 0 } },
};
static PCCONNECTION_DESCRIPTOR AirMicWaveConnections[] = { { PCFILTER_NODE, 0, PCFILTER_NODE, 1 } };
static PCPROPERTY_ITEM AirMicWaveProperties[] = {
    { &KSPROPSETID_General, KSPROPERTY_GENERAL_COMPONENTID, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_WaveFilter },
    { &KSPROPSETID_Pin, KSPROPERTY_PIN_PROPOSEDATAFORMAT, KSPROPERTY_TYPE_SET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_WaveFilter },
    { &KSPROPSETID_Pin, KSPROPERTY_PIN_PROPOSEDATAFORMAT2, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_WaveFilter },
};
DEFINE_PCAUTOMATION_TABLE_PROP(AirMicWaveAutomation, AirMicWaveProperties);
static PCFILTER_DESCRIPTOR AirMicRenderWave = {
    0, &AirMicWaveAutomation, sizeof(PCPIN_DESCRIPTOR), ARRAYSIZE(AirMicRenderWavePins), AirMicRenderWavePins,
    sizeof(PCNODE_DESCRIPTOR), 0, NULL, ARRAYSIZE(AirMicWaveConnections), AirMicWaveConnections, 0, NULL
};
static PCFILTER_DESCRIPTOR AirMicCaptureWave = {
    0, &AirMicWaveAutomation, sizeof(PCPIN_DESCRIPTOR), ARRAYSIZE(AirMicCaptureWavePins), AirMicCaptureWavePins,
    sizeof(PCNODE_DESCRIPTOR), 0, NULL, ARRAYSIZE(AirMicWaveConnections), AirMicWaveConnections, 0, NULL
};

static PCPIN_DESCRIPTOR AirMicRenderTopologyPins[] = {
    { 0, 0, 0, NULL, { 0, NULL, 0, NULL, ARRAYSIZE(AirMicAnalogRanges), AirMicAnalogRanges,
      KSPIN_DATAFLOW_IN, KSPIN_COMMUNICATION_NONE, &KSCATEGORY_AUDIO, NULL, 0 } },
    { 0, 0, 0, NULL, { 0, NULL, 0, NULL, ARRAYSIZE(AirMicAnalogRanges), AirMicAnalogRanges,
      KSPIN_DATAFLOW_OUT, KSPIN_COMMUNICATION_NONE, &KSNODETYPE_SPEAKER, NULL, 0 } },
};
static PCPIN_DESCRIPTOR AirMicCaptureTopologyPins[] = {
    { 0, 0, 0, NULL, { 0, NULL, 0, NULL, ARRAYSIZE(AirMicAnalogRanges), AirMicAnalogRanges,
      KSPIN_DATAFLOW_IN, KSPIN_COMMUNICATION_NONE, &KSNODETYPE_MICROPHONE, NULL, 0 } },
    { 0, 0, 0, NULL, { 0, NULL, 0, NULL, ARRAYSIZE(AirMicAnalogRanges), AirMicAnalogRanges,
      KSPIN_DATAFLOW_OUT, KSPIN_COMMUNICATION_NONE, &KSCATEGORY_AUDIO, NULL, 0 } },
};
static PCPROPERTY_ITEM AirMicRenderTopologyProperties[] = {
    { &KSPROPSETID_Jack, KSPROPERTY_JACK_DESCRIPTION, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_AirMicRenderTopology },
    { &KSPROPSETID_Jack, KSPROPERTY_JACK_DESCRIPTION2, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_AirMicRenderTopology },
};
static PCPROPERTY_ITEM AirMicCaptureTopologyProperties[] = {
    { &KSPROPSETID_Jack, KSPROPERTY_JACK_DESCRIPTION, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_AirMicCaptureTopology },
    { &KSPROPSETID_Jack, KSPROPERTY_JACK_DESCRIPTION2, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_AirMicCaptureTopology },
};
DEFINE_PCAUTOMATION_TABLE_PROP(AirMicRenderTopologyAutomation, AirMicRenderTopologyProperties);
DEFINE_PCAUTOMATION_TABLE_PROP(AirMicCaptureTopologyAutomation, AirMicCaptureTopologyProperties);
static PCFILTER_DESCRIPTOR AirMicRenderTopology = {
    0, &AirMicRenderTopologyAutomation, sizeof(PCPIN_DESCRIPTOR), ARRAYSIZE(AirMicRenderTopologyPins), AirMicRenderTopologyPins,
    sizeof(PCNODE_DESCRIPTOR), 0, NULL, ARRAYSIZE(AirMicWaveConnections), AirMicWaveConnections, 0, NULL
};
static PCFILTER_DESCRIPTOR AirMicCaptureTopology = {
    0, &AirMicCaptureTopologyAutomation, sizeof(PCPIN_DESCRIPTOR), ARRAYSIZE(AirMicCaptureTopologyPins), AirMicCaptureTopologyPins,
    sizeof(PCNODE_DESCRIPTOR), 0, NULL, ARRAYSIZE(AirMicWaveConnections), AirMicWaveConnections, 0, NULL
};

static KSAUDIO_PACKETSIZE_CONSTRAINTS2 AirMicPacketConstraints = {
    2 * HNSTIME_PER_MILLISECOND, FILE_BYTE_ALIGNMENT, 19200, 1,
    { { STATIC_AUDIO_SIGNALPROCESSINGMODE_DEFAULT, 480, 0 } }
};
static const SYSVAD_DEVPROPERTY AirMicWaveInterfaceProperties[] = {
    { &DEVPKEY_KsAudio_PacketSize_Constraints2, DEVPROP_TYPE_BINARY,
      sizeof(AirMicPacketConstraints), &AirMicPacketConstraints }
};
static PHYSICALCONNECTIONTABLE AirMicRenderConnection[] = { { 0, 1, CONNECTIONTYPE_WAVE_OUTPUT } };
static PHYSICALCONNECTIONTABLE AirMicCaptureConnection[] = { { 1, 0, CONNECTIONTYPE_TOPOLOGY_OUTPUT } };
static ENDPOINT_MINIPAIR AirMicRenderPair = {
    eSpeakerDevice, L"TopologyAirMicInput", NULL, CreateMiniportTopologySYSVAD, &AirMicRenderTopology, 0, NULL,
    L"WaveAirMicInput", NULL, CreateMiniportWaveRTSYSVAD, &AirMicRenderWave,
    ARRAYSIZE(AirMicWaveInterfaceProperties), AirMicWaveInterfaceProperties,
    1, AirMicRenderFormats, ARRAYSIZE(AirMicRenderFormats), AirMicRenderConnection, ARRAYSIZE(AirMicRenderConnection),
    ENDPOINT_NO_FLAGS, NULL, 0, NULL
};
static ENDPOINT_MINIPAIR AirMicCapturePair = {
    eMicInDevice, L"TopologyAirMicMicrophone", NULL, CreateMiniportTopologySYSVAD, &AirMicCaptureTopology, 0, NULL,
    L"WaveAirMicMicrophone", NULL, CreateMiniportWaveRTSYSVAD, &AirMicCaptureWave,
    ARRAYSIZE(AirMicWaveInterfaceProperties), AirMicWaveInterfaceProperties,
    1, AirMicCaptureFormats, ARRAYSIZE(AirMicCaptureFormats), AirMicCaptureConnection, ARRAYSIZE(AirMicCaptureConnection),
    ENDPOINT_NO_FLAGS, NULL, 0, NULL
};
static PENDPOINT_MINIPAIR g_RenderEndpoints[] = { &AirMicRenderPair };
static PENDPOINT_MINIPAIR g_CaptureEndpoints[] = { &AirMicCapturePair };
#define g_cRenderEndpoints ARRAYSIZE(g_RenderEndpoints)
#define g_cCaptureEndpoints ARRAYSIZE(g_CaptureEndpoints)
#define g_MaxMiniports ((g_cRenderEndpoints + g_cCaptureEndpoints) * 2)
