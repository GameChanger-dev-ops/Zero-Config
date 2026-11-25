using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
    private int _reconnectAttempts;
    private DiscoveredPhoneEndpoint? _activeEndpoint;

    public TrayApplicationContext()
    {
        var appIcon = LoadAppIcon();
        _discoverer.OnLog += AppendLog;
        _tun2Socks.OnLog += AppendLog;
        _hotspot.OnLog += AppendLog;
        _ics.OnLog += AppendLog;
        _tun2Socks.OnExitedUnexpectedly += code =>
        {
            if (_isConnected && !_isBusy)
            {
                AppendLog($"ALERT: tun2socks exited unexpectedly (code {code}).");
                _dashboard.BeginInvoke(async () => await HandleUnexpectedTunnelExitAsync());
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
            Icon = appIcon,
            Text = "Zero-Config VPN Bridge (Disconnected)",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowDashboard();

        _dashboard = new BridgeDashboardForm(appIcon, ConnectAsync, DisconnectAsync, ForceResetIcs);
        _telemetryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _telemetryTimer.Tick += (_, _) => UpdateHotspotTelemetry();
        _telemetryTimer.Start();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupSync();
        ShowDashboard();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ZeroConfig.ico");
            if (File.Exists(icoPath)) return new Icon(icoPath);
            if (!string.IsNullOrEmpty(Application.ExecutablePath))
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield;
        }
        catch { }
        return SystemIcons.Shield;
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

    private async Task HandleUnexpectedTunnelExitAsync()
    {
        if (!_dashboard.AutoReconnectEnabled || _reconnectAttempts >= 3)
        {
            await DisconnectAsync();
            return;
        }
        _reconnectAttempts++;
        AppendLog($"Auto-Reconnect attempt {_reconnectAttempts}/3 in 3 seconds...");
        await CleanupAsync(stopHotspot: false);
        _isConnected = false;
        await Task.Delay(3000);
        await ConnectAsync(_dashboard.SelectedMode, _dashboard.HotspotSsid, _dashboard.HotspotPassword);
    }

    private async Task ConnectAsync(PreferredDiscoveryMode mode, string ssid, string password)
    {
        if (_isBusy || _isConnected) return;
        _isBusy = true;
        _bridgeCts = new CancellationTokenSource();
        var ct = _bridgeCts.Token;

        SetUiState("Discovering Android Hub...", busy: true, connected: false, stage: 1);
        try
        {
            AppendLog($"Starting Zero-Config Bridge (Discovery mode: {mode})...");
            var endpoint = await _discoverer.DiscoverAsync(mode, TimeSpan.FromSeconds(18), ct);
            _activeEndpoint = endpoint;

            SetUiState("Starting Wintun VPNBridge...", busy: true, connected: false, stage: 2);
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

            SetUiState("Enabling Hotspot & ICS Bridge...", busy: true, connected: false, stage: 3);
            var hsStatus = await _hotspot.StartHotspotAsync(ssid, password, ct);
            if (string.IsNullOrWhiteSpace(hsStatus.VirtualAdapterName))
                throw new InvalidOperationException("Hotspot started, but its virtual network adapter was not found.");

            _ics.EnableBridgeSharing(Tun2SocksManager.AdapterName, hsStatus.VirtualAdapterName);
            _isConnected = true;
            _reconnectAttempts = 0;

            SetUiState($"Connected ({endpoint.Transport}) | Hotspot: {hsStatus.Ssid}", busy: false, connected: true, stage: 4);
            _trayIcon.ShowBalloonTip(3000, "Zero-Config Active", $"Sharing Android VPN on Wi-Fi '{hsStatus.Ssid}'.", ToolTipIcon.Info);
            AppendLog("READY: Connect Smart TVs, phones, or laptops to the Windows Mobile Hotspot with zero proxy settings.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            await CleanupAsync(stopHotspot: false);
            SetUiState("Disconnected (Error — check Logs)", busy: false, connected: false, stage: 0);
            MessageBox.Show(ex.Message, "Zero-Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _isBusy = false; }
    }

    private async Task DisconnectAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        _reconnectAttempts = 0;
        SetUiState("Disconnecting...", busy: true, connected: _isConnected, stage: 0);
        await CleanupAsync(stopHotspot: true);
        _isConnected = false;
        _activeEndpoint = null;
        SetUiState("Disconnected", busy: false, connected: false, stage: 0);
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
        _dashboard.UpdateTelemetry(
            _activeEndpoint?.Transport.ToString() ?? "--",
            _activeEndpoint != null ? $"{_activeEndpoint.Address}:{_activeEndpoint.Port}" : "--",
            st.Ssid,
            st.ClientCount);
    }

    private void SetUiState(string status, bool busy, bool connected, int stage)
    {
        if (_dashboard.InvokeRequired) { _dashboard.BeginInvoke(() => SetUiState(status, busy, connected, stage)); return; }
        _statusMenuItem.Text = $"Status: {status}";
        _connectMenuItem.Enabled = !busy && !connected;
        _disconnectMenuItem.Enabled = !busy && connected;
        _trayIcon.Text = status.Length > 63 ? status[..63] : status;
        _dashboard.SetConnectionState(status, busy, connected, stage);
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
        private static readonly Color BgWindow = Color.FromArgb(11, 17, 32);
        private static readonly Color BgSidebar = Color.FromArgb(15, 23, 42);
        private static readonly Color BgCard = Color.FromArgb(17, 28, 51);
        private static readonly Color BorderColor = Color.FromArgb(30, 41, 59);
        private static readonly Color AccentBlue = Color.FromArgb(37, 99, 235);
        private static readonly Color AccentCyan = Color.FromArgb(56, 189, 248);
        private static readonly Color AccentGreen = Color.FromArgb(52, 211, 153);
        private static readonly Color AccentAmber = Color.FromArgb(251, 191, 36);
        private static readonly Color TextMain = Color.FromArgb(248, 250, 252);
        private static readonly Color TextMuted = Color.FromArgb(148, 163, 184);

        private readonly Button _navBridge, _navLogs, _navGuide;
        private readonly Panel _pageBridge, _pageLogs, _pageGuide;

        private readonly Label _lblStatusPill;
        private readonly Label _lblStepPhone, _lblStepTun, _lblStepHotspot;
        private readonly ComboBox _cmbMode;
        private readonly TextBox _txtSsid, _txtPass;
        private readonly CheckBox _chkAutoReconnect;
        private readonly Label _lblTelemetry;
        private readonly Button _btnAction, _btnLeakTest;
        private readonly TextBox _txtLog;

        private readonly Func<PreferredDiscoveryMode, string, string, Task> _onConnect;
        private readonly Func<Task> _onDisconnect;
        private bool _connected;

        public bool AllowClose { get; set; }
        public PreferredDiscoveryMode SelectedMode => (PreferredDiscoveryMode)_cmbMode.SelectedIndex;
        public string HotspotSsid => _txtSsid.Text;
        public string HotspotPassword => _txtPass.Text;
        public bool AutoReconnectEnabled => _chkAutoReconnect.Checked;

        public BridgeDashboardForm(
            Icon appIcon,
            Func<PreferredDiscoveryMode, string, string, Task> onConnect,
            Func<Task> onDisconnect,
            Action onForceResetIcs)
        {
            _onConnect = onConnect;
            _onDisconnect = onDisconnect;

            Text = "Zero-Config — VPN Hotspot Bridge";
            Icon = appIcon;
            Size = new Size(780, 540);
            MinimumSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgWindow;
            ForeColor = TextMain;
            Font = new Font("Segoe UI", 9.5f);

            // Left Sidebar Navigation Rail
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 175, BackColor = BgSidebar, Padding = new Padding(12) };
            var lblBrand = new Label { Text = "Zero-Config", Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI", 13.5f, FontStyle.Bold), ForeColor = TextMain };
            var lblVer = new Label { Text = "PC Bridge Controller", Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 8.5f), ForeColor = TextMuted };

            _navBridge = CreateNavButton("Bridge", true);
            _navLogs = CreateNavButton("Activity Logs", false);
            _navGuide = CreateNavButton("Quick Guide", false);

            sidebar.Controls.Add(_navGuide);
            sidebar.Controls.Add(_navLogs);
            sidebar.Controls.Add(_navBridge);
            sidebar.Controls.Add(lblVer);
            sidebar.Controls.Add(lblBrand);

            // Top Status Bar
            var topBar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = BgSidebar, Padding = new Padding(18, 12, 18, 10) };
            _lblStatusPill = new Label
            {
                Text = "Status: Disconnected",
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = AccentCyan,
                Location = new Point(18, 15)
            };
            topBar.Controls.Add(_lblStatusPill);

            // Content Area
            var contentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };

            // --- PAGE 1: BRIDGE ---
            _pageBridge = new Panel { Dock = DockStyle.Fill };

            // Pipeline Card
            var pipelineCard = CreateCard(105);
            var lblPipeTitle = new Label { Text = "CONNECTION PIPELINE", Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = TextMuted, Location = new Point(16, 12), AutoSize = true };
            _lblStepPhone = CreatePipelineNode("1. Android Hub\nIdle", new Point(16, 38));
            var arrow1 = new Label { Text = "──►", ForeColor = TextMuted, Location = new Point(182, 48), AutoSize = true };
            _lblStepTun = CreatePipelineNode("2. VPNBridge TUN\nIdle", new Point(218, 38));
            var arrow2 = new Label { Text = "──►", ForeColor = TextMuted, Location = new Point(384, 48), AutoSize = true };
            _lblStepHotspot = CreatePipelineNode("3. Wi-Fi Hotspot\nIdle", new Point(420, 38));
            pipelineCard.Controls.AddRange(new Control[] { lblPipeTitle, _lblStepPhone, arrow1, _lblStepTun, arrow2, _lblStepHotspot });

            // Config Card
            var cfgCard = CreateCard(175);
            cfgCard.Location = new Point(0, 118);
            var lblCfgTitle = new Label { Text = "BRIDGE CONFIGURATION", Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = TextMuted, Location = new Point(16, 12), AutoSize = true };
            var lblMode = new Label { Text = "Discovery Uplink:", Location = new Point(16, 42), AutoSize = true, ForeColor = TextMuted };
            _cmbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(135, 38), Width = 415, BackColor = BgSidebar, ForeColor = TextMain };
            _cmbMode.Items.AddRange(new object[] {
                "Auto (USB Tethering -> Wi-Fi mDNS -> ADB Fallback)",
                "USB Tethering Only (Zero Wi-Fi Contention)",
                "Wi-Fi / LAN mDNS Only",
                "ADB USB Cable Fallback Only"
            });
            _cmbMode.SelectedIndex = 0;

            var lblSsid = new Label { Text = "Hotspot SSID:", Location = new Point(16, 82), AutoSize = true, ForeColor = TextMuted };
            _txtSsid = new TextBox { Text = "ZeroConfig-VPN", Location = new Point(135, 79), Width = 175, BackColor = BgSidebar, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
            var lblPass = new Label { Text = "Password:", Location = new Point(325, 82), AutoSize = true, ForeColor = TextMuted };
            _txtPass = new TextBox { Text = "zeroconfig123", Location = new Point(395, 79), Width = 120, UseSystemPasswordChar = true, BackColor = BgSidebar, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
            var btnReveal = new Button { Text = "👁", Location = new Point(518, 78), Size = new Size(32, 25), FlatStyle = FlatStyle.Flat, BackColor = BorderColor, ForeColor = TextMain };
            btnReveal.FlatAppearance.BorderSize = 0;
            btnReveal.Click += (_, _) => _txtPass.UseSystemPasswordChar = !_txtPass.UseSystemPasswordChar;

            _chkAutoReconnect = new CheckBox
            {
                Text = "Auto-reconnect tunnel automatically if phone VPN resets",
                Checked = true,
                AutoSize = true,
                Location = new Point(135, 122),
                ForeColor = TextMuted
            };
            cfgCard.Controls.AddRange(new Control[] { lblCfgTitle, lblMode, _cmbMode, lblSsid, _txtSsid, lblPass, _txtPass, btnReveal, _chkAutoReconnect });

            // Action & Session Card
            var actCard = CreateCard(125);
            actCard.Location = new Point(0, 306);
            _lblTelemetry = new Label
            {
                Text = "Uplink: --  |  Phone: --  |  Hotspot SSID: --  |  Clients: 0",
                Location = new Point(16, 18),
                AutoSize = true,
                ForeColor = TextMuted
            };
            _btnAction = new Button
            {
                Text = "Connect & Share VPN",
                Location = new Point(16, 54),
                Size = new Size(220, 44),
                BackColor = AccentBlue,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            _btnAction.FlatAppearance.BorderSize = 0;
            _btnAction.Click += async (_, _) => { if (_connected) await _onDisconnect(); else await _onConnect(SelectedMode, HotspotSsid, HotspotPassword); };

            _btnLeakTest = new Button
            {
                Text = "Verify IP / DNS Leak (ipleak.net)",
                Location = new Point(250, 54),
                Size = new Size(240, 44),
                BackColor = BorderColor,
                ForeColor = TextMain,
                FlatStyle = FlatStyle.Flat
            };
            _btnLeakTest.FlatAppearance.BorderSize = 0;
            _btnLeakTest.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("https://ipleak.net") { UseShellExecute = true }); } catch { }
            };
            actCard.Controls.AddRange(new Control[] { _lblTelemetry, _btnAction, _btnLeakTest });

            _pageBridge.Controls.AddRange(new Control[] { pipelineCard, cfgCard, actCard });

            // --- PAGE 2: ACTIVITY LOGS ---
            _pageLogs = new Panel { Dock = DockStyle.Fill, Visible = false };
            var logToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 0, 0, 8) };
            var btnCopyLog = CreateToolButton("Copy Logs");
            btnCopyLog.Click += (_, _) => { if (!string.IsNullOrEmpty(_txtLog.Text)) Clipboard.SetText(_txtLog.Text); };
            var btnClearLog = CreateToolButton("Clear");
            btnClearLog.Click += (_, _) => _txtLog.Clear();
            var btnResetIcs = CreateToolButton("Force Reset Windows ICS");
            btnResetIcs.BackColor = Color.FromArgb(127, 29, 29);
            btnResetIcs.Width = 185;
            btnResetIcs.Click += (_, _) => onForceResetIcs();
            logToolbar.Controls.AddRange(new Control[] { btnCopyLog, btnClearLog, btnResetIcs });

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(2, 6, 23),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _pageLogs.Controls.Add(_txtLog);
            _pageLogs.Controls.Add(logToolbar);

            // --- PAGE 3: GUIDE ---
            _pageGuide = new Panel { Dock = DockStyle.Fill, Visible = false };
            var guideBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                BackColor = BgCard,
                ForeColor = TextMain,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10f),
                Text =
                    "ZERO-CONFIG QUICK GUIDE\r\n\r\n" +
                    "1. Start your VPN app on Android, open Zero-Config, and tap 'Start Zero-Config Hub'.\r\n\r\n" +
                    "2. Connect Phone to PC (best order):\r\n" +
                    "   • USB Tethering (Recommended): Fastest speed, zero Wi-Fi interference.\r\n" +
                    "   • Wi-Fi LAN: Both devices on the same Wi-Fi (auto-discovered via mDNS).\r\n" +
                    "   • USB ADB Fallback: Works even if carrier blocks USB Tethering (requires adb.exe in PATH).\r\n\r\n" +
                    "3. Click 'Connect & Share VPN' on the Bridge tab.\r\n" +
                    "   Your Smart TV, console, or second phone simply joins the Windows hotspot with zero proxy settings.\r\n\r\n" +
                    "4. Troubleshooting:\r\n" +
                    "   • If Windows Firewall prompts for ZeroConfigBridge.exe or tun2socks.exe, allow Private & Public.\r\n" +
                    "   • If a previous crash left Windows internet sharing stuck, open 'Activity Logs' -> 'Force Reset Windows ICS'."
            };
            _pageGuide.Controls.Add(guideBox);

            contentHost.Controls.AddRange(new Control[] { _pageBridge, _pageLogs, _pageGuide });
            Controls.Add(contentHost);
            Controls.Add(topBar);
            Controls.Add(sidebar);

            _navBridge.Click += (_, _) => SwitchPage(0);
            _navLogs.Click += (_, _) => SwitchPage(1);
            _navGuide.Click += (_, _) => SwitchPage(2);

            FormClosing += (_, e) => { if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }

        private void SwitchPage(int index)
        {
            _pageBridge.Visible = index == 0;
            _pageLogs.Visible = index == 1;
            _pageGuide.Visible = index == 2;
            _navBridge.BackColor = index == 0 ? AccentBlue : Color.Transparent;
            _navLogs.BackColor = index == 1 ? AccentBlue : Color.Transparent;
            _navGuide.BackColor = index == 2 ? AccentBlue : Color.Transparent;
        }

        private static Panel CreateCard(int height) => new()
        {
            Size = new Size(566, height),
            BackColor = BgCard,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        private static Label CreatePipelineNode(string text, Point loc) => new()
        {
            Text = text,
            Location = loc,
            Size = new Size(160, 48),
            BackColor = BgSidebar,
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };

        private static Button CreateNavButton(string text, bool active)
        {
            var btn = new Button
            {
                Text = "  " + text,
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                BackColor = active ? AccentBlue : Color.Transparent,
                ForeColor = TextMain,
                Margin = new Padding(0, 4, 0, 4),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private static Button CreateToolButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                Width = 100,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = BorderColor,
                ForeColor = TextMain,
                Margin = new Padding(0, 0, 8, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        public void SetConnectionState(string status, bool busy, bool connected, int stage)
        {
            _connected = connected;
            _lblStatusPill.Text = $"Status: {status}";
            _lblStatusPill.ForeColor = connected ? AccentGreen : (busy ? AccentAmber : AccentCyan);

            _btnAction.Enabled = !busy;
            _btnAction.Text = connected ? "Disconnect Bridge" : "Connect & Share VPN";
            _btnAction.BackColor = connected ? Color.FromArgb(220, 38, 38) : AccentBlue;

            _cmbMode.Enabled = !busy && !connected;
            _txtSsid.Enabled = !busy && !connected;
            _txtPass.Enabled = !busy && !connected;
            _chkAutoReconnect.Enabled = !busy;

            UpdateNode(_lblStepPhone, "1. Android Hub", stage >= 2 || connected ? "Connected" : (stage == 1 ? "Scanning..." : "Idle"), stage >= 2 || connected, stage == 1);
            UpdateNode(_lblStepTun, "2. VPNBridge TUN", stage >= 3 || connected ? "Active" : (stage == 2 ? "Starting..." : "Idle"), stage >= 3 || connected, stage == 2);
            UpdateNode(_lblStepHotspot, "3. Wi-Fi Hotspot", connected ? "Sharing VPN" : (stage == 3 ? "Bridging..." : "Idle"), connected, stage == 3);

            if (!connected)
                _lblTelemetry.Text = "Uplink: --  |  Phone: --  |  Hotspot SSID: --  |  Clients: 0";
        }

        private static void UpdateNode(Label lbl, string title, string state, bool ok, bool working)
        {
            lbl.Text = $"{title}\n{state}";
            lbl.ForeColor = ok ? AccentGreen : (working ? AccentAmber : TextMuted);
        }

        public void UpdateTelemetry(string transport, string endpoint, string ssid, uint clients)
        {
            if (InvokeRequired) { BeginInvoke(() => UpdateTelemetry(transport, endpoint, ssid, clients)); return; }
            _lblTelemetry.Text = $"Uplink: {transport}  |  Phone: {endpoint}  |  Hotspot SSID: {ssid}  |  Clients: {clients}";
        }

        public void AppendLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
            _txtLog.AppendText(line + Environment.NewLine);
        }
    }
}