namespace SensaDS5Control.Core;

public enum TriggerPresetKind
{
    AutoGame,
    Off,
    ContinuousResistance,
    SectionBreakpoint,
    Weapon,
    Vibration,
    BowSlope
}

public static class TriggerPresets
{
    /// Builds the 11-byte DualSense trigger effect block matching Linux hid-playstation / DualSense docs.
    public static byte[] BuildBlock(TriggerPresetKind kind, byte startZone, byte endZone, byte force, byte frequencyHz)
    {
        var b = new byte[11];
        byte f = (byte)Math.Clamp((int)force, 0, 255);
        byte s = (byte)Math.Clamp((int)startZone, 0, 255);
        byte e = (byte)Math.Clamp((int)endZone, 0, 255);

        switch (kind)
        {
            case TriggerPresetKind.Off:
                b[0] = 0x05; // Reset / off
                break;

            case TriggerPresetKind.ContinuousResistance:
                b[0] = 0x01;
                b[1] = s;
                b[2] = f;
                break;

            case TriggerPresetKind.SectionBreakpoint:
                b[0] = 0x02;
                b[1] = s;
                b[2] = Math.Max(e, (byte)Math.Min(255, s + 25));
                b[3] = f;
                break;

            case TriggerPresetKind.Weapon:
            {
                // Gun snap trigger (mode 0x25: startPos 2..7, endPos 3..8, force 0..7 packed)
                int sp = Math.Clamp(s * 7 / 255, 2, 7);
                int ep = Math.Clamp(e * 8 / 255, sp + 1, 8);
                int fp = Math.Clamp(f * 7 / 255, 1, 7);
                ushort mask = (ushort)((1 << sp) | (1 << ep));
                b[0] = 0x25;
                b[1] = (byte)(mask & 0xFF);
                b[2] = (byte)((mask >> 8) & 0x03);
                b[3] = (byte)(fp & 0x07);
                break;
            }

            case TriggerPresetKind.Vibration:
            {
                // Vibrating / rapid-fire trigger (mode 0x26)
                int sp = Math.Clamp(s * 9 / 255, 0, 9);
                int fp = Math.Clamp(f * 7 / 255, 1, 7);
                b[0] = 0x26;
                b[1] = (byte)(0xFF << sp);
                b[2] = (byte)(0x03 & (0x3FF >> Math.Max(0, 8 - sp)));
                b[3] = (byte)((fp & 0x07) | ((fp & 0x07) << 3));
                b[9] = Math.Clamp(frequencyHz, (byte)5, (byte)60);
                break;
            }

            case TriggerPresetKind.BowSlope:
            {
                // Bow / progressive ramp (mode 0x22)
                int sp = Math.Clamp(s * 7 / 255, 0, 7);
                int ep = Math.Clamp(e * 8 / 255, sp + 1, 8);
                int fp = Math.Clamp(f * 7 / 255, 1, 7);
                ushort mask = (ushort)((1 << sp) | (1 << ep));
                b[0] = 0x22;
                b[1] = (byte)(mask & 0xFF);
                b[2] = (byte)((mask >> 8) & 0x03);
                b[3] = (byte)(1 | (fp << 3));
                break;
            }
        }
        return b;
    }

    public static string FormatCliCommand(string side, TriggerPresetKind kind, byte startZone, byte endZone, byte force, byte frequencyHz)
    {
        if (kind == TriggerPresetKind.AutoGame)
            return $"trig {side} auto";
        var block = BuildBlock(kind, startZone, endZone, force, frequencyHz);
        return $"trig {side} " + string.Join(' ', block.Select(x => $"0x{x:X2}"));
    }
}
