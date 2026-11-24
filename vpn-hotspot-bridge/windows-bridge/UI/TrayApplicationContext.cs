using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZeroConfigBridge.Core;

namespace ZeroConfigBridge.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _connectMenuItem;
    private readonly ToolStripMenuItem _disconnectMenuItem;

    private readonly MdnsPhoneDiscoverer _discoverer = new();
    private readonly Tun2SocksManager _tun2Socks = new();
    private readonly HotspotController _hotspot = new();
    private readonly IcsSharingManager _ics = new();
    private readonly AdbUdpSocksBridge _adbUdpBridge = new();

    private readonly BridgeDashboardForm _dashboard;
    private readonly System.Windows.Forms.Timer _telemetryTimer;

    private CancellationTokenSource? _bridgeCts;
    private bool _isConnected;
    private bool _isBusy;
    private DiscoveredPhoneEndpoint? _activeEndpoint;

    public TrayApplicationContext()
    {
        _discoverer.OnLog += AppendLog;
        _tun2Socks.OnLog += AppendLog;
        _hotspot.OnLog += AppendLog;
        _ics.OnLog += AppendLog;
        _tun2Socks.OnExitedUnexpectedly += code =>
        {
            if (_isConnected && !_isBusy)
            {
                AppendLog($"ALERT: tun2socks exited unexpectedly (code {code}). Cleaning up bridge...");
                _dashboard.BeginInvoke(async () => await DisconnectAsync());
            }
        };

        _statusMenuItem = new ToolStripMenuItem("Status: Disconnected") { Enabled = false };
        _connectMenuItem = new ToolStripMenuItem("Connect & Share VPN", null, async (_, _) => await ConnectFromUiAsync());
        _disconnectMenuItem = new ToolStripMenuItem("Disconnect", null, async (_, _) => await DisconnectAsync()) { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_connectMenuItem);
        menu.Items.Add(_disconnectMenuItem);
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Force Reset Windows ICS", null, (_, _) => ForceResetIcs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Zero-Config", null, (_, _) => ShutdownAndExit());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Zero-Config VPN Bridge (Disconnected)",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowDashboard();

        _dashboard = new BridgeDashboardForm(ConnectAsync, DisconnectAsync, ForceResetIcs);
        _telemetryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _telemetryTimer.Tick += (_, _) => UpdateHotspotTelemetry();
        _telemetryTimer.Start();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupSync();
        ShowDashboard();
    }

    private void ShowDashboard()
    {
        if (_dashboard.IsDisposed) return;
        if (!_dashboard.Visible) _dashboard.Show();
        if (_dashboard.WindowState == FormWindowState.Minimized) _dashboard.WindowState = FormWindowState.Normal;
        _dashboard.Activate();
    }

    private async Task ConnectFromUiAsync()
    {
        ShowDashboard();
        await ConnectAsync(_dashboard.SelectedMode, _dashboard.HotspotSsid, _dashboard.HotspotPassword);
    }

    private async Task ConnectAsync(PreferredDiscoveryMode mode, string ssid, string password)
    {
        if (_isBusy || _isConnected) return;
        _isBusy = true;
        _bridgeCts = new CancellationTokenSource();
        var ct = _bridgeCts.Token;

        SetUiState("Connecting...", busy: true, connected: false);
        try
        {
            AppendLog($"Starting Zero-Config Bridge (Discovery mode: {mode})...");
            var endpoint = await _discoverer.DiscoverAsync(mode, TimeSpan.FromSeconds(18), ct);
            _activeEndpoint = endpoint;

            string socksHost = endpoint.Address.ToString();
            int socksPort = endpoint.Port;
            var hostRouteIp = endpoint.Address;
            var originalGw = endpoint.UplinkGateway;

            if (endpoint.Transport == DiscoveryTransport.AdbFallback)
            {
                int localBridgePort = _adbUdpBridge.Start(1082);
                socksHost = "127.0.0.1";
                socksPort = localBridgePort;
                hostRouteIp = null;
                originalGw = null;
                AppendLog($"Started local ADB TCP+UDP SOCKS5 adapter on 127.0.0.1:{localBridgePort}.");
            }

            await _tun2Socks.StartAsync(socksHost, socksPort, endpoint.UplinkInterfaceName, hostRouteIp, originalGw, ct);
            var hsStatus = await _hotspot.StartHotspotAsync(ssid, password, ct);
            if (string.IsNullOrWhiteSpace(hsStatus.VirtualAdapterName))
                throw new InvalidOperationException("Hotspot started, but its virtual network adapter was not found.");

            _ics.EnableBridgeSharing(Tun2SocksManager.AdapterName, hsStatus.VirtualAdapterName);
            _isConnected = true;
            SetUiState($"Connected ({endpoint.Transport}) | Hotspot: {hsStatus.Ssid}", busy: false, connected: true);
            _trayIcon.ShowBalloonTip(3000, "Zero-Config Active", $"Sharing Android VPN on Wi-Fi '{hsStatus.Ssid}'.", ToolTipIcon.Info);
            AppendLog("READY: Connect Smart TVs, phones, or laptops to the Windows Mobile Hotspot with zero proxy settings.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            await CleanupAsync(stopHotspot: false);
            SetUiState($"Failed: {ex.Message}", busy: false, connected: false);
            MessageBox.Show(ex.Message, "Zero-Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _isBusy = false; }
    }

    private async Task DisconnectAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        SetUiState("Disconnecting...", busy: true, connected: _isConnected);
        await CleanupAsync(stopHotspot: true);
        _isConnected = false;
        _activeEndpoint = null;
        SetUiState("Disconnected", busy: false, connected: false);
        AppendLog("Bridge cleanly disconnected and Windows ICS restored.");
        _isBusy = false;
    }

    private async Task CleanupAsync(bool stopHotspot)
    {
        try { _bridgeCts?.Cancel(); } catch { }
        _ics.DisableAllSharing();
        if (stopHotspot) await _hotspot.StopHotspotAsync();
        _tun2Socks.Stop();
        _adbUdpBridge.Stop();
        _bridgeCts?.Dispose();
        _bridgeCts = null;
    }

    private void CleanupSync()
    {
        _ics.DisableAllSharing();
        _tun2Socks.Stop();
        _adbUdpBridge.Stop();
    }

    private void ForceResetIcs()
    {
        _ics.DisableAllSharing();
        _tun2Socks.Stop();
        _adbUdpBridge.Stop();
        AppendLog("Emergency ICS Reset completed: All ICS sharing bindings cleared.");
        MessageBox.Show("All Windows ICS sharing bindings have been reset.", "Zero-Config", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateHotspotTelemetry()
    {
        if (!_isConnected || _dashboard.IsDisposed) return;
        var st = _hotspot.GetCurrentStatus();
        _dashboard.UpdateTelemetry(_activeEndpoint?.Transport.ToString() ?? "--",
            _activeEndpoint != null ? $"{_activeEndpoint.Address}:{_activeEndpoint.Port}" : "--",
            st.Ssid, st.ClientCount);
    }

    private void SetUiState(string status, bool busy, bool connected)
    {
        if (_dashboard.InvokeRequired) { _dashboard.BeginInvoke(() => SetUiState(status, busy, connected)); return; }
        _statusMenuItem.Text = $"Status: {status}";
        _connectMenuItem.Enabled = !busy && !connected;
        _disconnectMenuItem.Enabled = !busy && connected;
        _trayIcon.Text = status.Length > 63 ? status[..63] : status;
        _dashboard.SetConnectionState(status, busy, connected);
    }

    private void AppendLog(string message)
    {
        if (!_dashboard.IsDisposed) _dashboard.AppendLog($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void ShutdownAndExit()
    {
        _telemetryTimer.Stop();
        CleanupSync();
        _hotspot.StopHotspotAsync().GetAwaiter().GetResult();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _dashboard.AllowClose = true;
        _dashboard.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _telemetryTimer.Dispose();
            _trayIcon.Dispose();
            _tun2Socks.Dispose();
            _ics.Dispose();
            _adbUdpBridge.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class BridgeDashboardForm : Form
    {
        private readonly ComboBox _cmbMode;
        private readonly TextBox _txtSsid;
        private readonly TextBox _txtPass;
        private readonly Button _btnAction;
        private readonly Button _btnResetIcs;
        private readonly Label _lblStatus;
        private readonly Label _lblTelemetry;
        private readonly TextBox _txtLog;

        private readonly Func<PreferredDiscoveryMode, string, string, Task> _onConnect;
        private readonly Func<Task> _onDisconnect;
        private bool _connected;

        public bool AllowClose { get; set; }
        public PreferredDiscoveryMode SelectedMode => (PreferredDiscoveryMode)_cmbMode.SelectedIndex;
        public string HotspotSsid => _txtSsid.Text;
        public string HotspotPassword => _txtPass.Text;

        public BridgeDashboardForm(
            Func<PreferredDiscoveryMode, string, string, Task> onConnect,
            Func<Task> onDisconnect,
            Action onForceResetIcs)
        {
            _onConnect = onConnect;
            _onDisconnect = onDisconnect;

            Text = "Zero-Config — Android-to-PC VPN Hotspot Bridge";
            Size = new Size(640, 520);
            MinimumSize = new Size(560, 440);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 9.5f);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 5, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _lblStatus = new Label { Text = "Status: Disconnected", AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Color.FromArgb(56, 189, 248) };
            _lblTelemetry = new Label { Text = "Uplink: -- | Phone: -- | Hotspot SSID: -- | Connected Clients: 0", AutoSize = true, ForeColor = Color.FromArgb(148, 163, 184), Margin = new Padding(0, 4, 0, 10) };

            var settingsGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, 10) };
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            _cmbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _cmbMode.Items.AddRange(new object[] {
                "Auto (USB Tethering -> Wi-Fi mDNS -> ADB)",
                "USB Tethering Only (Zero Wi-Fi Contention)",
                "Wi-Fi / LAN mDNS Only",
                "ADB USB Cable Fallback Only"
            });
            _cmbMode.SelectedIndex = 0;

            _txtSsid = new TextBox { Text = "ZeroConfig-VPN", Dock = DockStyle.Fill };
            _txtPass = new TextBox { Text = "zeroconfig123", Dock = DockStyle.Fill, UseSystemPasswordChar = true };

            settingsGrid.Controls.Add(new Label { Text = "Discovery:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            settingsGrid.Controls.Add(_cmbMode, 1, 0);
            settingsGrid.SetColumnSpan(_cmbMode, 3);
            settingsGrid.Controls.Add(new Label { Text = "Hotspot SSID:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 6, 0) }, 0, 1);
            settingsGrid.Controls.Add(_txtSsid, 1, 1);
            settingsGrid.Controls.Add(new Label { Text = "Password:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(10, 8, 6, 0) }, 2, 1);
            settingsGrid.Controls.Add(_txtPass, 3, 1);

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            _btnAction = new Button { Text = "Connect & Share VPN", Width = 190, Height = 38, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            _btnAction.Click += async (_, _) => { if (_connected) await _onDisconnect(); else await _onConnect(SelectedMode, HotspotSsid, HotspotPassword); };

            _btnResetIcs = new Button { Text = "Force Reset ICS", Width = 140, Height = 38, BackColor = Color.FromArgb(51, 65, 85), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(10, 0, 0, 0) };
            _btnResetIcs.Click += (_, _) => onForceResetIcs();

            btnRow.Controls.Add(_btnAction);
            btnRow.Controls.Add(_btnResetIcs);

            _txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(2, 6, 23), ForeColor = Color.FromArgb(203, 213, 225), Font = new Font("Consolas", 9f) };

            root.Controls.Add(_lblStatus, 0, 0);
            root.Controls.Add(_lblTelemetry, 0, 1);
            root.Controls.Add(settingsGrid, 0, 2);
            root.Controls.Add(btnRow, 0, 3);
            root.Controls.Add(_txtLog, 0, 4);
            Controls.Add(root);

            FormClosing += (_, e) => { if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }

        public void SetConnectionState(string status, bool busy, bool connected)
        {
            _connected = connected;
            _lblStatus.Text = $"Status: {status}";
            _lblStatus.ForeColor = connected ? Color.FromArgb(52, 211, 153) : Color.FromArgb(56, 189, 248);
            _btnAction.Enabled = !busy;
            _btnAction.Text = connected ? "Disconnect Bridge" : "Connect & Share VPN";
            _btnAction.BackColor = connected ? Color.FromArgb(220, 38, 38) : Color.FromArgb(37, 99, 235);
            _cmbMode.Enabled = !busy && !connected;
            _txtSsid.Enabled = !busy && !connected;
            _txtPass.Enabled = !busy && !connected;
            if (!connected) _lblTelemetry.Text = "Uplink: -- | Phone: -- | Hotspot SSID: -- | Connected Clients: 0";
        }

        public void UpdateTelemetry(string transport, string endpoint, string ssid, uint clients)
        {
            if (InvokeRequired) { BeginInvoke(() => UpdateTelemetry(transport, endpoint, ssid, clients)); return; }
            _lblTelemetry.Text = $"Uplink: {transport} | Phone: {endpoint} | Hotspot SSID: {ssid} | Connected Clients: {clients}";
        }

        public void AppendLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
            _txtLog.AppendText(line + Environment.NewLine);
        }
    }
}