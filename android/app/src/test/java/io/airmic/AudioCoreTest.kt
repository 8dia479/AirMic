package io.airmic

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.abs
import kotlin.math.sin

class AudioCoreTest {
    @Test fun wireGoldenAndAck() {
        val frame = CaptureFrame()
        frame.timestamp = 0x0102030405060708L; frame.flags = AudioProtocol.START
        frame.samples[0] = -1f; frame.samples[1] = -1f / 32768; frame.samples[2] = 1f / 32768; frame.samples[3] = 32767f / 32768
        val bytes = ByteArray(976)
        AudioProtocol.encode(frame, bytes)
        assertEquals("00000000010203040506070801e000018000ffff00017fff", bytes.take(24).joinToString("") { "%02x".format(it.toInt() and 255) })
        val ackBytes = ByteArray(24)
        "AMAK".toByteArray(Charsets.US_ASCII).copyInto(ackBytes)
        AudioProtocol.put32(ackBytes, 4, -1); AudioProtocol.put64(ackBytes, 8, frame.timestamp)
        AudioProtocol.put32(ackBytes, 16, -1); AudioProtocol.put16(ackBytes, 20, 1)
        val ack = AudioProtocol.Ack()
        assertTrue(AudioProtocol.decodeAck(ackBytes, 24, ack))
        assertEquals(-1, ack.sequence); assertEquals(frame.timestamp, ack.timestamp); assertEquals(0xffff_ffffL, ack.accepted)
        for (length in 0..23) assertFalse(AudioProtocol.decodeAck(ackBytes, length, ack))
        ackBytes[23] = 1; assertFalse(AudioProtocol.decodeAck(ackBytes, 24, ack))
        assertEquals(0, AudioProtocol.pcm16(Float.NaN)); assertEquals(-32768, AudioProtocol.pcm16(-2f)); assertEquals(32767, AudioProtocol.pcm16(2f))
    }
    @Test fun dspIsStatefulFiniteAndBounded() {
        val processor = DspProcessor(); val samples = FloatArray(480)
        for (frame in 0 until 1000) {
            for (i in samples.indices) samples[i] = (sin((frame * 480 + i) * .17) * 2).toFloat()
            if (frame == 100) { samples[0] = Float.NaN; samples[1] = Float.POSITIVE_INFINITY }
            if (frame == 500) processor.configure(DspConfig(noiseReductionDb = 18f, gain = 24f, low = 12f, mid = -12f, high = 12f))
            processor.process(samples)
            assertTrue(samples.all { it.isFinite() && abs(it) <= .891251f })
        }
        processor.configure(DspConfig(enabled = false)); samples.fill(.123f); processor.process(samples)
        samples.fill(.123f); processor.process(samples) // Aligned bypass after the transition hop.
        assertTrue(samples.all { it == .123f })
    }
    @Test(timeout = 15_000) fun fixedQueueOrdersAndRejectsOverflow() {
        val queue = CaptureQueue(); val source = CaptureFrame(); val output = CaptureFrame()
        for (i in 0 until CaptureQueue.CAPACITY) { source.sequence = i; assertTrue(queue.offer(source)) }
        assertFalse(queue.offer(source))
        for (i in 0 until CaptureQueue.CAPACITY) { assertTrue(queue.poll(output)); assertEquals(i, output.sequence) }
        assertFalse(queue.poll(output))
        val error = AtomicReference<Throwable?>()
        val producer = Thread {
            try {
                for (i in 0 until 100_000) {
                    source.sequence = i; source.timestamp = i * 480L; source.samples[0] = i.toFloat()
                    while (!queue.offer(source)) Thread.yield()
                }
            } catch (e: Throwable) { error.set(e) }
        }.apply { isDaemon = true; start() }
        for (i in 0 until 100_000) {
            while (!queue.poll(output)) { error.get()?.let { throw it }; Thread.yield() }
            assertEquals(i, output.sequence); assertEquals(i * 480L, output.timestamp); assertEquals(i.toFloat(), output.samples[0], 0f)
        }
        producer.join(3000); assertFalse(producer.isAlive); error.get()?.let { throw it }
    }
}
