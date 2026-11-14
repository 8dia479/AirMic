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
        repeat(3000) { index ->
            if (index % 16 == 0) dsp.configure(configs[index / 16 and 3])
            signal.copyInto(frame); dsp.process(frame)
        }
        val id = Thread.currentThread().id
        val before = bean.getThreadAllocatedBytes(id)
        repeat(2000) { index ->
            if (index % 16 == 0) dsp.configure(configs[index / 16 and 3])
            signal.copyInto(frame); dsp.process(frame)
        }
        val allocated = bean.getThreadAllocatedBytes(id) - before
        println("DSP_METRIC warmed_allocated_bytes=$allocated frames=2000 includes_configure=true; HotSpot JVM core only, NOT ART/AudioRecord/network")
        assertEquals("Kotlin DSP hot-path allocations", 0L, allocated)
    }
}
