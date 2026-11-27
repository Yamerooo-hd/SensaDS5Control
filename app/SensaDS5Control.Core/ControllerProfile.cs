namespace SensaDS5Control.Core;

public sealed class ControllerProfile
{
    public string Name { get; set; } = "Default";
    public string ExeName { get; set; } = "";
    public int UsbPollRateHz { get; set; } = 1000;
    public int HapticGainPct { get; set; } = 150;
    public int StickDeadzonePct { get; set; } = 4;
    public int StickCurve { get; set; }
    public int StickOuterDzPct { get; set; }
    public int StickAntiDzPct { get; set; }
    public int ImpulseTriggerMode { get; set; } = 1;
    public int ImpulseTriggerStrengthPct { get; set; } = 85;
    public int SpeakerVolumePct { get; set; } = 100;
    public int SpeakerGain { get; set; } = 6;
    public bool TouchpadMouse { get; set; }
    public bool TouchpadTapToClick { get; set; }
    public double TouchpadSens { get; set; } = 1.2;
    public bool GyroMouse { get; set; }
    public bool GyroOnlyOnL2 { get; set; } = true;
    public bool GyroInvertY { get; set; }
    public int GyroActivationMode { get; set; }
    public double GyroSens { get; set; } = 0.035;
    public bool LedOverride { get; set; }
    public byte LedR { get; set; }
    public byte LedG { get; set; } = 160;
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
        TouchpadMouse = s.TouchpadMouse,
        TouchpadTapToClick = s.TouchpadTapToClick,
        TouchpadSens = s.TouchpadSens,
        GyroMouse = s.GyroMouse,
        GyroOnlyOnL2 = s.GyroOnlyOnL2,
        GyroInvertY = s.GyroInvertY,
        GyroActivationMode = s.GyroActivationMode,
        GyroSens = s.GyroSens,
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
        s.TouchpadMouse = TouchpadMouse;
        s.TouchpadTapToClick = TouchpadTapToClick;
        s.TouchpadSens = TouchpadSens;
        s.GyroMouse = GyroMouse;
        s.GyroOnlyOnL2 = GyroOnlyOnL2;
        s.GyroInvertY = GyroInvertY;
        s.GyroActivationMode = GyroActivationMode;
        s.GyroSens = GyroSens;
        s.LedOverride = LedOverride;
        s.LedR = LedR;
        s.LedG = LedG;
        s.LedB = LedB;
    }
}
