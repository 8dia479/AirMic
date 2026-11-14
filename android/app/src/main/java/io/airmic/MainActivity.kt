package io.airmic

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.text.method.DigitsKeyListener
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import kotlin.math.roundToInt

class MainActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private val prefs by lazy { getSharedPreferences(MicrophoneService.PREFERENCES, MODE_PRIVATE) }
    private lateinit var host: EditText
    private lateinit var port: EditText
    private lateinit var action: Button
    private lateinit var phase: TextView
    private lateinit var detail: TextView
    private lateinit var statistics: TextView
    private lateinit var meter: ProgressBar
    private var pendingStart = false
    private val refresh = object : Runnable {
        override fun run() {
            val s = MicrophoneService.ui
            phase.text = s.phase; detail.text = s.detail; statistics.text = s.statistics
            meter.progress = s.peak
            action.text = if (s.active) "전송 중지" else "마이크 전송 시작"
            action.isEnabled = !pendingStart
            host.isEnabled = !s.active; port.isEnabled = !s.active
            handler.postDelayed(this, 500)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        pendingStart = savedInstanceState?.getBoolean("pendingStart") ?: false
        window.statusBarColor = Color.rgb(12, 22, 28)
        window.navigationBarColor = Color.rgb(12, 22, 28)
        val scroll = ScrollView(this).apply { setBackgroundColor(Color.rgb(12, 22, 28)); isFillViewport = true }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; setPadding(dp(24), dp(30), dp(24), dp(36))
        }
        scroll.addView(column)
        fun text(value: String, size: Float = 15f, bright: Boolean = false): TextView = TextView(this).apply {
            this.text = value; textSize = size
            setTextColor(if (bright) Color.WHITE else Color.rgb(167, 187, 196))
            setPadding(0, dp(7), 0, dp(7))
        }
        column.addView(text("AIR MIC", 34f, true).apply { typeface = Typeface.DEFAULT_BOLD; letterSpacing = 0.08f })
        column.addView(text("WIRELESS MICROPHONE  /  MVP 0.1", 11f).apply { letterSpacing = 0.12f })
        column.addView(text("48 kHz  ·  MONO  ·  PCM16  ·  Wi-Fi", 13f))
        column.addView(text("PC 연결", 21f, true).apply { setPadding(0, dp(24), 0, dp(4)) })
        column.addView(text("Windows PC IPv4 주소"))
        host = EditText(this).apply {
            id = R.id.host_input; setSingleLine(); hint = "192.168.0.10"
            inputType = InputType.TYPE_CLASS_PHONE
            keyListener = DigitsKeyListener.getInstance("0123456789.")
            setText(prefs.getString("host", "")); setTextColor(Color.WHITE)
        }
        column.addView(host)
        column.addView(text("UDP 포트"))
        port = EditText(this).apply {
            id = R.id.port_input; setSingleLine(); inputType = InputType.TYPE_CLASS_NUMBER
            setText(prefs.getInt("port", 49000).toString()); setTextColor(Color.WHITE)
        }
        column.addView(port)
        action = Button(this).apply {
            text = "마이크 전송 시작"; isAllCaps = false
            setTextColor(Color.rgb(8, 32, 31)); textSize = 16f
            background = GradientDrawable().apply { setColor(Color.rgb(86, 225, 195)); cornerRadius = dp(12).toFloat() }
            setOnClickListener { if (MicrophoneService.ui.active) stopTransmission() else requestStart() }
        }
        column.addView(action, LinearLayout.LayoutParams(-1, dp(56)).apply { topMargin = dp(18); bottomMargin = dp(16) })
        phase = text("대기 중", 22f, true); column.addView(phase)
        detail = text(""); column.addView(detail)
        meter = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply { max = 1000 }
        column.addView(meter, LinearLayout.LayoutParams(-1, dp(10)))
        statistics = text("", 12f).apply { typeface = Typeface.MONOSPACE; setPadding(0, dp(14), 0, dp(14)) }
        column.addView(statistics)
        column.addView(text("실시간 음성 보정", 21f, true))
        fun toggle(label: String, key: String, default: Boolean) {
            val control = Switch(this).apply {
                this.text = label; setTextColor(Color.WHITE); isChecked = prefs.getBoolean(key, default)
                setPadding(0, dp(8), 0, dp(8))
                setOnCheckedChangeListener { _, checked -> prefs.edit().putBoolean(key, checked).apply(); sendConfig() }
            }
            column.addView(control)
        }
        toggle("HPF → Gate → EQ → Compressor → Limiter", "dsp", true)
        toggle("기본 노이즈 게이트 (WebRTC 아님)", "gate", false)
        fun slider(label: String, key: String, minimum: Int, maximum: Int) {
            val initial = prefs.getFloat(key, 0f).roundToInt().coerceIn(minimum, maximum)
            val title = text("$label  ${initial} dB"); column.addView(title)
            column.addView(SeekBar(this).apply {
                max = maximum - minimum; progress = initial - minimum
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(bar: SeekBar, progress: Int, fromUser: Boolean) { title.text = "$label  ${progress + minimum} dB" }
                    override fun onStartTrackingTouch(bar: SeekBar) { title.alpha = 1f }
                    override fun onStopTrackingTouch(bar: SeekBar) {
                        prefs.edit().putFloat(key, (bar.progress + minimum).toFloat()).apply(); sendConfig()
                    }
                })
            })
        }
        slider("입력 게인", "gain", -24, 24)
        slider("저음 EQ · 180 Hz", "low", -12, 12)
        slider("중음 EQ · 1.5 kHz", "mid", -12, 12)
        slider("고음 EQ · 4.5 kHz", "high", -12, 12)
        column.addView(text("PC: CABLE Input으로 출력 → Discord/게임: CABLE Output을 마이크로 선택하세요.\n\n화면을 꺼도 알림 서비스가 마이크를 사용합니다. 종료하려면 전송 중지 또는 알림의 중지 버튼을 누르세요.\n\n암호화되지 않은 LAN 전용 전송입니다. 신뢰하는 Wi-Fi에서만 사용하세요. 30–50 ms는 목표이며 실측값이 아닙니다.", 12f))
        // Target 35 enforces edge-to-edge; keep controls clear of bars and the IME.
        scroll.setOnApplyWindowInsetsListener { view, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val bars = insets.getInsets(android.view.WindowInsets.Type.systemBars() or android.view.WindowInsets.Type.ime())
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            } else {
                @Suppress("DEPRECATION")
                view.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop, insets.systemWindowInsetRight, insets.systemWindowInsetBottom)
            }
            insets
        }
        setContentView(scroll)
        scroll.requestApplyInsets()
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).roundToInt()
    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_LONG).show()
    private fun requestStart() {
        val ip = host.text.toString().trim()
        val parts = ip.split('.')
        if (parts.size != 4 || parts.any { it.isEmpty() || it.length > 3 || it.toIntOrNull() !in 0..255 } ||
            parts[0].toIntOrNull() !in 1..223 || parts[0].toIntOrNull() == 127) {
            toast("같은 Wi-Fi PC의 올바른 IPv4 주소를 입력하세요."); return
        }
        val number = port.text.toString().toIntOrNull()
        if (number == null || number !in 1..65535) { toast("포트는 1~65535 범위여야 합니다."); return }
        prefs.edit().putString("host", ip).putInt("port", number).apply()
        val permissions = mutableListOf<String>()
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) permissions += Manifest.permission.RECORD_AUDIO
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            permissions += Manifest.permission.POST_NOTIFICATIONS
        if (permissions.isNotEmpty()) {
            pendingStart = true; requestPermissions(permissions.toTypedArray(), 10)
        } else startTransmission()
    }
    private fun startTransmission() {
        val intent = Intent(this, MicrophoneService::class.java).setAction(MicrophoneService.ACTION_START)
            .putExtra("host", prefs.getString("host", "")).putExtra("port", prefs.getInt("port", 49000))
        try { startForegroundService(intent) }
        catch (e: Exception) { toast("서비스 시작 실패: ${e.message}") }
    }
    private fun stopTransmission() {
        startService(Intent(this, MicrophoneService::class.java).setAction(MicrophoneService.ACTION_STOP))
    }
    private fun sendConfig() {
        if (MicrophoneService.ui.active) startService(
            Intent(this, MicrophoneService::class.java).setAction(MicrophoneService.ACTION_CONFIG),
        )
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 10) {
            pendingStart = false
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) startTransmission()
            else toast("마이크 권한이 거부되어 전송할 수 없습니다. 앱 설정에서 권한을 허용하세요.")
        }
    }
    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean("pendingStart", pendingStart)
        super.onSaveInstanceState(outState)
    }
    override fun onResume() { super.onResume(); handler.post(refresh) }
    override fun onPause() { handler.removeCallbacks(refresh); super.onPause() }
}
