using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ZeroConfigBridge.Core;

namespace VpnBridge.Tests;

public class BridgeCoreTests
{
    [Fact]
    public async Task AdbUdpSocksBridge_CompletesHandshakeAndRejectsUnsupportedCommand()
    {
        using var bridge = new AdbUdpSocksBridge();
        int port = bridge.Start(0);
        Assert.True(port > 0);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        // Greeting: VER=5, NMETHODS=1, NO_AUTH=0
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        byte[] authResp = new byte[2];
        int n = await stream.ReadAsync(authResp);
        Assert.Equal(2, n);
        Assert.Equal(0x05, authResp[0]);
        Assert.Equal(0x00, authResp[1]);

        // Unsupported BIND (0x02) -> expects REP=0x07 (Command not supported)
        await stream.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x01, 127, 0, 0, 1, 0x00, 0x50 });
        byte[] cmdResp = new byte[10];
        int m = await stream.ReadAsync(cmdResp);
        Assert.Equal(10, m);
        Assert.Equal(0x05, cmdResp[0]);
        Assert.Equal(0x07, cmdResp[1]);
    }

    [Fact]
    public async Task Tun2SocksManager_ThrowsCleanlyWhenBinariesMissing()
    {
        using var mgr = new Tun2SocksManager();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            mgr.StartAsync("127.0.0.1", 1080, null, null, null, CancellationToken.None));
        mgr.Stop(); // Must be safe/idempotent
    }

    [Fact]
    public void HotspotController_GetCurrentStatusDoesNotThrowOnHeadlessVm()
    {
        var controller = new HotspotController();
        var status = controller.GetCurrentStatus();
        Assert.NotNull(status);
    }
}