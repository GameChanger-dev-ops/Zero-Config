package com.vpnbridge.hub

import com.vpnbridge.hub.network.Socks5ProxyServer
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.DataInputStream
import java.net.ServerSocket
import java.net.Socket

class Socks5ProxyServerTest {

    private fun findFreePort(): Int = ServerSocket(0).use { it.localPort }

    @Test
    fun socks5HandshakeAndFailClosedWithoutVpn() {
        val socksPort = findFreePort()
        val muxPort = findFreePort()
        val server = Socks5ProxyServer(
            vpnNetworkProvider = { null },
            socksPort = socksPort,
            udpMuxPort = muxPort
        )
        server.start()
        try {
            val client = Socket("127.0.0.1", socksPort)
            try {
                client.soTimeout = 3000
                val out = client.getOutputStream()
                val inp = DataInputStream(client.getInputStream())

                // 1. Send SOCKS5 greeting (No Auth 0x00)
                out.write(byteArrayOf(0x05, 0x01, 0x00))
                out.flush()
                val authResp = ByteArray(2)
                inp.readFully(authResp)
                assertArrayEquals(byteArrayOf(0x05, 0x00), authResp)

                // 2. Send CONNECT 1.1.1.1:80 -> must fail closed with 0x03 (Network Unreachable)
                out.write(byteArrayOf(0x05, 0x01, 0x00, 0x01, 1, 1, 1, 1, 0x00, 0x50))
                out.flush()
                val connResp = ByteArray(10)
                inp.readFully(connResp)
                assertEquals(0x05.toByte(), connResp[0])
                assertEquals(0x03.toByte(), connResp[1])
            } finally {
                client.close()
            }
        } finally {
            server.stop()
        }
    }

    @Test
    fun socks5RejectsUnsupportedAuthMethods() {
        val socksPort = findFreePort()
        val muxPort = findFreePort()
        val server = Socks5ProxyServer(
            vpnNetworkProvider = { null },
            socksPort = socksPort,
            udpMuxPort = muxPort
        )
        server.start()
        try {
            val client = Socket("127.0.0.1", socksPort)
            try {
                client.soTimeout = 3000
                val out = client.getOutputStream()
                val inp = DataInputStream(client.getInputStream())

                // Offer only Username/Password auth (0x02)
                out.write(byteArrayOf(0x05, 0x01, 0x02))
                out.flush()
                val authResp = ByteArray(2)
                inp.readFully(authResp)
                assertArrayEquals(byteArrayOf(0x05, 0xFF.toByte()), authResp)
            } finally {
                client.close()
            }
        } finally {
            server.stop()
        }
    }
}