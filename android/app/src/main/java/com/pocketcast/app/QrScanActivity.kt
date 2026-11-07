package com.pocketcast.app

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.Image
import android.media.ImageReader
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.TypedValue
import android.view.Surface
import android.view.TextureView
import android.widget.TextView
import android.widget.Toast
import com.google.zxing.BinaryBitmap
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import java.nio.ByteBuffer

/**
 * 二维码扫描：Camera2 预览 + ZXing 解码，识别电脑端显示的配对码。
 *
 * 这个界面最容易崩在两处，代码里都做了防护：
 * 1) 识别成功 finish 之后，相机回调可能还会进来 —— 用 finished 标记 + 先停重复请求；
 * 2) onDestroy 关掉 ImageReader/CameraDevice 之后，后台线程还可能有排队回调 ——
 *    先摘掉监听、置空引用，再关闭，最后才退出 HandlerThread。
 */
class QrScanActivity : Activity() {

    private lateinit var textureView: TextureView
    private lateinit var hint: TextView
    private var cameraDevice: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var imageReader: ImageReader? = null
    private var previewSurface: Surface? = null
    private val background = HandlerThread("qr-camera").apply { start() }
    private val handler = Handler(background.looper)
    private var handled = false
    private var opening = false
    private var lastDecodeMs = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = android.widget.FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        textureView = TextureView(this)
        root.addView(textureView, android.widget.FrameLayout.LayoutParams(-1, -1))
        hint = TextView(this).apply {
            text = "将电脑屏幕上的二维码放入画面"
            setTextColor(Color.WHITE)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
            setPadding(24, 48, 24, 24)
        }
        root.addView(hint)
        setContentView(root)

        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), 2001)
        } else {
            openCamera()
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) openCamera()
        else {
            Toast.makeText(this, "没有相机权限，无法扫码", Toast.LENGTH_SHORT).show()
            finish()
        }
    }

    private fun openCamera() {
        textureView.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(st: SurfaceTexture, w: Int, h: Int) = startCamera()
            override fun onSurfaceTextureSizeChanged(st: SurfaceTexture, w: Int, h: Int) {}
            // 返回 true 让 TextureView 真正释放 SurfaceTexture，避免重新进入时残留旧纹理
            override fun onSurfaceTextureDestroyed(st: SurfaceTexture) = true
            override fun onSurfaceTextureUpdated(st: SurfaceTexture) {}
        }
        if (textureView.isAvailable) startCamera()
    }

    private fun startCamera() {
        if (opening || handled) return
        opening = true

        val manager = getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val ids = try { manager.cameraIdList } catch (e: Exception) { emptyArray() }
        if (ids.isEmpty()) {
            hint.text = "找不到相机：请确认相机未被其他 App 占用"
            opening = false
            return
        }
        val cameraId = ids.firstOrNull { id ->
            val c = manager.getCameraCharacteristics(id)
            c.get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK
        } ?: ids.firstOrNull()!!

        imageReader = ImageReader.newInstance(1280, 720, android.graphics.ImageFormat.YUV_420_888, 2).apply {
            setOnImageAvailableListener({ reader -> onImage(reader.acquireLatestImage()) }, handler)
        }

        try {
            manager.openCamera(cameraId, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) {
                    if (handled) { device.close(); return }
                    cameraDevice = device
                    val texture = textureView.surfaceTexture
                    if (texture == null) {
                        hint.text = "预览画面还没准备好，请重试"
                        device.close()
                        opening = false
                        return
                    }
                    previewSurface = Surface(texture)
                    val surfaces = listOf(previewSurface!!, imageReader!!.surface)
                    device.createCaptureSession(surfaces, object : CameraCaptureSession.StateCallback() {
                        override fun onConfigured(s: CameraCaptureSession) {
                            if (handled) { s.close(); return }
                            session = s
                            try {
                                val req = device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                                    addTarget(previewSurface!!)
                                    addTarget(imageReader!!.surface)
                                    set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
                                }
                                s.setRepeatingRequest(req.build(), null, handler)
                            } catch (e: Exception) {
                                hint.text = "相机启动失败：${e.message}"
                            }
                        }

                        override fun onConfigureFailed(s: CameraCaptureSession) {
                            hint.text = "相机配置失败，请退出重试"
                        }
                    }, handler)
                }

                override fun onDisconnected(device: CameraDevice) {
                    device.close()
                    if (cameraDevice === device) cameraDevice = null
                    opening = false
                }

                override fun onError(device: CameraDevice, error: Int) {
                    device.close()
                    if (cameraDevice === device) cameraDevice = null
                    opening = false
                    if (!handled) {
                        hint.text = if (error == ERROR_CAMERA_IN_USE)
                            "相机被占用：请先在主界面关闭摄像头开关再扫码"
                        else
                            "相机错误 $error，请退出重试"
                    }
                }
            }, handler)
        } catch (e: Exception) {
            opening = false
            hint.text = "打开相机失败：${e.message}"
        }
    }

    private fun onImage(image: Image?) {
        if (image == null) return
        if (handled) { image.close(); return }
        try {
            // 限制解码频率，避免每帧都跑 ZXing 把主线程/相机线程拖死
            val now = SystemClock.elapsedRealtime()
            if (now - lastDecodeMs < 100) return
            lastDecodeMs = now

            val plane = image.planes[0]
            val buffer: ByteBuffer = plane.buffer
            val width = image.width
            val height = image.height
            val rowStride = plane.rowStride
            val data = ByteArray(rowStride * height)
            buffer.get(data)
            val source = PlanarYUVLuminanceSource(data, rowStride, height, 0, 0, width, height, false)
            val bitmap = BinaryBitmap(HybridBinarizer(source))
            val result = QRCodeReader().decode(bitmap)

            handled = true
            // 先停掉相机，再返回结果：否则 finish 之后画面仍在解码，回调会打到已销毁的界面
            try { session?.stopRepeating() } catch (_: Exception) {}
            setResult(RESULT_OK, Intent().putExtra("payload", result.text))
            finish()
        } catch (_: Exception) {
            // 没识别到二维码是正常情况，继续下一帧
        } finally {
            image.close()
        }
    }

    override fun onDestroy() {
        handled = true
        try { imageReader?.setOnImageAvailableListener(null, null) } catch (_: Exception) {}
        try { session?.stopRepeating() } catch (_: Exception) {}
        try { session?.close() } catch (_: Exception) {}
        try { cameraDevice?.close() } catch (_: Exception) {}
        try { imageReader?.close() } catch (_: Exception) {}
        try { previewSurface?.release() } catch (_: Exception) {}
        session = null
        cameraDevice = null
        imageReader = null
        previewSurface = null
        background.quitSafely()
        super.onDestroy()
    }
}
