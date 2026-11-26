using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace SensaDS5Control.App;

public sealed record AppThemeDef(
    string Id,
    string DisplayName,
    bool IsLight,
    string AppBg,
    string SidebarBg,
    string CardBg,
    string CardAltBg,
    string BorderSubtle,
    string PillBg,
    string PillBorder,
    string NavHoverBg,
    string NavActiveBg,
    string NavActiveFg,
    string TextPrimary,
    string TextSecondary,
    string TextMuted,
    string AccentHex,
    string AccentHoverHex,
    string AccentFgHex,
    string DangerBgHex,
    string DangerFgHex,
    string SuccessBgHex,
    string InputSurfaceBg,
    string ConsoleBg);

public static class ThemeManager
{
    public static readonly IReadOnlyList<AppThemeDef> Presets =
    [
        new(
            Id: "midnight",
            DisplayName: "Midnight Cobalt",
            IsLight: false,
            AppBg: "#090C12",
            SidebarBg: "#101520",
            CardBg: "#151B29",
            CardAltBg: "#1B2335",
            BorderSubtle: "#242D42",
            PillBg: "#1C2436",
            PillBorder: "#2E3B56",
            NavHoverBg: "#1A2233",
            NavActiveBg: "#1D2C49",
            NavActiveFg: "#38BDF8",
            TextPrimary: "#F8FAFC",
            TextSecondary: "#CBD5E1",
            TextMuted: "#8896AE",
            AccentHex: "#2563EB",
            AccentHoverHex: "#1D4ED8",
            AccentFgHex: "#FFFFFF",
            DangerBgHex: "#7F1D1D",
            DangerFgHex: "#FECACA",
            SuccessBgHex: "#15803D",
            InputSurfaceBg: "#0D111A",
            ConsoleBg: "#070A0F"),
        new(
            Id: "xbox",
            DisplayName: "Xbox Emerald",
            IsLight: false,
            AppBg: "#0A0F0C",
            SidebarBg: "#0F1713",
            CardBg: "#141E19",
            CardAltBg: "#1A2821",
            BorderSubtle: "#25382E",
            PillBg: "#1B2B22",
            PillBorder: "#2E4B3B",
            NavHoverBg: "#19261E",
            NavActiveBg: "#163322",
            NavActiveFg: "#4ADE80",
            TextPrimary: "#F8FAFC",
            TextSecondary: "#D1FAE5",
            TextMuted: "#86A896",
            AccentHex: "#16A34A",
            AccentHoverHex: "#15803D",
            AccentFgHex: "#FFFFFF",
            DangerBgHex: "#7F1D1D",
            DangerFgHex: "#FECACA",
            SuccessBgHex: "#0EA5E9",
            InputSurfaceBg: "#0B120E",
            ConsoleBg: "#070B08"),
        new(
            Id: "crimson",
            DisplayName: "Cosmic Crimson",
            IsLight: false,
            AppBg: "#10090D",
            SidebarBg: "#180F15",
            CardBg: "#20141C",
            CardAltBg: "#291A24",
            BorderSubtle: "#3D2534",
            PillBg: "#2D1C27",
            PillBorder: "#4C2E41",
            NavHoverBg: "#291923",
            NavActiveBg: "#3E182A",
            NavActiveFg: "#FB7185",
            TextPrimary: "#FFF1F2",
            TextSecondary: "#E2C7D5",
            TextMuted: "#A9899B",
            AccentHex: "#E11D48",
            AccentHoverHex: "#BE123C",
            AccentFgHex: "#FFFFFF",
            DangerBgHex: "#450A0A",
            DangerFgHex: "#FCA5A5",
            SuccessBgHex: "#15803D",
            InputSurfaceBg: "#120B0F",
            ConsoleBg: "#0A0608"),
        new(
            Id: "frost",
            DisplayName: "DualSense Frost",
            IsLight: true,
            AppBg: "#EEF2F7",
            SidebarBg: "#FFFFFF",
            CardBg: "#FFFFFF",
            CardAltBg: "#F4F7FB",
            BorderSubtle: "#D8E0EC",
            PillBg: "#EEF3FA",
            PillBorder: "#CBD6E6",
            NavHoverBg: "#F1F5F9",
            NavActiveBg: "#E0ECFF",
            NavActiveFg: "#00439C",
            TextPrimary: "#0F172A",
            TextSecondary: "#334155",
            TextMuted: "#64748B",
            AccentHex: "#0055D4",
            AccentHoverHex: "#0041A8",
            AccentFgHex: "#FFFFFF",
            DangerBgHex: "#FEE2E2",
            DangerFgHex: "#991B1B",
            SuccessBgHex: "#16A34A",
            InputSurfaceBg: "#F1F5F9",
            ConsoleBg: "#F8FAFC")
    ];

    public static AppThemeDef FindById(string? id)
        => Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

    public static void Apply(string? themeId)
    {
        var app = Application.Current;
        if (app == null) return;

        var theme = FindById(themeId);
        app.RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

        void SetBrush(string key, string hex) => app.Resources[key] = new SolidColorBrush(Color.Parse(hex));

        SetBrush("AppBgBrush", theme.AppBg);
        SetBrush("SidebarBgBrush", theme.SidebarBg);
        SetBrush("CardBgBrush", theme.CardBg);
        SetBrush("CardAltBgBrush", theme.CardAltBg);
        SetBrush("BorderSubtleBrush", theme.BorderSubtle);
        SetBrush("PillBgBrush", theme.PillBg);
        SetBrush("PillBorderBrush", theme.PillBorder);
        SetBrush("NavHoverBgBrush", theme.NavHoverBg);
        SetBrush("NavActiveBgBrush", theme.NavActiveBg);
        SetBrush("NavActiveFgBrush", theme.NavActiveFg);
        SetBrush("TextPrimaryBrush", theme.TextPrimary);
        SetBrush("TextSecondaryBrush", theme.TextSecondary);
        SetBrush("TextMutedBrush", theme.TextMuted);
        SetBrush("AccentBrush", theme.AccentHex);
        SetBrush("AccentHoverBrush", theme.AccentHoverHex);
        SetBrush("AccentFgBrush", theme.AccentFgHex);
        SetBrush("DangerBgBrush", theme.DangerBgHex);
        SetBrush("DangerFgBrush", theme.DangerFgHex);
        SetBrush("SuccessBgBrush", theme.SuccessBgHex);
        SetBrush("InputSurfaceBgBrush", theme.InputSurfaceBg);
        SetBrush("ConsoleBgBrush", theme.ConsoleBg);
    }
}
