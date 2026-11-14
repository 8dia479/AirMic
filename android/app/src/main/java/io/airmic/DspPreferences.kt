package io.airmic

import android.content.SharedPreferences

internal data class DspPreferences(
    val enabled: Boolean = true,
    val gate: Boolean = false,
    val gain: Float = 0f,
    val low: Float = 0f,
    val mid: Float = 0f,
    val high: Float = 0f,
) {
    fun applyToEngine(): Boolean = NativeBridge.configure(enabled, gate, gain, low, mid, high)
    companion object {
        fun load(p: SharedPreferences) = DspPreferences(
            p.getBoolean("dsp", true), p.getBoolean("gate", false), p.getFloat("gain", 0f),
            p.getFloat("low", 0f), p.getFloat("mid", 0f), p.getFloat("high", 0f),
        )
    }
}
