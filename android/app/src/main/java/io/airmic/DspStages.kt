// SPDX-License-Identifier: MIT
package io.airmic

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.pow
import kotlin.math.sin
import kotlin.math.sqrt

internal fun finiteSetting(value: Float, minimum: Float, maximum: Float, fallback: Float): Float =
    if (value.isFinite()) value.coerceIn(minimum, maximum) else fallback

internal class Biquad {
    private var b0 = 1.0; private var b1 = 0.0; private var b2 = 0.0
    private var a1 = 0.0; private var a2 = 0.0
    private var z1 = 0.0; private var z2 = 0.0
    fun set(b0: Double, b1: Double, b2: Double, a1: Double, a2: Double) {
        this.b0 = b0; this.b1 = b1; this.b2 = b2; this.a1 = a1; this.a2 = a2
    }
    fun reset() { z1 = 0.0; z2 = 0.0 }
    fun tick(input: Float): Float {
        val x = if (input.isFinite()) input else 0f
        val y = b0 * x + z1
        z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y
        if (abs(z1) < 1e-24) z1 = 0.0
        if (abs(z2) < 1e-24) z2 = 0.0
        return y.toFloat()
    }
}

internal class HighPassFilter {
    private val filter = Biquad()
    init { configure(80f) }
    fun configure(cutoffHz: Float) {
        val w = 2 * PI * finiteSetting(cutoffHz, 40f, 200f, 80f) / AudioProtocol.RATE
        val c = cos(w); val alpha = sin(w) / sqrt(2.0); val a0 = 1 + alpha
        filter.set((1 + c) / (2 * a0), -(1 + c) / a0, (1 + c) / (2 * a0), -2 * c / a0, (1 - alpha) / a0)
    }
    fun tick(x: Float) = filter.tick(x)
    fun reset() = filter.reset()
}

internal class VoiceEqualizer {
    private val low = Biquad(); private val mid = Biquad(); private val high = Biquad()
    init { configure(0f, 0f, 0f) }
    fun configure(lowDb: Float, midDb: Float, highDb: Float) {
        shelf(low, 180.0, finiteSetting(lowDb, -12f, 12f, 0f), false)
        peak(mid, 1500.0, .8, finiteSetting(midDb, -12f, 12f, 0f))
        shelf(high, 4500.0, finiteSetting(highDb, -12f, 12f, 0f), true)
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
                -2 * a * ((a - 1) + (a + 1) * c) / a0, a * ((a + 1) - (a - 1) * c - beta) / a0,
                2 * ((a - 1) - (a + 1) * c) / a0, ((a + 1) - (a - 1) * c - beta) / a0)
        } else {
            val a0 = (a + 1) + (a - 1) * c + beta
            filter.set(a * ((a + 1) - (a - 1) * c + beta) / a0,
                2 * a * ((a - 1) - (a + 1) * c) / a0, a * ((a + 1) - (a - 1) * c - beta) / a0,
                -2 * ((a - 1) + (a + 1) * c) / a0, ((a + 1) + (a - 1) * c - beta) / a0)
        }
    }
    fun tick(x: Float) = high.tick(mid.tick(low.tick(x)))
    fun reset() { low.reset(); mid.reset(); high.reset() }
}

// Feed-forward envelope compressor; hard knee, no automatic gain control or
// lookahead. Detector attack/release 5/100ms, gain smoothing 2/80ms, makeup +2dB.
internal class VoiceCompressor {
    private var threshold = 10.0.pow(-18.0 / 20.0).toFloat()
    private var exponent = 1f / 3 - 1
    private var detector = 0f; private var gain = 1f
    private val detectorAttack = exp(-1.0 / (.005 * AudioProtocol.RATE)).toFloat()
    private val detectorRelease = exp(-1.0 / (.100 * AudioProtocol.RATE)).toFloat()
    private val gainAttack = exp(-1.0 / (.002 * AudioProtocol.RATE)).toFloat()
    private val gainRelease = exp(-1.0 / (.080 * AudioProtocol.RATE)).toFloat()
    private val makeup = 10.0.pow(2.0 / 20.0).toFloat()
    fun configure(thresholdDb: Float, ratio: Float) {
        threshold = 10.0.pow(finiteSetting(thresholdDb, -36f, -6f, -18f) / 20.0).toFloat()
        exponent = 1f / finiteSetting(ratio, 1f, 10f, 3f) - 1
    }
    fun tick(x: Float): Float {
        val level = abs(x)
        val envelope = if (level > detector) detectorAttack else detectorRelease
        detector = envelope * detector + (1 - envelope) * level
        val desired = if (detector > threshold) (detector / threshold).pow(exponent) else 1f
        val smooth = if (desired < gain) gainAttack else gainRelease
        gain = smooth * gain + (1 - smooth) * desired
        return x * gain * makeup
    }
    fun reset() { detector = 0f; gain = 1f }
}

// Final zero-lookahead sample-peak safety limiter. It is deliberately not called
// a true-peak/loudness/lookahead limiter; abrupt overload can be audibly clipped.
internal class PeakLimiter {
    var ceiling = 10.0.pow(-1.0 / 20.0).toFloat()
        private set
    fun configure(ceilingDb: Float) { ceiling = 10.0.pow(finiteSetting(ceilingDb, -12f, -0.1f, -1f) / 20.0).toFloat() }
    fun tick(x: Float): Float = if (x.isFinite()) x.coerceIn(-ceiling, ceiling) else 0f
}
