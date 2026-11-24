package com.vpnbridge.hub.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.net.InetAddress

data class VpnState(
    val isActive: Boolean = false,
    val network: Network? = null,
    val interfaceName: String = "",
    val dnsServers: List<InetAddress> = emptyList()
)

class VpnNetworkMonitor(context: Context) {

    private val connectivityManager =
        context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager

    private val _vpnState = MutableStateFlow(VpnState())
    val vpnState: StateFlow<VpnState> = _vpnState.asStateFlow()

    @Volatile
    private var activeVpnNetwork: Network? = null
    private var registered = false

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            updateNetwork(network)
        }

        override fun onCapabilitiesChanged(
            network: Network,
            networkCapabilities: NetworkCapabilities
        ) {
            if (networkCapabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
                updateNetwork(network)
            }
        }

        override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
            if (network == activeVpnNetwork) {
                _vpnState.value = VpnState(
                    isActive = true,
                    network = network,
                    interfaceName = linkProperties.interfaceName ?: "tun0",
                    dnsServers = linkProperties.dnsServers
                )
            }
        }

        override fun onLost(network: Network) {
            if (network == activeVpnNetwork) {
                val replacement = findExistingVpnNetwork(excluding = network)
                if (replacement != null) {
                    updateNetwork(replacement)
                } else {
                    activeVpnNetwork = null
                    _vpnState.value = VpnState(isActive = false)
                }
            }
        }
    }

    fun start() {
        if (registered) return
        val existing = findExistingVpnNetwork()
        if (existing != null) {
            updateNetwork(existing)
        }
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_VPN)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            .build()
        connectivityManager.registerNetworkCallback(request, networkCallback)
        registered = true
    }

    fun stop() {
        if (!registered) return
        try {
            connectivityManager.unregisterNetworkCallback(networkCallback)
        } catch (_: IllegalArgumentException) {
        }
        registered = false
        activeVpnNetwork = null
        _vpnState.value = VpnState(isActive = false)
    }

    fun currentVpnNetwork(): Network? = activeVpnNetwork

    private fun updateNetwork(network: Network) {
        val caps = connectivityManager.getNetworkCapabilities(network) ?: return
        if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) return
        activeVpnNetwork = network
        val linkProps = connectivityManager.getLinkProperties(network)
        _vpnState.value = VpnState(
            isActive = true,
            network = network,
            interfaceName = linkProps?.interfaceName ?: "tun0",
            dnsServers = linkProps?.dnsServers ?: emptyList()
        )
    }

    @Suppress("DEPRECATION")
    private fun findExistingVpnNetwork(excluding: Network? = null): Network? {
        for (net in connectivityManager.allNetworks) {
            if (net == excluding) continue
            val caps = connectivityManager.getNetworkCapabilities(net) ?: continue
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
                return net
            }
        }
        return null
    }
}