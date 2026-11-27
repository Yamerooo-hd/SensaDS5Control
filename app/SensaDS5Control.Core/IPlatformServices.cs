namespace SensaDS5Control.Core;

public enum PicoHardwareState
{
    NotConnected,
    BootselDriveReady,
    GenericPicoCom,
    SensaBridgeActive
}

public sealed record PicoHardwareProbe(
    PicoHardwareState State,
    string? ComPort,
    string? BootselDrivePath);

public interface IPlatformServices
{
    IReadOnlyList<string> DiscoverPicoComPorts();
    PicoHardwareProbe ProbePicoHardware();
    Task<bool> TryEnterBootsel1200BaudAsync(string comPort, CancellationToken ct);
    void MoveMouseRelative(int dx, int dy);
    void MouseLeftClick(bool down);
    void MouseRightClick(bool down);
    void MouseScroll(int delta);
    bool GetSystemMicMuted();
    void SetSystemMicMuted(bool muted);
    bool GetStartWithWindows();
    void SetStartWithWindows(bool enabled, bool startMinimized = true);
    Task<string> FlashUf2ToPicoAsync(byte[] uf2Bytes, string uf2FileName, Action? sendBootselCommand, IProgress<string>? status, CancellationToken ct);
}
