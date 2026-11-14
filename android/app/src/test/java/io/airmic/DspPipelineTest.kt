package io.airmic

import java.util.Random
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.pow
import kotlin.math.sin
import org.junit.Assert.*
import org.junit.Test

class DspPipelineTest {
    private fun db(ratio: Double) = 10 * log10(ratio.coerceAtLeast(1e-30))
    private fun tone(sample: Int) = (0.12 * sin(2 * PI * 187.5 * sample / AudioProtocol.RATE)
        + 0.06 * sin(2 * PI * 937.5 * sample / AudioProtocol.RATE)
        + 0.035 * sin(2 * PI * 2250 * sample / AudioProtocol.RATE)).toFloat()

    @Test fun fftRoundTripAndBinLocation() {
        val fft = Radix2Fft(1024)
        val source = DoubleArray(1024) { 0.4 * sin(2 * PI * 17 * it / 1024) + if (it == 123) 0.3 else 0.0 }
        val real = source.copyOf(); val imaginary = DoubleArray(1024)
        fft.transform(real, imaginary)
        assertEquals(-204.8, imaginary[17], 0.31)
        fft.transform(real, imaginary, inverse = true)
        for (i in source.indices) { assertEquals(source[i], real[i], 1e-11); assertEquals(0.0, imaginary[i], 1e-11) }
    }
    @Test fun suppressionOffIsExactDelayedDryAndUnityWolaReconstructs() {
        val random = Random(83)
        val dry = NoiseSuppressor().apply { configure(false, 12f); reset() }
        val unity = NoiseSuppressor().apply { configure(true, 0f); reset() }
        var previous = FloatArray(480)
        repeat(100) {
            val source = FloatArray(480) { random.nextFloat() * .8f - .4f }
            val a = source.copyOf(); val b = source.copyOf()
            dry.process(a); unity.process(b)
            assertArrayEquals(previous, a, 0f)
            assertArrayEquals(previous, b, 1e-6f)
            previous = source
        }
        println("DSP_METRIC identity_delay_samples=${NoiseSuppressor.LATENCY_SAMPLES} sample_rate=48000; OFF and unity-WOLA fixtures, not E2E")
    }
    @Test fun noiseSuppressionIsFrequencySelectiveWhileSignalIsActive() {
        val ns = NoiseSuppressor().apply { configure(true, 12f); reset() }
        val random = Random(90427)
        val frames = 450; val input = FloatArray(frames * 480); val clean = FloatArray(input.size)
        val output = FloatArray(input.size); val block = FloatArray(480)
        for (n in input.indices) {
            clean[n] = if (n in 72_000 until 168_000) tone(n) else 0f
            input[n] = clean[n] + (random.nextGaussian() * .015).toFloat()
        }
        repeat(frames) { frame ->
            input.copyInto(block, 0, frame * 480, (frame + 1) * 480)
            ns.process(block); block.copyInto(output, frame * 480)
        }
        var originalNoise = 0.0; var remainingNoise = 0.0
        for (n in 24_000 until 67_200) { originalNoise += input[n] * input[n]; remainingNoise += output[n + 480] * output[n + 480] }
        val reduction = db(originalNoise / remainingNoise)
        var signal = 0.0; var beforeError = 0.0; var afterError = 0.0; var projection = 0.0
        for (n in 96_000 until 144_000) {
            val expected = clean[n].toDouble(); val filtered = output[n + 480].toDouble()
            signal += expected * expected
            beforeError += (input[n] - expected).pow(2)
            afterError += (filtered - expected).pow(2)
            projection += filtered * expected
        }
        val inputSnr = db(signal / beforeError); val outputSnr = db(signal / afterError)
        val retained = projection / signal
        println("DSP_METRIC synthetic_noise_reduction_db=$reduction input_snr_db=$inputSnr output_snr_db=$outputSnr active_signal_gain=$retained; seeded stationary noise + three-tone proxy, NOT speech corpus/device quality")
        assertTrue("Stationary noise reduction $reduction dB", reduction > 5)
        assertTrue("Active-signal SNR improvement ${outputSnr - inputSnr} dB", outputSnr > inputSnr + 3)
        assertTrue("Did not preserve the active component: $retained", retained in .75..1.1)
    }
    @Test fun suppressionAdaptsToAnIncreasedNoiseFloor() {
        val ns = NoiseSuppressor(); val random = Random(673)
        val block = FloatArray(480); var before = 0.0; var after = 0.0
        repeat(600) { frame ->
            val amplitude = if (frame < 200) .006 else .025
            for (i in block.indices) block[i] = (random.nextGaussian() * amplitude).toFloat()
            if (frame >= 500) for (x in block) before += x * x
            ns.process(block)
            if (frame >= 500) for (x in block) after += x * x
        }
        val reduction = db(before / after)
        println("DSP_METRIC raised_noise_floor_reduction_db=$reduction; synthetic stationary-noise fixture")
        assertTrue("Noise model did not adapt: $reduction dB", reduction > 5)
    }
    @Test fun coherentSignalAtStartupDoesNotRequireASilentCalibrationPeriod() {
        val ns = NoiseSuppressor(); val block = FloatArray(480); val random = Random(94)
        var cleanPower = 0.0; var projection = 0.0
        repeat(200) { frame ->
            for (i in block.indices) block[i] = tone(frame * 480 + i) + (random.nextGaussian() * .005).toFloat()
            ns.process(block)
            if (frame >= 50) for (i in block.indices) {
                val expected = tone((frame - 1) * 480 + i).toDouble()
                cleanPower += expected * expected; projection += expected * block[i]
            }
        }
        val retained = projection / cleanPower
        println("DSP_METRIC startup_coherent_signal_gain=$retained; three-tone proxy at first frame, NOT arbitrary speech preservation")
        assertTrue("Startup active component was learned as noise: $retained", retained in .8..1.1)
    }
    @Test fun runtimeNoiseSuppressionToggleKeepsTheSameDelayAndWarmState() {
        val ns = NoiseSuppressor(); val block = FloatArray(480); val random = Random(51)
        var previous = FloatArray(480)
        repeat(120) { frame ->
            if (frame == 100) ns.configure(false, 12f)
            if (frame == 110) ns.configure(true, 12f)
            val source = FloatArray(480) { (random.nextGaussian() * .02).toFloat() }
            source.copyInto(block); ns.process(block)
            if (frame in 101..109) assertArrayEquals(previous, block, 0f)
            if (frame >= 110) assertTrue("Toggle unexpectedly reset/emptied the overlap", block.any { abs(it) > 1e-5f })
            assertTrue(block.all { it.isFinite() })
            previous = source
        }
    }
    @Test fun highPassHasSpecifiedLowAndVoiceBandResponse() {
        fun response(hz: Double): Double {
            val filter = HighPassFilter(); var before = 0.0; var after = 0.0
            repeat(96_000) { n ->
                val x = (.1 * sin(2 * PI * hz * n / AudioProtocol.RATE)).toFloat()
                val y = filter.tick(x)
                if (n >= 48_000) { before += x * x; after += y * y }
            }
            return db(after / before)
        }
        val low = response(20.0); val voice = response(1000.0)
        assertTrue("20Hz rejection $low dB", low < -20)
        assertEquals(0.0, voice, .1)
        println("DSP_METRIC hpf_20hz_db=$low hpf_1000hz_db=$voice cutoff_hz=80")
    }
    @Test fun equalizerIsFlatAtZeroAndReachesTheMidBandSetting() {
        val flat = VoiceEqualizer(); val boosted = VoiceEqualizer().apply { configure(0f, 6f, 0f) }
        var before = 0.0; var after = 0.0
        repeat(96_000) { n ->
            val x = (.03 * sin(2 * PI * 1500 * n / AudioProtocol.RATE)).toFloat()
            assertEquals(x, flat.tick(x), 1e-6f)
            val y = boosted.tick(x)
            if (n >= 48_000) { before += x * x; after += y * y }
        }
        assertEquals(6.0, db(after / before), .05)
    }
    @Test fun compressorFollowsTheRatioAndReleasesRatherThanPermanentlyDucking() {
        val compressor = VoiceCompressor(); var high = 0f; var low = 0f
        repeat(48_000) { high = compressor.tick(.5f) }
        val inputDb = 20 * log10(.5); val expectedDb = -18 + (inputDb + 18) / 3 + 2
        assertEquals(10.0.pow(expectedDb / 20), high.toDouble(), .001)
        val releaseStart = compressor.tick(.01f)
        repeat(48_000) { low = compressor.tick(.01f) }
        assertTrue(releaseStart < low * .8f)
        assertEquals(.01 * 10.0.pow(2.0 / 20), low.toDouble(), .0001)
    }
    @Test fun limiterIsLastAndBoundsBothPolaritiesAndNonFiniteInput() {
        val limiter = PeakLimiter(); limiter.configure(-3f)
        assertEquals(limiter.ceiling, limiter.tick(8f), 0f)
        assertEquals(-limiter.ceiling, limiter.tick(-8f), 0f)
        assertEquals(.1f, limiter.tick(.1f), 0f)
        assertEquals(0f, limiter.tick(Float.NaN), 0f)
        assertEquals(0f, limiter.tick(Float.POSITIVE_INFINITY), 0f)
        assertEquals(0f, limiter.tick(Float.NEGATIVE_INFINITY), 0f)
    }
    @Test fun productionPipelineMatchesExplicitlyOrderedIndependentStages() {
        val config = DspConfig(highPassHz = 100f, low = -6f, mid = 9f, high = 3f,
            compressorThresholdDb = -30f, compressorRatio = 6f, limiterCeilingDb = -2f)
        val dsp = DspProcessor().apply { configure(config); reset() }
        val hp = HighPassFilter().apply { configure(config.highPassHz) }
        val ns = NoiseSuppressor().apply { configure(config.noiseSuppression, config.noiseReductionDb); reset() }
        val eq = VoiceEqualizer().apply { configure(config.low, config.mid, config.high) }
        val comp = VoiceCompressor().apply { configure(config.compressorThresholdDb, config.compressorRatio) }
        val limit = PeakLimiter().apply { configure(config.limiterCeilingDb) }
        val random = Random(91); val actual = FloatArray(480); val reference = FloatArray(480)
        repeat(150) { frame ->
            for (i in actual.indices) actual[i] = tone(frame * 480 + i) * 2 + (random.nextGaussian() * .02).toFloat()
            actual.copyInto(reference); dsp.process(actual)
            for (i in reference.indices) reference[i] = hp.tick(reference[i])
            ns.process(reference)
            for (i in reference.indices) reference[i] = limit.tick(comp.tick(eq.tick(reference[i])))
            assertArrayEquals(reference, actual, 1e-6f)
        }
    }
    @Test fun bypassIsAlignedAndFinalSafetyRemainsActive() {
        val dsp = DspProcessor().apply { configure(DspConfig(enabled = false)); reset() }
        val frame = FloatArray(480) { .123f }; dsp.process(frame)
        assertTrue(frame.all { it == 0f })
        frame.fill(.123f); dsp.process(frame); assertTrue(frame.all { it == .123f })
        frame.fill(8f); dsp.process(frame); frame.fill(8f); dsp.process(frame)
        assertTrue(frame.all { it in -.891251f.. .891251f })
    }
    @Test fun togglesInvalidSettingsAndResetsStayFiniteWithoutOldSpeechTails() {
        val dsp = DspProcessor(); val samples = FloatArray(480)
        val invalid = DspConfig(noiseReductionDb = Float.NaN, gain = Float.POSITIVE_INFINITY,
            low = Float.NaN, mid = Float.NEGATIVE_INFINITY, high = Float.NaN, highPassHz = Float.NaN,
            compressorThresholdDb = Float.NaN, compressorRatio = Float.NaN, limiterCeilingDb = Float.NaN)
        repeat(200) { frame ->
            if (frame % 10 == 0) dsp.configure(if (frame % 20 == 0) invalid else DspConfig(noiseSuppression = false))
            for (i in samples.indices) samples[i] = sin((frame * 480 + i) * .1).toFloat() * 4
            samples[0] = Float.NaN; samples[1] = Float.POSITIVE_INFINITY
            dsp.process(samples)
            for (x in samples) assertTrue(x.isFinite() && abs(x) <= .891251f)
        }
        dsp.reset(); samples.fill(0f); dsp.process(samples)
        assertTrue(samples.all { it == 0f })
        samples.fill(0f); dsp.process(samples); assertTrue(samples.all { it == 0f })
    }
    @Test fun frameTimestampsGapsFlagsAndUint32WrapFollowTheDelayedContent() {
        val processor = DspFrameProcessor().apply { configure(DspConfig(enabled = false)) }
        val frame = CaptureFrame()
        fun process(sequence: Int, value: Float, flags: Int = 0, timestamp: Long = 96_000L + sequence * 480L) {
            frame.sequence = sequence; frame.timestamp = timestamp; frame.flags = flags; frame.samples.fill(value)
            processor.process(frame)
            assertEquals(timestamp - DspProcessor.LATENCY_SAMPLES, frame.timestamp)
        }
        process(0, .11f, AudioProtocol.START); assertTrue(frame.samples.all { it == 0f })
        assertEquals(AudioProtocol.START, frame.flags)
        process(1, .22f); assertTrue(frame.samples.all { it == .11f })
        process(5, .55f); assertTrue(frame.samples.all { it == 0f }); assertEquals(AudioProtocol.DISCONTINUITY, frame.flags)
        process(6, .66f); assertTrue(frame.samples.all { it == .55f })
        process(7, .77f, AudioProtocol.DISCONTINUITY); assertTrue(frame.samples.all { it == 0f })
        process(-1, .33f, timestamp = 200_000); assertTrue(frame.samples.all { it == 0f })
        process(0, .44f, timestamp = 200_480); assertTrue(frame.samples.all { it == .33f }); assertEquals(0, frame.flags)
    }
}
