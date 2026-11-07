package com.pocketcast.app

import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.LinkedBlockingQueue
import kotlin.concurrent.thread

/**
 * 与电脑端之间的一条 TCP 信令连接（NDJSON，端口 8765）。
 * 音视频不走这里，这里只做配对、SDP/ICE、控制指令与延迟测试。
 */
class SignalingClient(
    private val host: String,
    private val port: Int,
    private val token: String,
    private val deviceName: String,
    private val listener: Listener
) {
    interface Listener {
        fun onConnected()
        fun onMessage(msg: JSONObject)
        fun onClosed(reason: String)
    }

    private var socket: Socket? = null
    private val outbox = LinkedBlockingQueue<String>()
    @Volatile private var running = false
    private val main = Handler(Looper.getMainLooper())

    fun connect() {
        running = true
        thread(name = "signal-connect") {
            try {
                val s = Socket()
                s.tcpNoDelay = true
                s.connect(InetSocketAddress(host, port), 5000)
                s.soTimeout = 0
                socket = s
                val writer = OutputStreamWriter(s.getOutputStream(), Charsets.UTF_8)
                val reader = BufferedReader(InputStreamReader(s.getInputStream(), Charsets.UTF_8))

                thread(name = "signal-write") {
                    try {
                        while (running) {
                            val line = outbox.take()
                            writer.write(line)
                            writer.write("\n")
                            writer.flush()
                        }
                    } catch (_: Exception) {
                        close("发送线程结束")
                    }
                }

                send(JSONObject().put("t", "hello").put("ver", 1).put("token", token).put("device", deviceName))

                while (running) {
                    val line = reader.readLine() ?: break
                    if (line.isBlank()) continue
                    val obj = try { JSONObject(line) } catch (e: Exception) { continue }
                    if (obj.optString("t") == "welcome") {
                        main.post { listener.onConnected() }
                    } else {
                        main.post { listener.onMessage(obj) }
                    }
                }
                close("电脑断开连接")
            } catch (e: Exception) {
                close("连接失败：${e.message}")
            }
        }
    }

    fun send(obj: JSONObject) {
        if (running) outbox.offer(obj.toString())
    }

    fun close(reason: String = "已断开") {
        if (!running) return
        running = false
        try { socket?.close() } catch (_: Exception) {}
        main.post { listener.onClosed(reason) }
    }
}
