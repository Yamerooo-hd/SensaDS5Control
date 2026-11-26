namespace SensaDS5Control.Core;

public interface IPlatformServices
{
    IReadOnlyList<string> DiscoverPicoComPorts();
    void MoveMouseRelative(int dx, int dy);
    void MouseLeftClick(bool down);
    void MouseRightClick(bool down);
    void MouseScroll(int delta);
    bool GetSystemMicMuted();
    void SetSystemMicMuted(bool muted);
    Task<string> FlashUf2ToPicoAsync(byte[] uf2Bytes, Action sendBootselCommand, CancellationToken ct);
}
