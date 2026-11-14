package io.airmic

import kotlin.math.*

internal data class DspConfig(
    val enabled: Boolean = true, val gate: Boolean = false,
    val gain: Float = 0f, val low: Float = 0f, val mid: Float = 0f, val high: Float = 0f,
)

// Persistent pure-Kotlin DSP. A basic gate, NOT WebRTC/RNNoise noise suppression.
internal class DspProcessor {
    private class Biquad {
        var b0 = 1.0; var b1 = 0.0; var b2 = 0.0; var a1 = 0.0; var a2 = 0.0
        var z1 = 0.0; var z2 = 0.0
        fun set(b0: Double, b1: Double, b2: Double, a1: Double, a2: Double) {
            this.b0 = b0; this.b1 = b1; this.b2 = b2; this.a1 = a1; this.a2 = a2
        }
        fun clear() { z1 = 0.0; z2 = 0.0 }
        fun tick(x: Float): Float {
            val y = b0 * x + z1
            z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y
            if (abs(z1) < 1e-24) z1 = 0.0
            if (abs(z2) < 1e-24) z2 = 0.0
            return y.toFloat()
        }
    }
    private val hp = Biquad(); private val low = Biquad(); private val mid = Biquad(); private val high = Biquad()
    private var config = DspConfig()
    private var gain = 1f
    private var detector = 0f; private var compression = 1f; private var gateGain = 1f; private var noisePower = .000001f
    private val ceiling = 10.0.pow(-1.0 / 20).toFloat()
    private val makeup = 10.0.pow(2.0 / 20).toFloat()
    init { configure(config) }
    private fun valid(x: Float, low: Float, high: Float) = if (x.isFinite()) x.coerceIn(low, high) else 0f
    fun configure(value: DspConfig) {
        if (value.enabled != config.enabled) reset()
        config = value
        gain = 10.0.pow(valid(value.gain, -24f, 24f) / 20.0).toFloat()
        val w = 2 * PI * 80 / AudioProtocol.RATE
        val c = cos(w); val alpha = sin(w) / sqrt(2.0); val a0 = 1 + alpha
        hp.set((1 + c) / (2 * a0), -(1 + c) / a0, (1 + c) / (2 * a0), -2 * c / a0, (1 - alpha) / a0)
        shelf(low, 180.0, valid(value.low, -12f, 12f), false)
        peak(mid, 1500.0, .8, valid(value.mid, -12f, 12f))
        shelf(high, 4500.0, valid(value.high, -12f, 12f), true)
    }
    private fun peak(filter: Biquad, hz: Double, q: Double, db: Float) {
        val a = 10.0.pow(db / 40.0); val w = 2 * PI * hz / AudioProtocol.RATE
        val c = cos(w); val alpha = sin(w) / (2 * q); val a0 = 1 + alpha / a
        filter.set((1 + alpha * a) / a0, -2 * c / a0, (1 - alpha * a) / a0, -2 * c / a0, (1 - alpha / a) / a0)
    }
    private fun shelf(filter: Biquad, hz: Double, db: Float, high: Boolean) {
        val a = 10.0.pow(db / 40.0); val w = 2 * PI * hz / AudioProtocol.RATE
        val c = cos(w); val beta = sqrt(2 * a) * sin(w)
        if (high) {
            val a0 = (a + 1) - (a - 1) * c + beta
            filter.set(a * ((a + 1) + (a - 1) * c + beta) / a0,
                -2 * a * ((a - 1) + (a + 1) * c) / a0, a * ((a + 1) + (a - 1) * c - beta) / a0,
                2 * ((a - 1) - (a + 1) * c) / a0, ((a + 1) - (a - 1) * c - beta) / a0)
        } else {
            val a0 = (a + 1) + (a - 1) * c + beta
            filter.set(a * ((a + 1) - (a - 1) * c + beta) / a0,
                2 * a * ((a - 1) - (a + 1) * c) / a0, a * ((a + 1) - (a - 1) * c - beta) / a0,
                -2 * ((a - 1) + (a + 1) * c) / a0, ((a + 1) + (a - 1) * c - beta) / a0)
        }
    }
    fun reset() {
        hp.clear(); low.clear(); mid.clear(); high.clear()
        detector = 0f; compression = 1f; gateGain = 1f; noisePower = .000001f
    }
    fun process(samples: FloatArray) {
        require(samples.size == AudioProtocol.SAMPLES)
        var power = 0f
        for (i in samples.indices) {
            var x = if (samples[i].isFinite()) samples[i].coerceIn(-8f, 8f) else 0f
            if (config.enabled) x = hp.tick(x * gain)
            samples[i] = x; power += x * x
        }
        if (!config.enabled) return
        power /= AudioProtocol.SAMPLES
        noisePower += (if (power < noisePower) .1f else .0005f) * (power - noisePower)
        val threshold = max(.000004f, noisePower * 3)
        val gateTarget = if (config.gate) (power / threshold).coerceIn(.04f, 1f) else 1f
        val thresholdLinear = .12589254f
        for (i in samples.indices) {
            gateGain += (if (gateTarget > gateGain) .01f else .0005f) * (gateTarget - gateGain)
            var x = high.tick(mid.tick(low.tick(samples[i] * gateGain)))
            val level = abs(x)
            val coefficient = if (level > detector) .995842f else .999792f
            detector = coefficient * detector + (1 - coefficient) * level
            val desired = if (detector > thresholdLinear) (detector / thresholdLinear).pow(1f / 3 - 1) else 1f
            val smooth = if (desired < compression) .989637f else .999740f
            compression = smooth * compression + (1 - smooth) * desired
            x *= compression * makeup
            samples[i] = if (x.isFinite()) x.coerceIn(-ceiling, ceiling) else 0f
        }
    }
}
