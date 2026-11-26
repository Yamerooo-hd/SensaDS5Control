using System.IO.Ports;
using System.Text;

namespace SensaDS5Control.Core;

public sealed class PicoBridgeClient : IDisposable
{
    private SerialPort? _port;
    private readonly object _txLock = new();
    private CancellationTokenSource? _rxCts;
    private Task? _rxTask;

    public bool IsConnected => _port is { IsOpen: true };
    public string? ActivePortName { get; private set; }

    public event Action<bool, string?>? ConnectionChanged;
    public event Action<TelemetrySample>? TelemetryReceived;
    public event Action<string>? LogLineReceived;

    public bool Connect(string portName)
    {
        Disconnect();
        try
        {
            var sp = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = 200,
                WriteTimeout = 500,
                Encoding = Encoding.ASCII,
                NewLine = "\n"
            };
            sp.Open();
            _port = sp;
            ActivePortName = portName;
            _rxCts = new CancellationTokenSource();
            _rxTask = Task.Run(() => ReadLoop(sp, _rxCts.Token));
            ConnectionChanged?.Invoke(true, portName);
            SendCommand("tele 1");
            return true;
        }
        catch (Exception ex)
        {
            LogLineReceived?.Invoke($"[APP] Connect failed on {portName}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        try { _rxCts?.Cancel(); } catch { }
        var sp = _port;
        _port = null;
        ActivePortName = null;
        if (sp != null)
        {
            try { if (sp.IsOpen) SendRaw(sp, "tele 0"); } catch { }
            try { sp.Close(); } catch { }
            try { sp.Dispose(); } catch { }
        }
        ConnectionChanged?.Invoke(false, null);
    }

    public void SendCommand(string cmd)
    {
        var sp = _port;
        if (sp is not { IsOpen: true }) return;
        lock (_txLock)
        {
            try
            {
                SendRaw(sp, cmd);
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

    private void ReadLoop(SerialPort sp, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        var buf = new byte[512];
        while (!ct.IsCancellationRequested && sp.IsOpen)
        {
            try
            {
                int n = sp.Read(buf, 0, buf.Length);
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
                            TelemetryReceived?.Invoke(sample);
                        }
                        else
                        {
                            LogLineReceived?.Invoke(line);
                        }
                    }
                    else if (sb.Length < 512)
                    {
                        sb.Append(c);
                    }
                }
            }
            catch (TimeoutException) { }
            catch (Exception)
            {
                if (!ct.IsCancellationRequested)
                {
                    Disconnect();
                }
                break;
            }
        }
    }

    public void SetLed(byte r, byte g, byte b) => SendCommand($"led {r} {g} {b}");
    public void SetLedAuto() => SendCommand("led auto");
    public void SetMicLed(byte mode) => SendCommand($"micled {mode}");
    public void SetHapticGain(int pct) => SendCommand($"tune hgain {Math.Clamp(pct, 25, 255)}");
    public void SetMode(string mode) => SendCommand($"mode {mode}");
    public void TriggerTestPulse(int r, int l, int ms) => SendCommand($"pulse {r} {l} {ms}");
    public void TriggerTestWave(string proto, int amp, int hz, int ms) => SendCommand($"test{proto} {amp} {hz} {ms}");
    public void SetTrigger(string side, TriggerPresetKind kind, byte start, byte end, byte force, byte freqHz)
        => SendCommand(TriggerPresets.FormatCliCommand(side, kind, start, end, force, freqHz));
    public void SendUnlock(int mode) => SendCommand($"unlock {mode}");
    public void SetBtMon(bool on) => SendCommand($"btmon {(on ? 1 : 0)}");
    public void EnterBootsel() => SendCommand("bootsel");

    public void Dispose() => Disconnect();
}
