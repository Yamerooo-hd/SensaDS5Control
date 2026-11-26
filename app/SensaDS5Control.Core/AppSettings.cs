using System.Text.Json;

namespace SensaDS5Control.Core;

public sealed class AppSettings
{
    public bool LedOverride { get; set; }
    public byte LedR { get; set; } = 0;
    public byte LedG { get; set; } = 160;
    public byte LedB { get; set; } = 255;

    public byte MicLedMode { get; set; } = 0; // 0=Off, 1=Solid, 2=Pulse
    public bool SyncControllerMicButton { get; set; } = true;

    public int HapticGainPct { get; set; } = 150;

    public TriggerPresetKind LeftTriggerKind { get; set; } = TriggerPresetKind.AutoGame;
    public byte LeftStart { get; set; } = 40;
    public byte LeftEnd { get; set; } = 180;
    public byte LeftForce { get; set; } = 200;
    public byte LeftFreq { get; set; } = 20;

    public TriggerPresetKind RightTriggerKind { get; set; } = TriggerPresetKind.AutoGame;
    public byte RightStart { get; set; } = 60;
    public byte RightEnd { get; set; } = 170;
    public byte RightForce { get; set; } = 220;
    public byte RightFreq { get; set; } = 25;

    public bool TouchpadMouse { get; set; }
    public double TouchpadSens { get; set; } = 1.2;
    public bool GyroMouse { get; set; }
    public bool GyroOnlyOnL2 { get; set; } = true;
    public double GyroSens { get; set; } = 0.035;

    public string GitHubToken { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SensaDS5Control", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
