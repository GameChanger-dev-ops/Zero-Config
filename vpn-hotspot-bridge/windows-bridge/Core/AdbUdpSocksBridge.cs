using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroConfigBridge.Core;

public sealed class AdbUdpSocksBridge : IDisposable
{
    private readonly int _adbSocksPort;
    private readonly int _adbUdpMuxPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int LocalPort { get; private set; }

    public AdbUdpSocksBridge(int adbSocksPort = 1080, int adbUdpMuxPort = 1081)
    {
        _adbSocksPort = adbSocksPort;
        _adbUdpMuxPort = adbUdpMuxPort;
    }

    public int Start(int listenPort = 1082)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _listener.Start(128);
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return LocalPort;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { if (ct.IsCancellationRequested) break; }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var clientStream = client.GetStream();
                byte[] header = new byte[258];

                int read = await clientStream.ReadAsync(header.AsMemory(0, 2), ct).ConfigureAwait(false);
                if (read < 2 || header[0] != 0x05) return;
                int nMethods = header[1];
                await ReadExactAsync(clientStream, header, 0, nMethods, ct).ConfigureAwait(false);
                await clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, ct).ConfigureAwait(false);

                await ReadExactAsync(clientStream, header, 0, 4, ct).ConfigureAwait(false);
                if (header[0] != 0x05) return;
                byte cmd = header[1];
                byte atyp = header[3];

                int addrLen = atyp switch
                {
                    0x01 => 4,
                    0x04 => 16,
                    0x03 => await ReadDomainLengthAsync(clientStream, header, ct).ConfigureAwait(false),
                    _ => -1
                };
                if (addrLen <= 0) return;

                int addrOffset = atyp == 0x03 ? 5 : 4;
                await ReadExactAsync(clientStream, header, addrOffset, addrLen + 2, ct).ConfigureAwait(false);
                int totalReqLen = addrOffset + addrLen + 2;

                if (cmd == 0x01)
                    await ProxyTcpConnectAsync(clientStream, header, totalReqLen, ct).ConfigureAwait(false);
                else if (cmd == 0x03)
                    await ProxyUdpAssociateAsync(clientStream, ct).ConfigureAwait(false);
                else
                    await clientStream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task ProxyTcpConnectAsync(NetworkStream clientStream, byte[] reqBytes, int reqLen, CancellationToken ct)
    {
        using var upstream = new TcpClient { NoDelay = true };
        await upstream.ConnectAsync(IPAddress.Loopback, _adbSocksPort, ct).ConfigureAwait(false);
        var upStream = upstream.GetStream();

        await upStream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct).ConfigureAwait(false);
        byte[] authResp = new byte[2];
        await ReadExactAsync(upStream, authResp, 0, 2, ct).ConfigureAwait(false);
        if (authResp[1] != 0x00) return;

        await upStream.WriteAsync(reqBytes.AsMemory(0, reqLen), ct).ConfigureAwait(false);

        var t1 = RelayAsync(clientStream, upStream, ct);
        var t2 = RelayAsync(upStream, clientStream, ct);
        await Task.WhenAny(t1, t2).ConfigureAwait(false);
    }

    private async Task ProxyUdpAssociateAsync(NetworkStream clientControlStream, CancellationToken ct)
    {
        using var udpSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var muxClient = new TcpClient { NoDelay = true };
        await muxClient.ConnectAsync(IPAddress.Loopback, _adbUdpMuxPort, ct).ConfigureAwait(false);
        var muxStream = muxClient.GetStream();

        var localEp = (IPEndPoint)udpSocket.Client.LocalEndPoint!;
        byte[] addrBytes = localEp.Address.GetAddressBytes();
        byte[] reply = new byte[10];
        reply[0] = 0x05; reply[1] = 0x00; reply[2] = 0x00; reply[3] = 0x01;
        Buffer.BlockCopy(addrBytes, 0, reply, 4, 4);
        reply[8] = (byte)((localEp.Port >> 8) & 0xFF);
        reply[9] = (byte)(localEp.Port & 0xFF);
        await clientControlStream.WriteAsync(reply, ct).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;
        IPEndPoint? lastPeer = null;
        var muxWriteLock = new SemaphoreSlim(1, 1);

        var udpToMux = Task.Run(async () =>
        {
            byte[] lenPrefix = new byte[2];
            while (!token.IsCancellationRequested)
            {
                var result = await udpSocket.ReceiveAsync(token).ConfigureAwait(false);
                lastPeer = result.RemoteEndPoint;
                int len = result.Buffer.Length;
                if (len <= 0 || len > 65535) continue;

                lenPrefix[0] = (byte)((len >> 8) & 0xFF);
                lenPrefix[1] = (byte)(len & 0xFF);
                await muxWriteLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await muxStream.WriteAsync(lenPrefix, token).ConfigureAwait(false);
                    await muxStream.WriteAsync(result.Buffer, token).ConfigureAwait(false);
                }
                finally { muxWriteLock.Release(); }
            }
        }, token);

        var muxToUdp = Task.Run(async () =>
        {
            byte[] lenBuf = new byte[2];
            byte[] frameBuf = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await ReadExactAsync(muxStream, lenBuf, 0, 2, token).ConfigureAwait(false);
                    int len = (lenBuf[0] << 8) | lenBuf[1];
                    if (len <= 0 || len > frameBuf.Length) break;
                    await ReadExactAsync(muxStream, frameBuf, 0, len, token).ConfigureAwait(false);
                    if (lastPeer != null)
                        await udpSocket.SendAsync(frameBuf.AsMemory(0, len), lastPeer, token).ConfigureAwait(false);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(frameBuf); }
        }, token);

        var controlWatcher = Task.Run(async () =>
        {
            byte[] sink = new byte[16];
            while (!token.IsCancellationRequested)
            {
                int n = await clientControlStream.ReadAsync(sink, token).ConfigureAwait(false);
                if (n <= 0) break;
            }
        }, token);

        await Task.WhenAny(udpToMux, muxToUdp, controlWatcher).ConfigureAwait(false);
        linkedCts.Cancel();
    }

    private static async Task<int> ReadDomainLengthAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        await ReadExactAsync(s, buf, 4, 1, ct).ConfigureAwait(false);
        return buf[4];
    }

    private static async Task RelayAsync(Stream src, Stream dst, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await src.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        catch { }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset + total, count - total), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();
            total += n;
        }
    }

    public void Dispose() => Stop();
}