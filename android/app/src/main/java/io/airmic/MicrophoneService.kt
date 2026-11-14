package io.airmic

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.os.SystemClock
import java.util.Locale
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.ThreadFactory
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

internal data class UiState(
    val active: Boolean = false,
    val phase: String = "대기 중",
    val detail: String = "같은 Wi-Fi의 PC IPv4 주소를 입력하세요.",
    val statistics: String = "48 kHz · Mono · PCM16 · 10 ms",
    val peak: Int = 0,
)

class MicrophoneService : Service() {
    companion object {
        const val ACTION_START = "io.airmic.START"
        const val ACTION_STOP = "io.airmic.STOP"
        const val ACTION_CONFIG = "io.airmic.CONFIG"
        const val PREFERENCES = "airmic"
        private const val CHANNEL = "microphone"
        private const val NOTIFICATION = 1
        // Process-wide serialization prevents an old Service.onDestroy cleanup from
        // stopping a newly created service's native engine or unbinding its Wi-Fi.
        private val controller = ScheduledThreadPoolExecutor(1, ThreadFactory { task ->
            Thread(task, "AirMic-control")
        }).apply { removeOnCancelPolicy = true }
        private val ownerIds = AtomicLong()
        @Volatile private var currentOwner = 0L
        @Volatile internal var ui = UiState()
            private set
    }

    // Instance tasks share the process controller; it intentionally survives services.
    private val ownerId = ownerIds.incrementAndGet()
    private lateinit var connectivity: ConnectivityManager
    private lateinit var notifications: NotificationManager
    private var monitor: ScheduledFuture<*>? = null
    @Volatile private var destroyed = false
    @Volatile private var requested = false
    private var nativeRunning = false
    private var nativeStartedAt = 0L
    private var boundNetwork: Network? = null
    private var host = ""
    private var port = 49000
    private var failures = 0
    private var retryAt = 0L
    private var notificationText = ""
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var callbackRegistered = false

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = requestTick()
        override fun onLost(network: Network) = requestTick()
    }

    override fun onCreate() {
        super.onCreate()
        currentOwner = ownerId
        connectivity = getSystemService(ConnectivityManager::class.java)
        notifications = getSystemService(NotificationManager::class.java)
        notifications.createNotificationChannel(NotificationChannel(
            CHANNEL, "AirMic 마이크 전송", NotificationManager.IMPORTANCE_LOW,
        ).apply { description = "마이크 사용 및 PC 연결 상태" })
        connectivity.registerNetworkCallback(
            NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build(),
            networkCallback,
        )
        callbackRegistered = true
        monitor = controller.scheduleWithFixedDelay({ guardedTick() }, 500, 500, TimeUnit.MILLISECONDS)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                requested = false
                ui = ui.copy(phase = "중지 중", detail = "마이크와 네트워크를 정리하고 있습니다.")
                stopSelf()
            }
            ACTION_CONFIG -> {
                if (requested) controller.execute {
                    try {
                        if (!DspPreferences.load(getSharedPreferences(PREFERENCES, MODE_PRIVATE)).applyToEngine()) {
                            ui = ui.copy(detail = "DSP 설정 큐가 가득 찼습니다. 다시 조정하세요.")
                        }
                    } catch (e: LinkageError) { fatal("네이티브 라이브러리 오류: ${e.message}") }
                } else stopSelf(startId)
            }
            ACTION_START -> {
                if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                    fatal("마이크 권한이 필요합니다.")
                    return START_NOT_STICKY
                }
                val targetHost = intent.getStringExtra("host") ?: ""
                val targetPort = intent.getIntExtra("port", 49000)
                try {
                    val notification = notification("마이크 시작 중")
                    if (Build.VERSION.SDK_INT >= 29) {
                        startForeground(NOTIFICATION, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
                    } else startForeground(NOTIFICATION, notification)
                    acquireLocks()
                    ui = UiState(true, "시작 중", "$targetHost:$targetPort")
                    requested = true
                    controller.execute {
                        try {
                            if (nativeRunning) NativeBridge.stop()
                            nativeRunning = false
                            host = targetHost; port = targetPort; failures = 0; retryAt = 0
                            guardedTick()
                        } catch (e: LinkageError) { fatal("네이티브 라이브러리 로드 실패: ${e.message}") }
                    }
                } catch (e: Exception) {
                    fatal("마이크 서비스 시작 실패: ${e.message}")
                }
            }
            else -> stopSelf(startId)
        }
        // Never silently restart background microphone after process death.
        return START_NOT_STICKY
    }

    private fun requestTick() {
        if (!destroyed) runCatching { controller.execute { guardedTick() } }
    }

    private fun chooseWifi(): Network? {
        fun isWifi(n: Network) = connectivity.getNetworkCapabilities(n)
            ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
        return boundNetwork?.takeIf { isWifi(it) } ?: connectivity.allNetworks.firstOrNull { isWifi(it) }
    }

    private fun guardedTick() {
        if (destroyed || !requested) return
        try {
            tick()
        } catch (e: LinkageError) {
            fatal("네이티브 라이브러리 로드 실패: ${e.message}")
        } catch (e: Exception) {
            runCatching { NativeBridge.stop() }
            nativeRunning = false
            scheduleRetry("엔진 오류: ${e.message}")
        }
    }

    private fun tick() {
        val wifi = chooseWifi()
        if (wifi == null) {
            if (nativeRunning) NativeBridge.stop()
            nativeRunning = false
            if (boundNetwork != null) connectivity.bindProcessToNetwork(null)
            boundNetwork = null; retryAt = 0
            publish(UiState(true, "Wi-Fi 대기", "모바일 데이터는 사용하지 않습니다. PC와 같은 Wi-Fi에 연결하세요."))
            return
        }
        if (boundNetwork != wifi) {
            if (nativeRunning) NativeBridge.stop()
            nativeRunning = false
            if (!connectivity.bindProcessToNetwork(wifi)) throw IllegalStateException("Wi-Fi 네트워크 바인딩 실패")
            boundNetwork = wifi; retryAt = 0
        }
        if (!nativeRunning) {
            if (SystemClock.elapsedRealtime() < retryAt) return
            DspPreferences.load(getSharedPreferences(PREFERENCES, MODE_PRIVATE)).applyToEngine()
            val error = NativeBridge.start(host, port)
            if (error != null) { scheduleRetry(error); return }
            nativeRunning = true; nativeStartedAt = SystemClock.elapsedRealtime()
        }
        if (SystemClock.elapsedRealtime() - nativeStartedAt >= 30_000) failures = 0
        val s = NativeBridge.stats()
        check(s.size == 17) { "네이티브 통계 버전 불일치" }
        if (s[0] == 3L) {
            val error = NativeBridge.lastError()
            NativeBridge.stop(); nativeRunning = false
            scheduleRetry(error)
            return
        }
        val confirmed = s[6] in 0L..1999L
        val rtt = if (confirmed) String.format(Locale.ROOT, "%.2f ms", s[7] / 1000.0) else "측정 대기"
        val status = if (confirmed) "PC 응답 확인됨" else "전송 중 · PC 응답 없음"
        val detail = if (confirmed) "$host:$port · ACK ${s[6]} ms 전" else "$host:$port · PC 실행 / 방화벽 / AP 격리를 확인하세요."
        val statistics = "캡처 ${s[1]} / 전송 ${s[3]} / PC 수신 ${s[15]} 패킷\n" +
            "큐 손실 ${s[2]} · 오래된 프레임 폐기 ${s[5]} · 송신 오류 ${s[4]}\n" +
            "UDP ACK RTT $rtt (종단 간 오디오 지연 아님)\n" +
            "Oboe burst ${s[9]} / buffer ${s[10]} frames · ${if (s[11] == 1L) "Exclusive" else "Shared"}\n" +
            "입력 xrun ${s[8]} (-1: 미지원) · 송신 대기 ${s[16]} · errno ${s[14]}"
        publish(UiState(true, status, detail, statistics, s[12].toInt().coerceIn(0, 1000)))
    }

    private fun scheduleRetry(error: String) {
        failures = (failures + 1).coerceAtMost(6)
        val delaySeconds = (1L shl (failures - 1)).coerceAtMost(30)
        retryAt = SystemClock.elapsedRealtime() + delaySeconds * 1000
        publish(UiState(true, "재연결 대기", "$error · ${delaySeconds}초 후 재시도"))
    }

    private fun fatal(message: String) {
        requested = false
        if (currentOwner == ownerId) ui = UiState(false, "오류", message)
        stopSelf()
    }

    private fun publish(state: UiState) {
        if (destroyed || currentOwner != ownerId) return
        ui = state
        if (notificationText != state.phase && !destroyed) {
            notificationText = state.phase
            notifications.notify(NOTIFICATION, notification(state.phase))
        }
    }

    private fun notification(text: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1,
            Intent(this, MicrophoneService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_mic).setContentTitle("AirMic · 마이크 사용 중")
            .setContentText(text).setContentIntent(open).setOngoing(true)
            .setCategory(Notification.CATEGORY_SERVICE).setOnlyAlertOnce(true)
            .addAction(Notification.Action.Builder(null, "전송 중지", stop).build())
            .build()
    }

    @Suppress("DEPRECATION")
    private fun acquireLocks() {
        if (wakeLock == null) wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AirMic:capture").apply {
                setReferenceCounted(false); acquire()
            }
        if (wifiLock == null) {
            val manager = applicationContext.getSystemService(WifiManager::class.java)
            val mode = if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
            wifiLock = manager.createWifiLock(mode, "AirMic:wifi").apply {
                setReferenceCounted(false); acquire()
            }
        }
    }

    override fun onDestroy() {
        destroyed = true; requested = false
        monitor?.cancel(false)
        if (callbackRegistered) runCatching { connectivity.unregisterNetworkCallback(networkCallback) }
        controller.execute {
            runCatching { NativeBridge.stop() }
            nativeRunning = false
            runCatching { connectivity.bindProcessToNetwork(null) }
            wakeLock?.let { if (it.isHeld) it.release() }
            wifiLock?.let { if (it.isHeld) it.release() }
            if (currentOwner == ownerId && ui.phase != "오류") ui = UiState(false, "중지됨", "마이크 캡처와 UDP 전송을 종료했습니다.")
        }
        stopForeground(STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null // Started foreground service, no bound-service API.
}
