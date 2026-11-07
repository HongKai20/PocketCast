package com.pocketcast.app

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.media.AudioManager
import android.media.AudioTrack
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.provider.Settings
import android.text.InputType
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Spinner
import android.widget.ArrayAdapter
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import org.json.JSONObject
import kotlin.math.sin

/**
 * 手机端主界面：两个大开关 + 分辨率/音量 + 连接状态与实时延迟。
 */
class MainActivity : Activity() {

    private lateinit var statusView: TextView
    private lateinit var latencyView: TextView
    private lateinit var switchAudio: Switch
    private lateinit var switchVideo: Switch
    private lateinit var resolutionSpinner: Spinner
    private lateinit var volumeBar: SeekBar
    private lateinit var deviceHint: TextView

    private var signaling: SignalingClient? = null
    private var rtc: WebRtcSession? = null
    private var nsd: NsdScanner? = null
    private var connected = false
    private var clockSynced = false
    private var clockOffsetMs = 0.0
    private var lastProbeResult = ""

    private val main = Handler(Looper.getMainLooper())
    private val resolutions = arrayOf("720p (1280x720)", "480p (640x480)", "自适应")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        buildUi()
        requestPermissionsIfNeeded()
        startDiscovery()
    }

    private fun dp(v: Int): Int = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), resources.displayMetrics).toInt()

    private fun buildUi() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.parseColor("#0E1116"))
            setPadding(dp(20), dp(28), dp(20), dp(20))
        }

        val title = TextView(this).apply {
            text = "PocketCast"
            setTextColor(Color.WHITE)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 28f)
        }
        root.addView(title)

        statusView = TextView(this).apply {
            text = "未连接"
            setTextColor(Color.parseColor("#9AA4B2"))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            setPadding(0, dp(6), 0, dp(14))
        }
        root.addView(statusView)

        root.addView(actionRow())

        deviceHint = TextView(this).apply {
            text = "正在搜索同一 WiFi 下的电脑…"
            setTextColor(Color.parseColor("#7C8798"))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            setPadding(0, dp(10), 0, dp(14))
        }
        root.addView(deviceHint)

        switchAudio = bigSwitch("🎙  麦克风", "#1F6FEB")
        switchVideo = bigSwitch("📷  摄像头", "#2EA043")
        root.addView(switchAudio)
        root.addView(space(12))
        root.addView(switchVideo)

        switchAudio.setOnCheckedChangeListener { _, on ->
            rtc?.setAudioEnabled(on)
            sendControl()
            statusView.text = if (on) "麦克风已开启" else "麦克风已关闭（硬件已释放）"
        }
        switchVideo.setOnCheckedChangeListener { _, on ->
            rtc?.setVideoEnabled(on)
            sendControl()
            statusView.text = if (on) "摄像头已开启" else "摄像头已关闭（硬件已释放）"
        }

        root.addView(space(18))

        val resLabel = label("分辨率")
        root.addView(resLabel)
        resolutionSpinner = Spinner(this).apply {
            adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, resolutions)
            setOnItemSelectedListener(object : android.widget.AdapterView.OnItemSelectedListener {
                override fun onItemSelected(parent: android.widget.AdapterView<*>?, view: View?, position: Int, id: Long) {
                    val (w, h) = when (position) {
                        0 -> 1280 to 720
                        1 -> 640 to 480
                        else -> 960 to 540
                    }
                    rtc?.setResolution(w, h)
                    sendControl()
                }
                override fun onNothingSelected(parent: android.widget.AdapterView<*>?) {}
            })
        }
        root.addView(resolutionSpinner)

        val volLabel = label("音量（测试蜂鸣 / 媒体音量）")
        root.addView(volLabel)
        volumeBar = SeekBar(this).apply {
            max = 15
            progress = 7
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                    val am = getSystemService(AUDIO_SERVICE) as AudioManager
                    am.setStreamVolume(AudioManager.STREAM_MUSIC, progress, 0)
                }
                override fun onStartTrackingTouch(seekBar: SeekBar?) {}
                override fun onStopTrackingTouch(seekBar: SeekBar?) {}
            })
        }
        root.addView(volumeBar)

        root.addView(space(10))
        val probeButton = Button(this).apply {
            text = "延迟自测（闪光 + 蜂鸣）"
            setOnClickListener { runProbe() }
        }
        root.addView(probeButton, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(52)))

        latencyView = TextView(this).apply {
            text = "延迟：等待连接"
            setTextColor(Color.parseColor("#58A6FF"))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
            setPadding(0, dp(14), 0, 0)
        }
        root.addView(latencyView)

        val scroll = ScrollView(this).apply { addView(root) }
        setContentView(scroll)
    }

    private fun actionRow(): LinearLayout {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.START
        }
        val scan = Button(this).apply {
            text = "扫码连接"
            setOnClickListener {
                // 扫码界面要用后置相机：先把 WebRTC 采集停掉，否则相机被占用会「找不到相机」。
                rtc?.pauseVideoForScan()
                startActivityForResult(Intent(this@MainActivity, QrScanActivity::class.java), REQ_QR)
            }
        }
        val discover = Button(this).apply {
            text = "刷新发现"
            setOnClickListener { startDiscovery() }
            setOnLongClickListener { showManualDialog(); true }
        }
        row.addView(scan, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        row.addView(discover, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        return row
    }

    private fun label(text: String): TextView = TextView(this).apply {
        this.text = text
        setTextColor(Color.parseColor("#9AA4B2"))
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
        setPadding(0, dp(10), 0, dp(2))
    }

    private fun space(height: Int): View = View(this).apply {
        layoutParams = LinearLayout.LayoutParams(1, dp(height))
    }

    private fun bigSwitch(text: String, colorHex: String): Switch = Switch(this).apply {
        this.text = text
        isChecked = false
        setTextColor(Color.WHITE)
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 22f)
        setPadding(dp(20), 0, dp(20), 0)
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(96))
        background = android.graphics.drawable.GradientDrawable().apply {
            setColor(Color.parseColor(colorHex))
            cornerRadius = dp(18).toFloat()
        }
        buttonTintList = android.content.res.ColorStateList.valueOf(Color.WHITE)
        thumbTintList = android.content.res.ColorStateList.valueOf(Color.WHITE)
    }

    private fun requestPermissionsIfNeeded() {
        val need = mutableListOf<String>()
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) need += Manifest.permission.CAMERA
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) need += Manifest.permission.RECORD_AUDIO
        // Android 13+ 前台服务的「正在推流」通知需要通知权限（不给也能运行，只是通知不显示）
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) need += Manifest.permission.POST_NOTIFICATIONS
        if (need.isNotEmpty()) requestPermissions(need.toTypedArray(), REQ_PERMS)
    }

    private fun startDiscovery() {
        nsd?.stop()
        nsd = NsdScanner(this) { name, host, port, token ->
            deviceHint.text = "发现电脑：$name  $host:$port（点“扫码连接”旁的刷新可继续搜索）"
        }
        nsd?.start()
        main.postDelayed({
            if (deviceHint.text.contains("正在搜索")) deviceHint.text = "未发现电脑：可点“扫码连接”，或长按“刷新发现”手动输入 IP"
        }, 4000)
    }

    private fun showManualDialog() {
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(12), dp(8), dp(12), dp(8))
        }
        val ip = EditText(this).apply { hint = "电脑 IP，例如 192.168.1.5"; inputType = InputType.TYPE_CLASS_TEXT }
        val token = EditText(this).apply { hint = "6 位配对码"; inputType = InputType.TYPE_CLASS_NUMBER }
        box.addView(ip)
        box.addView(token)
        AlertDialog.Builder(this)
            .setTitle("手动连接电脑")
            .setView(box)
            .setPositiveButton("连接") { _, _ -> connectTo(ip.text.toString().trim(), 8765, token.text.toString().trim()) }
            .setNegativeButton("取消", null)
            .show()
    }

    private val rtcListener = object : WebRtcSession.Listener {
        override fun onAnswer(sdp: String) {
            signaling?.send(JSONObject().put("t", "answer").put("sdp", sdp))
        }
        override fun onLocalCandidate(candidate: String, sdpMid: String, sdpMLineIndex: Int) {
            signaling?.send(JSONObject().put("t", "candidate").put("candidate", candidate)
                .put("sdpMid", sdpMid).put("sdpMLineIndex", sdpMLineIndex))
        }
        override fun onStats(rttMs: Double) {
            // onStatsDelivered 在 WebRTC 统计线程回调，必须切回主线程再碰控件，
            // 否则会抛 CalledFromWrongThreadException，被 JNI 的 ExceptionCheck 捕获后直接 SIGABRT
            main.post {
                latencyView.text = buildString {
                    append("网络往返 RTT：%.1f ms".format(rttMs))
                    if (lastProbeResult.isNotEmpty()) append("\n$lastProbeResult")
                }
            }
        }
        override fun onError(message: String) {
            main.post {
                statusView.text = message
                if (message.contains("相机") || message.contains("摄像头")) {
                    Toast.makeText(this@MainActivity, message, Toast.LENGTH_SHORT).show()
                }
            }
        }
        override fun onConnected() {
            main.post { statusView.text = "已连接，视频通道已建立" }
        }
    }

    /** 整个 App 生命周期只用一个 WebRtcSession：重连只重置 PeerConnection，
     *  不销毁 PeerConnectionFactory / AudioDeviceModule，避免音频采集线程与销毁竞争导致闪退。 */
    private fun ensureSession(): WebRtcSession {
        rtc?.let { return it }
        return WebRtcSession(this, rtcListener).also { rtc = it }
    }

    private fun connectTo(host: String, port: Int, token: String) {
        disconnect()
        val session = ensureSession()

        val client = SignalingClient(host, port, token, Build.MODEL, object : SignalingClient.Listener {
            override fun onConnected() {
                connected = true
                statusView.text = "已连接电脑 $host:$port"
                deviceHint.text = "已连接 $host"
                CastService.start(this@MainActivity)
                ensureBatteryUnrestricted()
                session.startStatsLoop()
                requestClockSync()
            }

            override fun onMessage(msg: JSONObject) {
                when (msg.optString("t")) {
                    "offer" -> session.handleOffer(msg.optString("sdp"))
                    "candidate" -> session.addRemoteCandidate(
                        msg.optString("candidate"), msg.optString("sdpMid", "0"), msg.optInt("sdpMLineIndex", 0)
                    )
                    "clock_ack" -> onClockAck(msg)
                    "probe_result" -> onProbeResult(msg)
                    "keyframe" -> {
                        // 电脑端发现画面停滞：重启视频轨，强制产生新的 IDR
                        session.requestKeyFrame()
                        statusView.text = "画面停滞：正在向电脑重发关键帧"
                    }
                    "reject" -> {
                        statusView.text = "配对失败：" + msg.optString("reason")
                        disconnect()
                    }
                    "bye" -> {
                        statusView.text = "电脑已断开：" + msg.optString("reason")
                        disconnect()
                    }
                }
            }

            override fun onClosed(reason: String) {
                connected = false
                if (reason.contains("失败")) statusView.text = reason
            }
        })
        signaling = client
        statusView.text = "正在连接 $host:$port …"
        client.connect()
    }

    /** 熄屏后系统会进 Doze 限制网络：申请「不受电池优化限制」，长时间锁屏也不断流。
     *  一次性系统弹窗，用户拒绝也不影响短时间使用。 */
    private fun ensureBatteryUnrestricted() {
        if (Build.VERSION.SDK_INT < 23) return
        try {
            val pm = getSystemService(POWER_SERVICE) as PowerManager
            if (pm.isIgnoringBatteryOptimizations(packageName)) return
            main.post {
                try {
                    startActivity(
                        Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName"))
                    )
                } catch (_: Exception) {
                    // 部分 ROM 没有这个系统页面，忽略即可
                }
            }
        } catch (_: Exception) {
        }
    }

    private fun sendControl() {
        signaling?.send(JSONObject().put("t", "control")
            .put("audio", switchAudio.isChecked)
            .put("video", switchVideo.isChecked)
            .put("height", rtc?.currentResolution()?.second ?: 720))
    }

    private fun requestClockSync() {
        signaling?.send(JSONObject().put("t", "clock").put("t1", SystemClock.elapsedRealtime()))
    }

    private fun onClockAck(msg: JSONObject) {
        val t1 = msg.optLong("t1")
        val t2 = msg.optLong("t2")
        val t3 = msg.optLong("t3")
        val t4 = SystemClock.elapsedRealtime()
        val rtt = (t4 - t1) - (t3 - t2)
        val offset = ((t2 - t1) + (t3 - t4)) / 2.0
        clockOffsetMs = offset
        clockSynced = (rtt in 0..50)
        signaling?.send(JSONObject().put("t", "clock_result").put("rtt", rtt.toDouble()).put("offset", offset))
        latencyView.text = "时钟同步 RTT %d ms".format(rtt)
    }

    private fun onProbeResult(msg: JSONObject) {
        val audio = msg.optDouble("audioMs", Double.NaN)
        val video = msg.optDouble("videoMs", Double.NaN)
        lastProbeResult = buildString {
            append("实测端到端：")
            append(if (audio.isNaN()) "音频 未检测到" else "音频 %.0f ms".format(audio))
            append(" / ")
            append(if (video.isNaN()) "视频 未检测到" else "视频 %.0f ms".format(video))
        }
        latencyView.text = lastProbeResult
    }

    /** 手机时钟（毫秒）：与电脑做 NTP 式同步后用来对齐闪光/蜂鸣。 */
    private fun nowMs(): Long = SystemClock.elapsedRealtime() - clockOffsetMs.toLong()

    private fun runProbe() {
        if (!connected) { Toast.makeText(this, "请先连接电脑", Toast.LENGTH_SHORT).show(); return }
        if (!clockSynced) requestClockSync()
        val t0 = SystemClock.elapsedRealtime() + 120
        signaling?.send(JSONObject().put("t", "probe").put("t0", t0))
        main.postDelayed({
            flash()
            beep()
        }, 120)
    }

    private fun flash() {
        val overlay = View(this).apply { setBackgroundColor(Color.WHITE) }
        val decor = window.decorView as ViewGroup
        decor.addView(overlay, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        main.postDelayed({ (decor).removeView(overlay) }, 130)
    }

    private fun beep() {
        try {
            val sr = 48000
            val n = sr / 10 // 100ms
            val samples = FloatArray(n)
            for (i in 0 until n) samples[i] = (0.9 * sin(2.0 * Math.PI * 1000.0 * i / sr)).toFloat()
            val track = AudioTrack.Builder()
                .setAudioAttributes(android.media.AudioAttributes.Builder()
                    .setUsage(android.media.AudioAttributes.USAGE_MEDIA)
                    .setContentType(android.media.AudioAttributes.CONTENT_TYPE_MUSIC).build())
                .setAudioFormat(android.media.AudioFormat.Builder()
                    .setEncoding(android.media.AudioFormat.ENCODING_PCM_FLOAT)
                    .setSampleRate(sr).setChannelMask(android.media.AudioFormat.CHANNEL_OUT_MONO).build())
                .setTransferMode(AudioTrack.MODE_STATIC)
                .setBufferSizeInBytes(n * 4)
                .build()
            track.write(samples, 0, n, AudioTrack.WRITE_BLOCKING)
            track.play()
            main.postDelayed({ try { track.stop(); track.release() } catch (_: Exception) {} }, 400)
        } catch (_: Exception) {}
    }

    private fun disconnect() {
        connected = false
        signaling?.close()
        signaling = null
        CastService.stop(this)
        // 只重置 PeerConnection，保留工厂与音频设备（见 ensureSession 的说明）
        rtc?.reset()
        switchAudio.isChecked = false
        switchVideo.isChecked = false
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        var reconnected = false
        if (requestCode == REQ_QR && resultCode == RESULT_OK) {
            val payload = data?.getStringExtra("payload")
            if (payload != null) {
                try {
                    val obj = JSONObject(payload)
                    connectTo(obj.getString("host"), obj.optInt("port", 8765), obj.getString("token"))
                    reconnected = true
                } catch (e: Exception) {
                    Toast.makeText(this, "二维码内容无效", Toast.LENGTH_SHORT).show()
                }
            }
        }
        // 扫码界面关掉了相机：没重连就恢复原来的视频采集
        if (!reconnected) rtc?.resumeVideoAfterScan()
    }

    override fun onDestroy() {
        disconnect()
        rtc?.dispose()
        rtc = null
        nsd?.stop()
        super.onDestroy()
    }

    companion object {
        private const val REQ_PERMS = 1001
        private const val REQ_QR = 1002
    }
}
