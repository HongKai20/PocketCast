package com.pocketcast.app

import android.content.Context
import org.webrtc.AudioSource
import org.webrtc.AudioTrack
import org.webrtc.Camera2Enumerator
import org.webrtc.CameraVideoCapturer
import org.webrtc.DefaultVideoDecoderFactory
import org.webrtc.DefaultVideoEncoderFactory
import org.webrtc.EglBase
import org.webrtc.IceCandidate
import org.webrtc.MediaConstraints
import org.webrtc.MediaStreamTrack
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.RtpParameters
import org.webrtc.RtpTransceiver
import org.webrtc.RTCStatsCollectorCallback
import org.webrtc.SdpObserver
import org.webrtc.SessionDescription
import org.webrtc.SurfaceTextureHelper
import org.webrtc.VideoSource
import org.webrtc.VideoTrack
import org.webrtc.audio.JavaAudioDeviceModule

/**
 * WebRTC 推流端：H.264 硬编（MediaCodec）+ Opus 48kHz 单声道 10ms 帧。
 * 只有开关打开时才真正启动采集，关闭立刻释放摄像头/麦克风。
 */
class WebRtcSession(private val context: Context, private val listener: Listener) {

    interface Listener {
        fun onAnswer(sdp: String)
        fun onLocalCandidate(candidate: String, sdpMid: String, sdpMLineIndex: Int)
        fun onStats(rttMs: Double)
        fun onError(message: String)
        fun onConnected()
    }

    private val eglBase: EglBase = EglBase.create()
    private val adm: JavaAudioDeviceModule
    private val factory: PeerConnectionFactory
    private var pc: PeerConnection? = null
    private var audioSource: AudioSource? = null
    private var audioTrack: AudioTrack? = null
    private var videoSource: VideoSource? = null
    private var videoTrack: VideoTrack? = null
    private var capturer: CameraVideoCapturer? = null
    private var surfaceHelper: SurfaceTextureHelper? = null
    private var audioEnabled = false
    private var videoEnabled = false
    private var audioStartRequested = false
    private var pausedForScan = false
    private var currentWidth = 1280
    private var currentHeight = 720
    private var statsRunning = false

    init {
        PeerConnectionFactory.initialize(
            PeerConnectionFactory.InitializationOptions.builder(context).createInitializationOptions()
        )
        adm = JavaAudioDeviceModule.builder(context)
            .setUseHardwareAcousticEchoCanceler(true)
            .setUseHardwareNoiseSuppressor(true)
            .setAudioSource(android.media.MediaRecorder.AudioSource.VOICE_COMMUNICATION)
            .setUseStereoInput(false)
            .setUseStereoOutput(false)
            .createAudioDeviceModule()
        factory = PeerConnectionFactory.builder()
            .setOptions(PeerConnectionFactory.Options())
            .setVideoEncoderFactory(DefaultVideoEncoderFactory(eglBase.eglBaseContext, true, true))
            .setVideoDecoderFactory(DefaultVideoDecoderFactory(eglBase.eglBaseContext))
            .setAudioDeviceModule(adm)
            .createPeerConnectionFactory()
    }

    /** 收到电脑的 offer 后调用：加两路 sendonly 轨道并回 answer。 */
    fun handleOffer(offerSdp: String) {
        try {
            closePeer()

            val config = PeerConnection.RTCConfiguration(emptyList()).apply {
                sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
                continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
                bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
            }
            val peer = factory.createPeerConnection(config, observer) ?: run {
                listener.onError("创建 PeerConnection 失败"); return
            }
            pc = peer

            audioSource = factory.createAudioSource(MediaConstraints())
            audioTrack = factory.createAudioTrack("audio0", audioSource)
            videoSource = factory.createVideoSource(false)
            videoTrack = factory.createVideoTrack("video0", videoSource)

            audioTrack?.setEnabled(audioEnabled)
            videoTrack?.setEnabled(videoEnabled)
            if (audioEnabled) applyAudioCapture(true)
            if (videoEnabled) startCamera(currentWidth, currentHeight)

            // 轨道必须在 setRemoteDescription 之后挂到「与 offer 对应的」transceiver 上。
            // 之前用 addTransceiver 预先建轨，实测 libwebrtc 会把它当成新的 transceiver，
            // 结果 answer 里两个 m 行都是 a=inactive，电脑端一个 RTP 包也收不到。
            peer.setRemoteDescription(
                object : SdpObserver {
                    override fun onCreateSuccess(p0: SessionDescription?) {}
                    override fun onSetSuccess() {
                        attachLocalTracks(peer)
                        createAnswer(peer)
                    }
                    override fun onCreateFailure(p0: String?) {}
                    override fun onSetFailure(p0: String?) {
                        listener.onError("设置远端 SDP 失败：$p0")
                    }
                },
                SessionDescription(SessionDescription.Type.OFFER, offerSdp)
            )
        } catch (e: Exception) {
            listener.onError("处理 offer 失败：${e.message}")
        }
    }

    /** 把本地音视频轨挂到 offer 对应的 transceiver 上，并把方向改成 sendonly。 */
    private fun attachLocalTracks(peer: PeerConnection) {
        for (t in peer.transceivers) {
            val kind = try { t.receiver?.track()?.kind() } catch (_: Exception) { null }
            val track = when (kind) {
                "audio" -> audioTrack
                "video" -> videoTrack
                else -> null
            } ?: continue
            t.sender?.setTrack(track, false)
            t.direction = RtpTransceiver.RtpTransceiverDirection.SEND_ONLY
        }
    }

    private fun createAnswer(peer: PeerConnection) {
        peer.createAnswer(
            object : SdpObserver {
                override fun onCreateSuccess(answer: SessionDescription?) {
                    if (answer == null) return
                    peer.setLocalDescription(
                        object : SdpObserver {
                            override fun onCreateSuccess(p0: SessionDescription?) {}
                            override fun onSetSuccess() {
                                listener.onAnswer(answer.description)
                            }
                            override fun onCreateFailure(p0: String?) {}
                            override fun onSetFailure(p0: String?) {
                                listener.onError("设置本地 SDP 失败：$p0")
                            }
                        },
                        answer
                    )
                }
                override fun onSetSuccess() {}
                override fun onCreateFailure(p0: String?) {
                    listener.onError("createAnswer 失败：$p0")
                }
                override fun onSetFailure(p0: String?) {}
            },
            MediaConstraints()
        )
    }

    fun addRemoteCandidate(candidate: String, sdpMid: String, mLineIndex: Int) {
        try {
            pc?.addIceCandidate(IceCandidate(sdpMid, mLineIndex, candidate))
        } catch (_: Exception) {
        }
    }

    /** 真正开始/停止麦克风采集。setAudioRecordEnabled 只是设置“是否使用 AudioRecord”的开关，
     *  必须再调 requestStartRecording 才会启动录音，否则永远没有音频 RTP。
     *
     *  两个细节决定了不会闪退：
     *  1) 用 audioStartRequested 记住状态，重复打开开关时不再重复 requestStartRecording
     *     （重复 start 会让同一个 AudioRecord 被两条采集线程同时读取）；
     *  2) 停止时必须先 requestStopRecording 再 setAudioRecordEnabled(false)，让采集线程
     *     先退出，之后才允许释放 AudioRecord / 销毁工厂。 */
    private fun applyAudioCapture(enabled: Boolean) {
        try {
            if (enabled) {
                adm.setAudioRecordEnabled(true)
                if (!audioStartRequested) {
                    adm.requestStartRecording()
                    audioStartRequested = true
                }
            } else {
                if (audioStartRequested) {
                    adm.requestStopRecording()
                    audioStartRequested = false
                }
                adm.setAudioRecordEnabled(false)
            }
        } catch (_: Exception) {
        }
    }

    fun setAudioEnabled(enabled: Boolean) {
        audioEnabled = enabled
        audioTrack?.setEnabled(enabled)
        applyAudioCapture(enabled)
    }

    fun setVideoEnabled(enabled: Boolean) {
        videoEnabled = enabled
        if (enabled) startCamera(currentWidth, currentHeight) else stopCamera()
        videoTrack?.setEnabled(enabled)
    }

    /** 720p → 480p 自适应。 */
    fun setResolution(width: Int, height: Int) {
        currentWidth = width
        currentHeight = height
        if (videoEnabled) {
            if (capturer != null) {
                try {
                    capturer?.changeCaptureFormat(width, height, 30)
                } catch (_: Exception) {
                    stopCamera(); startCamera(width, height)
                }
            } else {
                startCamera(width, height)
            }
        }
    }

    fun currentResolution(): Pair<Int, Int> = currentWidth to currentHeight

    /** 打开扫码界面之前调用：把相机句柄让出来，否则扫码界面会「找不到相机」。 */
    fun pauseVideoForScan() {
        pausedForScan = true
        stopCamera()
    }

    /** 扫码界面关闭后调用（没连上也没关系）：恢复视频采集。 */
    fun resumeVideoAfterScan() {
        if (!pausedForScan) return
        pausedForScan = false
        if (videoEnabled) startCamera(currentWidth, currentHeight)
    }

    /** 电脑端发现画面停滞时调用：重启视频轨，新编码器会话一定会产生关键帧（IDR）。 */
    fun requestKeyFrame() {
        if (!videoEnabled || pausedForScan) return
        try {
            stopCamera()
            android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({
                if (videoEnabled && !pausedForScan) startCamera(currentWidth, currentHeight)
            }, 200)
        } catch (_: Exception) {
        }
    }

    /** 断开当前电脑但保留工厂/音频设备：重连时复用，避免反复销毁 WebRtcAudioRecord 触发原生崩溃。 */
    fun reset() {
        statsRunning = false
        audioEnabled = false
        videoEnabled = false
        pausedForScan = false
        applyAudioCapture(false)
        closePeer()
    }

    private fun startCamera(width: Int, height: Int) {
        // 扫码界面占用相机期间不要抢：等 resumeVideoAfterScan 再启动。
        if (pausedForScan) return
        // 没有视频源（还没收到电脑的 offer）时不能启动：CameraCapturer 会拿到 null 观察者，
        // 相机就绪回调里必然空指针闪退。等 handleOffer 建好视频轨后会再次调用本方法。
        val source = videoSource
        if (source == null) {
            listener.onError("视频通道还没建立，连上电脑后再开摄像头")
            return
        }
        try {
            if (capturer == null) {
                val enumerator = Camera2Enumerator(context)
                val name = enumerator.deviceNames.firstOrNull { enumerator.isFrontFacing(it) }
                    ?: enumerator.deviceNames.firstOrNull { enumerator.isBackFacing(it) }
                    ?: run { listener.onError("没有可用摄像头"); return }
                val c = enumerator.createCapturer(name, null)
                val helper = SurfaceTextureHelper.create("PocketCastCapture", eglBase.eglBaseContext)
                c.initialize(helper, context, source.capturerObserver)
                capturer = c
                surfaceHelper = helper
            }
            capturer?.startCapture(width, height, 30)
        } catch (e: Exception) {
            listener.onError("摄像头启动失败：${e.message}")
        }
    }

    private fun stopCamera() {
        try {
            capturer?.stopCapture()
        } catch (_: Exception) {
        }
    }

    fun startStatsLoop() {
        if (statsRunning) return
        statsRunning = true
        val handler = android.os.Handler(android.os.Looper.getMainLooper())
        val tick = object : Runnable {
            override fun run() {
                if (!statsRunning) return
                // 从 candidate-pair 统计里取当前往返时延（不依赖具体统计类，兼容各版本 SDK）
                pc?.getStats(object : RTCStatsCollectorCallback {
                    override fun onStatsDelivered(report: org.webrtc.RTCStatsReport?) {
                        val map = report?.statsMap ?: return
                        var best = -1.0
                        for (s in map.values) {
                            if (s.type != "candidate-pair") continue
                            val state = s.members["state"]?.toString()
                            val nominated = s.members["nominated"]
                            if (state != null && state != "succeeded") continue
                            if (nominated is Boolean && !nominated) continue
                            val rtt = s.members["currentRoundTripTime"]
                            val v = when (rtt) {
                                is Double -> rtt
                                is Number -> rtt.toDouble()
                                else -> -1.0
                            }
                            if (v > 0 && v < 1.0) best = if (best < 0) v * 1000 else minOf(best, v * 1000)
                        }
                        if (best >= 0) listener.onStats(best)
                    }
                })
                handler.postDelayed(this, 1000)
            }
        }
        handler.post(tick)
    }

    private val observer = object : PeerConnection.Observer {
        override fun onSignalingChange(state: PeerConnection.SignalingState?) {}
        override fun onIceConnectionChange(state: PeerConnection.IceConnectionState?) {
            if (state == PeerConnection.IceConnectionState.CONNECTED) listener.onConnected()
        }
        override fun onIceConnectionReceivingChange(receiving: Boolean) {}
        override fun onIceGatheringChange(state: PeerConnection.IceGatheringState?) {}
        override fun onIceCandidate(candidate: IceCandidate?) {
            val c = candidate ?: return
            listener.onLocalCandidate(c.sdp, c.sdpMid ?: "0", c.sdpMLineIndex)
        }
        override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>?) {}
        override fun onAddStream(stream: org.webrtc.MediaStream?) {}
        override fun onRemoveStream(stream: org.webrtc.MediaStream?) {}
        override fun onDataChannel(dc: org.webrtc.DataChannel?) {}
        override fun onRenegotiationNeeded() {}
    }

    private fun closePeer() {
        // 先停相机：capturer 持有旧 videoSource 的观察者，源释放后相机回调会空指针
        try { stopCamera() } catch (_: Exception) {}
        try { capturer?.dispose() } catch (_: Exception) {}
        try { surfaceHelper?.dispose() } catch (_: Exception) {}
        capturer = null
        surfaceHelper = null
        try { pc?.close() } catch (_: Exception) {}
        try { pc?.dispose() } catch (_: Exception) {}
        pc = null
        try { audioSource?.dispose() } catch (_: Exception) {}
        try { videoSource?.dispose() } catch (_: Exception) {}
        audioSource = null
        videoSource = null
        audioTrack = null
        videoTrack = null
    }

    fun dispose() {
        reset()
        // 销毁工厂必须等录音线程彻底退出：否则 nativeDataIsRecorded 会在空指针上 memcpy，
        // 直接 SIGSEGV（这就是「重新扫码/断开重连时闪退」的根因）。延迟 300ms 释放，给线程收尾时间。
        val f = factory
        val egl = eglBase
        android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({
            try { f.dispose() } catch (_: Exception) {}
            try { egl.release() } catch (_: Exception) {}
        }, 300)
    }

    fun isAudioOn() = audioEnabled
    fun isVideoOn() = videoEnabled
}
