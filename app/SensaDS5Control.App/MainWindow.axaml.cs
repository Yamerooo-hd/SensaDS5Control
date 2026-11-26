using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SensaDS5Control.Core;
using SensaDS5Control.Platform.Windows;

namespace SensaDS5Control.App;

public partial class MainWindow : Window
{
    private readonly IPlatformServices _platform = new WindowsPlatformServices();
    private readonly PicoBridgeClient _pico = new();
    private readonly MouseMapper _mouse = new();
    private readonly AppSettings _settings;
    private readonly StringBuilder _logSb = new();
    private readonly object _logLock = new();
    private readonly DispatcherTimer _logFlushTimer;
    private readonly DispatcherTimer _hotplugTimer;

    private volatile bool _logDirty;
    private int _uiTelemetryPending;
    private int _hotplugBusy;
    private bool _wasMicBtn;
    private volatile bool _syncMicButton = true;
    private bool _loadingUi = true;
    private bool _switchingUsbMode;
    private bool _wasBtControllerReady;
    private DateTime _ignoreBtReadyUntilUtc = DateTime.MinValue;
    private int _activeNavIndex;
    private byte _lastUsbMode;

    private static readonly IBrush DotGreen = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush DotAmber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush DotGray = new SolidColorBrush(Color.Parse("#64748B"));

    public MainWindow() : this(AppSettings.Load()) { }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        PopulateStaticCombos();
        ApplySettingsToUi();
        _loadingUi = false;

        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _logFlushTimer.Tick += (_, _) => FlushConsoleLogIfDirty();
        _logFlushTimer.Start();

        _hotplugTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _hotplugTimer.Tick += (_, _) => _ = PollPicoUsbHotplugAsync();
        _hotplugTimer.Start();

        _pico.ConnectionChanged += OnPicoConnectionChanged;
        _pico.TelemetryReceived += OnTelemetryReceived;
        _pico.LogLineReceived += OnLogReceived;

        UpdateMicButtonText();
        _ = PollPicoUsbHotplugAsync();
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int idx))
            SelectNavPage(idx);
    }

    private void SelectNavPage(int idx)
    {
        _activeNavIndex = idx;
        Control[] pages = [Page0, Page1, Page2, Page3, Page4, Page5];
        Button[] navBtns = [NavDashBtn, NavTesterBtn, NavHapticsBtn, NavTriggersBtn, NavLightingBtn, NavConsoleBtn];
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].IsVisible = i == idx;
            if (i == idx) navBtns[i].Classes.Add("active");
            else navBtns[i].Classes.Remove("active");
        }
    }

    private void PopulateStaticCombos()
    {
        string[] triggerPresets =
        [
            "Follow Game (Auto)",
            "Off",
            "Continuous Resistance",
            "Section / Gun Breakpoint",
            "Weapon (Gun Snap)",
            "Vibration (Rapid Fire)",
            "Bow (Progressive Tension)"
        ];
        LeftTrigCombo.ItemsSource = triggerPresets;
        RightTrigCombo.ItemsSource = triggerPresets;

        ImpulseModeCombo.ItemsSource = new[]
        {
            "Off (Normal Triggers)",
            "Always (On Any Rumble)",
            "Only While Trigger Pulled"
        };

        SidebarThemeCombo.ItemsSource = ThemeManager.Presets.Select(t => t.DisplayName).ToArray();
    }

    private void ApplySettingsToUi()
    {
        int themeIdx = ThemeManager.Presets.ToList()
            .FindIndex(t => string.Equals(t.Id, _settings.ThemeId, StringComparison.OrdinalIgnoreCase));
        SidebarThemeCombo.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;

        TouchMouseChk.IsChecked = _settings.TouchpadMouse;
        QuickTouchMouseBtn.IsChecked = _settings.TouchpadMouse;
        QuickTouchMouseBtn.Content = _settings.TouchpadMouse ? "Touchpad Mouse: ON" : "Touchpad Mouse: OFF";
        TouchSensSlider.Value = _settings.TouchpadSens;
        TouchSensLabel.Text = $"{_settings.TouchpadSens:0.0}x";

        GyroMouseChk.IsChecked = _settings.GyroMouse;
        GyroL2Chk.IsChecked = _settings.GyroOnlyOnL2;

        HapticGainSlider.Value = _settings.HapticGainPct;
        HapticGainLabel.Text = $"{_settings.HapticGainPct}%";

        ImpulseModeCombo.SelectedIndex = Math.Clamp(_settings.ImpulseTriggerMode, 0, 2);
        ImpulseStrengthSlider.Value = Math.Clamp(_settings.ImpulseTriggerStrengthPct, 0, 100);
        ImpulseStrengthLabel.Text = $"{_settings.ImpulseTriggerStrengthPct}%";

        LedRSlider.Value = _settings.LedR;
        LedGSlider.Value = _settings.LedG;
        LedBSlider.Value = _settings.LedB;
        UpdateLedPreview();

        LeftTrigCombo.SelectedIndex = (int)_settings.LeftTriggerKind;
        LStartSlider.Value = _settings.LeftStart;
        LEndSlider.Value = _settings.LeftEnd;
        LForceSlider.Value = _settings.LeftForce;
        LFreqSlider.Value = _settings.LeftFreq;

        RightTrigCombo.SelectedIndex = (int)_settings.RightTriggerKind;
        RStartSlider.Value = _settings.RightStart;
        REndSlider.Value = _settings.RightEnd;
        RForceSlider.Value = _settings.RightForce;
        RFreqSlider.Value = _settings.RightFreq;
        UpdateTriggerSliderLabels();

        SyncMicBtnChk.IsChecked = _settings.SyncControllerMicButton;
        _syncMicButton = _settings.SyncControllerMicButton;
        TokenBox.Text = _settings.GitHubToken;

        _mouse.TouchpadMouseEnabled = _settings.TouchpadMouse;
        _mouse.TouchpadSensitivity = _settings.TouchpadSens;
        _mouse.GyroMouseEnabled = _settings.GyroMouse;
        _mouse.GyroOnlyOnL2 = _settings.GyroOnlyOnL2;
        _mouse.GyroSensitivity = _settings.GyroSens;
    }

    private void SelectTheme(string themeId)
    {
        ThemeManager.Apply(themeId);
        _settings.ThemeId = themeId;
        _settings.Save();
        int idx = ThemeManager.Presets.ToList()
            .FindIndex(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && SidebarThemeCombo.SelectedIndex != idx)
        {
            _loadingUi = true;
            SidebarThemeCombo.SelectedIndex = idx;
            _loadingUi = false;
        }
    }

    private void OnThemeComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        int idx = SidebarThemeCombo.SelectedIndex;
        if (idx >= 0 && idx < ThemeManager.Presets.Count)
            SelectTheme(ThemeManager.Presets[idx].Id);
    }

    private void OnThemePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string themeId })
            SelectTheme(themeId);
    }

    private async Task PollPicoUsbHotplugAsync()
    {
        if (_switchingUsbMode || Interlocked.CompareExchange(ref _hotplugBusy, 1, 0) != 0)
            return;

        try
        {
            var ports = await Task.Run(() => _platform.DiscoverPicoComPorts());
            var activePort = ports.FirstOrDefault();
            if (activePort == null)
            {
                if (_pico.IsConnected || _pico.ActivePortName != null)
                    _pico.NotifyUsbRemoved();
                return;
            }

            if (_pico.IsConnected)
            {
                if (_pico.ActivePortName != null && !ports.Contains(_pico.ActivePortName, StringComparer.OrdinalIgnoreCase))
                    _pico.NotifyUsbRemoved();
                return;
            }

            await _pico.ConnectAsync(activePort, wakeController: false);
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _hotplugBusy, 0);
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_switchingUsbMode) return;

        if (_pico.IsConnected)
        {
            if (_wasBtControllerReady)
            {
                _ignoreBtReadyUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
                _pico.DisconnectController();
                _mouse.ReleaseAllButtons(_platform);
                _wasBtControllerReady = false;
                SetConnectButton(isConnectedController: false, connectedToPico: true);
                StatusDot.Fill = DotAmber;
                StatusText.Text = "Controller Disconnected";
                SubStatusText.Text = "Press PS button on Sensa or click Reconnect Controller";
                DashControllerDiagram.ResetDisconnected();
                TesterControllerDiagram.ResetDisconnected();
            }
            else
            {
                _ignoreBtReadyUntilUtc = DateTime.MinValue;
                StatusText.Text = "Reconnecting Controller...";
                SubStatusText.Text = "Paging bonded Sensa controller (or press PS button)...";
                _pico.ReconnectController();
            }
            return;
        }

        StatusText.Text = "Searching for Pico USB...";
        var ports = await Task.Run(() => _platform.DiscoverPicoComPorts());
        if (ports.Count > 0)
        {
            await _pico.ConnectAsync(ports[0], wakeController: true);
        }
        else
        {
            StatusText.Text = "Pico USB Not Found";
            SubStatusText.Text = "Plug Pico 2 W into USB (auto-connects immediately)";
            StatusDot.Fill = DotGray;
        }
    }

    private void SetConnectButton(bool isConnectedController, bool connectedToPico)
    {
        ConnectBtn.Classes.Remove("danger");
        ConnectBtn.Classes.Remove("accent");
        if (!connectedToPico)
        {
            ConnectBtn.Content = "Connect";
            ConnectBtn.Classes.Add("accent");
            return;
        }
        ConnectBtn.Content = isConnectedController ? "Disconnect Controller" : "Reconnect Controller";
        ConnectBtn.Classes.Add(isConnectedController ? "danger" : "accent");
    }

    private void OnPicoConnectionChanged(bool connected, string? portOrError)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (connected)
            {
                _wasBtControllerReady = false;
                SetConnectButton(isConnectedController: false, connectedToPico: true);
                StatusDot.Fill = DotAmber;
                StatusText.Text = $"Pico Connected ({portOrError})";
                SubStatusText.Text = "Checking Sensa Bluetooth connection...";
                PortBadgeTxt.Text = $"Port: {portOrError}";
                PushSavedProfileToPico();
            }
            else
            {
                _mouse.ReleaseAllButtons(_platform);
                _wasBtControllerReady = false;
                SetConnectButton(isConnectedController: false, connectedToPico: false);
                StatusDot.Fill = DotGray;
                StatusText.Text = _switchingUsbMode ? "Rebooting Pico USB..." : "Disconnected";
                SubStatusText.Text = string.IsNullOrEmpty(portOrError) ? "Waiting for Pico USB..." : portOrError;
                PortBadgeTxt.Text = "USB: Offline";
                BatteryBadgeTxt.Text = "Bat: --";
                DashBridgeInfo.Text = "Bridge: Disconnected";
                DashBatteryInfo.Text = "Battery: --";
                DashReportMode.Text = "Input Mode: --";
                ResetTelemetryMeters();
                DashControllerDiagram.ResetDisconnected();
                TesterControllerDiagram.ResetDisconnected();
            }
        });
    }

    private void ResetTelemetryMeters()
    {
        DashHapticLBar.Value = 0;
        DashHapticRBar.Value = 0;
        HapticLBar.Value = 0;
        HapticRBar.Value = 0;
        L2Bar.Value = 0;
        R2Bar.Value = 0;
        ButtonsActiveTxt.Text = "Pressed: (none)";
    }

    private void PushSavedProfileToPico()
    {
        _pico.SetHapticGain(_settings.HapticGainPct);
        _pico.SetImpulseTriggers(_settings.ImpulseTriggerMode, _settings.ImpulseTriggerStrengthPct);
        _pico.SetMicLed(_settings.MicLedMode);
        if (_settings.LedOverride) _pico.SetLed(_settings.LedR, _settings.LedG, _settings.LedB);
        _pico.SetTrigger("l", _settings.LeftTriggerKind, _settings.LeftStart, _settings.LeftEnd, _settings.LeftForce, _settings.LeftFreq);
        _pico.SetTrigger("r", _settings.RightTriggerKind, _settings.RightStart, _settings.RightEnd, _settings.RightForce, _settings.RightFreq);
    }

    private void UpdateUsbIdentityIndicators(byte usbMode)
    {
        bool isXbox = usbMode == 1;
        _lastUsbMode = usbMode;

        BtnUsbDs5.Classes.Remove("accent");
        BtnUsbX360.Classes.Remove("accent");
        if (isXbox) BtnUsbX360.Classes.Add("accent");
        else BtnUsbDs5.Classes.Add("accent");

        string label = isXbox ? "Xbox 360" : "DualSense";
        HeroModePill.Text = $"Mode: {label}";
        TesterLayoutBadge.Text = $"Layout: {label}";
        TesterL2Label.Text = isXbox ? "LT" : "L2";
        TesterR2Label.Text = isXbox ? "RT" : "R2";

        DashControllerDiagram.SetXboxMode(isXbox);
        TesterControllerDiagram.SetXboxMode(isXbox);
    }

    private void OnTelemetryReceived(TelemetrySample s)
    {
        if (s.BtControllerReady)
        {
            _mouse.Process(s, _platform);
        }
        else
        {
            _mouse.ReleaseAllButtons(_platform);
        }

        bool micPressedEdge = s.BtControllerReady && _syncMicButton && s.MicMuteBtn && !_wasMicBtn;
        _wasMicBtn = s.BtControllerReady && s.MicMuteBtn;

        if (!micPressedEdge && Interlocked.Exchange(ref _uiTelemetryPending, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _uiTelemetryPending, 0);
            if (!_pico.IsConnected) return;

            bool btNow = s.BtControllerReady && DateTime.UtcNow >= _ignoreBtReadyUntilUtc;
            if (!s.BtControllerReady) _ignoreBtReadyUntilUtc = DateTime.MinValue;
            if (btNow && !_wasBtControllerReady) PushSavedProfileToPico();
            _wasBtControllerReady = btNow;

            if (_lastUsbMode != s.UsbMode)
                UpdateUsbIdentityIndicators(s.UsbMode);

            SetConnectButton(isConnectedController: btNow, connectedToPico: true);

            if (micPressedEdge)
            {
                bool nextMute = !_platform.GetSystemMicMuted();
                _platform.SetSystemMicMuted(nextMute);
                _settings.MicLedMode = (byte)(nextMute ? 1 : 0);
                _settings.Save();
                _pico.SetMicLed(_settings.MicLedMode);
                UpdateMicButtonText();
            }

            string usbShort = s.UsbMode == 1 ? "Xbox 360" : "DualSense";
            StatusDot.Fill = btNow ? DotGreen : DotAmber;
            StatusText.Text = btNow ? $"Sensa Active ({usbShort})" : "Pico Ready — Turn on Sensa";
            PortBadgeTxt.Text = $"{_pico.ActivePortName ?? "USB"} • {usbShort}";
            string chgTag = s.ChargingState != 0 ? " ⚡" : "";
            BatteryBadgeTxt.Text = btNow ? (s.FullMode0x31 ? $"Bat: {s.BatteryPct}%{chgTag}" : "Short 0x01") : "BT Idle";

            DashBridgeInfo.Text = $"Bridge: Mode {s.Mode} | 0x36 tx={s.Tx0x36} | Gain {s.HapticGainPct}% | {usbShort}";
            DashBatteryInfo.Text = btNow
                ? (s.FullMode0x31 ? $"Battery: {s.BatteryPct}%{chgTag} (charging={s.ChargingState})" : "Battery: (waiting for 0x31 full mode)")
                : "Battery: --";
            DashReportMode.Text = btNow
                ? (s.FullMode0x31 ? "Input Mode: Full 0x31 (Gyro + Touchpad active)" : "Input Mode: Short 0x01")
                : "Bluetooth Controller Disconnected";
            UsbModeStatus.Text = s.UsbMode == 1
                ? "Active USB Personality: Xbox 360 XInput Controller (1209:DB05)"
                : "Active USB Personality: DualSense Wireless Controller (054C:0CE6)";
            SubStatusText.Text = btNow
                ? $"{(s.FullMode0x31 ? "Full 0x31" : "Short 0x01")} | tx0x36={s.Tx0x36} | Peak L={s.PeakHapticL:0.0}% R={s.PeakHapticR:0.0}%"
                : "Press PS button on Sensa or click Reconnect Controller";

            if (!btNow)
            {
                DashControllerDiagram.ResetDisconnected();
                TesterControllerDiagram.ResetDisconnected();
                ResetTelemetryMeters();
                ButtonsActiveTxt.Text = "Pressed: (controller sleeping)";
                return;
            }

            double hl = Math.Clamp(s.PeakHapticL, 0, 100);
            double hr = Math.Clamp(s.PeakHapticR, 0, 100);
            DashHapticLBar.Value = hl;
            DashHapticRBar.Value = hr;
            HapticLBar.Value = hl;
            HapticRBar.Value = hr;

            DashControllerDiagram.UpdateTelemetry(s, _settings.LedR, _settings.LedG, _settings.LedB, _settings.LedOverride);
            if (_activeNavIndex == 1)
                UpdateTesterPage(s);
        });
    }

    private void UpdateTesterPage(TelemetrySample s)
    {
        TesterControllerDiagram.UpdateTelemetry(s, _settings.LedR, _settings.LedG, _settings.LedB, _settings.LedOverride);

        Canvas.SetLeft(LeftStickDot, (s.Lx / 255.0) * 104 + 2);
        Canvas.SetTop(LeftStickDot, (s.Ly / 255.0) * 104 + 2);
        LeftStickTxt.Text = $"{s.Lx}, {s.Ly}";

        Canvas.SetLeft(RightStickDot, (s.Rx / 255.0) * 104 + 2);
        Canvas.SetTop(RightStickDot, (s.Ry / 255.0) * 104 + 2);
        RightStickTxt.Text = $"{s.Rx}, {s.Ry}";

        L2Bar.Value = s.L2;
        R2Bar.Value = s.R2;

        bool xbox = s.UsbMode == 1;
        var pressed = new List<string>(14);
        if (s.Cross) pressed.Add(xbox ? "A" : "Cross");
        if (s.Circle) pressed.Add(xbox ? "B" : "Circle");
        if (s.Square) pressed.Add(xbox ? "X" : "Square");
        if (s.Triangle) pressed.Add(xbox ? "Y" : "Triangle");
        if (s.L1) pressed.Add(xbox ? "LB" : "L1");
        if (s.R1) pressed.Add(xbox ? "RB" : "R1");
        if (s.L3) pressed.Add(xbox ? "LS" : "L3");
        if (s.R3) pressed.Add(xbox ? "RS" : "R3");
        if (s.Create) pressed.Add(xbox ? "Back" : "Create");
        if (s.Options) pressed.Add(xbox ? "Start" : "Options");
        if (s.Ps) pressed.Add(xbox ? "Guide" : "PS");
        if (s.TouchpadClick && !xbox) pressed.Add("TP-Click");
        if (s.MicMuteBtn) pressed.Add("Mic");
        if (s.DPad != 8) pressed.Add($"DPad({s.DPad})");

        ButtonsActiveTxt.Text = pressed.Count > 0 ? "Pressed: " + string.Join(", ", pressed) : "Pressed: (none)";
        RawBytesTxt.Text = $"Raw Btn Bytes: {s.Btn0:X2} {s.Btn1:X2} {s.Btn2:X2} {s.Btn3:X2}";
        GyroTxt.Text = $"Gyro  (X,Y,Z): {s.GyroX,6}, {s.GyroY,6}, {s.GyroZ,6}";
        AccelTxt.Text = $"Accel (X,Y,Z): {s.AccelX,6}, {s.AccelY,6}, {s.AccelZ,6}";

        Touch0Dot.IsVisible = s.Touch0Down;
        if (s.Touch0Down)
        {
            Canvas.SetLeft(Touch0Dot, Math.Clamp(s.Touch0X / 1920.0 * 214, 0, 214));
            Canvas.SetTop(Touch0Dot, Math.Clamp(s.Touch0Y / 1080.0 * 104, 0, 104));
        }
        Touch1Dot.IsVisible = s.Touch1Down;
        if (s.Touch1Down)
        {
            Canvas.SetLeft(Touch1Dot, Math.Clamp(s.Touch1X / 1920.0 * 214, 0, 214));
            Canvas.SetTop(Touch1Dot, Math.Clamp(s.Touch1Y / 1080.0 * 104, 0, 104));
        }
        TouchTxt.Text = $"Touch 0: {(s.Touch0Down ? $"{s.Touch0X},{s.Touch0Y}" : "--")} | Touch 1: {(s.Touch1Down ? $"{s.Touch1X},{s.Touch1Y}" : "--")}";
    }

    private void OnLogReceived(string line)
    {
        lock (_logLock)
        {
            _logSb.AppendLine(line);
            if (_logSb.Length > 12000) _logSb.Remove(0, _logSb.Length - 8000);
            _logDirty = true;
        }
    }

    private void FlushConsoleLogIfDirty()
    {
        if (!_logDirty) return;
        string snap;
        lock (_logLock)
        {
            if (!_logDirty) return;
            _logDirty = false;
            snap = _logSb.ToString();
        }
        ConsoleLogBox.Text = snap;
        ConsoleLogBox.CaretIndex = snap.Length;
    }

    private void OnQuickTouchMouseClick(object? sender, RoutedEventArgs e)
    {
        bool on = QuickTouchMouseBtn.IsChecked == true;
        TouchMouseChk.IsChecked = on;
        QuickTouchMouseBtn.Content = on ? "Touchpad Mouse: ON" : "Touchpad Mouse: OFF";
        _mouse.TouchpadMouseEnabled = on;
        _settings.TouchpadMouse = on;
        _settings.Save();
    }

    private void OnMouseSettingsChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        bool tm = TouchMouseChk.IsChecked == true;
        QuickTouchMouseBtn.IsChecked = tm;
        QuickTouchMouseBtn.Content = tm ? "Touchpad Mouse: ON" : "Touchpad Mouse: OFF";
        _mouse.TouchpadMouseEnabled = tm;
        _mouse.TouchpadSensitivity = TouchSensSlider.Value;
        TouchSensLabel.Text = $"{TouchSensSlider.Value:0.0}x";

        _mouse.GyroMouseEnabled = GyroMouseChk.IsChecked == true;
        _mouse.GyroOnlyOnL2 = GyroL2Chk.IsChecked == true;

        _settings.TouchpadMouse = tm;
        _settings.TouchpadSens = TouchSensSlider.Value;
        _settings.GyroMouse = _mouse.GyroMouseEnabled;
        _settings.GyroOnlyOnL2 = _mouse.GyroOnlyOnL2;
        _settings.Save();
    }

    private void OnCalibrateGyroClick(object? sender, RoutedEventArgs e) => _mouse.StartGyroCalibration();

    private void OnToggleMicMuteClick(object? sender, RoutedEventArgs e)
    {
        bool muted = !_platform.GetSystemMicMuted();
        _platform.SetSystemMicMuted(muted);
        _settings.MicLedMode = (byte)(muted ? 1 : 0);
        _settings.Save();
        _pico.SetMicLed(_settings.MicLedMode);
        UpdateMicButtonText();
    }

    private void OnSyncMicChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _syncMicButton = SyncMicBtnChk.IsChecked == true;
        _settings.SyncControllerMicButton = _syncMicButton;
        _settings.Save();
    }

    private void UpdateMicButtonText()
    {
        bool m = _platform.GetSystemMicMuted();
        QuickMicMuteBtn.Content = m ? "Mic: MUTED" : "Mic: Unmuted";
        MicMuteTabBtn.Content = m ? "Windows Mic: MUTED (Click to Unmute)" : "Windows Mic: Unmuted (Click to Mute)";
    }

    private void OnMicLedOffClick(object? s, RoutedEventArgs e) => SetMicLed(0);
    private void OnMicLedSolidClick(object? s, RoutedEventArgs e) => SetMicLed(1);
    private void OnMicLedPulseClick(object? s, RoutedEventArgs e) => SetMicLed(2);

    private void SetMicLed(byte mode)
    {
        _settings.MicLedMode = mode;
        _settings.Save();
        _pico.SetMicLed(mode);
    }

    private void OnHapticGainChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingUi) return;
        int val = (int)Math.Round(HapticGainSlider.Value);
        HapticGainLabel.Text = $"{val}%";
        _settings.HapticGainPct = val;
        _settings.Save();
        _pico.SetHapticGain(val);
    }

    private void OnTestCrispTapClick(object? s, RoutedEventArgs e) => _pico.TriggerTestWave("36", 125, 160, 300);
    private void OnTestHapticBuzzClick(object? s, RoutedEventArgs e) => _pico.TriggerTestWave("36", 120, 120, 900);
    private void OnTestDeepThumpClick(object? s, RoutedEventArgs e) => _pico.TriggerTestWave("36", 125, 55, 700);
    private void OnTestRumbleLeftClick(object? s, RoutedEventArgs e) => _pico.TriggerTestPulse(0, 180, 400);
    private void OnTestRumbleRightClick(object? s, RoutedEventArgs e) => _pico.TriggerTestPulse(180, 0, 400);

    private void OnImpulseModeChanged(object? sender, SelectionChangedEventArgs e) => ApplyImpulseSettings();
    private void OnImpulseStrengthChanged(object? sender, RangeBaseValueChangedEventArgs e) => ApplyImpulseSettings();

    private void ApplyImpulseSettings()
    {
        if (_loadingUi) return;
        int mode = Math.Clamp(ImpulseModeCombo.SelectedIndex, 0, 2);
        int strength = Math.Clamp((int)Math.Round(ImpulseStrengthSlider.Value), 0, 100);
        ImpulseStrengthLabel.Text = $"{strength}%";
        _settings.ImpulseTriggerMode = mode;
        _settings.ImpulseTriggerStrengthPct = strength;
        _settings.Save();
        _pico.SetImpulseTriggers(mode, strength);
    }

    private void OnTestImpulseTriggersClick(object? s, RoutedEventArgs e)
    {
        int pct = Math.Clamp(_settings.ImpulseTriggerStrengthPct, 25, 100);
        _pico.TriggerImpulseTest(pct, 650);
    }

    private void OnTriggerSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingUi) return;
        UpdateTriggerSliderLabels();
    }

    private void UpdateTriggerSliderLabels()
    {
        LStartValTxt.Text = $"{(int)Math.Round(LStartSlider.Value)}";
        LEndValTxt.Text = $"{(int)Math.Round(LEndSlider.Value)}";
        LForceValTxt.Text = $"{(int)Math.Round(LForceSlider.Value)}";
        LFreqValTxt.Text = $"{(int)Math.Round(LFreqSlider.Value)} Hz";

        RStartValTxt.Text = $"{(int)Math.Round(RStartSlider.Value)}";
        REndValTxt.Text = $"{(int)Math.Round(REndSlider.Value)}";
        RForceValTxt.Text = $"{(int)Math.Round(RForceSlider.Value)}";
        RFreqValTxt.Text = $"{(int)Math.Round(RFreqSlider.Value)} Hz";
    }

    private void OnApplyLeftTriggerClick(object? s, RoutedEventArgs e)
    {
        _settings.LeftTriggerKind = (TriggerPresetKind)Math.Max(0, LeftTrigCombo.SelectedIndex);
        _settings.LeftStart = (byte)LStartSlider.Value;
        _settings.LeftEnd = (byte)LEndSlider.Value;
        _settings.LeftForce = (byte)LForceSlider.Value;
        _settings.LeftFreq = (byte)LFreqSlider.Value;
        _settings.Save();
        _pico.SetTrigger("l", _settings.LeftTriggerKind, _settings.LeftStart, _settings.LeftEnd, _settings.LeftForce, _settings.LeftFreq);
    }

    private void OnAutoLeftTriggerClick(object? s, RoutedEventArgs e)
    {
        LeftTrigCombo.SelectedIndex = 0;
        OnApplyLeftTriggerClick(s, e);
    }

    private void OnApplyRightTriggerClick(object? s, RoutedEventArgs e)
    {
        _settings.RightTriggerKind = (TriggerPresetKind)Math.Max(0, RightTrigCombo.SelectedIndex);
        _settings.RightStart = (byte)RStartSlider.Value;
        _settings.RightEnd = (byte)REndSlider.Value;
        _settings.RightForce = (byte)RForceSlider.Value;
        _settings.RightFreq = (byte)RFreqSlider.Value;
        _settings.Save();
        _pico.SetTrigger("r", _settings.RightTriggerKind, _settings.RightStart, _settings.RightEnd, _settings.RightForce, _settings.RightFreq);
    }

    private void OnAutoRightTriggerClick(object? s, RoutedEventArgs e)
    {
        RightTrigCombo.SelectedIndex = 0;
        OnApplyRightTriggerClick(s, e);
    }

    private void OnLedSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingUi) return;
        UpdateLedPreview();
    }

    private void UpdateLedPreview()
    {
        byte r = (byte)LedRSlider.Value, g = (byte)LedGSlider.Value, b = (byte)LedBSlider.Value;
        LedPreviewBox.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        LedHexTxt.Text = $"#{r:X2}{g:X2}{b:X2}";
    }

    private void ApplyLed(byte r, byte g, byte b)
    {
        LedRSlider.Value = r; LedGSlider.Value = g; LedBSlider.Value = b;
        UpdateLedPreview();
        _settings.LedOverride = true;
        _settings.LedR = r; _settings.LedG = g; _settings.LedB = b;
        _settings.Save();
        _pico.SetLed(r, g, b);
    }

    private void OnApplyLedClick(object? s, RoutedEventArgs e) => ApplyLed((byte)LedRSlider.Value, (byte)LedGSlider.Value, (byte)LedBSlider.Value);
    private void OnAutoLedClick(object? s, RoutedEventArgs e) { _settings.LedOverride = false; _settings.Save(); _pico.SetLedAuto(); }
    private void OnPresetPsBlueClick(object? s, RoutedEventArgs e) => ApplyLed(0, 67, 220);
    private void OnPresetCyanClick(object? s, RoutedEventArgs e) => ApplyLed(0, 180, 255);
    private void OnPresetRedClick(object? s, RoutedEventArgs e) => ApplyLed(255, 20, 20);
    private void OnPresetGreenClick(object? s, RoutedEventArgs e) => ApplyLed(0, 255, 60);
    private void OnPresetPurpleClick(object? s, RoutedEventArgs e) => ApplyLed(170, 0, 255);
    private void OnPresetAmberClick(object? s, RoutedEventArgs e) => ApplyLed(255, 165, 0);

    private void OnUsbModeDs5Click(object? s, RoutedEventArgs e) => _ = SwitchUsbIdentityAsync("ds5", "DualSense (054C:0CE6)", 0);
    private void OnUsbModeX360Click(object? s, RoutedEventArgs e) => _ = SwitchUsbIdentityAsync("x360", "Xbox 360 XInput (1209:DB05)", 1);

    private async Task SwitchUsbIdentityAsync(string cliMode, string label, byte targetModeId)
    {
        if (_switchingUsbMode) return;
        if (!_pico.IsConnected)
        {
            UsbModeStatus.Text = "Waiting for Pico USB connection first.";
            return;
        }

        _switchingUsbMode = true;
        BtnUsbDs5.IsEnabled = false;
        BtnUsbX360.IsEnabled = false;

        try
        {
            UsbModeStatus.Text = $"Switching USB personality to {label}...";
            StatusDot.Fill = DotAmber;
            StatusText.Text = "Rebooting Pico USB...";
            SubStatusText.Text = $"Waiting for {label} enumeration...";

            _pico.SetUsbMode(cliMode);
            await Task.Delay(120);
            _pico.Disconnect($"Rebooting into {label}...", disconnectController: false);
            await Task.Delay(900);

            for (int i = 0; i < 18; i++)
            {
                await Task.Delay(450);
                var ports = await Task.Run(() => _platform.DiscoverPicoComPorts());
                if (ports.Count > 0 && await _pico.ConnectAsync(ports[0], wakeController: false))
                {
                    UpdateUsbIdentityIndicators(targetModeId);
                    UsbModeStatus.Text = $"Active USB Personality: {label}";
                    return;
                }
            }
            UsbModeStatus.Text = $"Switched to {label} — re-attaching automatically when Windows finishes.";
        }
        finally
        {
            _switchingUsbMode = false;
            BtnUsbDs5.IsEnabled = true;
            BtnUsbX360.IsEnabled = true;
        }
    }

    private async void OnUpdateAppClick(object? sender, RoutedEventArgs e)
    {
        UpdaterStatusTxt.Text = "Downloading latest SensaDS5Control.exe from GitHub...";
        try
        {
            var exeBytes = await GitHubUpdater.DownloadLatestAppExeBytesAsync(_settings.GitHubToken, CancellationToken.None);
            if (exeBytes == null || exeBytes.Length < 100000)
            {
                const string errMsg = "No app build found (paste GitHub token for Private repo updates).";
                UpdaterStatusTxt.Text = errMsg;
                SubStatusText.Text = errMsg;
                return;
            }
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                UpdaterStatusTxt.Text = "Self-update only applies when running packaged SensaDS5Control.exe.";
                return;
            }
            var staged = currentExe + ".new";
            await File.WriteAllBytesAsync(staged, exeBytes);
            var bat = Path.Combine(Path.GetTempPath(), "sensads5_update.cmd");
            await File.WriteAllTextAsync(bat, $"@echo off\r\nping -n 2 127.0.0.1 >nul\r\nmove /y \"{staged}\" \"{currentExe}\"\r\nstart \"\" \"{currentExe}\"\r\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            Close();
        }
        catch (Exception ex)
        {
            UpdaterStatusTxt.Text = $"App update failed: {ex.Message}";
            SubStatusText.Text = UpdaterStatusTxt.Text;
        }
    }

    private void OnCmdStatusClick(object? s, RoutedEventArgs e) => _pico.SendCommand("status");
    private void OnCmdDbgClick(object? s, RoutedEventArgs e) => _pico.SendCommand("dbg");
    private void OnUnlock1Click(object? s, RoutedEventArgs e) => _pico.SendUnlock(1);
    private void OnUnlock2Click(object? s, RoutedEventArgs e) => _pico.SendUnlock(2);
    private void OnUnlock3Click(object? s, RoutedEventArgs e) => _pico.SendUnlock(3);
    private void OnUnlock4Click(object? s, RoutedEventArgs e) => _pico.SendUnlock(4);
    private void OnBtMonToggleClick(object? s, RoutedEventArgs e) => _pico.SetBtMon(BtMonToggle.IsChecked == true);

    private void OnSaveTokenClick(object? s, RoutedEventArgs e)
    {
        _settings.GitHubToken = TokenBox.Text ?? "";
        _settings.Save();
        UpdaterStatusTxt.Text = "GitHub token saved.";
    }

    private async void OnUpdateFirmwareClick(object? sender, RoutedEventArgs e)
    {
        UpdaterStatusTxt.Text = "Downloading latest SensaBridge.uf2 from GitHub...";
        try
        {
            var uf2 = await GitHubUpdater.DownloadLatestFirmwareBytesAsync(_settings.GitHubToken, CancellationToken.None);
            if (uf2 == null || uf2.Length < 4096)
            {
                const string errMsg = "No .uf2 found (if repo is Private, paste your GitHub token in Console & Updates).";
                UpdaterStatusTxt.Text = errMsg;
                SubStatusText.Text = errMsg;
                return;
            }
            UpdaterStatusTxt.Text = "Rebooting Pico into BOOTSEL and flashing...";
            SubStatusText.Text = UpdaterStatusTxt.Text;
            var msg = await _platform.FlashUf2ToPicoAsync(uf2, () => _pico.EnterBootsel(), CancellationToken.None);
            UpdaterStatusTxt.Text = msg;
            SubStatusText.Text = msg;
        }
        catch (Exception ex)
        {
            UpdaterStatusTxt.Text = $"Update failed: {ex.Message}";
            SubStatusText.Text = UpdaterStatusTxt.Text;
        }
    }

    private void OnSendCliClick(object? s, RoutedEventArgs e) => SendCliInput();

    private void OnCliKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendCliInput();
    }

    private void SendCliInput()
    {
        var txt = CliInputBox.Text?.Trim();
        if (string.IsNullOrEmpty(txt)) return;
        CliInputBox.Text = "";
        _pico.SendCommand(txt);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotplugTimer.Stop();
        _logFlushTimer.Stop();
        _mouse.ReleaseAllButtons(_platform);
        _pico.Dispose();
        base.OnClosed(e);
    }
}
