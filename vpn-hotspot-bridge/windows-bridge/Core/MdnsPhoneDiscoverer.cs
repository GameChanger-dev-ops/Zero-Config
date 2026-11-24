using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace ZeroConfigBridge.Core;

public enum DiscoveryTransport { UsbTethering, WifiMdns, AdbFallback }
public enum PreferredDiscoveryMode { Auto, UsbOnly, WifiOnly, AdbOnly }

public sealed record DiscoveredPhoneEndpoint(
    string ServiceName,
    IPAddress Address,
    int Port,
    int UdpMuxPort,
    DiscoveryTransport Transport,
    string? UplinkInterfaceName,
    IPAddress? UplinkGateway
);

public sealed class MdnsPhoneDiscoverer
{
    private const string ServiceType = "_vpnhotspot._tcp";
    private const int DefaultSocksPort = 1080;
    private const int DefaultUdpMuxPort = 1081;

    public event Action<string>? OnLog;

    public async Task<DiscoveredPhoneEndpoint> DiscoverAsync(
        PreferredDiscoveryMode mode,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        if (mode is PreferredDiscoveryMode.Auto or PreferredDiscoveryMode.UsbOnly)
        {
            var usb = await TryDiscoverUsbTetheringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (usb != null)
            {
                OnLog?.Invoke($"USB Tethering hub detected: {usb.Address}:{usb.Port} via [{usb.UplinkInterfaceName}]");
                return usb;
            }
            if (mode == PreferredDiscoveryMode.UsbOnly)
                throw new TimeoutException("No Android Zero-Config hub found on USB Tethering interface.");
        }

        if (mode is PreferredDiscoveryMode.Auto or PreferredDiscoveryMode.WifiOnly)
        {
            var mdnsTimeout = mode == PreferredDiscoveryMode.Auto ? TimeSpan.FromSeconds(6) : timeout;
            var wifi = await TryDiscoverMdnsAsync(mdnsTimeout, timeoutCts.Token).ConfigureAwait(false);
            if (wifi != null)
            {
                OnLog?.Invoke($"mDNS hub discovered: {wifi.ServiceName} at {wifi.Address}:{wifi.Port} via [{wifi.UplinkInterfaceName}]");
                return wifi;
            }
            if (mode == PreferredDiscoveryMode.WifiOnly)
                throw new TimeoutException("Timed out waiting for _vpnhotspot._tcp.local mDNS broadcast.");
        }

        if (mode is PreferredDiscoveryMode.Auto or PreferredDiscoveryMode.AdbOnly)
        {
            var adb = await TrySetupAdbFallbackAsync(timeoutCts.Token).ConfigureAwait(false);
            if (adb != null)
            {
                OnLog?.Invoke("ADB USB fallback active (TCP :1080 + UDP-Mux :1081 forwarded to 127.0.0.1).");
                return adb;
            }
        }

        throw new InvalidOperationException("Could not discover Zero-Config Android Hub via USB Tethering, Wi-Fi mDNS, or ADB.");
    }

    private async Task<DiscoveredPhoneEndpoint?> TryDiscoverUsbTetheringAsync(CancellationToken ct)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            string desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            if (!desc.Contains("rndis") && !desc.Contains("usb") && !desc.Contains("android") && !desc.Contains("ncm"))
                continue;

            var gateway = nic.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

            if (gateway == null) continue;

            if (await ProbeTcpAsync(gateway, DefaultSocksPort, TimeSpan.FromMilliseconds(900), ct).ConfigureAwait(false))
            {
                return new DiscoveredPhoneEndpoint(
                    $"USB-{nic.Name}", gateway, DefaultSocksPort, DefaultUdpMuxPort,
                    DiscoveryTransport.UsbTethering, nic.Name, gateway);
            }
        }
        return null;
    }

    private async Task<DiscoveredPhoneEndpoint?> TryDiscoverMdnsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<DiscoveredPhoneEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var mdns = new MulticastService();
        using var sd = new ServiceDiscovery(mdns);

        sd.ServiceInstanceDiscovered += (_, args) =>
        {
            try
            {
                string fullName = args.ServiceInstanceName.ToString();
                if (!fullName.Contains(ServiceType, StringComparison.OrdinalIgnoreCase)) return;

                var allRecords = args.Message.Answers.Concat(args.Message.AdditionalRecords).ToList();
                int port = allRecords.OfType<SRVRecord>().FirstOrDefault()?.Port ?? DefaultSocksPort;
                var ipv4 = allRecords.OfType<ARecord>()
                    .Select(a => a.Address)
                    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    ?? (args.RemoteEndPoint?.AddressFamily == AddressFamily.InterNetwork ? args.RemoteEndPoint.Address : null);

                if (ipv4 == null) return;

                int udpMux = DefaultUdpMuxPort;
                foreach (var txt in allRecords.OfType<TXTRecord>())
                {
                    foreach (var entry in txt.Strings)
                    {
                        if (entry.StartsWith("udpmux=", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(entry.AsSpan(7), out int parsedMux))
                            udpMux = parsedMux;
                    }
                }

                var (uplinkName, uplinkGw) = FindUplinkForTarget(ipv4);
                tcs.TrySetResult(new DiscoveredPhoneEndpoint(
                    fullName, ipv4, port, udpMux, DiscoveryTransport.WifiMdns, uplinkName, uplinkGw));
            }
            catch { }
        };

        mdns.NetworkInterfaceDiscovered += (_, _) => { try { sd.QueryServiceInstances(ServiceType); } catch { } };
        mdns.Start();
        sd.QueryServiceInstances(ServiceType);

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delayCts.CancelAfter(timeout);
        using var reg = delayCts.Token.Register(() => tcs.TrySetResult(null!));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<DiscoveredPhoneEndpoint?> TrySetupAdbFallbackAsync(CancellationToken ct)
    {
        try
        {
            var devicesOut = await RunProcessCaptureAsync("adb", "devices", ct).ConfigureAwait(false);
            if (devicesOut == null) return null;

            bool hasDevice = devicesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1).Any(line => line.Trim().EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase));
            if (!hasDevice) return null;

            await RunProcessCaptureAsync("adb", $"forward tcp:{DefaultSocksPort} tcp:{DefaultSocksPort}", ct).ConfigureAwait(false);
            await RunProcessCaptureAsync("adb", $"forward tcp:{DefaultUdpMuxPort} tcp:{DefaultUdpMuxPort}", ct).ConfigureAwait(false);

            if (await ProbeTcpAsync(IPAddress.Loopback, DefaultSocksPort, TimeSpan.FromMilliseconds(1200), ct).ConfigureAwait(false))
            {
                return new DiscoveredPhoneEndpoint(
                    "ADB-USB-Bridge", IPAddress.Loopback, DefaultSocksPort, DefaultUdpMuxPort,
                    DiscoveryTransport.AdbFallback, null, null);
            }
        }
        catch { }
        return null;
    }

    private static (string? InterfaceName, IPAddress? Gateway) FindUplinkForTarget(IPAddress targetIp)
    {
        byte[] targetBytes = targetIp.GetAddressBytes();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (nic.Name.Equals(Tun2SocksManager.AdapterName, StringComparison.OrdinalIgnoreCase)) continue;

            var ipProps = nic.GetIPProperties();
            foreach (var uni in ipProps.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] ipBytes = uni.Address.GetAddressBytes();
                byte[] maskBytes = uni.IPv4Mask.GetAddressBytes();
                bool sameSubnet = true;
                for (int i = 0; i < 4; i++)
                {
                    if ((ipBytes[i] & maskBytes[i]) != (targetBytes[i] & maskBytes[i])) { sameSubnet = false; break; }
                }
                if (sameSubnet)
                {
                    var gw = ipProps.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    return (nic.Name, gw);
                }
            }
        }
        return (null, null);
    }

    private static async Task<bool> ProbeTcpAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await tcp.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return tcp.Connected;
        }
        catch { return false; }
    }

    private static async Task<string?> RunProcessCaptureAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = file, Arguments = args, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            });
            if (proc == null) return null;
            string output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}