package com.vpnbridge.hub.network

import android.net.Network
import android.util.Log
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

class Socks5ProxyServer(
    private val vpnNetworkProvider: () -> Network?,
    val socksPort: Int = 1080,
    val udpMuxPort: Int = 1081
) {
    companion object {
        private const val TAG = "Socks5ProxyServer"
        private const val BUFFER_SIZE = 65536
        private const val MAX_POOL_BUFFERS = 128
        private const val SOCKET_BUF_SIZE = 262144
        private const val CONNECT_TIMEOUT_MS = 12000
        private const val UDP_SESSION_IDLE_MS = 120_000L
        private const val SOCKS_VERSION: Byte = 0x05
    }

    private val running = AtomicBoolean(false)
    private var socksServerSocket: ServerSocket? = null
    private var muxServerSocket: ServerSocket? = null
    private var workerPool: ExecutorService? = null
    private var watchdogPool: ScheduledExecutorService? = null

    private val bufferPool = ConcurrentLinkedQueue<ByteArray>()
    private val activeSockets = ConcurrentHashMap.newKeySet<Socket>()
    private val activeUdpSockets = ConcurrentHashMap.newKeySet<DatagramSocket>()

    val activeConnections = AtomicInteger(0)
    val txBytes = AtomicLong(0L)
    val rxBytes = AtomicLong(0L)

    fun start() {
        if (!running.compareAndSet(false, true)) return

        workerPool = Executors.newCachedThreadPool { r ->
            Thread(r, "ZeroConfig-Worker").apply { isDaemon = true }
        }
        watchdogPool = Executors.newSingleThreadScheduledExecutor { r ->
            Thread(r, "ZeroConfig-Watchdog").apply { isDaemon = true }
        }

        socksServerSocket = ServerSocket().apply {
            reuseAddress = true
            receiveBufferSize = SOCKET_BUF_SIZE
            bind(InetSocketAddress(InetAddress.getByName("0.0.0.0"), socksPort), 128)
        }

        muxServerSocket = ServerSocket().apply {
            reuseAddress = true
            receiveBufferSize = SOCKET_BUF_SIZE
            bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), udpMuxPort), 32)
        }

        workerPool?.execute { acceptLoop(socksServerSocket!!, isMux = false) }
        workerPool?.execute { acceptLoop(muxServerSocket!!, isMux = true) }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        try { socksServerSocket?.close() } catch (_: IOException) {}
        try { muxServerSocket?.close() } catch (_: IOException) {}
        socksServerSocket = null
        muxServerSocket = null

        activeSockets.forEach { try { it.close() } catch (_: IOException) {} }
        activeSockets.clear()
        activeUdpSockets.forEach { try { it.close() } catch (_: Exception) {} }
        activeUdpSockets.clear()

        workerPool?.shutdownNow()
        watchdogPool?.shutdownNow()
        workerPool = null
        watchdogPool = null
        bufferPool.clear()
        activeConnections.set(0)
    }

    private fun acquireBuffer(): ByteArray = bufferPool.poll() ?: ByteArray(BUFFER_SIZE)

    private fun releaseBuffer(buf: ByteArray) {
        if (buf.size == BUFFER_SIZE && bufferPool.size < MAX_POOL_BUFFERS) {
            bufferPool.offer(buf)
        }
    }

    private fun acceptLoop(server: ServerSocket, isMux: Boolean) {
        while (running.get()) {
            try {
                val client = server.accept()
                configureTcpSocket(client)
                activeSockets.add(client)
                workerPool?.execute {
                    activeConnections.incrementAndGet()
                    try {
                        if (isMux) handleUdpMuxClient(client) else handleSocksClient(client)
                    } finally {
                        activeConnections.decrementAndGet()
                        activeSockets.remove(client)
                        try { client.close() } catch (_: IOException) {}
                    }
                }
            } catch (e: IOException) {
                if (running.get()) Log.w(TAG, "Accept error: ${e.message}")
            }
        }
    }

    private fun configureTcpSocket(socket: Socket) {
        try {
            socket.tcpNoDelay = true
            socket.keepAlive = true
            socket.receiveBufferSize = SOCKET_BUF_SIZE
            socket.sendBufferSize = SOCKET_BUF_SIZE
        } catch (_: Exception) {}
    }

    private fun handleSocksClient(client: Socket) {
        try {
            client.soTimeout = 15000
            val input = BufferedInputStream(client.getInputStream(), 16384)
            val output = BufferedOutputStream(client.getOutputStream(), 16384)

            val ver = input.read()
            val nMethods = input.read()
            if (ver != 0x05 || nMethods <= 0) return

            var supportsNoAuth = false
            for (i in 0 until nMethods) {
                val method = input.read()
                if (method == -1) return
                if (method == 0x00) supportsNoAuth = true
            }
            if (!supportsNoAuth) {
                output.write(byteArrayOf(SOCKS_VERSION, 0xFF.toByte()))
                output.flush()
                return
            }
            output.write(byteArrayOf(SOCKS_VERSION, 0x00))
            output.flush()

            val reqVer = input.read()
            val cmd = input.read()
            val rsv = input.read()
            val atyp = input.read()
            if (reqVer != 0x05 || rsv != 0x00 || atyp == -1) return

            val targetHost = readAddress(input, atyp) ?: run {
                sendReply(output, 0x08)
                return
            }
            val portHi = input.read()
            val portLo = input.read()
            if (portHi == -1 || portLo == -1) return
            val targetPort = (portHi shl 8) or portLo

            client.soTimeout = 0

            val vpnNetwork = vpnNetworkProvider()
            if (vpnNetwork == null) {
                sendReply(output, 0x03)
                return
            }

            when (cmd) {
                0x01 -> handleTcpConnect(client, input, output, vpnNetwork, targetHost, targetPort)
                0x03 -> handleUdpAssociate(client, input, output, vpnNetwork)
                else -> sendReply(output, 0x07)
            }
        } catch (_: IOException) {}
    }

    private fun handleTcpConnect(
        client: Socket,
        clientIn: InputStream,
        clientOut: OutputStream,
        vpnNetwork: Network,
        host: String,
        port: Int
    ) {
        val remote = Socket()
        activeSockets.add(remote)
        try {
            configureTcpSocket(remote)
            vpnNetwork.bindSocket(remote)
            val resolved = resolveOverVpn(vpnNetwork, host)
            remote.connect(InetSocketAddress(resolved, port), CONNECT_TIMEOUT_MS)

            val boundAddr = remote.localAddress ?: InetAddress.getByAddress(byteArrayOf(0, 0, 0, 0))
            sendReply(clientOut, 0x00, boundAddr, remote.localPort)

            val remoteIn = remote.getInputStream()
            val remoteOut = remote.getOutputStream()

            val downstreamFuture = workerPool?.submit {
                pumpStream(remoteIn, clientOut, rxBytes)
                try { client.shutdownOutput() } catch (_: Exception) {}
            }

            pumpStream(clientIn, remoteOut, txBytes)
            try { remote.shutdownOutput() } catch (_: Exception) {}
            try { downstreamFuture?.get() } catch (_: Exception) {}
        } catch (_: IOException) {
            try { sendReply(clientOut, 0x04) } catch (_: IOException) {}
        } finally {
            activeSockets.remove(remote)
            try { remote.close() } catch (_: IOException) {}
        }
    }

    private fun handleUdpAssociate(
        client: Socket,
        clientIn: InputStream,
        clientOut: OutputStream,
        vpnNetwork: Network
    ) {
        val clientFacingUdp = DatagramSocket(0, client.localAddress)
        val upstreamUdp = DatagramSocket()
        activeUdpSockets.add(clientFacingUdp)
        activeUdpSockets.add(upstreamUdp)

        try {
            clientFacingUdp.receiveBufferSize = SOCKET_BUF_SIZE
            clientFacingUdp.sendBufferSize = SOCKET_BUF_SIZE
            upstreamUdp.receiveBufferSize = SOCKET_BUF_SIZE
            upstreamUdp.sendBufferSize = SOCKET_BUF_SIZE

            vpnNetwork.bindSocket(upstreamUdp)
            sendReply(clientOut, 0x00, clientFacingUdp.localAddress, clientFacingUdp.localPort)

            val sessionAlive = AtomicBoolean(true)
            val lastActivity = AtomicLong(System.currentTimeMillis())
            val lastClientAddr = AtomicReference<SocketAddressHolder?>(null)

            val timeoutTask = watchdogPool?.scheduleWithFixedDelay({
                if (System.currentTimeMillis() - lastActivity.get() > UDP_SESSION_IDLE_MS) {
                    sessionAlive.set(false)
                    try { client.close() } catch (_: IOException) {}
                    try { clientFacingUdp.close() } catch (_: Exception) {}
                    try { upstreamUdp.close() } catch (_: Exception) {}
                }
            }, 30, 30, TimeUnit.SECONDS)

            val upstreamFuture = workerPool?.submit {
                val recvBuf = acquireBuffer()
                try {
                    val packet = DatagramPacket(recvBuf, recvBuf.size)
                    while (sessionAlive.get() && running.get()) {
                        upstreamUdp.receive(packet)
                        lastActivity.set(System.currentTimeMillis())
                        val peer = lastClientAddr.get() ?: continue
                        val framed = buildSocks5UdpPacket(packet.address, packet.port, recvBuf, packet.length)
                        rxBytes.addAndGet(packet.length.toLong())
                        clientFacingUdp.send(DatagramPacket(framed, framed.size, peer.address, peer.port))
                    }
                } catch (_: Exception) {
                } finally {
                    releaseBuffer(recvBuf)
                }
            }

            val clientFuture = workerPool?.submit {
                val recvBuf = acquireBuffer()
                try {
                    val packet = DatagramPacket(recvBuf, recvBuf.size)
                    while (sessionAlive.get() && running.get()) {
                        clientFacingUdp.receive(packet)
                        lastActivity.set(System.currentTimeMillis())
                        lastClientAddr.set(SocketAddressHolder(packet.address, packet.port))

                        val parsed = parseSocks5UdpPacket(recvBuf, packet.length, vpnNetwork) ?: continue
                        txBytes.addAndGet(parsed.payloadLength.toLong())
                        upstreamUdp.send(
                            DatagramPacket(recvBuf, parsed.payloadOffset, parsed.payloadLength, parsed.targetAddress, parsed.targetPort)
                        )
                    }
                } catch (_: Exception) {
                } finally {
                    releaseBuffer(recvBuf)
                }
            }

            try {
                while (sessionAlive.get() && running.get()) {
                    if (clientIn.read() == -1) break
                }
            } catch (_: IOException) {
            } finally {
                sessionAlive.set(false)
                timeoutTask?.cancel(false)
                try { clientFacingUdp.close() } catch (_: Exception) {}
                try { upstreamUdp.close() } catch (_: Exception) {}
                upstreamFuture?.cancel(true)
                clientFuture?.cancel(true)
            }
        } finally {
            activeUdpSockets.remove(clientFacingUdp)
            activeUdpSockets.remove(upstreamUdp)
            try { clientFacingUdp.close() } catch (_: Exception) {}
            try { upstreamUdp.close() } catch (_: Exception) {}
        }
    }

    private fun handleUdpMuxClient(tcpChannel: Socket) {
        val vpnNetwork = vpnNetworkProvider() ?: return
        val upstreamUdp = DatagramSocket()
        activeUdpSockets.add(upstreamUdp)
        try {
            upstreamUdp.receiveBufferSize = SOCKET_BUF_SIZE
            upstreamUdp.sendBufferSize = SOCKET_BUF_SIZE
            vpnNetwork.bindSocket(upstreamUdp)

            val dataIn = DataInputStream(BufferedInputStream(tcpChannel.getInputStream(), 32768))
            val dataOut = DataOutputStream(BufferedOutputStream(tcpChannel.getOutputStream(), 32768))
            val writeLock = Any()

            val readerTask = workerPool?.submit {
                val recvBuf = acquireBuffer()
                try {
                    val pkt = DatagramPacket(recvBuf, recvBuf.size)
                    while (running.get() && !tcpChannel.isClosed) {
                        upstreamUdp.receive(pkt)
                        val framed = buildSocks5UdpPacket(pkt.address, pkt.port, recvBuf, pkt.length)
                        rxBytes.addAndGet(pkt.length.toLong())
                        synchronized(writeLock) {
                            dataOut.writeShort(framed.size)
                            dataOut.write(framed)
                            dataOut.flush()
                        }
                    }
                } catch (_: Exception) {
                } finally {
                    releaseBuffer(recvBuf)
                }
            }

            val frameBuf = ByteArray(BUFFER_SIZE)
            while (running.get() && !tcpChannel.isClosed) {
                val len = try {
                    dataIn.readUnsignedShort()
                } catch (_: EOFException) {
                    break
                }
                if (len <= 0 || len > frameBuf.size) break
                dataIn.readFully(frameBuf, 0, len)

                val currentVpn = vpnNetworkProvider() ?: break
                val parsed = parseSocks5UdpPacket(frameBuf, len, currentVpn) ?: continue
                txBytes.addAndGet(parsed.payloadLength.toLong())
                upstreamUdp.send(
                    DatagramPacket(frameBuf, parsed.payloadOffset, parsed.payloadLength, parsed.targetAddress, parsed.targetPort)
                )
            }
            readerTask?.cancel(true)
        } catch (_: Exception) {
        } finally {
            activeUdpSockets.remove(upstreamUdp)
            try { upstreamUdp.close() } catch (_: Exception) {}
        }
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

    private fun resolveOverVpn(vpnNetwork: Network, host: String): InetAddress {
        val candidates = vpnNetwork.getAllByName(host)
        return candidates.firstOrNull { it is Inet4Address } ?: candidates.first()
    }

    private fun readAddress(input: InputStream, atyp: Int): String? {
        return when (atyp) {
            0x01 -> {
                val addr = ByteArray(4)
                if (!readExact(input, addr)) return null
                InetAddress.getByAddress(addr).hostAddress
            }
            0x03 -> {
                val len = input.read()
                if (len <= 0) return null
                val domain = ByteArray(len)
                if (!readExact(input, domain)) return null
                String(domain, Charsets.UTF_8)
            }
            0x04 -> {
                val addr = ByteArray(16)
                if (!readExact(input, addr)) return null
                InetAddress.getByAddress(addr).hostAddress
            }
            else -> null
        }
    }

    private fun readExact(input: InputStream, dest: ByteArray): Boolean {
        var offset = 0
        while (offset < dest.size) {
            val r = input.read(dest, offset, dest.size - offset)
            if (r == -1) return false
            offset += r
        }
        return true
    }

    private fun sendReply(
        output: OutputStream,
        rep: Int,
        bindAddress: InetAddress = InetAddress.getByAddress(byteArrayOf(0, 0, 0, 0)),
        bindPort: Int = 0
    ) {
        val addrBytes = bindAddress.address
        val atyp: Byte = if (addrBytes.size == 16) 0x04 else 0x01
        val resp = ByteArray(6 + addrBytes.size)
        resp[0] = SOCKS_VERSION
        resp[1] = rep.toByte()
        resp[2] = 0x00
        resp[3] = atyp
        System.arraycopy(addrBytes, 0, resp, 4, addrBytes.size)
        resp[4 + addrBytes.size] = ((bindPort ushr 8) and 0xFF).toByte()
        resp[5 + addrBytes.size] = (bindPort and 0xFF).toByte()
        output.write(resp)
        output.flush()
    }

    private data class SocketAddressHolder(val address: InetAddress, val port: Int)

    private data class ParsedUdpPacket(
        val targetAddress: InetAddress,
        val targetPort: Int,
        val payloadOffset: Int,
        val payloadLength: Int
    )

    private fun parseSocks5UdpPacket(data: ByteArray, length: Int, vpnNetwork: Network): ParsedUdpPacket? {
        if (length < 10) return null
        val frag = data[2].toInt() and 0xFF
        if (frag != 0) return null

        val atyp = data[3].toInt() and 0xFF
        var offset = 4
        val targetAddress: InetAddress = when (atyp) {
            0x01 -> {
                if (length < offset + 4 + 2) return null
                val raw = data.copyOfRange(offset, offset + 4)
                offset += 4
                InetAddress.getByAddress(raw)
            }
            0x03 -> {
                if (length < offset + 1) return null
                val dLen = data[offset].toInt() and 0xFF
                offset += 1
                if (dLen == 0 || length < offset + dLen + 2) return null
                val host = String(data, offset, dLen, Charsets.UTF_8)
                offset += dLen
                try { resolveOverVpn(vpnNetwork, host) } catch (_: Exception) { return null }
            }
            0x04 -> {
                if (length < offset + 16 + 2) return null
                val raw = data.copyOfRange(offset, offset + 16)
                offset += 16
                InetAddress.getByAddress(raw)
            }
            else -> return null
        }

        val port = ((data[offset].toInt() and 0xFF) shl 8) or (data[offset + 1].toInt() and 0xFF)
        offset += 2
        val payloadLen = length - offset
        if (payloadLen <= 0) return null

        return ParsedUdpPacket(targetAddress, port, offset, payloadLen)
    }

    private fun buildSocks5UdpPacket(
        srcAddress: InetAddress,
        srcPort: Int,
        payload: ByteArray,
        payloadLength: Int
    ): ByteArray {
        val addrBytes = srcAddress.address
        val atyp: Byte = if (srcAddress is Inet6Address || addrBytes.size == 16) 0x04 else 0x01
        val out = ByteBuffer.allocate(6 + addrBytes.size + payloadLength)
        out.put(0x00)
        out.put(0x00)
        out.put(0x00)
        out.put(atyp)
        out.put(addrBytes)
        out.putShort((srcPort and 0xFFFF).toShort())
        out.put(payload, 0, payloadLength)
        return out.array()
    }
}