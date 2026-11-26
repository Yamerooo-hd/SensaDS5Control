using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using SensaDS5Control.Core;

namespace SensaDS5Control.App;

public partial class ControllerDiagram : UserControl
{
    private static readonly IBrush IdleBtnBrush = new SolidColorBrush(Color.Parse("#242C3D"));
    private static readonly IBrush IdleXboxBtnBrush = new SolidColorBrush(Color.Parse("#232D27"));
    private static readonly IBrush IdlePadBrush = new SolidColorBrush(Color.Parse("#2C364D"));
    private static readonly IBrush IdleXboxPadBrush = new SolidColorBrush(Color.Parse("#29342E"));
    private static readonly IBrush ActiveCyanBrush = new SolidColorBrush(Color.Parse("#38BDF8"));
    private static readonly IBrush ActiveGreenBrush = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush ActiveRedBrush = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush ActiveBlueBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush ActivePinkBrush = new SolidColorBrush(Color.Parse("#DB2777"));
    private static readonly IBrush ActiveAmberBrush = new SolidColorBrush(Color.Parse("#CA8A04"));
    private static readonly IBrush StickIdleRing = new SolidColorBrush(Color.Parse("#303B54"));
    private static readonly IBrush XboxStickIdleRing = new SolidColorBrush(Color.Parse("#33423A"));
    private static readonly IBrush TouchClickFill = new SolidColorBrush(Color.Parse("#1E314B"));
    private static readonly IBrush TouchIdleFill = new SolidColorBrush(Color.Parse("#161C29"));
    private static readonly IBrush MicLedOnBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush MicLedOffBrush = new SolidColorBrush(Color.Parse("#334155"));
    private static readonly IBrush GuideActiveBrush = new SolidColorBrush(Color.Parse("#166534"));
    private static readonly IBrush GuideIdleBrush = new SolidColorBrush(Color.Parse("#1A2620"));
    private static readonly IBrush LightbarIdleBrush = new SolidColorBrush(Color.Parse("#2B354B"));

    private Color _cachedLbColor;
    private IBrush? _cachedLbBrush;

    public ControllerDiagram()
    {
        InitializeComponent();
        ResetDisconnected();
    }

    public void SetXboxMode(bool isXbox)
    {
        Ds5Layer.IsVisible = !isXbox;
        XboxLayer.IsVisible = isXbox;
    }

    public void UpdateTelemetry(TelemetrySample s, byte ledR, byte ledG, byte ledB, bool ledOverride)
    {
        bool isXbox = s.UsbMode == 1;
        SetXboxMode(isXbox);

        int dp = s.DPad;
        bool dpUp = dp == 0 || dp == 1 || dp == 7;
        bool dpRight = dp == 1 || dp == 2 || dp == 3;
        bool dpDown = dp == 3 || dp == 4 || dp == 5;
        bool dpLeft = dp == 5 || dp == 6 || dp == 7;

        if (isXbox)
        {
            UpdateXboxLayer(s, dpUp, dpRight, dpDown, dpLeft);
        }
        else
        {
            UpdateDualSenseLayer(s, dpUp, dpRight, dpDown, dpLeft, ledR, ledG, ledB, ledOverride);
        }
    }

    private void UpdateDualSenseLayer(
        TelemetrySample s,
        bool dpUp, bool dpRight, bool dpDown, bool dpLeft,
        byte ledR, byte ledG, byte ledB, bool ledOverride)
    {
        L2Fill.Width = (s.L2 / 255.0) * 74.0;
        R2Fill.Width = (s.R2 / 255.0) * 74.0;
        L2Lbl.Text = $"L2 {(s.L2 * 100 / 255)}%";
        R2Lbl.Text = $"R2 {(s.R2 * 100 / 255)}%";

        BtnL1.Background = s.L1 ? ActiveCyanBrush : IdleBtnBrush;
        BtnR1.Background = s.R1 ? ActiveCyanBrush : IdleBtnBrush;
        BtnCreate.Background = s.Create ? ActiveCyanBrush : IdleBtnBrush;
        BtnOptions.Background = s.Options ? ActiveCyanBrush : IdleBtnBrush;
        BtnPs.Background = s.Ps ? ActiveCyanBrush : IdleBtnBrush;
        BtnMic.Background = s.MicMuteBtn ? ActiveCyanBrush : IdleBtnBrush;
        MicLedStrip.Background = s.MicLedMode > 0 ? MicLedOnBrush : MicLedOffBrush;

        DpadUp.Background = dpUp ? ActiveCyanBrush : IdlePadBrush;
        DpadRight.Background = dpRight ? ActiveCyanBrush : IdlePadBrush;
        DpadDown.Background = dpDown ? ActiveCyanBrush : IdlePadBrush;
        DpadLeft.Background = dpLeft ? ActiveCyanBrush : IdlePadBrush;

        BtnTriangle.Background = s.Triangle ? ActiveGreenBrush : IdleBtnBrush;
        BtnCircle.Background = s.Circle ? ActiveRedBrush : IdleBtnBrush;
        BtnCross.Background = s.Cross ? ActiveBlueBrush : IdleBtnBrush;
        BtnSquare.Background = s.Square ? ActivePinkBrush : IdleBtnBrush;

        PositionStick(LeftStickDome, LeftStickRing, 154, 148, s.Lx, s.Ly, s.L3, StickIdleRing, ActiveCyanBrush);
        PositionStick(RightStickDome, RightStickRing, 294, 148, s.Rx, s.Ry, s.R3, StickIdleRing, ActiveCyanBrush);

        Ds5TouchpadShape.Fill = s.TouchpadClick ? TouchClickFill : TouchIdleFill;
        var lbColor = ledOverride ? Color.FromRgb(ledR, ledG, ledB) : Color.Parse("#00A0FF");
        if (_cachedLbBrush == null || _cachedLbColor != lbColor)
        {
            _cachedLbColor = lbColor;
            _cachedLbBrush = new SolidColorBrush(lbColor);
        }
        Ds5LightbarGlow.Stroke = _cachedLbBrush;

        PositionTouchDot(TpDot0, s.Touch0Down, s.Touch0X, s.Touch0Y);
        PositionTouchDot(TpDot1, s.Touch1Down, s.Touch1X, s.Touch1Y);
    }

    private void UpdateXboxLayer(TelemetrySample s, bool dpUp, bool dpRight, bool dpDown, bool dpLeft)
    {
        LtFill.Width = (s.L2 / 255.0) * 76.0;
        RtFill.Width = (s.R2 / 255.0) * 76.0;
        LtLbl.Text = $"LT {(s.L2 * 100 / 255)}%";
        RtLbl.Text = $"RT {(s.R2 * 100 / 255)}%";

        BtnLb.Background = s.L1 ? ActiveGreenBrush : IdleXboxBtnBrush;
        BtnRb.Background = s.R1 ? ActiveGreenBrush : IdleXboxBtnBrush;
        BtnXboxBack.Background = s.Create ? ActiveGreenBrush : IdleXboxBtnBrush;
        BtnXboxStart.Background = s.Options ? ActiveGreenBrush : IdleXboxBtnBrush;
        BtnXboxGuide.Background = s.Ps ? GuideActiveBrush : GuideIdleBrush;

        XbxDpadUp.Background = dpUp ? ActiveGreenBrush : IdleXboxPadBrush;
        XbxDpadRight.Background = dpRight ? ActiveGreenBrush : IdleXboxPadBrush;
        XbxDpadDown.Background = dpDown ? ActiveGreenBrush : IdleXboxPadBrush;
        XbxDpadLeft.Background = dpLeft ? ActiveGreenBrush : IdleXboxPadBrush;

        BtnXboxA.Background = s.Cross ? ActiveGreenBrush : IdleXboxBtnBrush;
        BtnXboxB.Background = s.Circle ? ActiveRedBrush : IdleXboxBtnBrush;
        BtnXboxX.Background = s.Square ? ActiveBlueBrush : IdleXboxBtnBrush;
        BtnXboxY.Background = s.Triangle ? ActiveAmberBrush : IdleXboxBtnBrush;

        PositionStick(XbxLsDome, XbxLsRing, 99, 91, s.Lx, s.Ly, s.L3, XboxStickIdleRing, ActiveGreenBrush);
        PositionStick(XbxRsDome, XbxRsRing, 293, 158, s.Rx, s.Ry, s.R3, XboxStickIdleRing, ActiveGreenBrush);
    }

    private static void PositionStick(
        Ellipse dome, Ellipse ring, double baseLeft, double baseTop,
        byte axisX, byte axisY, bool click, IBrush idleRing, IBrush activeRing)
    {
        double dx = ((axisX - 128) / 128.0) * 11.0;
        double dy = ((axisY - 128) / 128.0) * 11.0;
        Canvas.SetLeft(dome, baseLeft + dx);
        Canvas.SetTop(dome, baseTop + dy);
        ring.Stroke = click ? activeRing : idleRing;
    }

    private static void PositionTouchDot(Ellipse dot, bool down, ushort x, ushort y)
    {
        dot.IsVisible = down;
        if (!down) return;
        Canvas.SetLeft(dot, Math.Clamp(x / 1920.0 * 130.0, 2, 130));
        Canvas.SetTop(dot, Math.Clamp(y / 1080.0 * 60.0, 2, 60));
    }

    public void ResetDisconnected()
    {
        L2Fill.Width = 0;
        R2Fill.Width = 0;
        L2Lbl.Text = "L2  --";
        R2Lbl.Text = "R2  --";

        BtnL1.Background = IdleBtnBrush;
        BtnR1.Background = IdleBtnBrush;
        BtnCreate.Background = IdleBtnBrush;
        BtnOptions.Background = IdleBtnBrush;
        BtnPs.Background = IdleBtnBrush;
        BtnMic.Background = IdleBtnBrush;
        MicLedStrip.Background = MicLedOffBrush;

        DpadUp.Background = IdlePadBrush;
        DpadRight.Background = IdlePadBrush;
        DpadDown.Background = IdlePadBrush;
        DpadLeft.Background = IdlePadBrush;

        BtnTriangle.Background = IdleBtnBrush;
        BtnCircle.Background = IdleBtnBrush;
        BtnCross.Background = IdleBtnBrush;
        BtnSquare.Background = IdleBtnBrush;

        PositionStick(LeftStickDome, LeftStickRing, 154, 148, 128, 128, false, StickIdleRing, ActiveCyanBrush);
        PositionStick(RightStickDome, RightStickRing, 294, 148, 128, 128, false, StickIdleRing, ActiveCyanBrush);

        Ds5TouchpadShape.Fill = TouchIdleFill;
        Ds5LightbarGlow.Stroke = LightbarIdleBrush;
        TpDot0.IsVisible = false;
        TpDot1.IsVisible = false;

        LtFill.Width = 0;
        RtFill.Width = 0;
        LtLbl.Text = "LT  --";
        RtLbl.Text = "RT  --";
        BtnLb.Background = IdleXboxBtnBrush;
        BtnRb.Background = IdleXboxBtnBrush;
        BtnXboxBack.Background = IdleXboxBtnBrush;
        BtnXboxStart.Background = IdleXboxBtnBrush;
        BtnXboxGuide.Background = GuideIdleBrush;
        XbxDpadUp.Background = IdleXboxPadBrush;
        XbxDpadRight.Background = IdleXboxPadBrush;
        XbxDpadDown.Background = IdleXboxPadBrush;
        XbxDpadLeft.Background = IdleXboxPadBrush;
        BtnXboxA.Background = IdleXboxBtnBrush;
        BtnXboxB.Background = IdleXboxBtnBrush;
        BtnXboxX.Background = IdleXboxBtnBrush;
        BtnXboxY.Background = IdleXboxBtnBrush;
        PositionStick(XbxLsDome, XbxLsRing, 99, 91, 128, 128, false, XboxStickIdleRing, ActiveGreenBrush);
        PositionStick(XbxRsDome, XbxRsRing, 293, 158, 128, 128, false, XboxStickIdleRing, ActiveGreenBrush);
    }
}
