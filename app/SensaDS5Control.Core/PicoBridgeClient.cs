using System.IO.Ports;
using System.Text;

namespace SensaDS5Control.Core;

public sealed class PicoBridgeClient : IDisposable
{
    private SerialPort? _port;
    private CancellationTokenSource? _rxCts;
    private readonly object _txLock = new();
    private long _sessionGen;

    public bool IsConnected => _port is { IsOpen: true };
    public string? ActivePortName { get; private set; }

    public event Action<bool, string?>? ConnectionChanged;
    public event Action<TelemetrySample>? TelemetryReceived;
    public event Action<PicoConfigSample>? ConfigReceived;
    public event Action<string>? LogLineReceived;

    public async Task<bool> ConnectAsync(string portName, bool wakeController = false)
    {
        long gen = Interlocked.Increment(ref _sessionGen);
        CloseCurrentPort(null, sendDisconnect: false);

        return await Task.Run(async () =>
        {
            Exception? lastEx = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (Volatile.Read(ref _sessionGen) != gen) return false;
                try
                {
                    var sp = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                    {
                        Handshake = Handshake.None,
                        DtrEnable = true,
                        RtsEnable = true,
                        ReadTimeout = 250,
                        WriteTimeout = 350,
                        Encoding = Encoding.ASCII,
                        NewLine = "\n"
                    };
                    sp.Open();
                    sp.DtrEnable = true;
                    sp.RtsEnable = true;

                    if (Volatile.Read(ref _sessionGen) != gen)
                    {
                        DisposePortOffThread(sp);
                        return false;
                    }

                    var cts = new CancellationTokenSource();
                    _port = sp;
                    _rxCts = cts;
                    ActivePortName = portName;

                    ConnectionChanged?.Invoke(true, portName);
                    _ = Task.Run(() => ReadLoopAsync(gen, sp, cts.Token));
                    SendCommand("tele 1");
                    SendCommand("getcfg");
                    if (wakeController) SendCommand("reconnect");
                    return true;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    await Task.Delay(180);
                }
            }

            if (Volatile.Read(ref _sessionGen) == gen)
            {
                var err = $"Connect failed on {portName}: {lastEx?.Message}";
                LogLineReceived?.Invoke($"[APP] {err}");
                ConnectionChanged?.Invoke(false, err);
            }
            return false;
        });
    }

    public void Disconnect(string? reason = null, bool disconnectController = false)
    {
        Interlocked.Increment(ref _sessionGen);
        CloseCurrentPort(reason, disconnectController);
    }

    public void NotifyUsbRemoved()
    {
        if (_port == null && ActivePortName == null) return;
        Disconnect("Pico USB unplugged", disconnectController: false);
    }

    private void CloseCurrentPort(string? reason, bool sendDisconnect)
    {
        var cts = Interlocked.Exchange(ref _rxCts, null);
        try { cts?.Cancel(); } catch { }

        var sp = Interlocked.Exchange(ref _port, null);
        var wasOpen = sp != null || ActivePortName != null;
        ActivePortName = null;

        if (sp != null)
        {
            Task.Run(() =>
            {
                if (sendDisconnect)
                {
                    try
                    {
                        if (sp.IsOpen)
                        {
                            lock (_txLock)
                            {
                                SendRaw(sp, "disconnect");
                                SendRaw(sp, "tele 0");
                            }
                            Thread.Sleep(55);
                        }
                    }
                    catch { }
                }
                else if (reason == null)
                {
                    try
                    {
                        if (sp.IsOpen)
                        {
                            lock (_txLock) { SendRaw(sp, "tele 0"); }
                        }
                    }
                    catch { }
                }
                DisposePortSafe(sp);
            });
        }

        if (wasOpen)
        {
            ConnectionChanged?.Invoke(false, reason);
        }
    }

    private static void DisposePortOffThread(SerialPort sp) => Task.Run(() => DisposePortSafe(sp));

    private static void DisposePortSafe(SerialPort sp)
    {
        try { sp.Close(); } catch { }
        try { sp.Dispose(); } catch { }
    }

    public void SendCommand(string cmd)
    {
        var sp = _port;
        if (sp is not { IsOpen: true }) return;
        lock (_txLock)
        {
            try
            {
                if (sp.IsOpen) SendRaw(sp, cmd);
            }
            catch (Exception ex)
            {
                LogLineReceived?.Invoke($"[APP] Write error: {ex.Message}");
            }
        }
    }

    private static void SendRaw(SerialPort sp, string cmd)
    {
        var bytes = Encoding.ASCII.GetBytes(cmd.TrimEnd() + "\r\n");
        sp.Write(bytes, 0, bytes.Length);
    }

    private async Task ReadLoopAsync(long gen, SerialPort sp, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        var buf = new byte[512];
        while (!ct.IsCancellationRequested && Volatile.Read(ref _sessionGen) == gen && sp.IsOpen)
        {
            try
            {
                int n = await sp.BaseStream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buf[i];
                    if (c == '\r') continue;
                    if (c == '\n')
                    {
                        var line = sb.ToString();
                        sb.Clear();
                        if (line.Length == 0) continue;
                        if (TelemetrySample.TryParse(line, out var sample))
                        {
                            try { TelemetryReceived?.Invoke(sample); }
                            catch (Exception ex) { LogLineReceived?.Invoke($"[APP] Telemetry error: {ex.Message}"); }
                        }
                        else if (PicoConfigSample.TryParse(line, out var cfg))
                        {
                            try { ConfigReceived?.Invoke(cfg); }
                            catch (Exception ex) { LogLineReceived?.Invoke($"[APP] Config error: {ex.Message}"); }
                        }
                        else
                        {
                            try { LogLineReceived?.Invoke(line); } catch { }
                        }
                    }
                    else if (sb.Length < 512)
                    {
                        sb.Append(c);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested && Volatile.Read(ref _sessionGen) == gen)
                {
                    var err = $"Connection lost: {ex.Message}";
                    LogLineReceived?.Invoke($"[APP] {err}");
                    if (Interlocked.CompareExchange(ref _sessionGen, gen + 1, gen) == gen)
                    {
                        CloseCurrentPort(err, sendDisconnect: false);
                    }
                }
                break;
            }
        }
    }

    public void SetLed(byte r, byte g, byte b) => SendCommand($"led {r} {g} {b}");
    public void SetLedAuto() => SendCommand("led auto");
    public void SetMicLed(byte mode) => SendCommand($"micled {mode}");
    public void SetHapticGain(int pct) => SendCommand($"tune hgain {Math.Clamp(pct, 25, 255)}");
    public void SetImpulseTriggers(int mode, int strengthPct) => SendCommand($"imptrig {Math.Clamp(mode, 0, 2)} {Math.Clamp(strengthPct, 0, 100)}");
    public void TriggerImpulseTest(int pct = 85, int ms = 650) => SendCommand($"imptrigtest {Math.Clamp(pct, 10, 100)} {Math.Clamp(ms, 50, 3000)}");
    public void SetStickDeadzone(int pct) => SendCommand($"deadzone {Math.Clamp(pct, 0, 25)}");
    public void SetPollRate(int hz) => SendCommand($"pollrate {(hz <= 375 ? 250 : hz <= 750 ? 500 : 1000)}");
    public void SetSpeakerVolume(int volPct) => SendCommand($"spkvol {Math.Clamp(volPct, 0, 100)}");
    public void SetSpeakerGain(int gain1to7) => SendCommand($"spkgain {Math.Clamp(gain1to7, 1, 7)}");
    public void TriggerTestSpeaker(int ms = 900) => SendCommand($"testspk {Math.Clamp(ms, 150, 4000)}");
    public void SetIdleSleep(int minutes) => SendCommand($"idlesleep {Math.Clamp(minutes, 0, 60)}");
    public void SetStickTuning(int curve, int outerDzPct, int antiDzPct) =>
        SendCommand($"sticktune {Math.Clamp(curve, 0, 2)} {Math.Clamp(outerDzPct, 0, 15)} {Math.Clamp(antiDzPct, 0, 20)}");
    public void RequestConfig() => SendCommand("getcfg");
    public void SaveConfigToFlash() => SendCommand("save");
    public void StartPairing() => SendCommand("pair");
    public void ForgetAllBonds() => SendCommand("forget");
    public void SetMode(string mode) => SendCommand($"mode {mode}");
    public void TriggerTestPulse(int r, int l, int ms) => SendCommand($"pulse {r} {l} {ms}");
    public void TriggerTestWave(string proto, int amp, int hz, int ms) => SendCommand($"test{proto} {amp} {hz} {ms}");
    public void SetTrigger(string side, TriggerPresetKind kind, byte start, byte end, byte force, byte freqHz)
        => SendCommand(TriggerPresets.FormatCliCommand(side, kind, start, end, force, freqHz));
    public void SendUnlock(int mode) => SendCommand($"unlock {mode}");
    public void SetBtMon(bool on) => SendCommand($"btmon {(on ? 1 : 0)}");
    public void SetUsbMode(string mode) => SendCommand($"usbmode {mode}");
    public void DisconnectController() => SendCommand("disconnect");
    public void ReconnectController() => SendCommand("reconnect");
    public void EnterBootsel() => SendCommand("bootsel");

    public void Dispose() => Disconnect(reason: "Closing", disconnectController: false);
}
