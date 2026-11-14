package io.airmic

import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Build
import android.os.Process
import android.os.SystemClock
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.PortUnreachableException
import java.nio.ByteBuffer
import java.nio.channels.DatagramChannel
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.locks.LockSupport
import kotlin.math.abs
import kotlin.math.max

// Control entry points are called only by MicrophoneService's serialized controller.
// Each open owns its record, threads, queues and counters: old sessions cannot corrupt a new one.
internal object AudioEngine {
    private var current: Session? = null
    private var desired = DspConfig()
    private var error = ""
    fun configure(enabled: Boolean, gate: Boolean, gain: Float, low: Float, mid: Float, high: Float): Boolean {
        desired = DspConfig(enabled, gate, gain, low, mid, high)
        current?.configuration?.set(desired)
        return true
    }
    fun start(host: String, port: Int): String? {
        stop(); error = ""
        return try {
            require(port in 1..65535) { "포트는 1~65535 범위여야 합니다." }
            val parts = host.split('.')
            require(parts.size == 4 && parts.all { it.isNotEmpty() && it.length <= 3 && it.toIntOrNull() in 0..255 }) { "숫자 IPv4 주소가 필요합니다." }
            val address = InetAddress.getByAddress(ByteArray(4) { parts[it].toInt().toByte() })
            val session = Session(address, port, desired)
            current = session
            session.start()
            null
        } catch (e: Exception) {
            error = e.message ?: e.javaClass.simpleName
            runCatching { stop() }
            error
        }
    }
    fun stop() {
        val session = current ?: return
        session.close() // Do not permit reopening if an old capture failed to quiesce.
        current = null
    }
    fun stats(): LongArray = current?.snapshot() ?: LongArray(17).also { it[6] = -1; it[8] = -1 }
    fun lastError(): String = current?.error?.takeIf { it.isNotEmpty() } ?: error
    private fun nowUs() = SystemClock.elapsedRealtimeNanos() / 1000

    private class Session(address: InetAddress, port: Int, initial: DspConfig) {
        val configuration = AtomicReference(initial)
        private val queue = CaptureQueue()
        private val channel = DatagramChannel.open()
        private var record: AudioRecord? = null
        private var capture: Thread? = null
        private var sender: Thread? = null
        @Volatile private var stopping = false
        @Volatile var error = ""
        private val state = AtomicLong(1)
        private val captured = AtomicLong(); private val drops = AtomicLong(); private val sent = AtomicLong()
        private val sendErrors = AtomicLong(); private val stale = AtomicLong(); private val lastAck = AtomicLong()
        private val rtt = AtomicLong(); private val accepted = AtomicLong(); private val peak = AtomicLong()
        private val audioError = AtomicLong(); private val networkError = AtomicLong()
        private var encoding = AudioFormat.ENCODING_PCM_FLOAT
        private var bufferFrames = 0
        private var preset = MediaRecorder.AudioSource.UNPROCESSED
        init {
            try { channel.configureBlocking(false); channel.connect(InetSocketAddress(address, port)) }
            catch (e: Exception) { channel.close(); throw e }
        }
        @android.annotation.SuppressLint("MissingPermission") // Service checks RECORD_AUDIO; SecurityException is reported to control.
        fun start() {
            var opened: AudioRecord? = null
            for (source in intArrayOf(MediaRecorder.AudioSource.UNPROCESSED, MediaRecorder.AudioSource.VOICE_RECOGNITION)) {
                for (format in intArrayOf(AudioFormat.ENCODING_PCM_FLOAT, AudioFormat.ENCODING_PCM_16BIT)) {
                    try {
                        val minimum = AudioRecord.getMinBufferSize(AudioProtocol.RATE, AudioFormat.CHANNEL_IN_MONO, format)
                        if (minimum <= 0) continue
                        val builder = AudioRecord.Builder().setAudioSource(source).setAudioFormat(
                            AudioFormat.Builder().setSampleRate(AudioProtocol.RATE).setChannelMask(AudioFormat.CHANNEL_IN_MONO)
                                .setEncoding(format).build(),
                        ).setBufferSizeInBytes(max(minimum * 2, AudioProtocol.SAMPLES * 4 * 4))
                        if (Build.VERSION.SDK_INT >= 30) builder.setPrivacySensitive(true)
                        val candidate = builder.build()
                        if (candidate.state != AudioRecord.STATE_INITIALIZED || candidate.sampleRate != AudioProtocol.RATE || candidate.channelCount != 1) {
                            candidate.release(); continue
                        }
                        opened = candidate; encoding = format; preset = source; break
                    } catch (_: IllegalArgumentException) { }
                }
                if (opened != null) break
            }
            val audio = opened ?: throw IllegalStateException("48 kHz mono AudioRecord 입력을 열 수 없습니다.")
            record = audio; bufferFrames = audio.bufferSizeInFrames
            audio.startRecording()
            check(audio.recordingState == AudioRecord.RECORDSTATE_RECORDING) { "마이크 녹음 시작 실패" }
            state.set(2)
            sender = Thread({ sendLoop() }, "AirMic-DSP-UDP").also { it.start() }
            capture = Thread({ captureLoop(audio) }, "AirMic-capture").also { it.start() }
        }
        private fun captureLoop(audio: AudioRecord) {
            runCatching { Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO) }
            val floats = FloatArray(AudioProtocol.SAMPLES)
            val shorts = ShortArray(AudioProtocol.SAMPLES)
            val frame = CaptureFrame()
            val us = nowUs()
            frame.timestamp = (us / 1_000_000) * AudioProtocol.RATE + (us % 1_000_000) * AudioProtocol.RATE / 1_000_000
            frame.flags = AudioProtocol.START
            var used = 0
            try {
                while (!stopping) {
                    val count = if (encoding == AudioFormat.ENCODING_PCM_FLOAT)
                        audio.read(floats, 0, floats.size, AudioRecord.READ_BLOCKING)
                    else audio.read(shorts, 0, shorts.size, AudioRecord.READ_BLOCKING)
                    if (count < 0) {
                        if (!stopping) { audioError.set(count.toLong()); throw IllegalStateException("AudioRecord read 오류 $count") }
                        break
                    }
                    if (count == 0) { LockSupport.parkNanos(500_000); continue }
                    for (i in 0 until count) {
                        frame.samples[used++] = if (encoding == AudioFormat.ENCODING_PCM_FLOAT) floats[i] else shorts[i] / 32768f
                        if (used == AudioProtocol.SAMPLES) {
                            frame.readyUs = nowUs(); captured.incrementAndGet()
                            val queued = queue.offer(frame)
                            if (!queued) drops.incrementAndGet()
                            frame.sequence++; frame.timestamp += AudioProtocol.SAMPLES
                            frame.flags = if (queued) 0 else AudioProtocol.DISCONTINUITY
                            used = 0
                        }
                    }
                }
            } catch (e: Exception) { if (!stopping) { error = e.message ?: "캡처 실패"; state.set(3) } }
        }
        private fun sendLoop() {
            val frame = CaptureFrame(); val dsp = DspProcessor()
            var applied: DspConfig? = null
            val bytes = ByteArray(AudioProtocol.PACKET_BYTES); val packet = ByteBuffer.wrap(bytes)
            val replies = ByteArray(256); val reply = ByteBuffer.wrap(replies); val ack = AudioProtocol.Ack()
            val sequences = IntArray(1024); val timestamps = LongArray(1024); val sentTimes = LongArray(1024); val valid = BooleanArray(1024)
            try {
                while (!stopping) {
                    val update = configuration.get()
                    if (update !== applied) { dsp.configure(update); applied = update }
                    for (iteration in 0 until 8) {
                        if (!queue.poll(frame)) break
                        if (nowUs() - frame.readyUs > 60_000) { stale.incrementAndGet(); continue }
                        dsp.process(frame.samples)
                        var maximum = 0f
                        for (x in frame.samples) maximum = max(maximum, abs(x))
                        peak.set((maximum.coerceAtMost(1f) * 1000).toLong())
                        AudioProtocol.encode(frame, bytes)
                        packet.clear()
                        val now = nowUs()
                        try {
                            if (channel.write(packet) == bytes.size) {
                                sent.incrementAndGet(); networkError.set(0)
                                val index = frame.sequence and 1023
                                sequences[index] = frame.sequence; timestamps[index] = frame.timestamp; sentTimes[index] = now; valid[index] = true
                            } else { sendErrors.incrementAndGet(); networkError.set(1) }
                        } catch (_: java.io.IOException) { if (!stopping) { sendErrors.incrementAndGet(); networkError.set(1) } }
                    }
                    for (iteration in 0 until 8) {
                        reply.clear()
                        val count = try { channel.read(reply) } catch (_: PortUnreachableException) { networkError.set(1); break }
                        if (count <= 0) break
                        if (!AudioProtocol.decodeAck(replies, count, ack)) continue
                        val index = ack.sequence and 1023
                        if (!valid[index] || sequences[index] != ack.sequence || timestamps[index] != ack.timestamp) continue
                        val now = nowUs(); val sample = now - sentTimes[index]
                        if (sample !in 0L..5_000_000L) continue
                        val previous = rtt.get(); rtt.set(if (previous == 0L) sample else (previous * 7 + sample) / 8)
                        lastAck.set(now); accepted.set(ack.accepted)
                    }
                    if (queue.size() == 0) LockSupport.parkNanos(1_000_000)
                }
            } catch (e: Exception) { if (!stopping) { error = e.message ?: "송신 실패"; state.set(3) } }
        }
        fun close() {
            stopping = true
            runCatching { record?.stop() } // Unblocks READ_BLOCKING; control thread only.
            runCatching { channel.close() }
            capture?.join(3000); sender?.join(3000)
            check(capture?.isAlive != true && sender?.isAlive != true) { "이전 오디오 thread 종료 대기 시간 초과" }
            record?.release(); record = null; state.set(0)
        }
        fun snapshot(): LongArray {
            val ackAt = lastAck.get()
            return longArrayOf(state.get(), captured.get(), drops.get(), sent.get(), sendErrors.get(), stale.get(),
                if (ackAt == 0L) -1 else (nowUs() - ackAt) / 1000, rtt.get(), -1, AudioProtocol.SAMPLES.toLong(), bufferFrames.toLong(),
                if (encoding == AudioFormat.ENCODING_PCM_FLOAT) 1 else 0, peak.get(), audioError.get(), networkError.get(), accepted.get(), queue.size().toLong())
        }
        fun sourceName() = if (preset == MediaRecorder.AudioSource.UNPROCESSED) "Unprocessed" else "VoiceRecognition (기기 DSP 가능)"
    }
    fun sourceName() = current?.sourceName() ?: "입력 없음"
}
