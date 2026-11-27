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
    private readonly DispatcherTimer _hotplugTimer;

    private TrayIcon? _trayIcon;
    private CancellationTokenSource? _fwActionCts;
    private bool _startMinimized;
    private bool _reallyClosing;
    private bool _flashingBusy;

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

        _hotplugTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hotplugTimer.Tick += (_, _) => _ = PollPicoUsbHotplugAsync();
        _hotplugTimer.Start();

        _pico.ConnectionChanged += OnPicoConnectionChanged;
        _pico.TelemetryReceived += OnTelemetryReceived;
        _pico.ConfigReceived += OnPicoConfigReceived;

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

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int idx))
            SelectNavPage(idx);
    }

    private void SelectNavPage(int idx)
    {
        _activeNavIndex = idx;
        Control[] pages = [Page0, Page1, Page2, Page3, Page4, Page5];
        Button[] navBtns = [NavDashBtn, NavTesterBtn, NavHapticsBtn, NavTriggersBtn, NavLightingBtn, NavFirmwareBtn];
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
        GyroSensSlider.Value = _settings.GyroSens;
        GyroSensLabel.Text = $"{_settings.GyroSens:0.000}";
        GyroL2Chk.IsChecked = _settings.GyroOnlyOnL2;

        StickDeadzoneSlider.Value = _settings.StickDeadzonePct;
        StickDeadzoneLabel.Text = $"{_settings.StickDeadzonePct}%";

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

        MinToTrayChk.IsChecked = _settings.MinimizeToTray;
        bool autostartNow = _platform.GetStartWithWindows();
        _settings.StartWithWindows = autostartNow;
        StartWithWinChk.IsChecked = autostartNow;

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
                DashBridgeInfo.Text = "Bridge: Disconnected";
                DashBatteryInfo.Text = "Battery: --";
                DashReportMode.Text = "Input Mode: --";
                FlashSyncBadgeTxt.Text = "Pico Flash: Offline (standalone active on Pico)";
                PairingLiveStateTxt.Text = "Pico Offline";
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
                ImpulseModeCombo.SelectedIndex = _settings.ImpulseTriggerMode;
                ImpulseStrengthSlider.Value = _settings.ImpulseTriggerStrengthPct;
                ImpulseStrengthLabel.Text = $"{_settings.ImpulseTriggerStrengthPct}%";

                _settings.StickDeadzonePct = Math.Clamp((int)cfg.StickDeadzonePct, 0, 25);
                StickDeadzoneSlider.Value = _settings.StickDeadzonePct;
                StickDeadzoneLabel.Text = $"{_settings.StickDeadzonePct}%";

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
            BatteryBadgeTxt.Text = btNow ? (s.FullMode0x31 ? $"Bat: {s.BatteryPct}%{chgTag}" : "Short 0x01") : "BT Idle";
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

    private async void OnUpdateFromReleaseClick(object? sender, RoutedEventArgs e)
    {
        BtnUpdateLatestRelease.IsEnabled = false;
        UpdaterStatusTxt.Text = "Checking latest GitHub release...";
        try
        {
            var (exeBytes, tagName, errorHint) = await GitHubUpdater.DownloadLatestReleaseExeBytesAsync(CancellationToken.None);
            if (exeBytes == null)
            {
                string msg = errorHint ?? "No release build available yet.";
                UpdaterStatusTxt.Text = msg;
                SubStatusText.Text = msg;
                return;
            }
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                UpdaterStatusTxt.Text = $"Latest release {tagName} ready — self-update applies to packaged SensaDS5Control.exe.";
                return;
            }
            UpdaterStatusTxt.Text = $"Installing release {tagName}...";
            var staged = currentExe + ".new";
            await File.WriteAllBytesAsync(staged, exeBytes);
            var bat = Path.Combine(Path.GetTempPath(), "sensads5_update.cmd");
            await File.WriteAllTextAsync(bat, $"@echo off\r\nping -n 2 127.0.0.1 >nul\r\nmove /y \"{staged}\" \"{currentExe}\"\r\nstart \"\" \"{currentExe}\"\r\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            _reallyClosing = true;
            Close();
        }
        catch (Exception ex)
        {
            UpdaterStatusTxt.Text = $"Release update failed: {ex.Message}";
            SubStatusText.Text = UpdaterStatusTxt.Text;
        }
        finally
        {
            BtnUpdateLatestRelease.IsEnabled = true;
        }
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
