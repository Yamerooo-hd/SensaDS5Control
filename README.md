# SensaDS5Control

Wireless DualSense / **Cosmic Byte Sensa (`PS5_JG11`)** bridge firmware for **Raspberry Pi Pico 2 W** + Windows (and future Linux) control suite.

## Repository Layout
- `firmware/` — Pico 2 W firmware (`SensaBridge.uf2`) built automatically by GitHub Actions
- `app/` — Cross-platform **C# / .NET 8 + Avalonia UI** desktop suite (`SensaDS5Control`)
- `CREDITS.md` — Full attribution and license details

## Key Features
- True wireless audio-haptics over Bluetooth (`0x36` `0x91` + `0x90` + `0x92` protocol)
- Live input, gyro/accelerometer, touchpad & back-paddle tester
- Custom adaptive trigger profiles (Resistance, Gun/Section, Weapon, Vibration, Bow)
- RGB lightbar & Mic Mute LED control + Windows mic mute sync
- Touchpad-to-Mouse mode
- USB identity switcher (DualSense / Xbox 360 XInput / Experimental Xbox Series Impulse Triggers)
- 1-click GitHub Releases firmware & software updater (`bootsel` -> `RP2350`)
