using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroConfigBridge.Core;

public sealed class Tun2SocksManager : IDisposable
{
    public const string AdapterName = "VPNBridge";
    private const string AdapterGuid = "{B7E8A2D4-6C11-4F3A-9E02-112233445566}";
    private const string TunIp = "10.250.0.2";
    private const string TunMask = "255.255.255.0";
    private const string TunGateway = "10.250.0.1";

    private Process? _process;
    private string? _addedHostRouteIp;

    public event Action<string>? OnLog;
    public event Action<int>? OnExitedUnexpectedly;

    public async Task StartAsync(
        string socksHost,
        int socksPort,
        string? uplinkInterfaceName,
        IPAddress? phoneIpForHostRoute,
        IPAddress? originalGateway,
        CancellationToken ct)
    {
        Stop();

        string baseDir = AppContext.BaseDirectory;
        string exePath = Path.Combine(baseDir, "tun2socks.exe");
        string dllPath = Path.Combine(baseDir, "wintun.dll");

        if (!File.Exists(exePath))
        {
            exePath = Path.Combine(baseDir, "Dependencies", "tun2socks.exe");
            dllPath = Path.Combine(baseDir, "Dependencies", "wintun.dll");
        }
        if (!File.Exists(exePath) || !File.Exists(dllPath))
            throw new FileNotFoundException("Missing tun2socks.exe or wintun.dll. Place x64 binaries in Dependencies\\ or alongside ZeroConfigBridge.exe.");

        if (phoneIpForHostRoute != null && !IPAddress.IsLoopback(phoneIpForHostRoute) && originalGateway != null)
        {
            await RunCommandAsync("route", $"add {phoneIpForHostRoute} mask 255.255.255.255 {originalGateway} metric 1", ct).ConfigureAwait(false);
            _addedHostRouteIp = phoneIpForHostRoute.ToString();
            OnLog?.Invoke($"Added bypass host route for {phoneIpForHostRoute}/32 via {originalGateway}.");
        }

        string ifaceFlag = string.IsNullOrWhiteSpace(uplinkInterfaceName) ? string.Empty : $" -interface \"{uplinkInterfaceName}\"";
        string args = $"-device \"tun://{AdapterName}?guid={AdapterGuid}\" -proxy \"socks5://{socksHost}:{socksPort}\" -mtu 1420 -udp-timeout 120s -loglevel warn{ifaceFlag}";
        OnLog?.Invoke($"Launching tun2socks: {args}");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath, Arguments = args, WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) OnLog?.Invoke($"[tun2socks] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) OnLog?.Invoke($"[tun2socks] {e.Data}"); };
        _process.Exited += (_, _) => OnExitedUnexpectedly?.Invoke(_process?.ExitCode ?? -1);

        if (!_process.Start()) throw new InvalidOperationException("Failed to start tun2socks.exe.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForAdapterAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        await ConfigureAdapterNetworkingAsync(ct).ConfigureAwait(false);
    }

    public void Stop()
    {
        var proc = _process;
        _process = null;
        if (proc != null)
        {
            try { if (!proc.HasExited) { proc.Kill(true); proc.WaitForExit(3000); } } catch { }
            finally { proc.Dispose(); }
        }
        if (_addedHostRouteIp != null)
        {
            string ip = _addedHostRouteIp;
            _addedHostRouteIp = null;
            try
            {
                using var p = Process.Start(new ProcessStartInfo { FileName = "route", Arguments = $"delete {ip}", UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(2000);
            }
            catch { }
        }
    }

    private async Task WaitForAdapterAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_process == null || _process.HasExited)
                throw new InvalidOperationException("tun2socks exited prematurely while creating Wintun adapter.");
            if (NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name.Equals(AdapterName, StringComparison.OrdinalIgnoreCase)))
            {
                OnLog?.Invoke($"Wintun adapter '{AdapterName}' detected.");
                return;
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"Timed out waiting for Wintun adapter '{AdapterName}'.");
    }

    private async Task ConfigureAdapterNetworkingAsync(CancellationToken ct)
    {
        await RunCommandAsync("netsh", $"interface ipv4 set address name=\"{AdapterName}\" static {TunIp} {TunMask} {TunGateway}", ct).ConfigureAwait(false);
        await RunCommandAsync("netsh", $"interface ipv4 set interface \"{AdapterName}\" metric=5", ct).ConfigureAwait(false);
        await RunCommandAsync("netsh", $"interface ipv4 set dnsservers name=\"{AdapterName}\" static 1.1.1.1 validate=no", ct).ConfigureAwait(false);
        await RunCommandAsync("netsh", $"interface ipv4 add dnsservers name=\"{AdapterName}\" 8.8.8.8 index=2 validate=no", ct).ConfigureAwait(false);
        OnLog?.Invoke($"Configured {AdapterName} ({TunIp}/24, GW {TunGateway}, Metric 5, DNS 1.1.1.1/8.8.8.8).");
    }

    private static async Task RunCommandAsync(string file, string args, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = file, Arguments = args, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Failed to run {file}");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            string err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            string std = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"{file} {args} failed (exit {proc.ExitCode}): {err} {std}");
        }
    }

    public void Dispose() => Stop();
}