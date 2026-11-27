using System.Runtime.InteropServices;
using Avalonia;

namespace SensaDS5Control.App;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [STAThread]
    public static void Main(string[] args)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\SensaDS5Control_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            nint existing = FindWindow(null, "SensaDS5Control — Cosmic Byte Sensa Wireless Suite");
            if (existing != nint.Zero)
            {
                ShowWindow(existing, 5); // SW_SHOW (un-hides tray-minimized window)
                ShowWindow(existing, 9); // SW_RESTORE
                SetForegroundWindow(existing);
            }
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
