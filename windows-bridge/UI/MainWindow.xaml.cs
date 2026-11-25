using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZeroConfigBridge.Core;

namespace ZeroConfigBridge.UI;

public partial class MainWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly MdnsPhoneDiscoverer _discoverer = new();
    private readonly Tun2SocksManager _tun2Socks = new();
    private readonly HotspotController _hotspot = new();
    private readonly IcsSharingManager _ics = new();
    private readonly AdbUdpSocksBridge _adbUdpBridge = new();
    private readonly DispatcherTimer _telemetryTimer;

    private CancellationTokenSource? _bridgeCts;
    private Storyboard? _pulseStoryboard;
    private DiscoveredPhoneEndpoint? _activeEndpoint;
    private bool _isConnected;
    private bool _isBusy;
    private int _reconnectAttempts;
    private int _currentTutStep;
    private int _currentStage;

    public bool AllowExit { get; set; }
    public event Action<string, bool, bool>? ConnectionStateChanged;

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();

        _discoverer.OnLog += AppendLog;
        _tun2Socks.OnLog += AppendLog;
        _hotspot.OnLog += AppendLog;
        _ics.OnLog += AppendLog;
        _tun2Socks.OnExitedUnexpectedly += code =>
        {
            if (_isConnected && !_isBusy)
            {
                AppendLog($"ALERT: tun2socks exited unexpectedly (code {code}).");
                Dispatcher.BeginInvoke(async () => await HandleUnexpectedTunnelExitAsync());
            }
        };

        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _telemetryTimer.Tick += (_, _) => UpdateHotspotTelemetry();
        _telemetryTimer.Start();

        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int pref = DwmwcpRound;
                _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
            }
            catch { }
        };

        Closing += OnWindowClosing;
        ApplyLocalization();
        RenderTutorialStep();
        UpdatePipelineUi(0);
    }

    private void LoadWindowIcon()
    {
        try
        {
            string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ZeroConfig.ico");
            if (File.Exists(icoPath))
                Icon = BitmapFrame.Create(new Uri(icoPath, UriKind.Absolute));
        }
        catch { }
    }

    public void ApplyLocalization()
    {
        TxtBrandSub.Text = AppStrings.Get("BrandSub");
        BtnNavBridge.Content = AppStrings.Get("NavBridge");
        BtnNavTutorial.Content = AppStrings.Get("NavTutorial");
        BtnNavLogs.Content = AppStrings.Get("NavLogs");
        BtnNavSupport.Content = AppStrings.Get("NavSupport");

        TxtPipelineTitle.Text = AppStrings.Get("PipelineTitle");
        TxtStepPhone.Text = AppStrings.Get("StepPhone");
        TxtStepTun.Text = AppStrings.Get("StepTun");
        TxtStepHotspot.Text = AppStrings.Get("StepHotspot");

        TxtConfigTitle.Text = AppStrings.Get("ConfigTitle");
        TxtLblDiscovery.Text = AppStrings.Get("LblDiscovery");

        int selectedIdx = Math.Max(0, CmbMode.SelectedIndex);
        CmbMode.Items.Clear();
        CmbMode.Items.Add(AppStrings.Get("ModeAuto"));
        CmbMode.Items.Add(AppStrings.Get("ModeUsb"));
        CmbMode.Items.Add(AppStrings.Get("ModeWifi"));
        CmbMode.Items.Add(AppStrings.Get("ModeAdb"));
        CmbMode.SelectedIndex = selectedIdx;

        TxtLblSsid.Text = AppStrings.Get("LblSsid");
        TxtLblPass.Text = AppStrings.Get("LblPassword");
        ChkAutoReconnect.Content = AppStrings.Get("ChkAutoReconnect");
        BtnLeakTest.Content = AppStrings.Get("BtnLeakTest");

        TxtTutHeader.Text = AppStrings.Get("TutTitle");
        TxtLogsTitle.Text = AppStrings.Get("LogsTitle");
        BtnCopyLogs.Content = AppStrings.Get("BtnCopyLogs");
        BtnClearLogs.Content = AppStrings.Get("BtnClearLogs");
        BtnResetIcs.Content = AppStrings.Get("BtnResetIcs");

        TxtSupportTitle.Text = AppStrings.Get("SupportTitle");
        TxtMadeByLine.Text = AppStrings.Get("MadeByLine");
        TxtSupportDesc.Text = AppStrings.Get("SupportDesc");
        BtnWhatsApp.Content = AppStrings.Get("BtnWhatsApp");
        BtnCopyPhone.Content = AppStrings.Get("BtnCopyPhone");
        TxtFooter1.Text = AppStrings.Get("SidebarFooter1");
        TxtFooter2.Text = AppStrings.Get("SidebarFooter2");

        BtnLangToggle.Content = AppStrings.CurrentLanguage == "fr" ? "FR → EN" : "EN → FR";
        BtnMainAction.Content = _isConnected ? AppStrings.Get("BtnDisconnect") : AppStrings.Get("BtnConnect");

        if (!_isConnected && !_isBusy)
        {
            TxtHeaderStatus.Text = AppStrings.Get("StatusDisconnected");
            TxtTelemetry.Text = AppStrings.Get("TelemetryEmpty");
        }
        UpdatePipelineUi(_currentStage);
        RenderTutorialStep();
    }

    public async Task ConnectFromTrayAsync()
    {
        ShowAndActivate();
        await ConnectAsync();
    }

    public async Task DisconnectFromTrayAsync() => await DisconnectAsync();

    public void ForceResetIcs()
    {
        _ics.DisableAllSharing();
        _tun2Socks.Stop();
        _adbUdpBridge.Stop();
        AppendLog(AppStrings.Get("ToastIcsReset"));
        ShowToast(AppStrings.Get("ToastIcsReset"));
    }

    public void CleanupOnExitSync()
    {
        _telemetryTimer.Stop();
        _ics.DisableAllSharing();
        _tun2Socks.Stop();
        _adbUdpBridge.Stop();
        try { _hotspot.StopHotspotAsync().GetAwaiter().GetResult(); } catch { }
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowExit)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnHideToTray(object sender, RoutedEventArgs e) => Hide();

    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        AppStrings.ToggleLanguage();
        ApplyLocalization();
        ConnectionStateChanged?.Invoke(TxtHeaderStatus.Text, _isBusy, _isConnected);
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out int idx)) return;
        SwitchPage(idx);
    }

    private void SwitchPage(int index)
    {
        PageBridge.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageTutorial.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageSupport.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        var navButtons = new[] { BtnNavBridge, BtnNavTutorial, BtnNavLogs, BtnNavSupport };
        var activeBg = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        var normalFg = new SolidColorBrush(Color.FromRgb(203, 213, 225));
        for (int i = 0; i < navButtons.Length; i++)
        {
            navButtons[i].Background = i == index ? activeBg : Brushes.Transparent;
            navButtons[i].Foreground = i == index ? Brushes.White : normalFg;
        }

        // Smooth fade + upward slide animation
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        PageHost.BeginAnimation(OpacityProperty, fade);
        PageSlideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void OnToggleRevealPass(object sender, RoutedEventArgs e)
    {
        if (TxtPassVisible.Visibility == Visibility.Collapsed)
        {
            TxtPassVisible.Text = TxtPassHidden.Password;
            TxtPassHidden.Visibility = Visibility.Collapsed;
            TxtPassVisible.Visibility = Visibility.Visible;
        }
        else
        {
            TxtPassHidden.Password = TxtPassVisible.Text;
            TxtPassVisible.Visibility = Visibility.Collapsed;
            TxtPassHidden.Visibility = Visibility.Visible;
        }
    }

    private string GetCurrentPassword() =>
        TxtPassVisible.Visibility == Visibility.Visible ? TxtPassVisible.Text : TxtPassHidden.Password;

    private async void OnMainActionClick(object sender, RoutedEventArgs e)
    {
        if (_isConnected) await DisconnectAsync();
        else await ConnectAsync();
    }

    private void OnLeakTestClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://ipleak.net") { UseShellExecute = true }); } catch { }
    }

    private void OnTutPrevClick(object sender, RoutedEventArgs e)
    {
        if (_currentTutStep > 0) { _currentTutStep--; RenderTutorialStep(animate: true); }
    }

    private void OnTutNextClick(object sender, RoutedEventArgs e)
    {
        if (_currentTutStep < 3) { _currentTutStep++; RenderTutorialStep(animate: true); }
        else SwitchPage(0);
    }

    private void RenderTutorialStep(bool animate = false)
    {
        int step = _currentTutStep + 1;
        TxtTutStepCounter.Text = string.Format(AppStrings.Get("TutStepFmt"), step);
        TxtTutNum.Text = step.ToString();
        TxtTutStepTitle.Text = AppStrings.Get($"Tut{step}Title");
        TxtTutStepDesc.Text = AppStrings.Get($"Tut{step}Desc");
        BtnTutPrev.IsEnabled = _currentTutStep > 0;
        BtnTutNext.Content = _currentTutStep == 3 ? AppStrings.Get("BtnStartNow") : AppStrings.Get("BtnNext");

        if (animate)
        {
            var anim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(220));
            TutContentPanel.BeginAnimation(OpacityProperty, anim);
        }
    }

    private void OnCopyLogsClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtLogs.Text))
        {
            Clipboard.SetText(TxtLogs.Text);
            ShowToast(AppStrings.Get("ToastCopied"));
        }
    }

    private void OnClearLogsClick(object sender, RoutedEventArgs e) => TxtLogs.Clear();
    private void OnResetIcsClick(object sender, RoutedEventArgs e) => ForceResetIcs();

    private void OnWhatsAppClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://wa.me/237673533489") { UseShellExecute = true }); } catch { }
    }

    private void OnCopyPhoneClick(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText("+237 673533489");
        ShowToast(AppStrings.Get("ToastCopied"));
    }

    private async Task ConnectAsync()
    {
        if (_isBusy || _isConnected) return;
        _isBusy = true;
        _bridgeCts = new CancellationTokenSource();
        var ct = _bridgeCts.Token;
        var mode = (PreferredDiscoveryMode)Math.Max(0, CmbMode.SelectedIndex);
        string ssid = TxtSsid.Text;
        string pass = GetCurrentPassword();

        SetBridgeState(AppStrings.Get("StatusDiscovering"), busy: true, connected: false, stage: 1);
        try
        {
            AppendLog($"Starting Zero-Config Bridge (Mode: {mode})...");
            var endpoint = await _discoverer.DiscoverAsync(mode, TimeSpan.FromSeconds(18), ct);
            _activeEndpoint = endpoint;

            SetBridgeState(AppStrings.Get("StatusTun"), busy: true, connected: false, stage: 2);
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

            SetBridgeState(AppStrings.Get("StatusHotspot"), busy: true, connected: false, stage: 3);
            var hsStatus = await _hotspot.StartHotspotAsync(ssid, pass, ct);
            if (string.IsNullOrWhiteSpace(hsStatus.VirtualAdapterName))
                throw new InvalidOperationException("Hotspot started, but its virtual network adapter was not found.");

            _ics.EnableBridgeSharing(Tun2SocksManager.AdapterName, hsStatus.VirtualAdapterName);
            _isConnected = true;
            _reconnectAttempts = 0;

            SetBridgeState($"{AppStrings.Get("StatusConnected")} ({hsStatus.Ssid})", busy: false, connected: true, stage: 4);
            AppendLog("READY: Smart TVs, consoles, and phones can now join the hotspot with zero proxy configuration.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            await CleanupAsync(stopHotspot: false);
            SetBridgeState(AppStrings.Get("StatusDisconnected"), busy: false, connected: false, stage: 0);
            ShowToast(ex.Message);
        }
        finally { _isBusy = false; }
    }

    private async Task DisconnectAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        _reconnectAttempts = 0;
        SetBridgeState(AppStrings.Get("StatusDisconnecting"), busy: true, connected: _isConnected, stage: 0);
        await CleanupAsync(stopHotspot: true);
        _isConnected = false;
        _activeEndpoint = null;
        SetBridgeState(AppStrings.Get("StatusDisconnected"), busy: false, connected: false, stage: 0);
        AppendLog("Bridge cleanly disconnected and Windows ICS restored.");
        _isBusy = false;
    }

    private async Task HandleUnexpectedTunnelExitAsync()
    {
        if (ChkAutoReconnect.IsChecked != true || _reconnectAttempts >= 3)
        {
            await DisconnectAsync();
            return;
        }
        _reconnectAttempts++;
        AppendLog($"Auto-Reconnect attempt {_reconnectAttempts}/3 in 3 seconds...");
        await CleanupAsync(stopHotspot: false);
        _isConnected = false;
        await Task.Delay(3000);
        await ConnectAsync();
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

    private void SetBridgeState(string status, bool busy, bool connected, int stage)
    {
        _currentStage = stage;
        TxtHeaderStatus.Text = status;
        var green = Color.FromRgb(52, 211, 153);
        var amber = Color.FromRgb(251, 191, 36);
        var cyan = Color.FromRgb(56, 189, 248);
        var color = connected ? green : (busy ? amber : cyan);
        TxtHeaderStatus.Foreground = new SolidColorBrush(color);
        StatusDot.Fill = new SolidColorBrush(color);

        BtnMainAction.IsEnabled = !busy;
        BtnMainAction.Content = connected ? AppStrings.Get("BtnDisconnect") : AppStrings.Get("BtnConnect");
        BtnMainAction.Background = new SolidColorBrush(connected ? Color.FromRgb(220, 38, 38) : Color.FromRgb(37, 99, 235));

        CmbMode.IsEnabled = !busy && !connected;
        TxtSsid.IsEnabled = !busy && !connected;
        TxtPassHidden.IsEnabled = !busy && !connected;
        TxtPassVisible.IsEnabled = !busy && !connected;

        if (connected) StartPulseAnimation();
        else StopPulseAnimation();

        UpdatePipelineUi(stage);
        if (!connected) TxtTelemetry.Text = AppStrings.Get("TelemetryEmpty");
        ConnectionStateChanged?.Invoke(status, busy, connected);
    }

    private void UpdatePipelineUi(int stage)
    {
        var green = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        var amber = new SolidColorBrush(Color.FromRgb(251, 191, 36));
        var muted = new SolidColorBrush(Color.FromRgb(100, 116, 139));

        TxtStepPhoneState.Text = stage >= 2 ? AppStrings.Get("StateConnected") : (stage == 1 ? AppStrings.Get("StateScanning") : AppStrings.Get("StateIdle"));
        TxtStepPhoneState.Foreground = stage >= 2 ? green : (stage == 1 ? amber : muted);

        TxtStepTunState.Text = stage >= 3 ? AppStrings.Get("StateActive") : (stage == 2 ? AppStrings.Get("StateStarting") : AppStrings.Get("StateIdle"));
        TxtStepTunState.Foreground = stage >= 3 ? green : (stage == 2 ? amber : muted);

        TxtStepHotspotState.Text = stage >= 4 ? AppStrings.Get("StateSharing") : (stage == 3 ? AppStrings.Get("StateBridging") : AppStrings.Get("StateIdle"));
        TxtStepHotspotState.Foreground = stage >= 4 ? green : (stage == 3 ? amber : muted);
    }

    private void StartPulseAnimation()
    {
        if (_pulseStoryboard != null) return;
        var sx = new DoubleAnimation(0.9, 1.9, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
        var sy = new DoubleAnimation(0.9, 1.9, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
        var op = new DoubleAnimation(0.7, 0.0, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(sx, PulseHalo);
        Storyboard.SetTargetProperty(sx, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        Storyboard.SetTarget(sy, PulseHalo);
        Storyboard.SetTargetProperty(sy, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        Storyboard.SetTarget(op, PulseHalo);
        Storyboard.SetTargetProperty(op, new PropertyPath(OpacityProperty));
        _pulseStoryboard = new Storyboard();
        _pulseStoryboard.Children.Add(sx);
        _pulseStoryboard.Children.Add(sy);
        _pulseStoryboard.Children.Add(op);
        _pulseStoryboard.Begin();
    }

    private void StopPulseAnimation()
    {
        _pulseStoryboard?.Stop();
        _pulseStoryboard = null;
        PulseHalo.Opacity = 0;
    }

    private void UpdateHotspotTelemetry()
    {
        if (!_isConnected) return;
        var st = _hotspot.GetCurrentStatus();
        string transport = _activeEndpoint?.Transport.ToString() ?? "--";
        string endpoint = _activeEndpoint != null ? $"{_activeEndpoint.Address}:{_activeEndpoint.Port}" : "--";
        TxtTelemetry.Text = string.Format(AppStrings.Get("TelemetryFmt"), transport, endpoint, st.Ssid, st.ClientCount);
    }

    private void AppendLog(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TxtLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            TxtLogs.ScrollToEnd();
        });
    }

    private async void ShowToast(string msg)
    {
        TxtToast.Text = msg;
        ToastBanner.Visibility = Visibility.Visible;
        ToastBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        await Task.Delay(3000);
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fadeOut.Completed += (_, _) => ToastBanner.Visibility = Visibility.Collapsed;
        ToastBanner.BeginAnimation(OpacityProperty, fadeOut);
    }
}