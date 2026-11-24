# Technical Research & Verification Log — Zero-Config

This document records verified OS-level APIs, protocol constraints, edge cases, and performance design decisions for **Zero-Config**, a non-rooted Android-to-Windows VPN Hotspot Bridge.

## 1. Android VPN Socket Binding (`TRANSPORT_VPN`)
- `NetworkRequest.Builder()` implicitly adds `NET_CAPABILITY_NOT_VPN` by default; it must be removed via `.removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)`.
- Both TCP `Socket` and UDP `DatagramSocket` instances bind via `vpnNetwork.bindSocket(socket)` before `connect()`, and domain resolution uses `vpnNetwork.getAllByName(host)` so DNS never leaks outside `tun0`.
- Android 14/15 foreground service uses `FOREGROUND_SERVICE_TYPE_SPECIAL_USE` plus `PARTIAL_WAKE_LOCK` and `WifiLock`.

## 2. High-Performance SOCKS5 TCP + UDP Relay
- Full RFC 1928 `CONNECT` (`0x01`) and `UDP ASSOCIATE` (`0x03`) with pooled 64 KB buffers, `TCP_NODELAY = true`, 256 KB socket buffers, and persistent per-session UDP NAT sockets.
- Port `1081` provides a length-framed UDP-over-TCP multiplexer for ADB USB fallback (`adb forward tcp:1080 tcp:1080` + `adb forward tcp:1081 tcp:1081`).

## 3. Windows Virtual NIC, Mobile Hotspot & ICS
- `tun2socks.exe` + `wintun.dll` (x64) create deterministic adapter `VPNBridge` (`MTU 1420`, metric `5`, DNS `1.1.1.1`/`8.8.8.8`, anti-loop `/32` host route).
- WinRT `NetworkOperatorTetheringManager` starts Mobile Hotspot and `HNetCfg.HNetShare` COM automation bridges `VPNBridge` (`PUBLIC`) to `Microsoft Wi-Fi Direct Virtual Adapter` (`PRIVATE`), with automatic cleanup on exit.