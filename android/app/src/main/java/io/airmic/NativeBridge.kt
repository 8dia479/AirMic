package io.airmic

// Called only by MicrophoneService's single control executor, never by UI/audio callbacks.
internal object NativeBridge {
    init { System.loadLibrary("airmic") }
    external fun start(host: String, port: Int): String?
    external fun stop()
    external fun configure(enabled: Boolean, gate: Boolean, gain: Float, low: Float, mid: Float, high: Float): Boolean
    external fun stats(): LongArray
    external fun lastError(): String
}
