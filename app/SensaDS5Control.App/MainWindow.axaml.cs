using System.Text;
using Avalonia;
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

    private TrayIcon? _trayIcon;
    private readonly DispatcherTimer _profileTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private string? _lastForegroundProc;
    private bool _circTesting;
    private readonly double[] _circMaxL = new double[16];
    private readonly double[] _circMaxR = new double[16];
    private readonly bool[] _circHitL = new bool[16];
    private readonly bool[] _circHitR = new bool[16];
    private CancellationTokenSource? _fwActionCts;
    private bool _startMinimized;
    private bool _reallyClosing;
    private bool _flashingBusy;

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
    private string _picoFwVersion = "";
    private bool _hasBondedKey;
    private bool _justFlashedFw;

    private static readonly IBrush DotGreen = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush DotAmber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush DotGray = new SolidColorBrush(Color.Parse("#64748B"));

    public MainWindow() : this(AppSettings.Load(), false) { }

    public MainWindow(AppSettings settings, bool startMinimized = false)
    {
        _settings = settings;
        _startMinimized = startMinimized;
        InitializeComponent();
        PopulateStaticCombos();
        ApplySettingsToUi();
        _profileTimer.Tick += (_, _) => CheckForegroundProfileAutoSwitch();
        _profileTimer.Start();
        _ = CheckLatestAppReleaseAsync();
        _loadingUi = false;

        FwBundledBadgeTxt.Text = $"Bundled FW: v{BundledFirmware.Version}";

        InitializeTrayIcon();
        Opened += (_, _) =>
        {
            if (_startMinimized)
            {
                _startMinimized = false;
                Hide();
            }
        };

        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _logFlushTimer.Tick += (_, _) => FlushConsoleLogIfDirty();
        _logFlushTimer.Start();

        _hotplugTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hotplugTimer.Tick += (_, _) => _ = PollPicoUsbHotplugAsync();
        _hotplugTimer.Start();

        _pico.ConnectionChanged += OnPicoConnectionChanged;
        _pico.TelemetryReceived += OnTelemetryReceived;
        _pico.ConfigReceived += OnPicoConfigReceived;
        _pico.LogLineReceived += OnLogReceived;

        UpdateMicButtonText();
        _ = PollPicoUsbHotplugAsync();
    }

    private void InitializeTrayIcon()
    {
        var menu = new NativeMenu();

        var openItem = new NativeMenuItem("Open SensaDS5Control");
        openItem.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(openItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        var ds5Item = new NativeMenuItem("Switch to DualSense (054C:0CE6)");
        ds5Item.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = SwitchUsbIdentityAsync("ds5", "DualSense (054C:0CE6)", 0));
        menu.Items.Add(ds5Item);

        var x360Item = new NativeMenuItem("Switch to Xbox 360 XInput (1209:DB05)");
        x360Item.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = SwitchUsbIdentityAsync("x360", "Xbox 360 XInput (1209:DB05)", 1));
        menu.Items.Add(x360Item);
        menu.Items.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Quit SensaDS5Control");
        exitItem.Click += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _reallyClosing = true;
            Close();
        });
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = Icon,
            ToolTipText = "SensaDS5Control — Cosmic Byte Sensa",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => RestoreFromTray();

        if (Application.Current != null)
        {
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _trayIcon });
        }
    }

    private void RestoreFromTray()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.D &&
            e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            SetDeveloperMode(!_settings.DeveloperMode);
            e.Handled = true;
        }
    }

    private void OnDeveloperModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        SetDeveloperMode(DeveloperModeChk.IsChecked == true);
    }

    private void SetDeveloperMode(bool enabled)
    {
        _settings.DeveloperMode = enabled;
        _settings.Save();

        _loadingUi = true;
        DeveloperModeChk.IsChecked = enabled;
        _loadingUi = false;

        NavConsoleBtn.IsVisible = enabled;
        if (!enabled && _activeNavIndex == 6)
        {
            SelectNavPage(0);
        }
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int idx))
            SelectNavPage(idx);
    }

    private void SelectNavPage(int idx)
    {
        _activeNavIndex = idx;
        Control[] pages = [Page0, Page1, Page2, Page3, Page4, Page5, Page6];
        Button[] navBtns = [NavDashBtn, NavTesterBtn, NavHapticsBtn, NavTriggersBtn, NavLightingBtn, NavFirmwareBtn, NavConsoleBtn];
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

        GyroActivationCombo.ItemsSource = new[]
        {
            "Hold L2 to Aim",
            "Hold L1 to Aim",
            "Always Active",
        };
        StickCurveCombo.ItemsSource = new[]
        {
            "Linear (Standard 1:1)",
            "Precision (Fine Center)",
            "Aggressive (Fast Ramp)",
        };
        IdleSleepCombo.ItemsSource = new[]
        {
            "Off (Never Sleep)",
            "5 Minutes",
            "10 Minutes (Default)",
            "15 Minutes",
            "30 Minutes",
        };
        RefreshProfileCombo();
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
        GyroSensSlider.Value = _settings.GyroSens;
        GyroSensLabel.Text = $"{_settings.GyroSens:0.000}";
        GyroInvertYChk.IsChecked = _settings.GyroInvertY;
        GyroActivationCombo.SelectedIndex = Math.Clamp(_settings.GyroActivationMode, 0, 2);
        TouchTapChk.IsChecked = _settings.TouchpadTapToClick;
        _mouse.GyroInvertY = _settings.GyroInvertY;
        _mouse.GyroActivationMode = Math.Clamp(_settings.GyroActivationMode, 0, 2);
        _mouse.GyroOnlyOnL2 = _mouse.GyroActivationMode == 0;
        _mouse.TouchpadTapToClick = _settings.TouchpadTapToClick;
        StickCurveCombo.SelectedIndex = Math.Clamp(_settings.StickCurve, 0, 2);
        StickOuterDzSlider.Value = Math.Clamp(_settings.StickOuterDzPct, 0, 15);
        StickOuterDzLabel.Text = $"{Math.Clamp(_settings.StickOuterDzPct, 0, 15)}%";
        StickAntiDzSlider.Value = Math.Clamp(_settings.StickAntiDzPct, 0, 20);
        StickAntiDzLabel.Text = $"{Math.Clamp(_settings.StickAntiDzPct, 0, 20)}%";
        IdleSleepCombo.SelectedIndex = _settings.IdleSleepMin switch { 0 => 0, <= 5 => 1, <= 10 => 2, <= 15 => 3, _ => 4 };
        AutoSwitchProfileChk.IsChecked = _settings.AutoSwitchProfiles;

        StickDeadzoneSlider.Value = _settings.StickDeadzonePct;
        StickDeadzoneLabel.Text = $"{_settings.StickDeadzonePct}%";
        UpdatePollRateUi(_settings.UsbPollRateHz);

        HapticGainSlider.Value = _settings.HapticGainPct;
        HapticGainLabel.Text = $"{_settings.HapticGainPct}%";

        SpeakerVolSlider.Value = _settings.SpeakerVolumePct;
        SpeakerVolLabel.Text = $"{_settings.SpeakerVolumePct}%";
        SpeakerGainSlider.Value = _settings.SpeakerGain;
        SpeakerGainLabel.Text = $"{_settings.SpeakerGain} / 7";

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

        MinToTrayChk.IsChecked = _settings.MinimizeToTray;
        bool autostartNow = _platform.GetStartWithWindows();
        _settings.StartWithWindows = autostartNow;
        StartWithWinChk.IsChecked = autostartNow;

        DeveloperModeChk.IsChecked = _settings.DeveloperMode;
        NavConsoleBtn.IsVisible = _settings.DeveloperMode;

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

    private void OnAppBehaviorChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.MinimizeToTray = MinToTrayChk.IsChecked == true;
        bool wantStartup = StartWithWinChk.IsChecked == true;
        _settings.StartWithWindows = wantStartup;
        _platform.SetStartWithWindows(wantStartup, startMinimized: true);
        _settings.Save();
    }

    private async Task PollPicoUsbHotplugAsync()
    {
        if (_switchingUsbMode || _flashingBusy || Interlocked.CompareExchange(ref _hotplugBusy, 1, 0) != 0)
            return;

        try
        {
            var probe = await Task.Run(() => _platform.ProbePicoHardware());
            UpdatePicoProbeCard(probe);

            if (probe.State != PicoHardwareState.SensaBridgeActive || probe.ComPort == null)
            {
                if (_pico.IsConnected || _pico.ActivePortName != null)
                    _pico.NotifyUsbRemoved();
                return;
            }

            if (_pico.IsConnected)
            {
                if (_pico.ActivePortName != null &&
                    !string.Equals(_pico.ActivePortName, probe.ComPort, StringComparison.OrdinalIgnoreCase))
                {
                    _pico.NotifyUsbRemoved();
                }
                return;
            }

            await _pico.ConnectAsync(probe.ComPort, wakeController: false);
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _hotplugBusy, 0);
        }
    }

    private void UpdatePicoProbeCard(PicoHardwareProbe probe)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (probe.State)
            {
                case PicoHardwareState.BootselDriveReady:
                    PicoProbeStatePill.Text = "BOOTSEL Ready";
                    PicoProbeSummaryTxt.Text = $"Pico 2 W USB drive detected at {probe.BootselDrivePath} — click 'Install / Update Bundled Firmware' (or Nuke Flash) right now.";
                    break;
                case PicoHardwareState.GenericPicoCom:
                    PicoProbeStatePill.Text = $"Pico ({probe.ComPort})";
                    PicoProbeSummaryTxt.Text = $"Standard Raspberry Pi Pico USB serial found on {probe.ComPort} (not running SensaBridge yet). Click 'Install / Update Bundled Firmware' to flash it.";
                    break;
                case PicoHardwareState.SensaBridgeActive:
                    string ver = string.IsNullOrEmpty(_picoFwVersion) ? "" : $" v{_picoFwVersion}";
                    PicoProbeStatePill.Text = $"SensaBridge{ver}";
                    PicoProbeSummaryTxt.Text = $"SensaBridge Pico active on {probe.ComPort}. Ready for controller pairing, flash settings, 1-click update, or flash wipe.";
                    break;
                default:
                    PicoProbeStatePill.Text = "Not Found";
                    PicoProbeSummaryTxt.Text = "No Pico 2 W detected. Plug a configured Pico into USB, or hold the white BOOTSEL button while plugging in a fresh Pico.";
                    break;
            }
        });
    }

    private async void OnInstallBundledFirmwareClick(object? sender, RoutedEventArgs e)
    {
        var uf2 = BundledFirmware.LoadBridgeUf2();
        if (uf2 == null || uf2.Length < 4096)
        {
            FwActionStatusTxt.Text = "Bundled SensaBridge.uf2 is missing in this dev build (run via CI Release build, or place SensaBridge.uf2 in BundledFirmware/).";
            return;
        }

        await RunFirmwareFlashOperationAsync(
            uf2,
            "SensaBridge.uf2",
            isNuke: false,
            successMessage: $"SensaBridge v{BundledFirmware.Version} installed! Checking controller pairing...");
    }

    private void OnNukePromptClick(object? sender, RoutedEventArgs e)
    {
        NukeConfirmBox.IsVisible = true;
    }

    private void OnCancelNukeClick(object? sender, RoutedEventArgs e)
    {
        NukeConfirmBox.IsVisible = false;
    }

    private async void OnConfirmNukeClick(object? sender, RoutedEventArgs e)
    {
        NukeConfirmBox.IsVisible = false;
        var nukeUf2 = BundledFirmware.LoadNukeUf2();
        if (nukeUf2 == null || nukeUf2.Length < 1024)
        {
            FwActionStatusTxt.Text = "Bundled flash_nuke.uf2 is missing in this dev build.";
            return;
        }

        await RunFirmwareFlashOperationAsync(
            nukeUf2,
            "flash_nuke.uf2",
            isNuke: true,
            successMessage: "Pico flash wiped clean! Your Pico 2 W has rebooted into blank RP2350 BOOTSEL drive mode.");
    }

    private void OnCancelFwOpClick(object? sender, RoutedEventArgs e)
    {
        _fwActionCts?.Cancel();
    }

    private async Task RunFirmwareFlashOperationAsync(byte[] uf2Bytes, string fileName, bool isNuke, string successMessage)
    {
        if (_flashingBusy) return;
        _flashingBusy = true;
        NukeConfirmBox.IsVisible = false;
        BtnInstallBundledFw.IsEnabled = false;
        BtnNukePico.IsEnabled = false;
        BtnCancelFwOp.IsVisible = true;
        _fwActionCts = new CancellationTokenSource();
        var progress = new UiStatusProgress(msg => FwActionStatusTxt.Text = msg);

        try
        {
            Action? sendBootsel = _pico.IsConnected ? (() => _pico.EnterBootsel()) : null;

            string result = await _platform.FlashUf2ToPicoAsync(
                uf2Bytes, fileName, sendBootsel, progress, _fwActionCts.Token);

            _pico.Disconnect("Pico rebooting...", disconnectController: false);
            progress.Complete($"{result} {successMessage}");
            SubStatusText.Text = successMessage;

            if (!isNuke)
            {
                _justFlashedFw = true;
                PairingLiveStateTxt.Text = "Rebooting Pico & checking bond...";
                await Task.Delay(1800, _fwActionCts.Token);
            }
            else
            {
                _justFlashedFw = false;
                _hasBondedKey = false;
                PairingLiveStateTxt.Text = "Pico wiped (RP2350 drive)";
            }
        }
        catch (OperationCanceledException)
        {
            progress.Complete("Firmware operation canceled.");
        }
        catch (Exception ex)
        {
            progress.Complete($"Operation failed: {ex.Message}");
        }
        finally
        {
            _flashingBusy = false;
            BtnInstallBundledFw.IsEnabled = true;
            BtnNukePico.IsEnabled = true;
            BtnCancelFwOp.IsVisible = false;
            _fwActionCts?.Dispose();
            _fwActionCts = null;
            _ = PollPicoUsbHotplugAsync();
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_switchingUsbMode || _flashingBusy) return;

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
            SubStatusText.Text = "Plug Pico 2 W into USB (or use Firmware & Pico tab)";
            StatusDot.Fill = DotGray;
        }
    }

    private void OnPairNewClick(object? sender, RoutedEventArgs e)
    {
        if (!_pico.IsConnected)
        {
            FwActionStatusTxt.Text = "Connect a SensaBridge-flashed Pico first before pairing.";
            return;
        }
        _pico.StartPairing();
        StatusDot.Fill = DotAmber;
        StatusText.Text = "Pairing Mode Active";
        SubStatusText.Text = "Hold Share + PS on Sensa controller for 3 seconds...";
        PairingLiveStateTxt.Text = "Scanning (45s)... Hold Share + PS";
    }

    private void OnForgetBondClick(object? sender, RoutedEventArgs e)
    {
        if (!_pico.IsConnected)
        {
            FwActionStatusTxt.Text = "Connect a SensaBridge-flashed Pico first.";
            return;
        }
        _pico.ForgetAllBonds();
        StatusDot.Fill = DotAmber;
        StatusText.Text = "Cleared Bonds — Pairing...";
        SubStatusText.Text = "Hold Share + PS on Sensa controller to re-pair...";
        PairingLiveStateTxt.Text = "Bonds cleared — Hold Share + PS";
    }

    private void OnSaveFlashClick(object? sender, RoutedEventArgs e)
    {
        if (!_pico.IsConnected) return;
        PushSavedProfileToPico();
        _pico.SaveConfigToFlash();
        FlashSyncBadgeTxt.Text = "Pico Flash: Saved Standalone Profile";
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
                SubStatusText.Text = "Syncing standalone Pico Flash profile...";
                PortBadgeTxt.Text = $"Port: {portOrError}";
                FlashSyncBadgeTxt.Text = "Pico Flash: Syncing...";
                PairingLiveStateTxt.Text = "Waiting for controller...";
            }
            else
            {
                _mouse.ReleaseAllButtons(_platform);
                _wasBtControllerReady = false;
                _picoFwVersion = "";
                SetConnectButton(isConnectedController: false, connectedToPico: false);
                StatusDot.Fill = DotGray;
                StatusText.Text = _switchingUsbMode ? "Rebooting Pico USB..." : "Disconnected";
                SubStatusText.Text = string.IsNullOrEmpty(portOrError) ? "Waiting for Pico USB..." : portOrError;
                PortBadgeTxt.Text = "USB: Offline";
                BatteryBadgeTxt.Text = "Bat: --";
                BatteryBadgeTxt.Foreground = Brush.Parse("#94A3B8");
                DashBridgeInfo.Text = "Bridge: Disconnected";
                DashBatteryInfo.Text = "Battery: --";
                DashReportMode.Text = "Input Mode: --";
                FlashSyncBadgeTxt.Text = "Pico Flash: Offline (standalone active on Pico)";
                PairingLiveStateTxt.Text = "Pico Offline";
                TesterRateTxt.Text = "Link Rate: BT Rx -- Hz | USB Target -- Hz";
                SpeakerStreamBadgeTxt.Text = "USB Audio Stream: Idle";
                if (_trayIcon != null) _trayIcon.ToolTipText = "SensaDS5Control — Pico Offline";
                ResetTelemetryMeters();
                DashControllerDiagram.ResetDisconnected();
                TesterControllerDiagram.ResetDisconnected();
            }
        });
    }

    private void OnPicoConfigReceived(PicoConfigSample cfg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _picoFwVersion = cfg.FirmwareVersion;
            _hasBondedKey = cfg.HasBondedKey;

            if (_justFlashedFw)
            {
                _justFlashedFw = false;
                if (cfg.HasBondedKey)
                {
                    FwActionStatusTxt.Text = $"SensaBridge v{cfg.FirmwareVersion} installed! Existing controller pairing preserved — press PS on your Sensa to connect.";
                    PairingLiveStateTxt.Text = "Already Paired — Press PS to Wake";
                }
                else
                {
                    FwActionStatusTxt.Text = $"SensaBridge v{cfg.FirmwareVersion} installed! No controller paired yet — hold Share + PS on your Sensa for 3s to pair.";
                    PairingLiveStateTxt.Text = "Not Paired — Hold Share + PS";
                }
            }
            else if (!_wasBtControllerReady)
            {
                PairingLiveStateTxt.Text = cfg.HasBondedKey ? "Bonded — Press PS on Sensa" : "Not Paired — Hold Share + PS";
            }

            if (cfg.IsFreshFlash)
            {
                PushSavedProfileToPico();
                _pico.SaveConfigToFlash();
                FlashSyncBadgeTxt.Text = "Pico Flash: Synced Initial Profile";
                return;
            }

            _loadingUi = true;
            try
            {
                _settings.HapticGainPct = Math.Clamp((int)cfg.HapticGainPct, 25, 255);
                HapticGainSlider.Value = _settings.HapticGainPct;
                HapticGainLabel.Text = $"{_settings.HapticGainPct}%";

                _settings.ImpulseTriggerMode = Math.Clamp((int)cfg.ImpulseMode, 0, 2);
                _settings.ImpulseTriggerStrengthPct = Math.Clamp((int)cfg.ImpulseStrengthPct, 0, 100);
                _settings.IdleSleepMin = cfg.IdleSleepMin;
                _settings.StickCurve = cfg.StickCurve;
                _settings.StickOuterDzPct = cfg.StickOuterDzPct;
                _settings.StickAntiDzPct = cfg.StickAntiDzPct;
                _settings.Save();
                IdleSleepCombo.SelectedIndex = _settings.IdleSleepMin switch { 0 => 0, <= 5 => 1, <= 10 => 2, <= 15 => 3, _ => 4 };
                StickCurveCombo.SelectedIndex = Math.Clamp(_settings.StickCurve, 0, 2);
                StickOuterDzSlider.Value = Math.Clamp(_settings.StickOuterDzPct, 0, 15);
                StickOuterDzLabel.Text = $"{Math.Clamp(_settings.StickOuterDzPct, 0, 15)}%";
                StickAntiDzSlider.Value = Math.Clamp(_settings.StickAntiDzPct, 0, 20);
                StickAntiDzLabel.Text = $"{Math.Clamp(_settings.StickAntiDzPct, 0, 20)}%";
                ImpulseModeCombo.SelectedIndex = _settings.ImpulseTriggerMode;
                ImpulseStrengthSlider.Value = _settings.ImpulseTriggerStrengthPct;
                ImpulseStrengthLabel.Text = $"{_settings.ImpulseTriggerStrengthPct}%";

                _settings.StickDeadzonePct = Math.Clamp((int)cfg.StickDeadzonePct, 0, 25);
                StickDeadzoneSlider.Value = _settings.StickDeadzonePct;
                StickDeadzoneLabel.Text = $"{_settings.StickDeadzonePct}%";

                _settings.UsbPollRateHz = NormalizePollRate(cfg.UsbPollRateHz);
                UpdatePollRateUi(_settings.UsbPollRateHz);

                _settings.SpeakerVolumePct = Math.Clamp(cfg.SpeakerVolumePct, 0, 100);
                SpeakerVolSlider.Value = _settings.SpeakerVolumePct;
                SpeakerVolLabel.Text = $"{_settings.SpeakerVolumePct}%";
                _settings.SpeakerGain = Math.Clamp(cfg.SpeakerGain, 1, 7);
                SpeakerGainSlider.Value = _settings.SpeakerGain;
                SpeakerGainLabel.Text = $"{_settings.SpeakerGain} / 7";

                _settings.LedOverride = cfg.LedOverride;
                _settings.LedR = cfg.LedR;
                _settings.LedG = cfg.LedG;
                _settings.LedB = cfg.LedB;
                LedRSlider.Value = cfg.LedR;
                LedGSlider.Value = cfg.LedG;
                LedBSlider.Value = cfg.LedB;
                UpdateLedPreview();

                _settings.MicLedMode = (byte)Math.Clamp((int)cfg.MicLedMode, 0, 2);
                _settings.Save();
                UpdateUsbIdentityIndicators(cfg.UsbMode);
                FlashSyncBadgeTxt.Text = $"Pico Flash: Loaded Standalone (v{cfg.FirmwareVersion})";
            }
            finally
            {
                _loadingUi = false;
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
        _pico.SetStickDeadzone(_settings.StickDeadzonePct);
        _pico.SetStickTuning(_settings.StickCurve, _settings.StickOuterDzPct, _settings.StickAntiDzPct);
        _pico.SetIdleSleep(_settings.IdleSleepMin);
        _pico.SetPollRate(_settings.UsbPollRateHz);
        _pico.SetSpeakerVolume(_settings.SpeakerVolumePct);
        _pico.SetSpeakerGain(_settings.SpeakerGain);
        _pico.SetMicLed(_settings.MicLedMode);
        if (_settings.LedOverride) _pico.SetLed(_settings.LedR, _settings.LedG, _settings.LedB);
        else _pico.SetLedAuto();
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
            _mouse.Process(s, _platform);
        else
            _mouse.ReleaseAllButtons(_platform);

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
            bool lowBat = btNow && s.FullMode0x31 && s.ChargingState == 0 && s.BatteryPct <= 10;
            BatteryBadgeTxt.Text = btNow ? (s.FullMode0x31 ? (lowBat ? $"Bat: {s.BatteryPct}% LOW!" : $"Bat: {s.BatteryPct}%{chgTag}") : "Short 0x01") : "BT Idle";
            BatteryBadgeTxt.Foreground = lowBat ? Brush.Parse("#F87171") : Brush.Parse("#CBD5E1");
            if (_trayIcon != null)
            {
                _trayIcon.ToolTipText = btNow && s.FullMode0x31
                    ? $"SensaDS5Control — {usbShort} • Bat {s.BatteryPct}%{(lowBat ? " (LOW!)" : chgTag)}"
                    : $"SensaDS5Control — {usbShort} • {(btNow ? "Active" : "BT Idle")}";
            }
            SpeakerStreamBadgeTxt.Text = s.UsbSpeakerStreaming ? "USB Audio Stream: Active" : "USB Audio Stream: Idle";
            SpeakerStreamBadgeTxt.Foreground = s.UsbSpeakerStreaming ? Brush.Parse("#38BDF8") : Brush.Parse("#8896AE");
            if (btNow)
            {
                PairingLiveStateTxt.Text = "✓ Sensa Paired & Connected";
            }
            else if (!PairingLiveStateTxt.Text.StartsWith("Scanning", StringComparison.Ordinal))
            {
                PairingLiveStateTxt.Text = _hasBondedKey ? "Bonded — Press PS on Sensa" : "Not Paired — Hold Share + PS";
            }

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
        TesterRateTxt.Text = $"Link Rate: BT Rx {s.BtRxHz} Hz | USB Target {_settings.UsbPollRateHz} Hz";
        RecordCircularitySample(s);

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
        _mouse.GyroActivationMode = Math.Clamp(GyroActivationCombo.SelectedIndex, 0, 2);
        _mouse.GyroOnlyOnL2 = _mouse.GyroActivationMode == 0;
        _mouse.GyroInvertY = GyroInvertYChk.IsChecked == true;
        _mouse.TouchpadTapToClick = TouchTapChk.IsChecked == true;
        _settings.GyroActivationMode = _mouse.GyroActivationMode;
        _settings.GyroOnlyOnL2 = _mouse.GyroOnlyOnL2;
        _settings.GyroInvertY = _mouse.GyroInvertY;
        _settings.TouchpadTapToClick = _mouse.TouchpadTapToClick;
        _mouse.GyroSensitivity = GyroSensSlider.Value;
        GyroSensLabel.Text = $"{GyroSensSlider.Value:0.000}";

        _settings.TouchpadMouse = tm;
        _settings.TouchpadSens = TouchSensSlider.Value;
        _settings.GyroMouse = _mouse.GyroMouseEnabled;
        _settings.GyroOnlyOnL2 = _mouse.GyroOnlyOnL2;
        _settings.GyroSens = GyroSensSlider.Value;
        _settings.Save();
    }

    private void OnDeadzoneChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingUi) return;
        int pct = Math.Clamp((int)Math.Round(StickDeadzoneSlider.Value), 0, 25);
        StickDeadzoneLabel.Text = $"{pct}%";
        _settings.StickDeadzonePct = pct;
        _settings.Save();
        _pico.SetStickDeadzone(pct);
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void OnPollRate250Click(object? sender, RoutedEventArgs e) => SetPollRate(250);
    private void OnPollRate500Click(object? sender, RoutedEventArgs e) => SetPollRate(500);
    private void OnPollRate1000Click(object? sender, RoutedEventArgs e) => SetPollRate(1000);

    private void SetPollRate(int hz)
    {
        hz = NormalizePollRate(hz);
        _settings.UsbPollRateHz = hz;
        _settings.Save();
        UpdatePollRateUi(hz);
        _pico.SetPollRate(hz);
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void UpdatePollRateUi(int hz)
    {
        hz = NormalizePollRate(hz);
        BtnPoll250.Classes.Remove("accent");
        BtnPoll500.Classes.Remove("accent");
        BtnPoll1000.Classes.Remove("accent");
        if (hz == 250) BtnPoll250.Classes.Add("accent");
        else if (hz == 500) BtnPoll500.Classes.Add("accent");
        else BtnPoll1000.Classes.Add("accent");

        PollRateBadgeTxt.Text = hz switch
        {
            250 => "250 Hz (4 ms)",
            500 => "500 Hz (2 ms)",
            _ => "1000 Hz (1 ms)"
        };
    }

    private static int NormalizePollRate(int hz)
        => hz <= 375 ? 250 : hz <= 750 ? 500 : 1000;

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
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void OnHapticGainChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingUi) return;
        int val = (int)Math.Round(HapticGainSlider.Value);
        HapticGainLabel.Text = $"{val}%";
        _settings.HapticGainPct = val;
        _settings.Save();
        _pico.SetHapticGain(val);
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void OnSpeakerVolChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingUi) return;
        int val = Math.Clamp((int)Math.Round(SpeakerVolSlider.Value), 0, 100);
        SpeakerVolLabel.Text = $"{val}%";
        _settings.SpeakerVolumePct = val;
        _settings.Save();
        _pico.SetSpeakerVolume(val);
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void OnSpeakerGainChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingUi) return;
        int val = Math.Clamp((int)Math.Round(SpeakerGainSlider.Value), 1, 7);
        SpeakerGainLabel.Text = $"{val} / 7";
        _settings.SpeakerGain = val;
        _settings.Save();
        _pico.SetSpeakerGain(val);
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void OnTestSpeakerClick(object? s, RoutedEventArgs e) => _pico.TriggerTestSpeaker(900);

    private void OnGyroActivationChanged(object? sender, SelectionChangedEventArgs e) => OnMouseSettingsChanged(sender, e);

    private void OnStickTuningChanged(object? sender, SelectionChangedEventArgs e) => ApplyStickTuning();
    private void OnStickTuningSliderChanged(object? sender, RangeBaseValueChangedEventArgs e) => ApplyStickTuning();

    private void ApplyStickTuning()
    {
        if (_loadingUi) return;
        int curve = Math.Clamp(StickCurveCombo.SelectedIndex, 0, 2);
        int outer = Math.Clamp((int)Math.Round(StickOuterDzSlider.Value), 0, 15);
        int anti = Math.Clamp((int)Math.Round(StickAntiDzSlider.Value), 0, 20);
        StickOuterDzLabel.Text = $"{outer}%";
        StickAntiDzLabel.Text = $"{anti}%";
        _settings.StickCurve = curve;
        _settings.StickOuterDzPct = outer;
        _settings.StickAntiDzPct = anti;
        _settings.Save();
        _pico.SetStickTuning(curve, outer, anti);
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void OnIdleSleepChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        int mins = IdleSleepCombo.SelectedIndex switch { 0 => 0, 1 => 5, 2 => 10, 3 => 15, _ => 30 };
        _settings.IdleSleepMin = mins;
        _settings.Save();
        _pico.SetIdleSleep(mins);
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void OnOpenSoundSettingsClick(object? sender, RoutedEventArgs e) => _platform.OpenSoundSettings();

    private void RefreshProfileCombo()
    {
        _settings.Profiles ??= [];
        bool prev = _loadingUi;
        _loadingUi = true;
        var items = new List<string> { "(Custom / Unlinked)" };
        foreach (var p in _settings.Profiles) items.Add(p.Name);
        ProfileCombo.ItemsSource = items;
        int sel = 0;
        if (!string.IsNullOrWhiteSpace(_settings.ActiveProfileName))
        {
            int idx = _settings.Profiles.FindIndex(p => string.Equals(p.Name, _settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) sel = idx + 1;
        }
        ProfileCombo.SelectedIndex = sel;
        if (sel > 0)
        {
            var prof = _settings.Profiles[sel - 1];
            ProfileNameBox.Text = prof.Name;
            ProfileExeBox.Text = prof.ExeName;
        }
        _loadingUi = prev;
    }

    private void ApplySelectedProfile(ControllerProfile profile)
    {
        profile.ApplyTo(_settings);
        _settings.Save();
        ApplySettingsToUi();
        if (_pico.IsConnected)
        {
            PushSavedProfileToPico();
            _pico.SaveConfigToFlash();
            FlashSyncBadgeTxt.Text = $"Pico Flash: Active Profile '{profile.Name}'";
        }
    }

    private void OnProfileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.Profiles ??= [];
        int idx = ProfileCombo.SelectedIndex - 1;
        if (idx >= 0 && idx < _settings.Profiles.Count)
        {
            var prof = _settings.Profiles[idx];
            ProfileNameBox.Text = prof.Name;
            ProfileExeBox.Text = prof.ExeName;
            ApplySelectedProfile(prof);
        }
    }

    private void OnSaveProfileClick(object? sender, RoutedEventArgs e)
    {
        _settings.Profiles ??= [];
        string name = (ProfileNameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            name = $"Profile {_settings.Profiles.Count + 1}";
        string exe = (ProfileExeBox.Text ?? "").Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

        var captured = ControllerProfile.CaptureFrom(name, exe, _settings);
        int existing = _settings.Profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _settings.Profiles[existing] = captured;
        else _settings.Profiles.Add(captured);

        _settings.ActiveProfileName = name;
        _settings.Save();
        RefreshProfileCombo();
    }

    private void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        _settings.Profiles ??= [];
        int idx = ProfileCombo.SelectedIndex - 1;
        if (idx >= 0 && idx < _settings.Profiles.Count)
        {
            _settings.Profiles.RemoveAt(idx);
            _settings.ActiveProfileName = "Default";
            ProfileNameBox.Text = "";
            ProfileExeBox.Text = "";
            _settings.Save();
            RefreshProfileCombo();
        }
    }

    private void OnAutoSwitchProfileChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.AutoSwitchProfiles = AutoSwitchProfileChk.IsChecked == true;
        _settings.Save();
    }

    private void CheckForegroundProfileAutoSwitch()
    {
        if (!_settings.AutoSwitchProfiles || _loadingUi) return;
        _settings.Profiles ??= [];
        if (_settings.Profiles.Count == 0) return;

        string? proc = _platform.GetForegroundProcessName();
        if (string.IsNullOrWhiteSpace(proc) ||
            string.Equals(proc, "SensaDS5Control", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, _lastForegroundProc, StringComparison.OrdinalIgnoreCase))
            return;

        _lastForegroundProc = proc;
        var match = _settings.Profiles.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.ExeName) &&
            string.Equals(p.ExeName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase), proc, StringComparison.OrdinalIgnoreCase));

        if (match != null && !string.Equals(_settings.ActiveProfileName, match.Name, StringComparison.OrdinalIgnoreCase))
        {
            ApplySelectedProfile(match);
            RefreshProfileCombo();
        }
    }

    private void OnCircTestToggleClick(object? sender, RoutedEventArgs e)
    {
        _circTesting = !_circTesting;
        BtnCircStart.Content = _circTesting ? "Stop Test" : "Start Test";
        if (_circTesting && !CircLResultTxt.Text.Contains("/16", StringComparison.Ordinal))
            ResetCircularityBuffers();
    }

    private void OnCircResetClick(object? sender, RoutedEventArgs e)
    {
        ResetCircularityBuffers();
    }

    private void ResetCircularityBuffers()
    {
        Array.Clear(_circMaxL);
        Array.Clear(_circMaxR);
        Array.Clear(_circHitL);
        Array.Clear(_circHitR);
        CircLResultTxt.Text = "Left Stick Avg Error: -- (0/16 sectors)";
        CircRResultTxt.Text = "Right Stick Avg Error: -- (0/16 sectors)";
    }

    private void RecordCircularitySample(TelemetrySample s)
    {
        if (!_circTesting) return;
        UpdateCircularityAxis(s.Lx, s.Ly, _circMaxL, _circHitL, "Left", CircLResultTxt);
        UpdateCircularityAxis(s.Rx, s.Ry, _circMaxR, _circHitR, "Right", CircRResultTxt);
    }

    private static void UpdateCircularityAxis(byte rawX, byte rawY, double[] maxR, bool[] hit, string label, TextBlock txt)
    {
        double nx = (rawX - 127.5) / 127.5;
        double ny = (rawY - 127.5) / 127.5;
        double r = Math.Sqrt(nx * nx + ny * ny);
        if (r >= 0.65)
        {
            double ang = Math.Atan2(ny, nx);
            if (ang < 0) ang += Math.PI * 2.0;
            int sec = Math.Clamp((int)(ang / (Math.PI * 2.0) * 16.0), 0, 15);
            hit[sec] = true;
            if (r > maxR[sec]) maxR[sec] = r;
        }
        int hits = 0;
        double sumErr = 0;
        for (int i = 0; i < 16; i++)
        {
            if (hit[i])
            {
                hits++;
                sumErr += Math.Abs(maxR[i] - 1.0) * 100.0;
            }
        }
        txt.Text = hits > 0
            ? $"{label} Stick Avg Error: {(sumErr / hits):0.0}% ({hits}/16 sectors)"
            : $"{label} Stick Avg Error: -- (0/16 sectors)";
    }

    private async Task CheckLatestAppReleaseAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SensaDS5Control");
            using var resp = await http.GetAsync("https://api.github.com/repos/Yamerooo-hd/SensaDS5Control/releases/latest");
            if (!resp.IsSuccessStatusCode)
            {
                await Dispatcher.UIThread.InvokeAsync(() => AppReleaseBadgeTxt.Text = $"v{BundledFirmware.Version} • Ready");
                return;
            }
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
            var tag = (tagEl.GetString() ?? "").Trim();
            var remoteStr = tag.TrimStart('v', 'V');
            var localStr = BundledFirmware.Version.TrimStart('v', 'V');
            bool newer = Version.TryParse(remoteStr, out var rv) && Version.TryParse(localStr, out var lv) && rv > lv;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AppReleaseBadgeTxt.Text = newer ? $"Update {tag} Available" : $"v{localStr} • Latest";
                AppReleaseBadgeTxt.Foreground = newer ? Brush.Parse("#F59E0B") : Brush.Parse("#38BDF8");
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => AppReleaseBadgeTxt.Text = $"v{BundledFirmware.Version}");
        }
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
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
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
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
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
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
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
        FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved";
    }

    private void OnApplyLedClick(object? s, RoutedEventArgs e) => ApplyLed((byte)LedRSlider.Value, (byte)LedGSlider.Value, (byte)LedBSlider.Value);
    private void OnAutoLedClick(object? s, RoutedEventArgs e) { _settings.LedOverride = false; _settings.Save(); _pico.SetLedAuto(); FlashSyncBadgeTxt.Text = "Pico Flash: Auto-Saved"; }
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
        if (_switchingUsbMode || _flashingBusy) return;
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
            var staged = Path.Combine(Path.GetTempPath(), "SensaDS5Control.exe.new");
            await File.WriteAllBytesAsync(staged, exeBytes);
            var bat = Path.Combine(Path.GetTempPath(), "sensads5_update.cmd");
            await File.WriteAllTextAsync(bat, $"@echo off\r\nping -n 2 127.0.0.1 >nul\r\nmove /y \"{staged}\" \"{currentExe}\"\r\nstart \"\" \"{currentExe}\"\r\n");
            bool needsAdmin = currentExe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                CreateNoWindow = !needsAdmin,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                UseShellExecute = needsAdmin,
                Verb = needsAdmin ? "runas" : ""
            });
            _reallyClosing = true;
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
    private void OnCmdGetCfgClick(object? s, RoutedEventArgs e) => _pico.RequestConfig();
    private void OnBtMonToggleClick(object? s, RoutedEventArgs e) => _pico.SetBtMon(BtMonToggle.IsChecked == true);

    private void OnSaveTokenClick(object? s, RoutedEventArgs e)
    {
        _settings.GitHubToken = TokenBox.Text ?? "";
        _settings.Save();
        UpdaterStatusTxt.Text = "GitHub token saved.";
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_reallyClosing && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
        }
        _fwActionCts?.Cancel();
        _hotplugTimer.Stop();
        _logFlushTimer.Stop();
        _profileTimer.Stop();
        _mouse.ReleaseAllButtons(_platform);
        _pico.Dispose();
        base.OnClosed(e);
    }

    private sealed class UiStatusProgress : IProgress<string>
    {
        private readonly Action<string> _apply;
        private volatile bool _active = true;

        public UiStatusProgress(Action<string> apply)
        {
            _apply = apply;
        }

        public void Complete(string finalMessage)
        {
            _active = false;
            if (Dispatcher.UIThread.CheckAccess())
                _apply(finalMessage);
            else
                Dispatcher.UIThread.Post(() => _apply(finalMessage));
        }

        public void Report(string value)
        {
            if (!_active) return;
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (_active) _apply(value);
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_active) _apply(value);
                });
            }
        }
    }
}
