using System.IO.Ports;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SensaDS5Control.Core;

namespace SensaDS5Control.Platform.Windows;

public sealed class WindowsPlatformServices : IPlatformServices
{
    public IReadOnlyList<string> DiscoverPicoComPorts()
    {
        var preferred = new List<string>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb != null)
            {
                foreach (var vidPid in usb.GetSubKeyNames())
                {
                    if (!vidPid.Contains("VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase) &&
                        !vidPid.Contains("VID_045E", StringComparison.OrdinalIgnoreCase) &&
                        !vidPid.Contains("VID_2E8A", StringComparison.OrdinalIgnoreCase))
                        continue;
                    using var devKey = usb.OpenSubKey(vidPid);
                    if (devKey == null) continue;
                    foreach (var inst in devKey.GetSubKeyNames())
                    {
                        using var devParams = devKey.OpenSubKey($@"{inst}\Device Parameters");
                        if (devParams?.GetValue("PortName") is string port && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        {
                            preferred.Add(port);
                        }
                    }
                }
            }
        }
        catch { }

        var all = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ordered = preferred.Where(p => all.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Concat(all.Where(p => !preferred.Contains(p, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        return ordered;
    }

    public void MoveMouseRelative(int dx, int dy) => SendMouse(0x0001, dx, dy, 0);
    public void MouseLeftClick(bool down) => SendMouse(down ? 0x0002u : 0x0004u, 0, 0, 0);
    public void MouseRightClick(bool down) => SendMouse(down ? 0x0008u : 0x0010u, 0, 0, 0);
    public void MouseScroll(int delta) => SendMouse(0x0800, 0, 0, delta);

    public bool GetSystemMicMuted()
    {
        try
        {
            var vol = GetDefaultCaptureVolume();
            if (vol != null && vol.GetMute(out bool muted) == 0)
            {
                Marshal.ReleaseComObject(vol);
                return muted;
            }
        }
        catch { }
        return false;
    }

    public void SetSystemMicMuted(bool muted)
    {
        try
        {
            var vol = GetDefaultCaptureVolume();
            if (vol != null)
            {
                var guid = Guid.Empty;
                vol.SetMute(muted, ref guid);
                Marshal.ReleaseComObject(vol);
            }
        }
        catch { }
    }

    public async Task<string> FlashUf2ToPicoAsync(byte[] uf2Bytes, Action sendBootselCommand, CancellationToken ct)
    {
        sendBootselCommand();
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType == DriveType.Removable && d.IsReady)
                {
                    var label = d.VolumeLabel ?? "";
                    if (label.Equals("RP2350", StringComparison.OrdinalIgnoreCase) ||
                        label.Equals("RPI-RP2", StringComparison.OrdinalIgnoreCase))
                    {
                        var target = Path.Combine(d.RootDirectory.FullName, "SensaBridge.uf2");
                        await File.WriteAllBytesAsync(target, uf2Bytes, ct);
                        return $"Flashed {uf2Bytes.Length:N0} bytes to {d.Name} ({label}). Pico rebooting...";
                    }
                }
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Timed out waiting for RP2350 BOOTSEL drive. Hold BOOTSEL and replug Pico if needed.");
    }

    private static void SendMouse(uint flags, int dx, int dy, int data)
    {
        var input = new INPUT
        {
            type = 0,
            mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    private static IAudioEndpointVolume? GetDefaultCaptureVolume()
    {
        var enumType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        if (enumType == null) return null;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;
        // eCapture = 1, eCommunications = 2 (or eConsole = 0)
        if (enumerator.GetDefaultAudioEndpoint(1, 0, out var dev) != 0 || dev == null)
        {
            Marshal.ReleaseComObject(enumerator);
            return null;
        }
        var iidVol = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        dev.Activate(ref iidVol, 1, nint.Zero, out var obj);
        Marshal.ReleaseComObject(dev);
        Marshal.ReleaseComObject(enumerator);
        return obj as IAudioEndpointVolume;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out nint ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(nint pNotify);
        int UnregisterControlChangeNotify(nint pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
