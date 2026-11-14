package io.airmic

import com.sun.management.ThreadMXBean
import java.lang.management.ManagementFactory
import kotlin.math.sin
import org.junit.Assert.*
import org.junit.Test

// Host-only instrumentation: management APIs do not belong to Android's API
// boot classpath. The production sources are compiled unchanged by this JVM module.
class DspAllocationTest {
    @Test(timeout = 30_000) fun warmedCoreAndRuntimeConfigurationDoNotAllocateOnTheJvm() {
        val bean = ManagementFactory.getThreadMXBean() as ThreadMXBean
        assertTrue(bean.isThreadAllocatedMemorySupported)
        bean.isThreadAllocatedMemoryEnabled = true
        val dsp = DspProcessor()
        val signal = FloatArray(480) { sin(it * .17).toFloat() * .2f }; val frame = FloatArray(480)
        val configs = arrayOf(DspConfig(), DspConfig(noiseSuppression = false, highPassHz = 120f, mid = 3f),
            DspConfig(enabled = false), DspConfig(noiseReductionDb = 18f, compressorRatio = 4f))
        val id = Thread.currentThread().id
        // Prime the measurement API too. A first ever MXBean read can initialize
        // management classes AFTER its counter snapshot; that is not DSP memory.
        repeat(100) { bean.getThreadAllocatedBytes(id) }
        runFrames(dsp, signal, frame, configs, 4000)
        val allocations = LongArray(3)
        val elapsedNs = LongArray(3)
        // Warm the exact batch/measurement call sites, not a separate loop that
        // first reaches OSR/JIT thresholds in the asserted measurement window.
        repeat(2) {
            val before = bean.getThreadAllocatedBytes(id)
            runFrames(dsp, signal, frame, configs, 2000)
            bean.getThreadAllocatedBytes(id) - before
        }
        for (batch in allocations.indices) {
            val start = System.nanoTime()
            val before = bean.getThreadAllocatedBytes(id)
            runFrames(dsp, signal, frame, configs, 2000)
            allocations[batch] = bean.getThreadAllocatedBytes(id) - before
            elapsedNs[batch] = System.nanoTime() - start
        }
        // Do not select the best window or tolerate recurring allocations.
        for (batch in allocations.indices) {
            println("DSP_METRIC warmed_allocated_bytes=${allocations[batch]} frames=2000 batch=$batch includes_configure=true host_ms_per_frame=${elapsedNs[batch] / 2_000_000_000.0}; HotSpot JVM core only, NOT ART/AudioRecord/network or phone timing")
            assertEquals("Kotlin DSP hot-path allocations in batch $batch", 0L, allocations[batch])
        }
    }
    private fun runFrames(dsp: DspProcessor, signal: FloatArray, frame: FloatArray, configs: Array<DspConfig>, count: Int) {
        repeat(count) { index ->
            if (index % 16 == 0) dsp.configure(configs[index / 16 and 3])
            signal.copyInto(frame); dsp.process(frame)
        }
    }
}
