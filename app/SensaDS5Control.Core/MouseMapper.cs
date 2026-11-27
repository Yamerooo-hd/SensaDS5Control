namespace SensaDS5Control.Core;

/// Converts DualSense touchpad and gyro telemetry into smooth relative mouse input via IPlatformServices.
public sealed class MouseMapper
{
    private bool _wasTouch0;
    private ushort _lastX, _lastY;
    private bool _wasClick;
    private bool _wasRightClick;
    private bool _wasTwoFinger;
    private ushort _lastScrollY;
    private double _fracX, _fracY, _fracScroll;

    private double _gyroZeroX, _gyroZeroY;
    private bool _calibrating;
    private int _calSamples;
    private double _calSumX, _calSumY;

    public bool TouchpadMouseEnabled { get; set; }
    public bool TouchpadTapToClick { get; set; }
    public double TouchpadSensitivity { get; set; } = 1.2;

    public bool GyroMouseEnabled { get; set; }
    public bool GyroOnlyOnL2 { get; set; } = true;
    public int GyroActivationMode { get; set; } // 0=Hold L2, 1=Hold L1, 2=Always On
    public bool GyroInvertY { get; set; }
    public double GyroSensitivity { get; set; } = 0.035;
    public int GyroDeadzone { get; set; } = 35;

    private bool _wasTouchForTap;
    private long _tapStartMs;
    private double _tapMoved;
    private long _tapReleaseAtMs;
    private bool _tapClickDown;

    public void StartGyroCalibration()
    {
        _calibrating = true;
        _calSamples = 0;
        _calSumX = 0;
        _calSumY = 0;
    }

    public void ReleaseAllButtons(IPlatformServices platform)
    {
        _wasTouch0 = false;
        _wasTwoFinger = false;
        if (_wasClick)
        {
            if (_wasRightClick) platform.MouseRightClick(false);
            else platform.MouseLeftClick(false);
            _wasClick = false;
            _wasRightClick = false;
        }
    }

    public void Process(in TelemetrySample s, IPlatformServices platform)
    {
        if (!s.FullMode0x31) return;

        if (_calibrating)
        {
            _calSumX += s.GyroX;
            _calSumY += s.GyroY;
            if (++_calSamples >= 30)
            {
                _gyroZeroX = _calSumX / _calSamples;
                _gyroZeroY = _calSumY / _calSamples;
                _calibrating = false;
            }
        }

        double dx = 0, dy = 0;

        if (TouchpadMouseEnabled)
        {
            long nowMs = Environment.TickCount64;
            if (_tapClickDown && nowMs >= _tapReleaseAtMs)
            {
                _tapClickDown = false;
                if (!s.TouchpadClick)
                    platform.MouseLeftClick(false);
            }

            bool twoFingers = s.Touch0Down && s.Touch1Down;
            if (twoFingers)
            {
                if (_wasTwoFinger)
                {
                    int sy = s.Touch0Y - _lastScrollY;
                    if (Math.Abs(sy) < 300)
                    {
                        _fracScroll += -sy * 0.6;
                        int scrollInt = (int)_fracScroll;
                        if (scrollInt != 0)
                        {
                            _fracScroll -= scrollInt;
                            platform.MouseScroll(scrollInt);
                        }
                    }
                }
                _lastScrollY = s.Touch0Y;
                _wasTwoFinger = true;
                _wasTouch0 = false;
                _wasTouchForTap = false;
            }
            else
            {
                _wasTwoFinger = false;
                if (s.Touch0Down)
                {
                    if (_wasTouch0)
                    {
                        int rawDx = s.Touch0X - _lastX;
                        int rawDy = s.Touch0Y - _lastY;
                        if (Math.Abs(rawDx) < 350 && Math.Abs(rawDy) < 350)
                        {
                            dx += rawDx * TouchpadSensitivity;
                            dy += rawDy * TouchpadSensitivity;
                            _tapMoved += Math.Abs(rawDx) + Math.Abs(rawDy);
                        }
                    }
                    else
                    {
                        _tapStartMs = nowMs;
                        _tapMoved = 0;
                        _wasTouchForTap = true;
                    }
                    _lastX = s.Touch0X;
                    _lastY = s.Touch0Y;
                    _wasTouch0 = true;
                }
                else
                {
                    if (_wasTouchForTap && TouchpadTapToClick && !s.TouchpadClick && (nowMs - _tapStartMs) <= 220 && _tapMoved < 30)
                    {
                        _tapClickDown = true;
                        _tapReleaseAtMs = nowMs + 40;
                        platform.MouseLeftClick(true);
                    }
                    _wasTouch0 = false;
                    _wasTouchForTap = false;
                }
            }

            bool clicked = s.TouchpadClick;
            if (clicked != _wasClick)
            {
                _tapClickDown = false;
                if (clicked)
                {
                    bool rightSide = s.Touch0Down && s.Touch0X > 960;
                    _wasRightClick = rightSide;
                    if (rightSide) platform.MouseRightClick(true);
                    else platform.MouseLeftClick(true);
                }
                else
                {
                    if (_wasRightClick) platform.MouseRightClick(false);
                    else platform.MouseLeftClick(false);
                    _wasRightClick = false;
                }
                _wasClick = clicked;
            }
        }
        else
        {
            _wasTouchForTap = false;
            if (_tapClickDown)
            {
                _tapClickDown = false;
                platform.MouseLeftClick(false);
            }
            ReleaseAllButtons(platform);
        }

        bool gyroActive = GyroActivationMode switch
        {
            1 => s.L1,
            2 => true,
            _ => !GyroOnlyOnL2 || s.L2 > 40,
        };
        if (GyroMouseEnabled && gyroActive)
        {
            double gx = s.GyroX - _gyroZeroX;
            double gy = s.GyroY - _gyroZeroY;
            if (Math.Abs(gy) > GyroDeadzone) dx += -gy * GyroSensitivity;
            if (Math.Abs(gx) > GyroDeadzone) dy += (GyroInvertY ? gx : -gx) * GyroSensitivity;
        }

        _fracX += dx;
        _fracY += dy;
        int ix = (int)_fracX;
        int iy = (int)_fracY;
        if (ix != 0 || iy != 0)
        {
            _fracX -= ix;
            _fracY -= iy;
            platform.MoveMouseRelative(ix, iy);
        }
    }
}
