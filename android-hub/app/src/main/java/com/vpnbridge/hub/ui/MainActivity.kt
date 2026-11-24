package com.vpnbridge.hub.ui

import android.Manifest
import android.annotation.SuppressLint
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.vpnbridge.hub.databinding.ActivityMainBinding
import com.vpnbridge.hub.network.VpnNetworkMonitor
import com.vpnbridge.hub.service.BridgeForegroundService
import com.vpnbridge.hub.service.BridgeTelemetry
import kotlinx.coroutines.launch
import java.util.Locale

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var previewVpnMonitor: VpnNetworkMonitor
    private var bridgeRunning = false

    private val notificationPermLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { startBridgeService() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        previewVpnMonitor = VpnNetworkMonitor(applicationContext)
        previewVpnMonitor.start()

        binding.btnToggleBridge.setOnClickListener {
            if (bridgeRunning) stopBridgeService() else requestPermissionsAndStart()
        }
        binding.btnBatteryOpt.setOnClickListener { promptBatteryOptimizationBypass() }

        observeState()
        updateBatteryButtonState()
    }

    override fun onResume() {
        super.onResume()
        updateBatteryButtonState()
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
        renderVpn(t.vpnState.isActive, t.vpnState.interfaceName, t.vpnState.dnsServers.joinToString { it.hostAddress ?: "" })

        if (t.isRunning) {
            binding.tvBridgeStatus.text = "Running (SOCKS5 :1080 | ADB-Mux :1081)"
            binding.tvBridgeStatus.setTextColor(Color.parseColor("#34D399"))
            binding.btnToggleBridge.text = "Stop Zero-Config Hub"
            binding.btnToggleBridge.setBackgroundColor(Color.parseColor("#DC2626"))
        } else {
            binding.tvBridgeStatus.text = "Stopped"
            binding.tvBridgeStatus.setTextColor(Color.parseColor("#F8FAFC"))
            binding.btnToggleBridge.text = "Start Zero-Config Hub"
            binding.btnToggleBridge.setBackgroundColor(Color.parseColor("#2563EB"))
        }

        binding.tvAddresses.text = if (t.localAddresses.isEmpty()) {
            "Listening: Connect Wi-Fi or USB Tethering (or USB ADB)"
        } else {
            t.localAddresses.joinToString("\n") { (iface, ip) -> "[$iface] socks5://$ip:1080" }
        }

        binding.tvMdnsStatus.text = if (t.mdnsActive) {
            "mDNS Broadcast: ${t.mdnsName}._vpnhotspot._tcp.local"
        } else {
            "mDNS Broadcast: Inactive"
        }

        binding.tvClients.text = "Active Flows: ${t.activeFlows}"
        binding.tvThroughput.text = String.format(Locale.US, "%s/s ↓  %s/s ↑", formatBytes(t.rxBytesPerSec), formatBytes(t.txBytesPerSec))
        binding.tvTotals.text = String.format(Locale.US, "Total: %s ↓ / %s ↑", formatBytes(t.totalRxBytes), formatBytes(t.totalTxBytes))
    }

    private fun renderVpn(active: Boolean, iface: String, dns: String) {
        if (active) {
            binding.tvVpnStatus.text = "VPN Active & Bound"
            binding.tvVpnStatus.setTextColor(Color.parseColor("#34D399"))
            binding.tvVpnDetails.text = "Interface: $iface | DNS: ${dns.ifEmpty { "VPN Default" }}"
        } else {
            binding.tvVpnStatus.text = "No Active VPN Detected"
            binding.tvVpnStatus.setTextColor(Color.parseColor("#FBBF24"))
            binding.tvVpnDetails.text = "Start your VPN app first so traffic binds to tun0"
        }
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
        val intent = Intent(this, BridgeForegroundService::class.java).apply { action = BridgeForegroundService.ACTION_START }
        ContextCompat.startForegroundService(this, intent)
    }

    private fun stopBridgeService() {
        val intent = Intent(this, BridgeForegroundService::class.java).apply { action = BridgeForegroundService.ACTION_STOP }
        startService(intent)
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

    private fun updateBatteryButtonState() {
        val pm = getSystemService(POWER_SERVICE) as PowerManager
        val exempt = pm.isIgnoringBatteryOptimizations(packageName)
        binding.btnBatteryOpt.isEnabled = !exempt
        binding.btnBatteryOpt.text = if (exempt) "Battery Optimization Bypassed ✓" else "Disable Battery Optimization (Recommended)"
    }

    private fun formatBytes(bytes: Long): String = when {
        bytes < 1024 -> "${bytes} B"
        bytes < 1024 * 1024 -> String.format(Locale.US, "%.1f KB", bytes / 1024.0)
        bytes < 1024L * 1024L * 1024L -> String.format(Locale.US, "%.2f MB", bytes / (1024.0 * 1024.0))
        else -> String.format(Locale.US, "%.2f GB", bytes / (1024.0 * 1024.0 * 1024.0))
    }
}