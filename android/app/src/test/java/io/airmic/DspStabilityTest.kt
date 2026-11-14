package io.airmic

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs
import kotlin.math.sin

class DspStabilityTest {
    @Test
    fun noiseSuppressorBypassKeepsExactlyOneHopOfAlignment() {
        val suppressor = NoiseSuppressor()
        suppressor.configure(enabled = false, reductionDb = 12f)
        val first = FloatArray(AudioProtocol.SAMPLES) { (it - 240) / 480f }
        val second = FloatArray(AudioProtocol.SAMPLES) { sin(it * 0.071).toFloat() * 0.2f }

        suppressor.process(first)
        assertTrue("first hop must be silence while the aligned path warms", first.all { it == 0f })

        suppressor.process(second)
        for (i in second.indices) {
            assertEquals("bypass must return the previous input hop at sample $i",
                (i - 240) / 480f, second[i], 0f)
        }
    }

    @Test
    fun fullDspBypassKeepsAlignmentAndFinalSafetyLimiter() {
        val dsp = DspProcessor()
        dsp.configure(DspConfig(enabled = false, noiseSuppression = false, limiterCeilingDb = -1f))
        val first = FloatArray(AudioProtocol.SAMPLES) { 0.25f }
        val second = FloatArray(AudioProtocol.SAMPLES) { -0.25f }

        dsp.process(first)
        assertTrue(first.all { it == 0f })
        dsp.process(second)
        for (sample in second) assertEquals(0.25f, sample, 1e-6f)

        val loud = FloatArray(AudioProtocol.SAMPLES) { if (it % 2 == 0) 4f else -4f }
        dsp.process(loud)
        val afterLoud = FloatArray(AudioProtocol.SAMPLES)
        dsp.process(afterLoud)
        assertTrue(afterLoud.all { it.isFinite() && abs(it) <= 0.892f })
        assertTrue("the delayed loud hop must actually exercise the limiter",
            afterLoud.any { abs(it) >= 0.88f })
    }

    @Test
    fun pathologicalSamplesNeverEscapeFiniteBoundedOutput() {
        val dsp = DspProcessor()
        dsp.configure(DspConfig(
            enabled = true,
            noiseSuppression = true,
            noiseReductionDb = 24f,
            gain = 24f,
            low = 12f,
            mid = 12f,
            high = 12f,
            highPassHz = 200f,
            compressorThresholdDb = -36f,
            compressorRatio = 10f,
            limiterCeilingDb = -1f,
        ))
        val frame = FloatArray(AudioProtocol.SAMPLES)
        val pathological = floatArrayOf(Float.NaN, Float.POSITIVE_INFINITY, Float.NEGATIVE_INFINITY, 8f, -8f)
        repeat(250) { block ->
            for (i in frame.indices) {
                frame[i] = if (i < pathological.size && block % 2 == 0) pathological[i]
                    else sin((block * frame.size + i) * 0.037).toFloat() * 8f
            }
            dsp.process(frame)
            assertTrue("output must remain finite and below the safety ceiling",
                frame.all { it.isFinite() && abs(it) <= 0.892f })
        }
    }

    @Test
    fun resetDropsOldSignalHistory() {
        val dsp = DspProcessor()
        dsp.configure(DspConfig(enabled = false, noiseSuppression = false))
        val old = FloatArray(AudioProtocol.SAMPLES) { 0.6f }
        dsp.process(old)

        dsp.reset()
        val fresh = FloatArray(AudioProtocol.SAMPLES) { -0.3f }
        dsp.process(fresh)
        assertTrue("first hop after reset must not contain the previous stream",
            fresh.all { abs(it) < 1e-7f })
    }
}
