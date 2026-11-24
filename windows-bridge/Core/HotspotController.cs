using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace ZeroConfigBridge.Core;

public sealed record HotspotStatus(bool IsActive, string Ssid, uint ClientCount, string? VirtualAdapterName);

public sealed class HotspotController
{
    public event Action<string>? OnLog;

    private NetworkOperatorTetheringManager? GetTetheringManager()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile != null) return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);

        foreach (var candidate in NetworkInformation.GetConnectionProfiles())
        {
            var level = candidate.GetNetworkConnectivityLevel();
            if (level is NetworkConnectivityLevel.InternetAccess or NetworkConnectivityLevel.ConstrainedInternetAccess or NetworkConnectivityLevel.LocalAccess)
            {
                try { return NetworkOperatorTetheringManager.CreateFromConnectionProfile(candidate); } catch { }
            }
        }
        return null;
    }

    public async Task<HotspotStatus> StartHotspotAsync(string? ssid, string? passphrase, CancellationToken ct)
    {
        var manager = GetTetheringManager()
            ?? throw new InvalidOperationException("No suitable Windows network profile found to initialize Mobile Hotspot. Ensure Wi-Fi is enabled.");

        if (!string.IsNullOrWhiteSpace(ssid) || !string.IsNullOrWhiteSpace(passphrase))
        {
            var cfg = manager.GetCurrentAccessPointConfiguration();
            if (!string.IsNullOrWhiteSpace(ssid)) cfg.Ssid = ssid.Trim();
            if (!string.IsNullOrWhiteSpace(passphrase))
            {
                if (passphrase.Length < 8) throw new ArgumentException("Hotspot passphrase must be at least 8 characters.");
                cfg.Passphrase = passphrase;
            }
            await manager.ConfigureAccessPointAsync(cfg).AsTask(ct).ConfigureAwait(false);
        }

        if (manager.TetheringOperationalState != TetheringOperationalState.On)
        {
            var result = await manager.StartTetheringAsync().AsTask(ct).ConfigureAwait(false);
            if (result.Status != TetheringOperationStatus.Success)
                throw new InvalidOperationException($"Failed to start Windows Mobile Hotspot: {result.Status} ({result.AdditionalErrorMessage})");
            OnLog?.Invoke("Windows Mobile Hotspot started.");
        }
        else OnLog?.Invoke("Windows Mobile Hotspot is already active.");

        string? adapterName = await WaitForHotspotVirtualAdapterAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var activeCfg = manager.GetCurrentAccessPointConfiguration();
        return new HotspotStatus(true, activeCfg.Ssid, manager.ClientCount, adapterName);
    }

    public async Task StopHotspotAsync()
    {
        try
        {
            var manager = GetTetheringManager();
            if (manager != null && manager.TetheringOperationalState == TetheringOperationalState.On)
            {
                await manager.StopTetheringAsync().AsTask().ConfigureAwait(false);
                OnLog?.Invoke("Windows Mobile Hotspot stopped.");
            }
        }
        catch (Exception ex) { OnLog?.Invoke($"Warning stopping hotspot: {ex.Message}"); }
    }

    public HotspotStatus GetCurrentStatus()
    {
        try
        {
            var manager = GetTetheringManager();
            if (manager == null) return new HotspotStatus(false, "", 0, null);
            var cfg = manager.GetCurrentAccessPointConfiguration();
            return new HotspotStatus(manager.TetheringOperationalState == TetheringOperationalState.On, cfg.Ssid, manager.ClientCount, FindHotspotVirtualAdapterName());
        }
        catch { return new HotspotStatus(false, "", 0, null); }
    }

    public static string? FindHotspotVirtualAdapterName()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var match = nics.FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up &&
            n.Description.Contains("Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase));
        match ??= nics.FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up &&
            n.Name.StartsWith("Local Area Connection*", StringComparison.OrdinalIgnoreCase));
        return match?.Name;
    }

    private async Task<string> WaitForHotspotVirtualAdapterAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            string? name = FindHotspotVirtualAdapterName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                OnLog?.Invoke($"Detected Hotspot Virtual Adapter: '{name}'.");
                return name;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for Microsoft Wi-Fi Direct Virtual Adapter.");
    }
}