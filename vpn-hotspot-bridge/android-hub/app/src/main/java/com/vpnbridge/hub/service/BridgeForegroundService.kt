package com.vpnbridge.hub.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import com.vpnbridge.hub.R
import com.vpnbridge.hub.network.MdnsAdvertiser
import com.vpnbridge.hub.network.Socks5ProxyServer
import com.vpnbridge.hub.network.VpnNetworkMonitor
import com.vpnbridge.hub.network.VpnState
import com.vpnbridge.hub.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

data class BridgeTelemetry(
    val isRunning: Boolean = false,
    val vpnState: VpnState = VpnState(),
    val mdnsActive: Boolean = false,
    val mdnsName: String = "",
    val localAddresses: List<Pair<String, String>> = emptyList(),
    val activeFlows: Int = 0,
    val totalRxBytes: Long = 0L,
    val totalTxBytes: Long = 0L,
    val rxBytesPerSec: Long = 0L,
    val txBytesPerSec: Long = 0L
)

class BridgeForegroundService : Service() {

    companion object {
        const val ACTION_START = "com.vpnbridge.hub.ACTION_START"
        const val ACTION_STOP = "com.vpnbridge.hub.ACTION_STOP"
        private const val CHANNEL_ID = "zeroconfig_bridge_channel"
        private const val NOTIFICATION_ID = 1001
        private const val SOCKS_PORT = 1080
        private const val UDP_MUX_PORT = 1081

        private val _telemetry = MutableStateFlow(BridgeTelemetry())
        val telemetry: StateFlow<BridgeTelemetry> = _telemetry.asStateFlow()
    }

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private lateinit var vpnMonitor: VpnNetworkMonitor
    private lateinit var mdnsAdvertiser: MdnsAdvertiser
    private var proxyServer: Socks5ProxyServer? = null

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var telemetryJob: Job? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        vpnMonitor = VpnNetworkMonitor(applicationContext)
        mdnsAdvertiser = MdnsAdvertiser(applicationContext)
        vpnMonitor.start()

        serviceScope.launch {
            vpnMonitor.vpnState.collect { vpn ->
                _telemetry.value = _telemetry.value.copy(vpnState = vpn)
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopBridge()
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_START, null -> {
                promoteToForeground()
                startBridge()
            }
        }
        return START_STICKY
    }

    private fun promoteToForeground() {
        val notification = buildNotification("Starting SOCKS5 + mDNS Hub...")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    @Synchronized
    private fun startBridge() {
        if (proxyServer != null) return
        acquireLocks()

        val server = Socks5ProxyServer(
            vpnNetworkProvider = { vpnMonitor.currentVpnNetwork() },
            socksPort = SOCKS_PORT,
            udpMuxPort = UDP_MUX_PORT
        )
        server.start()
        proxyServer = server
        mdnsAdvertiser.start(SOCKS_PORT, UDP_MUX_PORT)

        var lastRx = 0L
        var lastTx = 0L
        telemetryJob = serviceScope.launch {
            while (isActive) {
                val curRx = server.rxBytes.get()
                val curTx = server.txBytes.get()
                val rxRate = (curRx - lastRx).coerceAtLeast(0L)
                val txRate = (curTx - lastTx).coerceAtLeast(0L)
                lastRx = curRx
                lastTx = curTx

                val state = BridgeTelemetry(
                    isRunning = true,
                    vpnState = vpnMonitor.vpnState.value,
                    mdnsActive = mdnsAdvertiser.isRegistered.value,
                    mdnsName = mdnsAdvertiser.registeredName.value,
                    localAddresses = mdnsAdvertiser.getLocalInterfaceAddresses(),
                    activeFlows = server.activeConnections.get(),
                    totalRxBytes = curRx,
                    totalTxBytes = curTx,
                    rxBytesPerSec = rxRate,
                    txBytesPerSec = txRate
                )
                _telemetry.value = state
                updateNotification(state)
                delay(1000L)
            }
        }
    }

    @Synchronized
    private fun stopBridge() {
        telemetryJob?.cancel()
        telemetryJob = null
        mdnsAdvertiser.stop()
        proxyServer?.stop()
        proxyServer = null
        releaseLocks()
        _telemetry.value = BridgeTelemetry(
            isRunning = false,
            vpnState = vpnMonitor.vpnState.value
        )
        stopForeground(STOP_FOREGROUND_REMOVE)
    }

    private fun acquireLocks() {
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        if (wakeLock == null) {
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "ZeroConfig::HubWakeLock")
            wakeLock?.setReferenceCounted(false)
        }
        if (wakeLock?.isHeld == false) wakeLock?.acquire(12 * 60 * 60 * 1000L)

        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        if (wifiLock == null) {
            val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                WifiManager.WIFI_MODE_FULL_LOW_LATENCY
            } else {
                @Suppress("DEPRECATION")
                WifiManager.WIFI_MODE_FULL_HIGH_PERF
            }
            wifiLock = wm.createWifiLock(mode, "ZeroConfig::WifiLock")
            wifiLock?.setReferenceCounted(false)
        }
        if (wifiLock?.isHeld == false) wifiLock?.acquire()

        if (multicastLock == null) {
            multicastLock = wm.createMulticastLock("ZeroConfig::MdnsLock")
            multicastLock?.setReferenceCounted(false)
        }
        if (multicastLock?.isHeld == false) multicastLock?.acquire()
    }

    private fun releaseLocks() {
        try { if (wakeLock?.isHeld == true) wakeLock?.release() } catch (_: Exception) {}
        try { if (wifiLock?.isHeld == true) wifiLock?.release() } catch (_: Exception) {}
        try { if (multicastLock?.isHeld == true) multicastLock?.release() } catch (_: Exception) {}
    }

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_LOW
        ).apply { setShowBadge(false) }
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    private fun updateNotification(state: BridgeTelemetry) {
        val vpnTag = if (state.vpnState.isActive) "VPN: ${state.vpnState.interfaceName}" else "Waiting for VPN"
        val text = "$vpnTag | Flows: ${state.activeFlows} | ↓${state.rxBytesPerSec / 1024} KB/s ↑${state.txBytesPerSec / 1024} KB/s"
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, buildNotification(text))
    }

    private fun buildNotification(contentText: String): Notification {
        val openIntent = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val stopIntent = PendingIntent.getService(
            this, 1, Intent(this, BridgeForegroundService::class.java).apply { action = ACTION_STOP },
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_upload_done)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(contentText)
            .setContentIntent(openIntent)
            .setOngoing(true)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, "Stop Hub", stopIntent)
            .build()
    }

    override fun onDestroy() {
        stopBridge()
        vpnMonitor.stop()
        serviceScope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}