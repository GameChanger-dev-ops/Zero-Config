package com.vpnbridge.hub.ui

import android.Manifest
import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.View
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.vpnbridge.hub.R
import com.vpnbridge.hub.databinding.ActivityMainBinding
import com.vpnbridge.hub.network.LocalInterfaceEndpoint
import com.vpnbridge.hub.network.VpnNetworkMonitor
import com.vpnbridge.hub.service.BridgeForegroundService
import com.vpnbridge.hub.service.BridgeTelemetry
import kotlinx.coroutines.launch
import java.util.Locale

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var previewVpnMonitor: VpnNetworkMonitor
    private var bridgeRunning = false
    private var latestEndpoints: List<LocalInterfaceEndpoint> = emptyList()

    private val notificationPermLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) {
        updateHealthIndicators()
        startBridgeService()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        previewVpnMonitor = VpnNetworkMonitor(applicationContext)
        previewVpnMonitor.start()

        setupTabs()
        binding.btnToggleBridge.setOnClickListener {
            if (bridgeRunning) stopBridgeService() else requestPermissionsAndStart()
        }
        binding.switchHttpProxy.setOnCheckedChangeListener { _, isChecked ->
            if (bridgeRunning) {
                startService(Intent(this, BridgeForegroundService::class.java).apply {
                    action = BridgeForegroundService.ACTION_TOGGLE_HTTP
                    putExtra(BridgeForegroundService.EXTRA_ENABLE_HTTP, isChecked)
                })
            }
        }
        binding.btnCopyHttp.setOnClickListener {
            val ip = bestClientIp()
            if (ip == null) {
                Toast.makeText(this, "Start Hub and connect Wi-Fi/Hotspot first.", Toast.LENGTH_SHORT).show()
            } else {
                copyText("HTTP Proxy", "$ip:${BridgeForegroundService.HTTP_PORT}")
            }
        }
        binding.btnCopySocks.setOnClickListener {
            val ip = bestClientIp()
            if (ip == null) {
                Toast.makeText(this, "Start Hub and connect Wi-Fi/Hotspot first.", Toast.LENGTH_SHORT).show()
            } else {
                copyText("SOCKS5 Proxy", "$ip:${BridgeForegroundService.SOCKS_PORT}")
            }
        }
        binding.btnBatteryOpt.setOnClickListener { promptBatteryOptimizationBypass() }

        observeState()
        updateHealthIndicators()
    }

    private fun setupTabs() {
        binding.bottomNav.setOnItemSelectedListener { item ->
            binding.pageHub.visibility = if (item.itemId == R.id.nav_hub) View.VISIBLE else View.GONE
            binding.pageNetwork.visibility = if (item.itemId == R.id.nav_network) View.VISIBLE else View.GONE
            binding.pageSetup.visibility = if (item.itemId == R.id.nav_setup) View.VISIBLE else View.GONE
            true
        }
    }

    override fun onResume() {
        super.onResume()
        updateHealthIndicators()
    }

    override fun onDestroy() {
        previewVpnMonitor.stop()
        super.onDestroy()
    }

    private fun observeState() {
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                launch { BridgeForegroundService.telemetry.collect { renderTelemetry(it) } }
                launch {
                    previewVpnMonitor.vpnState.collect { vpn ->
                        if (!bridgeRunning) {
                            renderVpn(vpn.isActive, vpn.interfaceName, vpn.dnsServers.joinToString { it.hostAddress ?: "" })
                        }
                    }
                }
            }
        }
    }

    private fun renderTelemetry(t: BridgeTelemetry) {
        bridgeRunning = t.isRunning
        latestEndpoints = t.localAddresses
        if (binding.switchHttpProxy.isChecked != t.httpEnabled) {
            binding.switchHttpProxy.isChecked = t.httpEnabled
        }

        renderVpn(t.vpnState.isActive, t.vpnState.interfaceName, t.vpnState.dnsServers.joinToString { it.hostAddress ?: "" })

        if (t.isRunning) {
            val modes = if (t.httpEnabled) "SOCKS5 :1080  |  HTTP :8080" else "SOCKS5 :1080  |  ADB :1081"
            binding.tvBridgeStatus.text = "Hub Active"
            binding.tvBridgeStatus.setTextColor(Color.parseColor("#34D399"))
            binding.tvHeaderSub.text = modes
            binding.btnToggleBridge.text = "Stop Zero-Config Hub"
            binding.btnToggleBridge.setBackgroundColor(Color.parseColor("#DC2626"))
        } else {
            binding.tvBridgeStatus.text = "Hub Stopped"
            binding.tvBridgeStatus.setTextColor(Color.parseColor("#F8FAFC"))
            binding.tvHeaderSub.text = "VPN Hub & Direct Proxy"
            binding.btnToggleBridge.text = "Start Zero-Config Hub"
            binding.btnToggleBridge.setBackgroundColor(Color.parseColor("#2563EB"))
        }

        val h = t.uptimeSeconds / 3600
        val m = (t.uptimeSeconds % 3600) / 60
        val s = t.uptimeSeconds % 60
        binding.tvUptime.text = String.format(Locale.US, "%02d:%02d:%02d", h, m, s)

        binding.tvRxSpeed.text = "${formatBytes(t.rxBytesPerSec)}/s"
        binding.tvTxSpeed.text = "${formatBytes(t.txBytesPerSec)}/s"
        binding.tvFlows.text = "${t.activeFlows}"
        binding.tvTotalData.text = "${formatBytes(t.totalRxBytes)} ↓  ${formatBytes(t.totalTxBytes)} ↑"

        val best = latestEndpoints.firstOrNull()
        binding.tvPrimaryEndpoint.text = if (best == null) {
            "Connect Wi-Fi, USB Tethering, or Phone Hotspot to display address"
        } else {
            "Interface: ${best.label} (${best.ifaceName})\n" +
                "HTTP Proxy:   ${best.ip}:${BridgeForegroundService.HTTP_PORT}\n" +
                "SOCKS5 Proxy: ${best.ip}:${BridgeForegroundService.SOCKS_PORT}"
        }

        binding.tvMdnsStatus.text = if (t.mdnsActive) {
            "mDNS Active: ${t.mdnsName}._vpnhotspot._tcp.local"
        } else {
            "mDNS Broadcast: Inactive"
        }

        binding.tvInterfacesList.text = if (latestEndpoints.isEmpty()) {
            "No local LAN/USB/Hotspot interfaces active."
        } else {
            latestEndpoints.joinToString("\n") { ep -> "• ${ep.label} [${ep.ifaceName}]: ${ep.ip}" }
        }
    }

    private fun renderVpn(active: Boolean, iface: String, dns: String) {
        if (active) {
            binding.tvVpnBadge.text = "VPN BOUND"
            binding.tvVpnBadge.setTextColor(Color.parseColor("#34D399"))
            binding.tvVpnSummary.text = "Bound strictly to $iface (fail-closed protection active)."
            binding.tvVpnDetails.text = "Interface: $iface\nDNS: ${dns.ifEmpty { "VPN Default" }}"
            binding.tvHealthVpn.text = "• VPN Tunnel: Active ($iface) ✓"
            binding.tvHealthVpn.setTextColor(Color.parseColor("#34D399"))
        } else {
            binding.tvVpnBadge.text = "NO VPN"
            binding.tvVpnBadge.setTextColor(Color.parseColor("#FBBF24"))
            binding.tvVpnSummary.text = "No VPN detected — outbound proxy requests will pause safely."
            binding.tvVpnDetails.text = "Interface: None (Start your VPN app first)\nDNS: --"
            binding.tvHealthVpn.text = "• VPN Tunnel: Waiting for active VPN"
            binding.tvHealthVpn.setTextColor(Color.parseColor("#FBBF24"))
        }
    }

    private fun bestClientIp(): String? = latestEndpoints.firstOrNull()?.ip

    private fun copyText(label: String, value: String) {
        val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText(label, value))
        Toast.makeText(this, "Copied $label: $value", Toast.LENGTH_SHORT).show()
    }

    private fun requestPermissionsAndStart() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
            if (!granted) {
                notificationPermLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                return
            }
        }
        startBridgeService()
    }

    private fun startBridgeService() {
        val intent = Intent(this, BridgeForegroundService::class.java).apply {
            action = BridgeForegroundService.ACTION_START
            putExtra(BridgeForegroundService.EXTRA_ENABLE_HTTP, binding.switchHttpProxy.isChecked)
        }
        ContextCompat.startForegroundService(this, intent)
    }

    private fun stopBridgeService() {
        startService(Intent(this, BridgeForegroundService::class.java).apply {
            action = BridgeForegroundService.ACTION_STOP
        })
    }

    @SuppressLint("BatteryLife")
    private fun promptBatteryOptimizationBypass() {
        val pm = getSystemService(POWER_SERVICE) as PowerManager
        if (!pm.isIgnoringBatteryOptimizations(packageName)) {
            startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                data = Uri.parse("package:$packageName")
            })
        }
    }

    private fun updateHealthIndicators() {
        val pm = getSystemService(POWER_SERVICE) as PowerManager
        val exempt = pm.isIgnoringBatteryOptimizations(packageName)
        binding.btnBatteryOpt.isEnabled = !exempt
        binding.btnBatteryOpt.text = if (exempt) "Battery Optimization Bypassed ✓" else "Disable Battery Optimization (Recommended)"

        val notifGranted = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        } else true
        binding.tvHealthNotif.text = if (notifGranted) "• Notifications: Granted ✓" else "• Notifications: Will request on Start"
        binding.tvHealthNotif.setTextColor(Color.parseColor(if (notifGranted) "#34D399" else "#FBBF24"))
    }

    private fun formatBytes(bytes: Long): String = when {
        bytes < 1024 -> "${bytes} B"
        bytes < 1024 * 1024 -> String.format(Locale.US, "%.1f KB", bytes / 1024.0)
        bytes < 1024L * 1024L * 1024L -> String.format(Locale.US, "%.2f MB", bytes / (1024.0 * 1024.0))
        else -> String.format(Locale.US, "%.2f GB", bytes / (1024.0 * 1024.0 * 1024.0))
    }
}