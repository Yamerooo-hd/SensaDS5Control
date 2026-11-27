using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using SensaDS5Control.Core;

namespace SensaDS5Control.Platform.Windows;

public sealed class WindowsPlatformServices : IPlatformServices
{
    private static Guid GuidDevInterfaceComPort = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint DICS_FLAG_GLOBAL = 0x01;
    private const uint DIREG_DEV = 0x01;
    private const int KEY_READ = 0x20019;

    public IReadOnlyList<string> DiscoverPicoComPorts()
        => EnumeratePicoPorts(includeGenericPico: false).Select(p => p.Port).ToList();

    public PicoHardwareProbe ProbePicoHardware()
    {
        var bootsel = FindBootselDrive();
        if (bootsel != null)
            return new PicoHardwareProbe(PicoHardwareState.BootselDriveReady, null, bootsel);

        var ports = EnumeratePicoPorts(includeGenericPico: true);
        var sensaPort = ports.FirstOrDefault(p => !p.IsGeneric2E8A);
        if (sensaPort.Port != null)
            return new PicoHardwareProbe(PicoHardwareState.SensaBridgeActive, sensaPort.Port, null);

        var genericPort = ports.FirstOrDefault(p => p.IsGeneric2E8A);
        if (genericPort.Port != null)
            return new PicoHardwareProbe(PicoHardwareState.GenericPicoCom, genericPort.Port, null);

        return new PicoHardwareProbe(PicoHardwareState.NotConnected, null, null);
    }

    public async Task<bool> TryEnterBootsel1200BaudAsync(string comPort, CancellationToken ct)
    {
        try
        {
            using var sp = new SerialPort(comPort, 1200, Parity.None, 8, StopBits.One) { DtrEnable = false };
            sp.Open();
            await Task.Delay(80, ct);
            sp.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindBootselDrive()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Removable && d.IsReady)
                {
                    var label = d.VolumeLabel ?? "";
                    if (label.Equals("RP2350", StringComparison.OrdinalIgnoreCase) ||
                        label.Equals("RPI-RP2", StringComparison.OrdinalIgnoreCase))
                    {
                        return d.RootDirectory.FullName;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static List<(string Port, bool IsGeneric2E8A)> EnumeratePicoPorts(bool includeGenericPico)
    {
        var found = new List<(string Port, bool IsGeneric2E8A)>();
        nint devInfo = SetupDiGetClassDevs(ref GuidDevInterfaceComPort, null, nint.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == nint.Zero || devInfo == new nint(-1))
        {
            return found;
        }

        try
        {
            var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var idBuf = new char[512];
            for (uint idx = 0; SetupDiEnumDeviceInfo(devInfo, idx, ref devData); idx++)
            {
                if (!SetupDiGetDeviceInstanceId(devInfo, ref devData, idBuf, idBuf.Length, out int len) || len <= 0)
                    continue;

                var instanceId = new string(idBuf, 0, Math.Min(len, idBuf.Length)).TrimEnd('\0');
                bool isSensa = instanceId.Contains("VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase) ||
                               instanceId.Contains("VID_1209&PID_DB05", StringComparison.OrdinalIgnoreCase);
                bool is2e8a = instanceId.Contains("VID_2E8A", StringComparison.OrdinalIgnoreCase);
                if (!isSensa && !(includeGenericPico && is2e8a)) continue;

                nint hKey = SetupDiOpenDevRegKey(devInfo, ref devData, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
                if (hKey == nint.Zero || hKey == new nint(-1)) continue;
                try
                {
                    var data = new byte[128];
                    uint dataSize = (uint)data.Length;
                    if (RegQueryValueEx(hKey, "PortName", nint.Zero, out _, data, ref dataSize) == 0 && dataSize >= 4)
                    {
                        var port = Encoding.Unicode.GetString(data, 0, (int)dataSize).TrimEnd('\0');
                        if (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                            found.All(x => !x.Port.Equals(port, StringComparison.OrdinalIgnoreCase)))
                        {
                            found.Add((port, is2e8a));
                        }
                    }
                }
                finally
                {
                    RegCloseKey(hKey);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return found;
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

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SensaDS5Control";

    public bool GetStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public string? GetForegroundProcessName()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void OpenSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true });
            }
            catch { }
        }
    }

    public void SetStartWithWindows(bool enabled, bool startMinimized = true)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(RunValueName, startMinimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"");
                }
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public async Task<string> FlashUf2ToPicoAsync(
        byte[] uf2Bytes,
        string uf2FileName,
        Action? sendBootselCommand,
        IProgress<string>? status,
        CancellationToken ct)
    {
        var probe = ProbePicoHardware();
        string? rootDir = probe.BootselDrivePath;

        if (rootDir == null)
        {
            if (probe.State == PicoHardwareState.SensaBridgeActive && sendBootselCommand != null)
            {
                status?.Report("Rebooting SensaBridge Pico into RP2350 BOOTSEL mode...");
                sendBootselCommand();
            }
            else if (probe.State == PicoHardwareState.GenericPicoCom && probe.ComPort != null)
            {
                status?.Report($"Found Pico on {probe.ComPort}; sending 1200-baud BOOTSEL reset...");
                await TryEnterBootsel1200BaudAsync(probe.ComPort, ct);
            }
            else
            {
                status?.Report("Hold the white BOOTSEL button on Pico 2 W and plug USB in...");
            }

            // Wait up to 25 seconds for RP2350 / RPI-RP2 drive (handles both fast reboot & manual BOOTSEL plug)
            for (int i = 0; i < 100; i++)
            {
                ct.ThrowIfCancellationRequested();
                rootDir = FindBootselDrive();
                if (rootDir != null) break;
                if (i == 12 && probe.State == PicoHardwareState.GenericPicoCom)
                {
                    status?.Report("Still waiting — unplug Pico, hold white BOOTSEL button, and plug back in...");
                }
                await Task.Delay(250, ct);
            }
        }

        if (rootDir == null)
        {
            throw new TimeoutException("Timed out waiting for RP2350 BOOTSEL drive. Hold BOOTSEL on Pico and plug into USB.");
        }

        status?.Report($"Writing {uf2Bytes.Length:N0} bytes to {rootDir}...");
        var target = Path.Combine(rootDir, uf2FileName);
        await File.WriteAllBytesAsync(target, uf2Bytes, ct);
        return $"Flashed {uf2FileName} ({uf2Bytes.Length:N0} bytes) to {rootDir}.";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, [Out] char[] deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiOpenDevRegKey(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint scope, uint hwProfile, uint keyType, int samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueEx(nint hKey, string lpValueName, nint lpReserved, out uint lpType, [Out] byte[] lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(nint hKey);

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
