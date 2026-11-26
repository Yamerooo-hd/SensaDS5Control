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
    private volatile bool _logDirty;
    private int _uiTelemetryPending;
    private readonly DispatcherTimer _logFlushTimer;
    private bool _wasMicBtn;
    private volatile bool _syncMicButton = true;
    private bool _loadingUi = true;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        PopulateTriggerCombos();
        ApplySettingsToUi();
        _loadingUi = false;

        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _logFlushTimer.Tick += (_, _) => FlushConsoleLogIfDirty();
        _logFlushTimer.Start();

        _pico.ConnectionChanged += OnPicoConnectionChanged;
        _pico.TelemetryReceived += OnTelemetryReceived;
        _pico.LogLineReceived += OnLogReceived;

        RefreshPorts();
        UpdateMicButtonText();
        AutoConnectFirstPort();
    }

    private void PopulateTriggerCombos()
    {
        var items = new[]
        {
            "Follow Game (Auto)",
            "Off",
            "Continuous Resistance",
            "Section / Gun Breakpoint",
            "Weapon (Gun Snap)",
            "Vibration (Rapid Fire)",
            "Bow (Progressive Tension)"
        };
        LeftTrigCombo.ItemsSource = items;
        RightTrigCombo.ItemsSource = items;

        ImpulseModeCombo.ItemsSource = new[]
        {
            "Off (Normal Triggers)",
            "Always (On Any Rumble)",
            "Only While Trigger Pulled"
        };
    }

    private void ApplySettingsToUi()
    {
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

        SyncMicBtnChk.IsChecked = _settings.SyncControllerMicButton;
        _syncMicButton = _settings.SyncControllerMicButton;
        TokenBox.Text = _settings.GitHubToken;

        _mouse.TouchpadMouseEnabled = _settings.TouchpadMouse;
        _mouse.TouchpadSensitivity = _settings.TouchpadSens;
        _mouse.GyroMouseEnabled = _settings.GyroMouse;
        _mouse.GyroOnlyOnL2 = _settings.GyroOnlyOnL2;
        _mouse.GyroSensitivity = _settings.GyroSens;
    }

    private void RefreshPorts()
    {
        var ports = _platform.DiscoverPicoComPorts();
        PortCombo.ItemsSource = ports;
        if (ports.Count > 0) PortCombo.SelectedIndex = 0;
    }

    private void AutoConnectFirstPort()
    {
        if (PortCombo.SelectedItem is string p)
        {
            _pico.Connect(p);
        }
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_pico.IsConnected)
        {
            _pico.Disconnect();
            return;
        }
        RefreshPorts();
        if (PortCombo.SelectedItem is string p)
        {
            _pico.Connect(p);
        }
    }

    private void OnPicoConnectionChanged(bool connected, string? portOrError)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectBtn.Content = connected ? "Disconnect" : "Connect";
            StatusText.Text = connected ? $"Connected on {portOrError}" : "Disconnected";
            SubStatusText.Text = connected
                ? "Streaming 30 Hz live telemetry"
                : (string.IsNullOrEmpty(portOrError) ? "Plug Pico 2 W into USB and click Connect" : portOrError);
            if (connected) PushSavedProfileToPico();
        });
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

    private void OnTelemetryReceived(TelemetrySample s)
    {
        _mouse.Process(s, _platform);

        bool micPressedEdge = _syncMicButton && s.MicMuteBtn && !_wasMicBtn;
        _wasMicBtn = s.MicMuteBtn;

        if (!micPressedEdge && Interlocked.Exchange(ref _uiTelemetryPending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _uiTelemetryPending, 0);
            if (micPressedEdge)
            {
                bool nextMute = !_platform.GetSystemMicMuted();
                _platform.SetSystemMicMuted(nextMute);
                _settings.MicLedMode = (byte)(nextMute ? 1 : 0);
                _settings.Save();
                _pico.SetMicLed(_settings.MicLedMode);
                UpdateMicButtonText();
            }
            DashBridgeInfo.Text = $"Bridge: Mode {s.Mode} | 0x36 tx={s.Tx0x36} | Haptic Gain {s.HapticGainPct}%";
            DashBatteryInfo.Text = s.FullMode0x31 ? $"Battery: {s.BatteryPct}% (charging={s.ChargingState})" : "Battery: (waiting for 0x31 full mode)";
            DashReportMode.Text = s.FullMode0x31 ? "Input Mode: Full 0x31 (Gyro + Touchpad active)" : "Input Mode: Short 0x01 (Sticks/Buttons/Triggers)";
            UsbModeStatus.Text = s.UsbMode switch
            {
                1 => "Active USB Personality: Xbox 360 XInput Controller (1209:DB05)",
                _ => "Active USB Personality: DualSense Wireless Controller (054C:0CE6)"
            };
            SubStatusText.Text = $"{(s.FullMode0x31 ? "Full 0x31" : "Short 0x01")} | tx0x36={s.Tx0x36} | Peak L={s.PeakHapticL:0.0}% R={s.PeakHapticR:0.0}%";

            Canvas.SetLeft(LeftStickDot, (s.Lx / 255.0) * 104 + 2);
            Canvas.SetTop(LeftStickDot, (s.Ly / 255.0) * 104 + 2);
            LeftStickTxt.Text = $"{s.Lx}, {s.Ly}";

            Canvas.SetLeft(RightStickDot, (s.Rx / 255.0) * 104 + 2);
            Canvas.SetTop(RightStickDot, (s.Ry / 255.0) * 104 + 2);
            RightStickTxt.Text = $"{s.Rx}, {s.Ry}";

            L2Bar.Value = s.L2;
            R2Bar.Value = s.R2;

            HapticLBar.Value = Math.Clamp(s.PeakHapticL, 0, 100);
            HapticRBar.Value = Math.Clamp(s.PeakHapticR, 0, 100);

            var pressed = new List<string>();
            if (s.Cross) pressed.Add("Cross");
            if (s.Circle) pressed.Add("Circle");
            if (s.Square) pressed.Add("Square");
            if (s.Triangle) pressed.Add("Triangle");
            if (s.L1) pressed.Add("L1");
            if (s.R1) pressed.Add("R1");
            if (s.L3) pressed.Add("L3");
            if (s.R3) pressed.Add("R3");
            if (s.Create) pressed.Add("Create");
            if (s.Options) pressed.Add("Options");
            if (s.Ps) pressed.Add("PS");
            if (s.TouchpadClick) pressed.Add("TP-Click");
            if (s.MicMuteBtn) pressed.Add("Mic");
            if (s.DPad != 8) pressed.Add($"DPad({s.DPad})");
            ButtonsActiveTxt.Text = pressed.Count > 0 ? "Pressed: " + string.Join(", ", pressed) : "Pressed: (none)";
            RawBytesTxt.Text = $"Raw Btn Bytes: {s.Btn0:X2} {s.Btn1:X2} {s.Btn2:X2} {s.Btn3:X2}";

            GyroTxt.Text = $"Gyro (X, Y, Z): {s.GyroX}, {s.GyroY}, {s.GyroZ}";
            AccelTxt.Text = $"Accel (X, Y, Z): {s.AccelX}, {s.AccelY}, {s.AccelZ}";

            Touch0Dot.IsVisible = s.Touch0Down;
            if (s.Touch0Down)
            {
                Canvas.SetLeft(Touch0Dot, Math.Clamp(s.Touch0X / 1920.0 * 306, 0, 306));
                Canvas.SetTop(Touch0Dot, Math.Clamp(s.Touch0Y / 1080.0 * 126, 0, 126));
            }
            Touch1Dot.IsVisible = s.Touch1Down;
            if (s.Touch1Down)
            {
                Canvas.SetLeft(Touch1Dot, Math.Clamp(s.Touch1X / 1920.0 * 306, 0, 306));
                Canvas.SetTop(Touch1Dot, Math.Clamp(s.Touch1Y / 1080.0 * 126, 0, 126));
            }
            TouchTxt.Text = $"Touch 0: {(s.Touch0Down ? $"{s.Touch0X},{s.Touch0Y}" : "--")} | Touch 1: {(s.Touch1Down ? $"{s.Touch1X},{s.Touch1Y}" : "--")}";
        });
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

    private void OnMicLedOffClick(object? s, RoutedEventArgs e) { _settings.MicLedMode = 0; _settings.Save(); _pico.SetMicLed(0); }
    private void OnMicLedSolidClick(object? s, RoutedEventArgs e) { _settings.MicLedMode = 1; _settings.Save(); _pico.SetMicLed(1); }
    private void OnMicLedPulseClick(object? s, RoutedEventArgs e) { _settings.MicLedMode = 2; _settings.Save(); _pico.SetMicLed(2); }

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
    private void OnPresetCyanClick(object? s, RoutedEventArgs e) => ApplyLed(0, 180, 255);
    private void OnPresetRedClick(object? s, RoutedEventArgs e) => ApplyLed(255, 20, 20);
    private void OnPresetGreenClick(object? s, RoutedEventArgs e) => ApplyLed(0, 255, 60);
    private void OnPresetPurpleClick(object? s, RoutedEventArgs e) => ApplyLed(170, 0, 255);

    private void OnUsbModeDs5Click(object? s, RoutedEventArgs e) => _ = SwitchUsbIdentityAsync("ds5", "DualSense (054C:0CE6)");
    private void OnUsbModeX360Click(object? s, RoutedEventArgs e) => _ = SwitchUsbIdentityAsync("x360", "Xbox 360 XInput (1209:DB05)");

    private async Task SwitchUsbIdentityAsync(string cliMode, string label)
    {
        if (!_pico.IsConnected)
        {
            UsbModeStatus.Text = "Connect to Pico COM port first.";
            return;
        }
        UsbModeStatus.Text = $"Switching USB personality to {label}...";
        SubStatusText.Text = UsbModeStatus.Text;
        _pico.SetUsbMode(cliMode);
        await Task.Delay(250);
        _pico.Disconnect($"Rebooting into {label}...");
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(600);
            RefreshPorts();
            if (PortCombo.SelectedItem is string p && _pico.Connect(p))
            {
                UsbModeStatus.Text = $"Active USB Personality: {label}";
                return;
            }
        }
        UsbModeStatus.Text = $"Switched to {label} — click Connect if COM port changed.";
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
        _logFlushTimer.Stop();
        _pico.Dispose();
        base.OnClosed(e);
    }
}
