package com.vpnbridge.hub

import com.vpnbridge.hub.network.HttpProxyServer
import com.vpnbridge.hub.network.Socks5ProxyServer
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.BufferedReader
import java.io.DataInputStream
import java.io.InputStreamReader
import java.net.ServerSocket
import java.net.Socket

class Socks5ProxyServerTest {

    private fun findFreePort(): Int = ServerSocket(0).use { it.localPort }

    @Test
    fun socks5HandshakeAndFailClosedWithoutVpn() {
        val socksPort = findFreePort()
        val muxPort = findFreePort()
        val server = Socks5ProxyServer(vpnNetworkProvider = { null }, socksPort = socksPort, udpMuxPort = muxPort)
        server.start()
        try {
            val client = Socket("127.0.0.1", socksPort)
            try {
                client.soTimeout = 3000
                val out = client.getOutputStream()
                val inp = DataInputStream(client.getInputStream())

                out.write(byteArrayOf(0x05, 0x01, 0x00))
                out.flush()
                val authResp = ByteArray(2)
                inp.readFully(authResp)
                assertArrayEquals(byteArrayOf(0x05, 0x00), authResp)

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
        val server = Socks5ProxyServer(vpnNetworkProvider = { null }, socksPort = socksPort, udpMuxPort = muxPort)
        server.start()
        try {
            val client = Socket("127.0.0.1", socksPort)
            try {
                client.soTimeout = 3000
                val out = client.getOutputStream()
                val inp = DataInputStream(client.getInputStream())

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

    @Test
    fun httpProxyFailsClosedWith503WithoutVpn() {
        val httpPort = findFreePort()
        val server = HttpProxyServer(vpnNetworkProvider = { null }, httpPort = httpPort)
        server.start()
        try {
            val client = Socket("127.0.0.1", httpPort)
            try {
                client.soTimeout = 3000
                val out = client.getOutputStream()
                out.write("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n".toByteArray())
                out.flush()
                val reader = BufferedReader(InputStreamReader(client.getInputStream()))
                val statusLine = reader.readLine() ?: ""
                assertTrue("Expected 503 status, got: $statusLine", statusLine.contains("503"))
            } finally {
                client.close()
            }
        } finally {
            server.stop()
        }
    }
}