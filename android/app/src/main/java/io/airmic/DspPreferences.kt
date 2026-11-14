package io.airmic

import android.content.SharedPreferences

// Loaded only by the serialized control executor. Old "gate" preferences are
// deliberately NOT treated as consent/settings for the different spectral NS.
internal data class DspPreferences(val configuration: DspConfig) {
    fun applyToEngine(): Boolean = AudioEngine.configure(configuration)
    companion object {
        fun load(p: SharedPreferences) = DspPreferences(DspConfig(
            enabled = p.getBoolean("dsp", true),
            noiseSuppression = p.getBoolean("ns", true),
            noiseReductionDb = p.getFloat("ns_reduction", 12f),
            gain = p.getFloat("gain", 0f), low = p.getFloat("low", 0f),
            mid = p.getFloat("mid", 0f), high = p.getFloat("high", 0f),
            highPassHz = p.getFloat("hpf_hz", 80f),
            compressorThresholdDb = p.getFloat("compressor_threshold", -18f),
            compressorRatio = p.getFloat("compressor_ratio", 3f),
            limiterCeilingDb = p.getFloat("limiter_ceiling", -1f),
        ))
    }
}
