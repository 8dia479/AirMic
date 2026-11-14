// SPDX-License-Identifier: MIT
package io.airmic

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.sqrt

/**
 * Stationary-noise suppression, NOT a gate: estimate a separate noise PSD and
 * Wiener gain in each FFT bin, including while speech is present. No ML model,
 * Android effect, WebRTC/RNNoise, AEC or de-reverberation is hidden behind this API.
 *
 * 960-sample sqrt-Hann WOLA, 480-sample hop, zero-padded to a 1024-point FFT.
 * Both wet and dry paths have the same one-hop (10ms) alignment delay. All state
 * is allocated at construction. One worker owns configure/process/reset.
 */
internal class NoiseSuppressor {
    companion object {
        const val HOP = AudioProtocol.SAMPLES
        const val WINDOW = HOP * 2
        const val FFT_SIZE = 1024
        const val LATENCY_SAMPLES = HOP
        private const val BINS = FFT_SIZE / 2 + 1
        private const val BUCKETS = 8
        private const val BUCKET_FRAMES = 10 // 8 x 100ms minimum-statistics history.
        private const val EPSILON = 1e-12
    }
    private val fft = Radix2Fft(FFT_SIZE)
    private val window = DoubleArray(WINDOW) { sqrt(0.5 - 0.5 * cos(2 * PI * it / WINDOW)) }
    private val history = FloatArray(WINDOW)
    private val overlap = DoubleArray(HOP)
    private val real = DoubleArray(FFT_SIZE)
    private val imaginary = DoubleArray(FFT_SIZE)
    private val power = DoubleArray(BINS)
    private val smoothed = DoubleArray(BINS)
    private val noise = DoubleArray(BINS)
    private val previousClean = DoubleArray(BINS)
    private val gains = DoubleArray(BINS) { 1.0 }
    private val desiredGains = DoubleArray(BINS)
    private val minima = Array(BUCKETS) { DoubleArray(BINS) { Double.POSITIVE_INFINITY } }
    private var bucket = 0
    private var bucketFrame = 0
    private var primed = false
    private var minimumGain = 10.0.pow(-12.0 / 20.0)
    private var targetMix = 1.0
    private var mix = 1.0

    fun configure(enabled: Boolean, reductionDb: Float) {
        targetMix = if (enabled) 1.0 else 0.0
        val db = if (reductionDb.isFinite()) reductionDb.coerceIn(0f, 24f) else 12f
        minimumGain = 10.0.pow(-db / 20.0)
    }
    fun reset() {
        history.fill(0f); overlap.fill(0.0); real.fill(0.0); imaginary.fill(0.0)
        power.fill(0.0); smoothed.fill(0.0); noise.fill(0.0); previousClean.fill(0.0)
        gains.fill(1.0); desiredGains.fill(1.0)
        for (i in minima.indices) minima[i].fill(Double.POSITIVE_INFINITY)
        bucket = 0; bucketFrame = 0; primed = false; mix = targetMix
    }
    fun process(samples: FloatArray) {
        require(samples.size == HOP)
        // First half is the delayed dry block, second half is current input.
        for (i in 0 until HOP) history[i + HOP] = if (samples[i].isFinite()) samples[i].coerceIn(-128f, 128f) else 0f
        for (i in 0 until WINDOW) { real[i] = history[i] * window[i]; imaginary[i] = 0.0 }
        for (i in WINDOW until FFT_SIZE) { real[i] = 0.0; imaginary[i] = 0.0 }
        fft.transform(real, imaginary)
        for (k in 0 until BINS) {
            // A white-noise input with variance v has expected power v per bin.
            power[k] = (real[k] * real[k] + imaginary[k] * imaginary[k]) / HOP
            smoothed[k] = if (primed) 0.8 * smoothed[k] + 0.2 * power[k] else power[k]
            minima[bucket][k] = min(minima[bucket][k], smoothed[k])
        }
        for (k in 0 until BINS) {
            var minimum = smoothed[k]
            for (b in 0 until BUCKETS) minimum = min(minimum, minima[b][k])
            // Preserve coherent narrow-band components, including speech starting
            // at session open. This conservative rule can also preserve tonal fan
            // noise; this is not a general speech classifier or another-speaker filter.
            var neighbours = 0.0; var count = 0
            for (j in max(0, k - 6)..min(BINS - 1, k + 6)) {
                if (j < k - 2 || j > k + 2) { neighbours += smoothed[j]; count++ }
            }
            val background = max(EPSILON, neighbours / max(1, count))
            val tonal = smoothed[k] > 4.0 * background
            if (!primed) noise[k] = max(EPSILON, min(power[k], background))
            val speechRise = smoothed[k] > 2.5 * max(EPSILON, minimum) && smoothed[k] > 1.8 * noise[k]
            // Noise-like bins need a symmetric update: making every downward
            // fluctuation faster than upward ones systematically underestimates
            // the PSD and leaves stationary noise unsuppressed. Only a coherent
            // component or an actual rise above BOTH floor estimates slows growth.
            val adaptation = when {
                power[k] > noise[k] && tonal -> 0.0002
                power[k] > noise[k] && speechRise -> 0.002
                else -> 0.06
            }
            noise[k] = max(EPSILON, noise[k] + adaptation * (power[k] - noise[k]))
            val posterior = power[k] / noise[k]
            val prior = 0.96 * previousClean[k] / noise[k] + 0.04 * max(0.0, posterior - 1.0)
            // Wiener AMPLITUDE gain, not its square root, which would leave
            // too much noise in bins with near-unity a-posteriori SNR.
            desiredGains[k] = max(minimumGain, prior / (1.0 + prior)).coerceIn(0.0, 1.0)
        }
        for (k in 0 until BINS) {
            // Frequency and asymmetric time smoothing limit musical-noise/pumping.
            val target = (desiredGains[max(0, k - 1)] + 2 * desiredGains[k] + desiredGains[min(BINS - 1, k + 1)]) / 4
            gains[k] += (if (target > gains[k]) 0.7 else 0.15) * (target - gains[k])
            gains[k] = gains[k].coerceIn(minimumGain, 1.0)
            previousClean[k] = power[k] * gains[k] * gains[k]
            real[k] *= gains[k]; imaginary[k] *= gains[k]
            if (k in 1 until FFT_SIZE / 2) {
                real[FFT_SIZE - k] = real[k]; imaginary[FFT_SIZE - k] = -imaginary[k]
            }
        }
        fft.transform(real, imaginary, inverse = true)
        val step = (targetMix - mix) / HOP
        for (i in 0 until HOP) {
            val wet = real[i] * window[i] + overlap[i]
            overlap[i] = real[i + HOP] * window[i + HOP]
            mix += step
            // OFF is a bit-exact delayed dry path, not a different-latency path.
            val output = if (mix <= 0.0) history[i].toDouble() else history[i] * (1.0 - mix) + wet * mix
            samples[i] = if (!primed || !output.isFinite()) 0f else output.toFloat()
        }
        mix = targetMix
        history.copyInto(history, 0, HOP, WINDOW)
        primed = true
        bucketFrame++
        if (bucketFrame == BUCKET_FRAMES) {
            bucket = (bucket + 1) % BUCKETS; minima[bucket].fill(Double.POSITIVE_INFINITY); bucketFrame = 0
        }
    }
}
