package com.vpnbridge.hub.ui

import android.Manifest
import android.animation.AnimatorSet
import android.animation.ObjectAnimator
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
import android.view.animation.DecelerateInterpolator
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.core.content.ContextCompat
import androidx.core.os.LocaleListCompat
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

    companion object {
        private const val SUPPORT_NUMBER = "+237 673533489"
        private const val SUPPORT_WHATSAPP_URL = "https://wa.me/237673533489"
        private const val PREFS_NAME = "zeroconfig_prefs"
        private const val KEY_ONBOARDED = "onboarded_v1"
    }

    private lateinit var binding: ActivityMainBinding
    private lateinit var previewVpnMonitor: VpnNetworkMonitor
    private var bridgeRunning = false
    private var latestEndpoints: List<LocalInterfaceEndpoint> = emptyList()
    private var currentTutStep = 0
    private var pulseAnimator: AnimatorSet? = null

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
        setupTutorial()
        setupSupportAndLang()

        binding.btnToggleBridge.setOnClickListener { view ->
            bounceView(view)
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
            bounceView(it)
            val ip = bestClientIp()
            if (ip == null) {
                Toast.makeText(this, getString(R.string.toast_start_hub_first), Toast.LENGTH_SHORT).show()
            } else {
                copyText("HTTP", "$ip:${BridgeForegroundService.HTTP_PORT}")
            }
        }
        binding.btnCopySocks.setOnClickListener {
            bounceView(it)
            val ip = bestClientIp()
            if (ip == null) {
                Toast.makeText(this, getString(R.string.toast_start_hub_first), Toast.LENGTH_SHORT).show()
            } else {
                copyText("SOCKS5", "$ip:${BridgeForegroundService.SOCKS_PORT}")
            }
        }
        binding.btnBatteryOpt.setOnClickListener { promptBatteryOptimizationBypass() }

        observeState()
        updateHealthIndicators()
        renderTutorialStep(animate = false)

        // Show beginner tutorial automatically on first launch
        val prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE)
        if (!prefs.getBoolean(KEY_ONBOARDED, false)) {
            prefs.edit().putBoolean(KEY_ONBOARDED, true).apply()
            binding.bottomNav.selectedItemId = R.id.nav_tutorial
        }
    }

    private fun setupTabs() {
        binding.bottomNav.setOnItemSelectedListener { item ->
            val target = when (item.itemId) {
                R.id.nav_hub -> binding.pageHub
                R.id.nav_network -> binding.pageNetwork
                R.id.nav_tutorial -> binding.pageTutorial
                R.id.nav_setup -> binding.pageSetup
                else -> binding.pageHub
            }
            listOf(binding.pageHub, binding.pageNetwork, binding.pageTutorial, binding.pageSetup).forEach { page ->
                if (page == target) {
                    if (page.visibility != View.VISIBLE) {
                        page.alpha = 0f
                        page.translationY = 28f
                        page.visibility = View.VISIBLE
                        page.animate()
                            .alpha(1f)
                            .translationY(0f)
                            .setDuration(240L)
                            .setInterpolator(DecelerateInterpolator())
                            .start()
                    }
                } else {
                    page.visibility = View.GONE
                }
            }
            true
        }
    }

    private fun setupTutorial() {
        binding.btnTutPrev.setOnClickListener {
            if (currentTutStep > 0) {
                currentTutStep--
                renderTutorialStep(animate = true)
            }
        }
        binding.btnTutNext.setOnClickListener {
            if (currentTutStep < 3) {
                currentTutStep++
                renderTutorialStep(animate = true)
            } else {
                binding.bottomNav.selectedItemId = R.id.nav_hub
            }
        }
    }

    private fun renderTutorialStep(animate: Boolean) {
        val titles = listOf(R.string.tut_1_title, R.string.tut_2_title, R.string.tut_3_title, R.string.tut_4_title)
        val descs = listOf(R.string.tut_1_desc, R.string.tut_2_desc, R.string.tut_3_desc, R.string.tut_4_desc)

        val applyText = {
            binding.tvTutStepCounter.text = getString(R.string.tutorial_step_counter, currentTutStep + 1)
            binding.tvTutBadgeNum.text = (currentTutStep + 1).toString()
            binding.tvTutTitle.text = getString(titles[currentTutStep])
            binding.tvTutDesc.text = getString(descs[currentTutStep])
            binding.btnTutPrev.isEnabled = currentTutStep > 0
            binding.btnTutPrev.alpha = if (currentTutStep > 0) 1f else 0.45f
            binding.btnTutNext.text = getString(if (currentTutStep == 3) R.string.btn_done_tut else R.string.btn_next)
        }

        if (animate) {
            binding.tutSlideContainer.animate().alpha(0f).translationY(16f).setDuration(120L).withEndAction {
                applyText()
                binding.tutSlideContainer.translationY = -16f
                binding.tutSlideContainer.animate().alpha(1f).translationY(0f).setDuration(200L).start()
            }.start()
        } else {
            applyText()
        }
    }

    private fun setupSupportAndLang() {
        val activeTag = currentLangTag()
        binding.btnLangSwitch.text = if (activeTag.startsWith("fr")) "FR → EN" else "EN → FR"
        binding.btnLangSwitch.setOnClickListener {
            val next = if (currentLangTag().startsWith("fr")) "en" else "fr"
            AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(next))
        }

        binding.btnSupportWhatsApp.setOnClickListener {
            bounceView(it)
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(SUPPORT_WHATSAPP_URL)))
        }
        binding.btnSupportCall.setOnClickListener {
            bounceView(it)
            startActivity(Intent(Intent.ACTION_DIAL, Uri.parse("tel:+237673533489")))
        }
        binding.btnSupportCopy.setOnClickListener {
            bounceView(it)
            copyText("Support", SUPPORT_NUMBER)
        }
    }

    private fun currentLangTag(): String {
        val appTags = AppCompatDelegate.getApplicationLocales().toLanguageTags()
        return if (appTags.isNotEmpty()) appTags else Locale.getDefault().language
    }

    override fun onResume() {
        super.onResume()
        updateHealthIndicators()
    }

    override fun onDestroy() {
        pulseAnimator?.cancel()
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
        val wasRunning = bridgeRunning
        bridgeRunning = t.isRunning
        latestEndpoints = t.localAddresses
        if (binding.switchHttpProxy.isChecked != t.httpEnabled) {
            binding.switchHttpProxy.isChecked = t.httpEnabled
        }

        renderVpn(t.vpnState.isActive, t.vpnState.interfaceName, t.vpnState.dnsServers.joinToString { it.hostAddress ?: "" })

        if (t.isRunning) {
            binding.tvBridgeStatus.text = getString(R.string.hub_active)
            binding.tvBridgeStatus.setTextColor(Color.parseColor("#34D399"))
            binding.tvHeaderSub.text = getString(if (t.httpEnabled) R.string.header_subtitle_running else R.string.header_subtitle_socks)
            binding.btnToggleBridge.text = getString(R.string.btn_stop_hub)
            binding.btnToggleBridge.setBackgroundColor(Color.parseColor("#DC2626"))
            if (!wasRunning) startPulseAnimation()
        } else {
            binding.tvBridgeStatus.text = getString(R.string.hub_stopped)
            binding.tvBridgeStatus.setTextColor(Color.parseColor("#F8FAFC"))
            binding.tvHeaderSub.text = getString(R.string.header_subtitle)
            binding.btnToggleBridge.text = getString(R.string.btn_start_hub)
            binding.btnToggleBridge.setBackgroundColor(Color.parseColor("#2563EB"))
            stopPulseAnimation()
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
            getString(R.string.standalone_empty)
        } else {
            "${best.label} (${best.ifaceName})\nHTTP :   ${best.ip}:${BridgeForegroundService.HTTP_PORT}\nSOCKS5 : ${best.ip}:${BridgeForegroundService.SOCKS_PORT}"
        }

        binding.tvMdnsStatus.text = if (t.mdnsActive) {
            getString(R.string.mdns_active, t.mdnsName)
        } else {
            getString(R.string.mdns_inactive)
        }

        binding.tvInterfacesList.text = if (latestEndpoints.isEmpty()) {
            getString(R.string.interfaces_empty)
        } else {
            latestEndpoints.joinToString("\n") { ep -> "• ${ep.label} [${ep.ifaceName}]: ${ep.ip}" }
        }
    }

    private fun renderVpn(active: Boolean, iface: String, dns: String) {
        if (active) {
            binding.tvVpnBadge.text = getString(R.string.badge_vpn_bound)
            binding.tvVpnBadge.setTextColor(Color.parseColor("#34D399"))
            binding.tvVpnSummary.text = getString(R.string.vpn_bound_msg, iface)
            binding.tvVpnDetails.text = "Interface: $iface\nDNS: ${dns.ifEmpty { "VPN Default" }}"
            binding.tvHealthVpn.text = getString(R.string.health_vpn_ok, iface)
            binding.tvHealthVpn.setTextColor(Color.parseColor("#34D399"))
        } else {
            binding.tvVpnBadge.text = getString(R.string.badge_no_vpn)
            binding.tvVpnBadge.setTextColor(Color.parseColor("#FBBF24"))
            binding.tvVpnSummary.text = getString(R.string.vpn_waiting_msg)
            binding.tvVpnDetails.text = "Interface: --\nDNS: --"
            binding.tvHealthVpn.text = getString(R.string.health_vpn_wait)
            binding.tvHealthVpn.setTextColor(Color.parseColor("#FBBF24"))
        }
    }

    private fun startPulseAnimation() {
        if (pulseAnimator?.isRunning == true) return
        val sx = ObjectAnimator.ofFloat(binding.pulseHalo, View.SCALE_X, 0.85f, 1.38f).apply {
            repeatCount = ObjectAnimator.INFINITE
            duration = 1500L
        }
        val sy = ObjectAnimator.ofFloat(binding.pulseHalo, View.SCALE_Y, 0.85f, 1.38f).apply {
            repeatCount = ObjectAnimator.INFINITE
            duration = 1500L
        }
        val alpha = ObjectAnimator.ofFloat(binding.pulseHalo, View.ALPHA, 0.75f, 0f).apply {
            repeatCount = ObjectAnimator.INFINITE
            duration = 1500L
        }
        pulseAnimator = AnimatorSet().apply {
            playTogether(sx, sy, alpha)
            start()
        }
    }

    private fun stopPulseAnimation() {
        pulseAnimator?.cancel()
        pulseAnimator = null
        binding.pulseHalo.alpha = 0f
    }

    private fun bounceView(v: View) {
        v.animate().scaleX(0.95f).scaleY(0.95f).setDuration(80L).withEndAction {
            v.animate().scaleX(1f).scaleY(1f).setDuration(120L).start()
        }.start()
    }

    private fun bestClientIp(): String? = latestEndpoints.firstOrNull()?.ip

    private fun copyText(label: String, value: String) {
        val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText(label, value))
        Toast.makeText(this, getString(R.string.toast_copied, label, value), Toast.LENGTH_SHORT).show()
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
        binding.btnBatteryOpt.text = getString(if (exempt) R.string.btn_battery_ok else R.string.btn_battery_opt)

        val notifGranted = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        } else true
        binding.tvHealthNotif.text = getString(if (notifGranted) R.string.health_notif_ok else R.string.health_notif_wait)
        binding.tvHealthNotif.setTextColor(Color.parseColor(if (notifGranted) "#34D399" else "#FBBF24"))
    }

    private fun formatBytes(bytes: Long): String = when {
        bytes < 1024 -> "${bytes} B"
        bytes < 1024 * 1024 -> String.format(Locale.US, "%.1f KB", bytes / 1024.0)
        bytes < 1024L * 1024L * 1024L -> String.format(Locale.US, "%.2f MB", bytes / (1024.0 * 1024.0))
        else -> String.format(Locale.US, "%.2f GB", bytes / (1024.0 * 1024.0 * 1024.0))
    }
}