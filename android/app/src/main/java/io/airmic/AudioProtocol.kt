package io.airmic

import kotlin.math.roundToInt

// Shared wire contract with the managed Windows receiver. No JNI or native library.
internal object AudioProtocol {
    const val RATE = 48_000
    const val SAMPLES = 480
    const val PACKET_BYTES = 976
    const val START = 1
    const val DISCONTINUITY = 2
    class Ack { var sequence = 0; var timestamp = 0L; var accepted = 0L }
    fun put16(b: ByteArray, at: Int, value: Int) {
        b[at] = (value ushr 8).toByte(); b[at + 1] = value.toByte()
    }
    fun put32(b: ByteArray, at: Int, value: Int) {
        for (i in 0..3) b[at + i] = (value ushr (24 - i * 8)).toByte()
    }
    fun put64(b: ByteArray, at: Int, value: Long) {
        for (i in 0..7) b[at + i] = (value ushr (56 - i * 8)).toByte()
    }
    private fun get16(b: ByteArray, at: Int) = ((b[at].toInt() and 255) shl 8) or (b[at + 1].toInt() and 255)
    private fun get32(b: ByteArray, at: Int): Int {
        var value = 0
        for (i in 0..3) value = (value shl 8) or (b[at + i].toInt() and 255)
        return value
    }
    private fun get64(b: ByteArray, at: Int): Long {
        var value = 0L
        for (i in 0..7) value = (value shl 8) or (b[at + i].toLong() and 255)
        return value
    }
    fun pcm16(x: Float): Int = if (!x.isFinite()) 0 else
        (x.coerceIn(-1f, 1f) * 32768f).roundToInt().coerceIn(-32768, 32767)
    fun encode(frame: CaptureFrame, bytes: ByteArray) {
        require(bytes.size == PACKET_BYTES && frame.samples.size == SAMPLES)
        put32(bytes, 0, frame.sequence); put64(bytes, 4, frame.timestamp)
        put16(bytes, 12, SAMPLES); put16(bytes, 14, frame.flags)
        for (i in 0 until SAMPLES) put16(bytes, 16 + i * 2, pcm16(frame.samples[i]))
    }
    fun decodeAck(bytes: ByteArray, length: Int, destination: Ack): Boolean {
        if (length != 24 || bytes.size < length || bytes[0] != 65.toByte() || bytes[1] != 77.toByte() ||
            bytes[2] != 65.toByte() || bytes[3] != 75.toByte() || get16(bytes, 20) != 1 || get16(bytes, 22) != 0) return false
        destination.sequence = get32(bytes, 4); destination.timestamp = get64(bytes, 8)
        destination.accepted = get32(bytes, 16).toLong() and 0xffff_ffffL
        return true
    }
}

internal class CaptureFrame {
    val samples = FloatArray(AudioProtocol.SAMPLES)
    var sequence = 0
    var timestamp = 0L
    var readyUs = 0L
    var flags = 0
    fun copyFrom(other: CaptureFrame) {
        other.samples.copyInto(samples)
        sequence = other.sequence; timestamp = other.timestamp; readyUs = other.readyUs; flags = other.flags
    }
}

// One capture producer, one DSP/network consumer. Publication through volatile indexes.
internal class CaptureQueue {
    companion object { const val CAPACITY = 16 }
    private val slots = Array(CAPACITY) { CaptureFrame() }
    @Volatile private var write = 0
    @Volatile private var read = 0
    fun offer(frame: CaptureFrame): Boolean {
        val w = write
        if (w - read >= CAPACITY) return false
        slots[w and (CAPACITY - 1)].copyFrom(frame)
        write = w + 1
        return true
    }
    fun poll(frame: CaptureFrame): Boolean {
        val r = read
        if (r == write) return false
        frame.copyFrom(slots[r and (CAPACITY - 1)])
        read = r + 1
        return true
    }
    fun size() = (write - read).coerceIn(0, CAPACITY)
}
