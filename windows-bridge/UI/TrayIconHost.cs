using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ZeroConfigBridge.UI;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _connectItem;
    private readonly ToolStripMenuItem _disconnectItem;
    private readonly ToolStripMenuItem _resetIcsItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly MainWindow _window;
    private readonly Action _onExitRequested;

    public TrayIconHost(MainWindow window, Action onExitRequested)
    {
        _window = window;
        _onExitRequested = onExitRequested;

        _statusItem = new ToolStripMenuItem(AppStrings.Get("StatusDisconnected")) { Enabled = false };
        _openItem = new ToolStripMenuItem(AppStrings.Get("TrayOpen"), null, (_, _) => _window.ShowAndActivate());
        _connectItem = new ToolStripMenuItem(AppStrings.Get("TrayConnect"), null, async (_, _) => await _window.ConnectFromTrayAsync());
        _disconnectItem = new ToolStripMenuItem(AppStrings.Get("TrayDisconnect"), null, async (_, _) => await _window.DisconnectFromTrayAsync()) { Enabled = false };
        _resetIcsItem = new ToolStripMenuItem(AppStrings.Get("TrayResetIcs"), null, (_, _) => _window.ForceResetIcs());
        _exitItem = new ToolStripMenuItem(AppStrings.Get("TrayExit"), null, (_, _) => _onExitRequested());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openItem);
        menu.Items.Add(_connectItem);
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(_resetIcsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Zero-Config VPN Bridge",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _window.ShowAndActivate();
        _window.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(string status, bool busy, bool connected)
    {
        _statusItem.Text = status;
        _openItem.Text = AppStrings.Get("TrayOpen");
        _connectItem.Text = AppStrings.Get("TrayConnect");
        _disconnectItem.Text = AppStrings.Get("TrayDisconnect");
        _resetIcsItem.Text = AppStrings.Get("TrayResetIcs");
        _exitItem.Text = AppStrings.Get("TrayExit");

        _connectItem.Enabled = !busy && !connected;
        _disconnectItem.Enabled = !busy && connected;
        _notifyIcon.Text = status.Length > 63 ? status[..63] : status;
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ZeroConfig.ico");
            if (File.Exists(icoPath)) return new Icon(icoPath);
        }
        catch { }
        return SystemIcons.Shield;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}