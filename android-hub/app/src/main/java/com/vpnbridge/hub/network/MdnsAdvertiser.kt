package com.vpnbridge.hub.network

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.net.Inet4Address
import java.net.NetworkInterface

data class LocalInterfaceEndpoint(
    val ifaceName: String,
    val ip: String,
    val label: String
)

class MdnsAdvertiser(context: Context) {

    companion object {
        private const val TAG = "MdnsAdvertiser"
        const val SERVICE_TYPE = "_vpnhotspot._tcp."
    }

    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val _isRegistered = MutableStateFlow(false)
    val isRegistered: StateFlow<Boolean> = _isRegistered.asStateFlow()

    private val _registeredName = MutableStateFlow("")
    val registeredName: StateFlow<String> = _registeredName.asStateFlow()

    private var activeListener: NsdManager.RegistrationListener? = null

    fun start(socksPort: Int, udpMuxPort: Int, httpPort: Int? = null) {
        stop()

        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo) {
                _registeredName.value = serviceInfo.serviceName
                _isRegistered.value = true
                Log.i(TAG, "mDNS registered: ${serviceInfo.serviceName} on port ${serviceInfo.port}")
            }

            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                _isRegistered.value = false
                Log.e(TAG, "mDNS registration failed: $errorCode")
            }

            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo) {
                _isRegistered.value = false
            }

            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                _isRegistered.value = false
            }
        }

        val sanitizedHost = Build.MODEL.replace(Regex("[^A-Za-z0-9-]"), "").ifEmpty { "AndroidHub" }
        val info = NsdServiceInfo().apply {
            serviceName = "ZeroConfig-$sanitizedHost"
            serviceType = SERVICE_TYPE
            port = socksPort
            setAttribute("app", "Zero-Config")
            setAttribute("udpmux", udpMuxPort.toString())
            setAttribute("proto", "socks5")
            if (httpPort != null) setAttribute("http", httpPort.toString())
        }

        activeListener = listener
        try {
            nsdManager.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to register NSD service: ${e.message}")
            activeListener = null
        }
    }

    fun stop() {
        val listener = activeListener ?: return
        activeListener = null
        try {
            nsdManager.unregisterService(listener)
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "Unregister skipped: ${e.message}")
        }
        _isRegistered.value = false
        _registeredName.value = ""
    }

    fun getLocalInterfaceAddresses(): List<LocalInterfaceEndpoint> {
        val results = mutableListOf<LocalInterfaceEndpoint>()
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces() ?: return results
            while (interfaces.hasMoreElements()) {
                val iface = interfaces.nextElement()
                if (!iface.isUp || iface.isLoopback) continue
                val name = iface.name
                if (name.startsWith("tun") || name.startsWith("ppp") || name.startsWith("rmnet")) continue
                val addrs = iface.inetAddresses
                while (addrs.hasMoreElements()) {
                    val addr = addrs.nextElement()
                    if (!addr.isLoopbackAddress && addr is Inet4Address) {
                        addr.hostAddress?.let { ip ->
                            results.add(LocalInterfaceEndpoint(name, ip, classifyInterface(name, ip)))
                        }
                    }
                }
            }
        } catch (_: Exception) {}
        return results.sortedBy { priorityFor(it.label) }
    }

    private fun classifyInterface(name: String, ip: String): String {
        val n = name.lowercase()
        return when {
            n.startsWith("rndis") || n.startsWith("usb") || n.startsWith("ncm") -> "USB Tethering"
            n.startsWith("ap") || n.startsWith("swlan") || n.startsWith("softap") || n == "wlan1" || ip.startsWith("192.168.43.") -> "Phone Hotspot"
            n.startsWith("wlan") -> "Wi-Fi LAN"
            n.startsWith("eth") -> "Ethernet"
            else -> "Local ($name)"
        }
    }

    private fun priorityFor(label: String): Int = when {
        label == "Phone Hotspot" -> 0
        label == "USB Tethering" -> 1
        label == "Wi-Fi LAN" -> 2
        else -> 3
    }
}