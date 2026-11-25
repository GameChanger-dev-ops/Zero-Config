package com.vpnbridge.hub.network

import android.net.Network
import android.util.Log
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URI
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Standalone HTTP/HTTPS (CONNECT) proxy server ("Every-Proxy" compatible) on port 8080.
 * Strictly binds outbound sockets via vpnNetwork.bindSocket() and resolves hostnames via
 * vpnNetwork.getAllByName() so zero traffic leaks outside TRANSPORT_VPN.
 */
class HttpProxyServer(
    private val vpnNetworkProvider: () -> Network?,
    val httpPort: Int = 8080,
    private val sharedActiveConnections: AtomicInteger = AtomicInteger(0),
    private val sharedRxBytes: AtomicLong = AtomicLong(0L),
    private val sharedTxBytes: AtomicLong = AtomicLong(0L)
) {
    companion object {
        private const val TAG = "HttpProxyServer"
        private const val BUFFER_SIZE = 65536
        private const val MAX_POOL_BUFFERS = 64
        private const val SOCKET_BUF_SIZE = 262144
        private const val CONNECT_TIMEOUT_MS = 12000
        private const val MAX_HEADER_BYTES = 65536
    }

    private val running = AtomicBoolean(false)
    private var serverSocket: ServerSocket? = null
    private var workerPool: ExecutorService? = null
    private val bufferPool = ConcurrentLinkedQueue<ByteArray>()
    private val activeSockets = ConcurrentHashMap.newKeySet<Socket>()

    fun start() {
        if (!running.compareAndSet(false, true)) return
        workerPool = Executors.newCachedThreadPool { r ->
            Thread(r, "ZeroConfig-HttpWorker").apply { isDaemon = true }
        }
        serverSocket = ServerSocket().apply {
            reuseAddress = true
            receiveBufferSize = SOCKET_BUF_SIZE
            bind(InetSocketAddress(InetAddress.getByName("0.0.0.0"), httpPort), 128)
        }
        workerPool?.execute { acceptLoop() }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        try { serverSocket?.close() } catch (_: IOException) {}
        serverSocket = null
        activeSockets.forEach { try { it.close() } catch (_: IOException) {} }
        activeSockets.clear()
        workerPool?.shutdownNow()
        workerPool = null
        bufferPool.clear()
    }

    private fun acquireBuffer(): ByteArray = bufferPool.poll() ?: ByteArray(BUFFER_SIZE)
    private fun releaseBuffer(buf: ByteArray) {
        if (buf.size == BUFFER_SIZE && bufferPool.size < MAX_POOL_BUFFERS) bufferPool.offer(buf)
    }

    private fun acceptLoop() {
        val srv = serverSocket ?: return
        while (running.get()) {
            try {
                val client = srv.accept()
                configureSocket(client)
                activeSockets.add(client)
                workerPool?.execute {
                    sharedActiveConnections.incrementAndGet()
                    try {
                        handleClient(client)
                    } finally {
                        sharedActiveConnections.decrementAndGet()
                        activeSockets.remove(client)
                        try { client.close() } catch (_: IOException) {}
                    }
                }
            } catch (e: IOException) {
                if (running.get()) Log.w(TAG, "Accept error: ${e.message}")
            }
        }
    }

    private fun configureSocket(socket: Socket) {
        try {
            socket.tcpNoDelay = true
            socket.keepAlive = true
            socket.receiveBufferSize = SOCKET_BUF_SIZE
            socket.sendBufferSize = SOCKET_BUF_SIZE
        } catch (_: Exception) {}
    }

    private fun handleClient(client: Socket) {
        try {
            client.soTimeout = 15000
            val clientIn = BufferedInputStream(client.getInputStream(), 16384)
            val clientOut = client.getOutputStream()

            val headerBytes = readHttpHeaderBlock(clientIn) ?: return
            val headerText = String(headerBytes, Charsets.ISO_8859_1)
            val lines = headerText.split("\r\n").filter { it.isNotEmpty() }
            if (lines.isEmpty()) return

            val parts = lines[0].split(" ", limit = 3)
            if (parts.size < 3) {
                sendSimpleError(clientOut, 400, "Bad Request")
                return
            }

            val method = parts[0].uppercase()
            val target = parts[1]
            val version = parts[2]

            val vpnNetwork = vpnNetworkProvider()
            if (vpnNetwork == null) {
                sendSimpleError(clientOut, 503, "VPN Not Connected")
                return
            }

            client.soTimeout = 0

            if (method == "CONNECT") {
                handleConnectTunnel(client, clientIn, clientOut, vpnNetwork, target)
            } else {
                handlePlainHttp(client, clientIn, clientOut, vpnNetwork, method, target, version, lines.drop(1))
            }
        } catch (_: IOException) {}
    }

    private fun handleConnectTunnel(
        client: Socket,
        clientIn: InputStream,
        clientOut: OutputStream,
        vpnNetwork: Network,
        authority: String
    ) {
        val (host, port) = parseHostPort(authority, 443) ?: run {
            sendSimpleError(clientOut, 400, "Invalid CONNECT Target")
            return
        }

        val remote = Socket()
        activeSockets.add(remote)
        try {
            configureSocket(remote)
            vpnNetwork.bindSocket(remote)
            val ip = resolveOverVpn(vpnNetwork, host)
            remote.connect(InetSocketAddress(ip, port), CONNECT_TIMEOUT_MS)

            clientOut.write("HTTP/1.1 200 Connection Established\r\n\r\n".toByteArray(Charsets.ISO_8859_1))
            clientOut.flush()

            val remoteIn = remote.getInputStream()
            val remoteOut = remote.getOutputStream()
            val downstream = workerPool?.submit {
                pumpStream(remoteIn, clientOut, sharedRxBytes)
                try { client.shutdownOutput() } catch (_: Exception) {}
            }
            pumpStream(clientIn, remoteOut, sharedTxBytes)
            try { remote.shutdownOutput() } catch (_: Exception) {}
            try { downstream?.get() } catch (_: Exception) {}
        } catch (_: IOException) {
            try { sendSimpleError(clientOut, 502, "Bad Gateway") } catch (_: IOException) {}
        } finally {
            activeSockets.remove(remote)
            try { remote.close() } catch (_: IOException) {}
        }
    }

    private fun handlePlainHttp(
        client: Socket,
        clientIn: InputStream,
        clientOut: OutputStream,
        vpnNetwork: Network,
        method: String,
        targetUrl: String,
        version: String,
        rawHeaders: List<String>
    ) {
        val parsed = try { URI(targetUrl) } catch (_: Exception) { null }
        var host = parsed?.host
        var port = if ((parsed?.port ?: -1) > 0) parsed!!.port else 80
        var path = buildString {
            append(parsed?.rawPath?.ifEmpty { "/" } ?: targetUrl)
            if (!parsed?.rawQuery.isNullOrEmpty()) append("?").append(parsed!!.rawQuery)
        }

        if (host.isNullOrEmpty()) {
            val hostHeader = rawHeaders.firstOrNull { it.startsWith("Host:", ignoreCase = true) }
                ?.substringAfter(":")?.trim()
            val hp = hostHeader?.let { parseHostPort(it, 80) }
            if (hp != null) {
                host = hp.first
                port = hp.second
                if (!path.startsWith("/")) path = "/$path"
            }
        }
        if (host.isNullOrEmpty()) {
            sendSimpleError(clientOut, 400, "Missing Host")
            return
        }

        val remote = Socket()
        activeSockets.add(remote)
        try {
            configureSocket(remote)
            vpnNetwork.bindSocket(remote)
            val ip = resolveOverVpn(vpnNetwork, host)
            remote.connect(InetSocketAddress(ip, port), CONNECT_TIMEOUT_MS)

            val reqBuf = StringBuilder()
            reqBuf.append("$method $path $version\r\n")
            var hasHost = false
            for (h in rawHeaders) {
                val lower = h.lowercase()
                if (lower.startsWith("proxy-connection:") ||
                    lower.startsWith("proxy-authorization:") ||
                    lower.startsWith("connection:")) continue
                if (lower.startsWith("host:")) hasHost = true
                reqBuf.append(h).append("\r\n")
            }
            if (!hasHost) reqBuf.append("Host: $host\r\n")
            reqBuf.append("Connection: close\r\n\r\n")

            val remoteOut = remote.getOutputStream()
            val headBytes = reqBuf.toString().toByteArray(Charsets.ISO_8859_1)
            remoteOut.write(headBytes)
            remoteOut.flush()
            sharedTxBytes.addAndGet(headBytes.size.toLong())

            val remoteIn = remote.getInputStream()
            val upstream = workerPool?.submit {
                pumpStream(clientIn, remoteOut, sharedTxBytes)
                try { remote.shutdownOutput() } catch (_: Exception) {}
            }
            pumpStream(remoteIn, clientOut, sharedRxBytes)
            try { client.shutdownOutput() } catch (_: Exception) {}
            try { upstream?.get() } catch (_: Exception) {}
        } catch (_: IOException) {
            try { sendSimpleError(clientOut, 502, "Bad Gateway") } catch (_: IOException) {}
        } finally {
            activeSockets.remove(remote)
            try { remote.close() } catch (_: IOException) {}
        }
    }

    private fun readHttpHeaderBlock(input: InputStream): ByteArray? {
        val baos = ByteArrayOutputStream(512)
        var state = 0
        while (baos.size() < MAX_HEADER_BYTES) {
            val b = input.read()
            if (b == -1) return null
            baos.write(b)
            state = when (state) {
                0 -> if (b == '\r'.code) 1 else 0
                1 -> if (b == '\n'.code) 2 else if (b == '\r'.code) 1 else 0
                2 -> if (b == '\r'.code) 3 else 0
                3 -> if (b == '\n'.code) return baos.toByteArray() else if (b == '\r'.code) 1 else 0
                else -> 0
            }
        }
        return null
    }

    private fun parseHostPort(authority: String, defaultPort: Int): Pair<String, Int>? {
        if (authority.startsWith("[")) {
            val close = authority.indexOf(']')
            if (close <= 1) return null
            val host = authority.substring(1, close)
            val port = if (close + 1 < authority.length && authority[close + 1] == ':') {
                authority.substring(close + 2).toIntOrNull() ?: return null
            } else defaultPort
            return host to port
        }
        val colon = authority.lastIndexOf(':')
        return if (colon > 0) {
            val host = authority.substring(0, colon)
            val port = authority.substring(colon + 1).toIntOrNull() ?: return null
            host to port
        } else {
            authority to defaultPort
        }
    }

    private fun resolveOverVpn(vpnNetwork: Network, host: String): InetAddress {
        val candidates = vpnNetwork.getAllByName(host)
        return candidates.firstOrNull { it is Inet4Address } ?: candidates.first()
    }

    private fun pumpStream(src: InputStream, dst: OutputStream, counter: AtomicLong) {
        val buf = acquireBuffer()
        try {
            while (running.get()) {
                val n = src.read(buf)
                if (n <= 0) break
                dst.write(buf, 0, n)
                if (src.available() == 0) dst.flush()
                counter.addAndGet(n.toLong())
            }
            dst.flush()
        } catch (_: IOException) {
        } finally {
            releaseBuffer(buf)
        }
    }

    private fun sendSimpleError(out: OutputStream, code: Int, message: String) {
        val body = message.toByteArray(Charsets.UTF_8)
        val resp = "HTTP/1.1 $code $message\r\nContent-Type: text/plain\r\nContent-Length: ${body.size}\r\nConnection: close\r\n\r\n"
            .toByteArray(Charsets.ISO_8859_1)
        out.write(resp)
        out.write(body)
        out.flush()
    }
}