namespace SensaDS5Control.Core;

public sealed record TelemetrySample(
    bool FullMode0x31,
    byte Lx, byte Ly, byte Rx, byte Ry,
    byte L2, byte R2,
    byte Btn0, byte Btn1, byte Btn2, byte Btn3,
    short GyroX, short GyroY, short GyroZ,
    short AccelX, short AccelY, short AccelZ,
    bool Touch0Down, ushort Touch0X, ushort Touch0Y,
    bool Touch1Down, ushort Touch1X, ushort Touch1Y,
    byte BatteryPct, byte ChargingState,
    double PeakHapticL, double PeakHapticR,
    byte Mode, uint Tx0x36, byte HapticGainPct, byte MicLedMode,
    byte UsbMode = 0, byte ImpulseMode = 1, byte ImpulseStrengthPct = 85,
    bool BtControllerReady = true,
    ushort BtRxHz = 0,
    bool UsbSpeakerStreaming = false)
{
    public int DPad => Btn0 & 0x0F;
    public bool Square => (Btn0 & 0x10) != 0;
    public bool Cross => (Btn0 & 0x20) != 0;
    public bool Circle => (Btn0 & 0x40) != 0;
    public bool Triangle => (Btn0 & 0x80) != 0;

    public bool L1 => (Btn1 & 0x01) != 0;
    public bool R1 => (Btn1 & 0x02) != 0;
    public bool L2Btn => (Btn1 & 0x04) != 0;
    public bool R2Btn => (Btn1 & 0x08) != 0;
    public bool Create => (Btn1 & 0x10) != 0;
    public bool Options => (Btn1 & 0x20) != 0;
    public bool L3 => (Btn1 & 0x40) != 0;
    public bool R3 => (Btn1 & 0x80) != 0;

    public bool Ps => (Btn2 & 0x01) != 0;
    public bool TouchpadClick => (Btn2 & 0x02) != 0;
    public bool MicMuteBtn => (Btn2 & 0x04) != 0;

    public static bool TryParse(string line, out TelemetrySample sample)
    {
        sample = default!;
        if (!line.StartsWith("TELE,", StringComparison.Ordinal))
            return false;
        var p = line.Split(',');
        if (p.Length < 32)
            return false;
        try
        {
            sample = new TelemetrySample(
                FullMode0x31: p[1] == "1",
                Lx: byte.Parse(p[2]), Ly: byte.Parse(p[3]),
                Rx: byte.Parse(p[4]), Ry: byte.Parse(p[5]),
                L2: byte.Parse(p[6]), R2: byte.Parse(p[7]),
                Btn0: byte.Parse(p[8]), Btn1: byte.Parse(p[9]),
                Btn2: byte.Parse(p[10]), Btn3: byte.Parse(p[11]),
                GyroX: short.Parse(p[12]), GyroY: short.Parse(p[13]), GyroZ: short.Parse(p[14]),
                AccelX: short.Parse(p[15]), AccelY: short.Parse(p[16]), AccelZ: short.Parse(p[17]),
                Touch0Down: p[18] == "1", Touch0X: ushort.Parse(p[19]), Touch0Y: ushort.Parse(p[20]),
                Touch1Down: p[21] == "1", Touch1X: ushort.Parse(p[22]), Touch1Y: ushort.Parse(p[23]),
                BatteryPct: byte.Parse(p[24]), ChargingState: byte.Parse(p[25]),
                PeakHapticL: int.Parse(p[26]) / 10.0, PeakHapticR: int.Parse(p[27]) / 10.0,
                Mode: byte.Parse(p[28]), Tx0x36: uint.Parse(p[29]),
                HapticGainPct: byte.Parse(p[30]), MicLedMode: byte.Parse(p[31]),
                UsbMode: p.Length > 32 ? byte.Parse(p[32]) : (byte)0,
                ImpulseMode: p.Length > 33 ? byte.Parse(p[33]) : (byte)1,
                ImpulseStrengthPct: p.Length > 34 ? byte.Parse(p[34]) : (byte)85,
                BtControllerReady: p.Length > 35 ? p[35] == "1" : true,
                BtRxHz: p.Length > 36 && ushort.TryParse(p[36].Trim(), out var rxHz) ? rxHz : (ushort)0,
                UsbSpeakerStreaming: p.Length > 37 && p[37].Trim() == "1"
            );
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record PicoConfigSample(
    bool IsFreshFlash,
    byte UsbMode,
    byte HapticMode,
    byte HapticGainPct,
    byte ImpulseMode,
    byte ImpulseStrengthPct,
    byte StickDeadzonePct,
    bool LedOverride,
    byte LedR,
    byte LedG,
    byte LedB,
    byte MicLedMode,
    bool LeftTriggerOverride,
    bool RightTriggerOverride,
    string FirmwareVersion = "0.1.x",
    bool HasBondedKey = false,
    int UsbPollRateHz = 1000,
    int SpeakerVolumePct = 100,
    int SpeakerGain = 6,
    int IdleSleepMin = 10,
    int StickCurve = 0,
    int StickOuterDzPct = 0,
    int StickAntiDzPct = 0)
{
    public static bool TryParse(string line, out PicoConfigSample cfg)
    {
        cfg = default!;
        if (!line.StartsWith("CFG,", StringComparison.Ordinal)) return false;
        var p = line.Split(',');
        if (p.Length < 15) return false;
        try
        {
            int parsedPoll = (p.Length > 17 && int.TryParse(p[17].Trim(), out var hz)) ? hz : 1000;
            if (parsedPoll is not (250 or 500 or 1000)) parsedPoll = 1000;
            int spkVol = (p.Length > 18 && int.TryParse(p[18].Trim(), out var sv)) ? Math.Clamp(sv, 0, 100) : 100;
            int spkGain = (p.Length > 19 && int.TryParse(p[19].Trim(), out var sg)) ? Math.Clamp(sg, 1, 7) : 6;
            int idleSleep = (p.Length > 20 && int.TryParse(p[20].Trim(), out var isl)) ? Math.Clamp(isl, 0, 60) : 10;
            int curve = (p.Length > 21 && int.TryParse(p[21].Trim(), out var sc)) ? Math.Clamp(sc, 0, 2) : 0;
            int outerDz = (p.Length > 22 && int.TryParse(p[22].Trim(), out var odz)) ? Math.Clamp(odz, 0, 15) : 0;
            int antiDz = (p.Length > 23 && int.TryParse(p[23].Trim(), out var adz)) ? Math.Clamp(adz, 0, 20) : 0;
            cfg = new PicoConfigSample(
                IsFreshFlash: p[1] == "1",
                UsbMode: byte.Parse(p[2]),
                HapticMode: byte.Parse(p[3]),
                HapticGainPct: byte.Parse(p[4]),
                ImpulseMode: byte.Parse(p[5]),
                ImpulseStrengthPct: byte.Parse(p[6]),
                StickDeadzonePct: byte.Parse(p[7]),
                LedOverride: p[8] == "1",
                LedR: byte.Parse(p[9]),
                LedG: byte.Parse(p[10]),
                LedB: byte.Parse(p[11]),
                MicLedMode: byte.Parse(p[12]),
                LeftTriggerOverride: p[13] == "1",
                RightTriggerOverride: p[14] == "1",
                FirmwareVersion: p.Length > 15 && !string.IsNullOrWhiteSpace(p[15]) ? p[15].Trim() : "0.1.x",
                HasBondedKey: p.Length > 16 && p[16].Trim() == "1",
                UsbPollRateHz: parsedPoll,
                SpeakerVolumePct: spkVol,
                SpeakerGain: spkGain,
                IdleSleepMin: idleSleep,
                StickCurve: curve,
                StickOuterDzPct: outerDz,
                StickAntiDzPct: antiDz);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
