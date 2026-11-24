using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace ZeroConfigBridge.Core;

public sealed class IcsSharingManager : IDisposable
{
    private const int IcsSharingTypePublic = 0;
    private const int IcsSharingTypePrivate = 1;

    private string? _activePublicAdapter;
    private string? _activePrivateAdapter;

    public event Action<string>? OnLog;

    public void EnableBridgeSharing(string publicAdapterName, string privateAdapterName)
    {
        EnsureSharedAccessServiceRunning();
        var shareType = Type.GetTypeFromProgID("HNetCfg.HNetShare")
            ?? throw new COMException("COM ProgID 'HNetCfg.HNetShare' is unavailable on this Windows installation.");
        object netSharingManager = Activator.CreateInstance(shareType)
            ?? throw new COMException("Failed to instantiate HNetCfg.HNetShare.");

        var comObjects = new List<object>();
        try
        {
            var connectionsEnum = GetProperty(netSharingManager, "EnumEveryConnection") as IEnumerable
                ?? throw new InvalidOperationException("Failed to enumerate network connections from HNetShare.");

            object? publicConn = null, privateConn = null;
            var allConfigs = new List<(string Name, object Config)>();

            foreach (object conn in connectionsEnum)
            {
                comObjects.Add(conn);
                object? props = GetIndexedProperty(netSharingManager, "NetConnectionProps", conn);
                if (props == null) continue;
                comObjects.Add(props);

                string name = GetProperty(props, "Name")?.ToString() ?? string.Empty;
                object? cfg = GetIndexedProperty(netSharingManager, "INetSharingConfigurationForINetConnection", conn);
                if (cfg == null) continue;
                comObjects.Add(cfg);

                allConfigs.Add((name, cfg));
                if (name.Equals(publicAdapterName, StringComparison.OrdinalIgnoreCase)) publicConn = cfg;
                else if (name.Equals(privateAdapterName, StringComparison.OrdinalIgnoreCase)) privateConn = cfg;
            }

            if (publicConn == null) throw new InvalidOperationException($"ICS could not find public adapter '{publicAdapterName}'.");
            if (privateConn == null) throw new InvalidOperationException($"ICS could not find private hotspot adapter '{privateAdapterName}'.");

            foreach (var (name, cfg) in allConfigs)
            {
                if (Convert.ToBoolean(GetProperty(cfg, "SharingEnabled")))
                {
                    InvokeMethod(cfg, "DisableSharing");
                    OnLog?.Invoke($"Cleared prior ICS state on '{name}'.");
                }
            }

            InvokeMethod(publicConn, "EnableSharing", IcsSharingTypePublic);
            InvokeMethod(privateConn, "EnableSharing", IcsSharingTypePrivate);
            _activePublicAdapter = publicAdapterName;
            _activePrivateAdapter = privateAdapterName;
            OnLog?.Invoke($"ICS Bridge enabled: [{publicAdapterName}] (Public) -> [{privateAdapterName}] (Private).");
        }
        finally
        {
            foreach (var obj in comObjects) if (Marshal.IsComObject(obj)) try { Marshal.ReleaseComObject(obj); } catch { }
            if (Marshal.IsComObject(netSharingManager)) try { Marshal.ReleaseComObject(netSharingManager); } catch { }
        }
    }

    public void DisableAllSharing()
    {
        try
        {
            var shareType = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (shareType == null) return;
            object? netSharingManager = Activator.CreateInstance(shareType);
            if (netSharingManager == null) return;

            var toRelease = new List<object>();
            try
            {
                if (GetProperty(netSharingManager, "EnumEveryConnection") is not IEnumerable connectionsEnum) return;
                foreach (object conn in connectionsEnum)
                {
                    toRelease.Add(conn);
                    object? props = GetIndexedProperty(netSharingManager, "NetConnectionProps", conn);
                    if (props != null) toRelease.Add(props);
                    string name = props != null ? (GetProperty(props, "Name")?.ToString() ?? "") : "";
                    object? cfg = GetIndexedProperty(netSharingManager, "INetSharingConfigurationForINetConnection", conn);
                    if (cfg == null) continue;
                    toRelease.Add(cfg);

                    if (Convert.ToBoolean(GetProperty(cfg, "SharingEnabled")))
                    {
                        InvokeMethod(cfg, "DisableSharing");
                        OnLog?.Invoke($"Disabled ICS on '{name}'.");
                    }
                }
                _activePublicAdapter = null;
                _activePrivateAdapter = null;
            }
            finally
            {
                foreach (var obj in toRelease) if (Marshal.IsComObject(obj)) try { Marshal.ReleaseComObject(obj); } catch { }
                if (Marshal.IsComObject(netSharingManager)) try { Marshal.ReleaseComObject(netSharingManager); } catch { }
            }
        }
        catch (Exception ex) { OnLog?.Invoke($"Warning disabling ICS: {ex.Message}"); }
    }

    private void EnsureSharedAccessServiceRunning()
    {
        try
        {
            using var sc = new ServiceController("SharedAccess");
            if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                OnLog?.Invoke("Started Windows ICS (SharedAccess) service.");
            }
        }
        catch (Exception ex) { OnLog?.Invoke($"Note checking SharedAccess service: {ex.Message}"); }
    }

    private static object? GetProperty(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
    private static object? GetIndexedProperty(object target, string name, object arg) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, new[] { arg });
    private static void InvokeMethod(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args.Length == 0 ? null : args);

    public void Dispose()
    {
        if (_activePublicAdapter != null || _activePrivateAdapter != null) DisableAllSharing();
    }
}