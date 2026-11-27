namespace SensaDS5Control.Core;

public sealed class ControllerProfile
{
    public string Name { get; set; } = "Default";
    public string ExeName { get; set; } = "";
    public int UsbPollRateHz { get; set; } = 1000;
    public int HapticGainPct { get; set; } = 100;
    public int StickDeadzonePct { get; set; } = 4;
    public int StickCurve { get; set; }
    public int StickOuterDzPct { get; set; }
    public int StickAntiDzPct { get; set; }
    public int ImpulseTriggerMode { get; set; } = 1;
    public int ImpulseTriggerStrengthPct { get; set; } = 85;
    public int SpeakerVolumePct { get; set; } = 100;
    public int SpeakerGain { get; set; } = 6;
    public bool TouchpadMouseEnabled { get; set; }
    public bool TouchpadTapToClick { get; set; }
    public bool GyroMouseEnabled { get; set; }
    public bool GyroInvertY { get; set; }
    public int GyroActivationMode { get; set; }
    public bool LedOverride { get; set; }
    public byte LedR { get; set; }
    public byte LedG { get; set; } = 102;
    public byte LedB { get; set; } = 255;

    public static ControllerProfile CaptureFrom(string name, string exeName, AppSettings s) => new()
    {
        Name = name,
        ExeName = exeName,
        UsbPollRateHz = s.UsbPollRateHz,
        HapticGainPct = s.HapticGainPct,
        StickDeadzonePct = s.StickDeadzonePct,
        StickCurve = s.StickCurve,
        StickOuterDzPct = s.StickOuterDzPct,
        StickAntiDzPct = s.StickAntiDzPct,
        ImpulseTriggerMode = s.ImpulseTriggerMode,
        ImpulseTriggerStrengthPct = s.ImpulseTriggerStrengthPct,
        SpeakerVolumePct = s.SpeakerVolumePct,
        SpeakerGain = s.SpeakerGain,
        TouchpadMouseEnabled = s.TouchpadMouseEnabled,
        TouchpadTapToClick = s.TouchpadTapToClick,
        GyroMouseEnabled = s.GyroMouseEnabled,
        GyroInvertY = s.GyroInvertY,
        GyroActivationMode = s.GyroActivationMode,
        LedOverride = s.LedOverride,
        LedR = s.LedR,
        LedG = s.LedG,
        LedB = s.LedB,
    };

    public void ApplyTo(AppSettings s)
    {
        s.ActiveProfileName = Name;
        s.UsbPollRateHz = UsbPollRateHz;
        s.HapticGainPct = HapticGainPct;
        s.StickDeadzonePct = StickDeadzonePct;
        s.StickCurve = StickCurve;
        s.StickOuterDzPct = StickOuterDzPct;
        s.StickAntiDzPct = StickAntiDzPct;
        s.ImpulseTriggerMode = ImpulseTriggerMode;
        s.ImpulseTriggerStrengthPct = ImpulseTriggerStrengthPct;
        s.SpeakerVolumePct = SpeakerVolumePct;
        s.SpeakerGain = SpeakerGain;
        s.TouchpadMouseEnabled = TouchpadMouseEnabled;
        s.TouchpadTapToClick = TouchpadTapToClick;
        s.GyroMouseEnabled = GyroMouseEnabled;
        s.GyroInvertY = GyroInvertY;
        s.GyroActivationMode = GyroActivationMode;
        s.LedOverride = LedOverride;
        s.LedR = LedR;
        s.LedG = LedG;
        s.LedB = LedB;
    }
}
