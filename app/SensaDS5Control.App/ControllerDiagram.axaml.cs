using Avalonia.Controls;
using Avalonia.Media;
using SensaDS5Control.Core;

namespace SensaDS5Control.App;

public partial class ControllerDiagram : UserControl
{
    private static readonly IBrush IdleBtnBrush = new SolidColorBrush(Color.Parse("#242C3D"));
    private static readonly IBrush IdlePadBrush = new SolidColorBrush(Color.Parse("#2A3347"));
    private static readonly IBrush ActiveCyanBrush = new SolidColorBrush(Color.Parse("#38BDF8"));
    private static readonly IBrush ActiveGreenBrush = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush ActiveRedBrush = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush ActiveBlueBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush ActivePinkBrush = new SolidColorBrush(Color.Parse("#DB2777"));
    private static readonly IBrush StickIdleRing = new SolidColorBrush(Color.Parse("#2F3950"));
    private static readonly IBrush TouchClickFill = new SolidColorBrush(Color.Parse("#182538"));
    private static readonly IBrush TouchIdleFill = new SolidColorBrush(Color.Parse("#0F131C"));
    private static readonly IBrush MicLedOnBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush MicLedOffBrush = new SolidColorBrush(Color.Parse("#334155"));

    public ControllerDiagram()
    {
        InitializeComponent();
        ResetDisconnected();
    }

    public void UpdateTelemetry(TelemetrySample s, byte ledR, byte ledG, byte ledB, bool ledOverride)
    {
        // Trigger pressure bars
        L2Fill.Width = (s.L2 / 255.0) * 70.0;
        R2Fill.Width = (s.R2 / 255.0) * 70.0;
        L2Lbl.Text = $"L2 {(s.L2 * 100 / 255)}%";
        R2Lbl.Text = $"R2 {(s.R2 * 100 / 255)}%";

        // Bumpers / Meta
        BtnL1.Background = s.L1 ? ActiveCyanBrush : IdleBtnBrush;
        BtnR1.Background = s.R1 ? ActiveCyanBrush : IdleBtnBrush;
        BtnCreate.Background = s.Create ? ActiveCyanBrush : IdleBtnBrush;
        BtnOptions.Background = s.Options ? ActiveCyanBrush : IdleBtnBrush;
        BtnPs.Background = s.Ps ? ActiveCyanBrush : IdleBtnBrush;
        BtnMic.Background = s.MicMuteBtn ? ActiveCyanBrush : IdleBtnBrush;
        MicLedStrip.Background = s.MicLedMode > 0 ? MicLedOnBrush : MicLedOffBrush;

        // D-Pad (0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW, 8=Neutral)
        int dp = s.DPad;
        DpadUp.Background = (dp == 0 || dp == 1 || dp == 7) ? ActiveCyanBrush : IdlePadBrush;
        DpadRight.Background = (dp == 1 || dp == 2 || dp == 3) ? ActiveCyanBrush : IdlePadBrush;
        DpadDown.Background = (dp == 3 || dp == 4 || dp == 5) ? ActiveCyanBrush : IdlePadBrush;
        DpadLeft.Background = (dp == 5 || dp == 6 || dp == 7) ? ActiveCyanBrush : IdlePadBrush;

        // Face buttons
        BtnTriangle.Background = s.Triangle ? ActiveGreenBrush : IdleBtnBrush;
        BtnCircle.Background = s.Circle ? ActiveRedBrush : IdleBtnBrush;
        BtnCross.Background = s.Cross ? ActiveBlueBrush : IdleBtnBrush;
        BtnSquare.Background = s.Square ? ActivePinkBrush : IdleBtnBrush;

        // Thumbsticks (base at 149,152 and 281,152; travel +-11px)
        double lxOff = ((s.Lx - 128) / 128.0) * 11.0;
        double lyOff = ((s.Ly - 128) / 128.0) * 11.0;
        Canvas.SetLeft(LeftStickDome, 149 + lxOff);
        Canvas.SetTop(LeftStickDome, 152 + lyOff);
        LeftStickRing.Stroke = s.L3 ? ActiveCyanBrush : StickIdleRing;

        double rxOff = ((s.Rx - 128) / 128.0) * 11.0;
        double ryOff = ((s.Ry - 128) / 128.0) * 11.0;
        Canvas.SetLeft(RightStickDome, 281 + rxOff);
        Canvas.SetTop(RightStickDome, 152 + ryOff);
        RightStickRing.Stroke = s.R3 ? ActiveCyanBrush : StickIdleRing;

        // Touchpad click + lightbar color + finger dots
        LightbarBorder.Background = s.TouchpadClick ? TouchClickFill : TouchIdleFill;
        var lbColor = ledOverride ? Color.FromRgb(ledR, ledG, ledB) : Color.Parse("#00A0FF");
        LightbarBorder.BorderBrush = new SolidColorBrush(lbColor);

        TpDot0.IsVisible = s.Touch0Down;
        if (s.Touch0Down)
        {
            Canvas.SetLeft(TpDot0, Math.Clamp(s.Touch0X / 1920.0 * 132.0, 2, 132));
            Canvas.SetTop(TpDot0, Math.Clamp(s.Touch0Y / 1080.0 * 62.0, 2, 62));
        }
        TpDot1.IsVisible = s.Touch1Down;
        if (s.Touch1Down)
        {
            Canvas.SetLeft(TpDot1, Math.Clamp(s.Touch1X / 1920.0 * 132.0, 2, 132));
            Canvas.SetTop(TpDot1, Math.Clamp(s.Touch1Y / 1080.0 * 62.0, 2, 62));
        }
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

        Canvas.SetLeft(LeftStickDome, 149);
        Canvas.SetTop(LeftStickDome, 152);
        Canvas.SetLeft(RightStickDome, 281);
        Canvas.SetTop(RightStickDome, 152);
        LeftStickRing.Stroke = StickIdleRing;
        RightStickRing.Stroke = StickIdleRing;

        LightbarBorder.Background = TouchIdleFill;
        LightbarBorder.BorderBrush = new SolidColorBrush(Color.Parse("#293347"));
        TpDot0.IsVisible = false;
        TpDot1.IsVisible = false;
    }
}
