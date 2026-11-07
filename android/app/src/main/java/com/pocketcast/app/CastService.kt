package com.pocketcast.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.net.wifi.WifiManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log

/**
 * 前台服务：手机锁屏后保持 CPU 与 WiFi 不休眠，避免 WebRTC 采集/连接被系统掐断。
 * 连接建立时启动，断开时停止。
 */
class CastService : Service() {

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
        startInForeground()

        try {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PocketCast:cast").apply {
                setReferenceCounted(false)
                acquire(8 * 60 * 60 * 1000L)
            }
        } catch (_: Exception) {
        }

        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "PocketCast:wifi").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {
        }
    }

    /**
     * Android 14 起，声明了 camera|microphone 的服务必须用三参 startForeground 明确类型，
     * 否则系统会把「仅在使用时允许」的相机/麦克风权限按后台处理 —— 锁屏那一刻采集就被掐断。
     */
    private fun startInForeground() {
        val notification = buildNotification()
        if (Build.VERSION.SDK_INT >= 29) {
            val type = ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA or
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            try {
                startForeground(NOTIFICATION_ID, notification, type)
                return
            } catch (e: Exception) {
                Log.e(TAG, "startForeground(类型 camera|microphone) 失败：${e.message}")
            }
        }
        try {
            startForeground(NOTIFICATION_ID, notification)
        } catch (e: Exception) {
            Log.e(TAG, "startForeground 失败：${e.message}")
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_NOT_STICKY

    override fun onDestroy() {
        try { wakeLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        try { wifiLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        wakeLock = null
        wifiLock = null
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun createChannel() {
        val nm = getSystemService(NotificationManager::class.java) ?: return
        val channel = NotificationChannel(
            CHANNEL_ID,
            "投屏运行中",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "保持手机摄像头/麦克风持续传给电脑"
            setShowBadge(false)
        }
        nm.createNotificationChannel(channel)
    }

    private fun buildNotification(): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val builder = if (Build.VERSION.SDK_INT >= 26) {
            Notification.Builder(this, CHANNEL_ID)
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
        }
        return builder
            .setContentTitle("PocketCast 正在推流")
            .setContentText("锁屏后仍保持与电脑的连接")
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setOngoing(true)
            .setContentIntent(open)
            .build()
    }

    companion object {
        private const val TAG = "PocketCast"
        private const val CHANNEL_ID = "pocketcast-cast"
        private const val NOTIFICATION_ID = 1101

        fun start(context: Context) {
            try {
                val intent = Intent(context, CastService::class.java)
                if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent)
                else context.startService(intent)
            } catch (e: Exception) {
                // 以前这里静默吞掉异常：服务没起来（比如清单里漏了 <service>）时，
                // 锁屏后就是「麦克风和摄像头突然都用不了」，却没有任何线索。
                Log.e(TAG, "启动前台服务失败：${e.message}")
            }
        }

        fun stop(context: Context) {
            try { context.stopService(Intent(context, CastService::class.java)) } catch (_: Exception) {}
        }
    }
}
