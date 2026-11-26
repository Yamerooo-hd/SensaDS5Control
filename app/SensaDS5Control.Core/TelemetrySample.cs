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
    byte UsbMode = 0)
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
                UsbMode: p.Length > 32 ? byte.Parse(p[32]) : (byte)0
            );
            return true;
        }
        catch
        {
            return false;
        }
    }
}
