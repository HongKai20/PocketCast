package com.pocketcast.app

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper

/** mDNS 自动发现：同一 WiFi 下寻找 _pocketcast._tcp 电脑。 */
class NsdScanner(private val context: Context, private val onResult: (name: String, host: String, port: Int, token: String) -> Unit) {

    private val nsd = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var listener: NsdManager.DiscoveryListener? = null
    private val main = Handler(Looper.getMainLooper())

    fun start() {
        stop()
        val l = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onDiscoveryStopped(serviceType: String) {}

            override fun onServiceFound(info: NsdServiceInfo) {
                nsd.resolveService(info, object : NsdManager.ResolveListener {
                    override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {}
                    override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                        val host = serviceInfo.host?.hostAddress ?: return
                        val token = serviceInfo.attributes?.get("token")?.let { String(it, Charsets.UTF_8) } ?: ""
                        main.post { onResult(serviceInfo.serviceName, host, serviceInfo.port, token) }
                    }
                })
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {}
        }
        listener = l
        try {
            nsd.discoverServices("_pocketcast._tcp.", NsdManager.PROTOCOL_DNS_SD, l)
        } catch (_: Exception) {}
    }

    fun stop() {
        listener?.let { try { nsd.stopServiceDiscovery(it) } catch (_: Exception) {} }
        listener = null
    }
}
