// SPDX-License-Identifier: MIT
package io.airmic

import kotlin.math.pow

internal data class DspConfig(
    val enabled: Boolean = true,
    val noiseSuppression: Boolean = true,
    val noiseReductionDb: Float = 12f,
    val gain: Float = 0f, val low: Float = 0f, val mid: Float = 0f, val high: Float = 0f,
    val highPassHz: Float = 80f,
    val compressorThresholdDb: Float = -18f, val compressorRatio: Float = 3f,
    val limiterCeilingDb: Float = -1f,
)

/**
 * Production Kotlin voice path, owned exclusively by the DSP/network worker:
 * input trim -> HIGH-PASS -> NOISE SUPPRESSION -> EQ -> COMPRESSOR -> LIMITER.
 * De-reverb is a separate feature design, not a placeholder or a hidden NS mode.
 * configure() is applied at a 480-sample boundary; no state allocation/reset or
 * latency change on an ordinary setting update. reset() is for stream gaps only.
 */
internal class DspProcessor {
    companion object { const val LATENCY_SAMPLES = NoiseSuppressor.LATENCY_SAMPLES }
    private val highPass = HighPassFilter()
    private val noiseSuppressor = NoiseSuppressor()
    private val equalizer = VoiceEqualizer()
    private val compressor = VoiceCompressor()
    private val limiter = PeakLimiter()
    private val dryHistory = FloatArray(AudioProtocol.SAMPLES)
    private val delayedDry = FloatArray(AudioProtocol.SAMPLES)
    private var targetTrim = 1f
    private var trim = 1f
    private var targetMix = 1f
    private var mix = 1f
    init { configure(DspConfig()) }

    fun configure(value: DspConfig) {
        targetMix = if (value.enabled) 1f else 0f
        targetTrim = 10.0.pow(finiteSetting(value.gain, -24f, 24f, 0f) / 20.0).toFloat()
        highPass.configure(value.highPassHz)
        noiseSuppressor.configure(value.noiseSuppression, value.noiseReductionDb)
        equalizer.configure(value.low, value.mid, value.high)
        compressor.configure(value.compressorThresholdDb, value.compressorRatio)
        limiter.configure(value.limiterCeilingDb)
    }
    fun reset() {
        highPass.reset(); noiseSuppressor.reset(); equalizer.reset(); compressor.reset()
        dryHistory.fill(0f); delayedDry.fill(0f); mix = targetMix; trim = targetTrim
    }
    fun process(samples: FloatArray) {
        require(samples.size == AudioProtocol.SAMPLES)
        val trimStep = (targetTrim - trim) / AudioProtocol.SAMPLES
        for (i in samples.indices) {
            val x = if (samples[i].isFinite()) samples[i].coerceIn(-8f, 8f) else 0f
            delayedDry[i] = dryHistory[i]; dryHistory[i] = x
            trim += trimStep
            samples[i] = highPass.tick(x * trim)
        }
        trim = targetTrim
        noiseSuppressor.process(samples) // Always warm; OFF uses the same aligned dry path.
        val mixStep = (targetMix - mix) / AudioProtocol.SAMPLES
        for (i in samples.indices) {
            val enhanced = compressor.tick(equalizer.tick(samples[i]))
            mix += mixStep
            val x = if (mix <= 0f) delayedDry[i] else delayedDry[i] * (1f - mix) + enhanced * mix
            samples[i] = limiter.tick(x) // Last in every route, including global DSP bypass.
        }
        mix = targetMix
    }
}

// Keep delayed content, wire timestamps and discontinuities consistent. A gap
// discards every DSP tail rather than publishing speech from before that gap.
// The first hop is silence; at stop the final buffered hop is intentionally dropped.
internal class DspFrameProcessor {
    private val dsp = DspProcessor()
    private var started = false
    private var expectedSequence = 0
    fun configure(value: DspConfig) = dsp.configure(value)
    fun process(frame: CaptureFrame) {
        require(frame.timestamp >= DspProcessor.LATENCY_SAMPLES)
        if (!started || frame.sequence != expectedSequence || (frame.flags and AudioProtocol.DISCONTINUITY) != 0) {
            dsp.reset()
            if (started) frame.flags = frame.flags or AudioProtocol.DISCONTINUITY
        }
        started = true; expectedSequence = frame.sequence + 1 // Int wrap is the wire's uint32 wrap.
        dsp.process(frame.samples)
        frame.timestamp -= DspProcessor.LATENCY_SAMPLES
    }
}
