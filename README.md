# SensaDS5Control

Wireless **DualSense (`054C:0CE6`)** and **Xbox 360 XInput (`1209:DB05`)** wireless bridge firmware (`SensaBridge v0.2.0`) for **Raspberry Pi Pico 2 W** + cross-platform **SensaDS5Control** desktop suite, engineered for the **Cosmic Byte Sensa (`PS5_JG11`)** controller.

---

## Quick Start (Standalone After First Setup)

1. Download **SensaDS5Control-Setup-win-x64.exe** (installer) or **SensaDS5Control-Portable-win-x64.zip** from the [Latest Release](https://github.com/Yamerooo-hd/SensaDS5Control/releases/latest).
2. Open **Firmware & Pico** in the sidebar and click **Install / Update Bundled Firmware**:
   - **Blank Pico 2 W:** Hold the white `BOOTSEL` button while plugging into USB (`RP2350` drive) — the app flashes the embedded `SensaBridge.uf2` automatically offline.
   - **Already Running SensaBridge / Pico USB Serial:** The app reboots your Pico into `BOOTSEL` automatically and flashes the update (preserving existing Bluetooth bonds and saved settings).
3. Pair your controller (first-time setup or after Nuke/Reset):
   - Hold **Share (Create) + PS** on your Cosmic Byte Sensa for ~3 seconds until the lightbar double-blinks.
   - Click **Start Pairing Scan (45s)** (or tap Pico **BOOTSEL 3×**).
4. **Play completely standalone:** Once paired and configured, your DSP tuning, adaptive trigger mode, RGB LED, deadzone, USB identity (DualSense / Xbox 360), and Bluetooth link key live permanently in Pico flash. The desktop app is **not needed** while gaming unless you want to tweak settings, use Touchpad/Gyro Mouse, or sync Windows mic mute.

---

## Physical Pico `BOOTSEL` Button Controls

Even without opening the desktop app, the physical `BOOTSEL` button on the Pico 2 W acts as a multi-function control:

| Action | Function |
| :--- | :--- |
| **1× Tap** | Connect / Disconnect controller |
| **2× Tap** | Toggle **DualSense (`054C:0CE6`) ↔ Xbox 360 XInput (`1209:DB05`)**, save to flash & reboot |
| **3× Tap** | Start 45s Bluetooth Pairing Scan |
| **4× Tap** | Reset / forget all paired controller bonds & start fresh pairing |
| **Hold 2s** | Reboot into USB `RP2350` BOOTSEL firmware flash mode |

> **Smart Idle LED:** When no controller is connected or actively pairing, the Pico onboard LED blinks for 12 seconds and turns off automatically instead of blinking indefinitely. Press **PS** on a paired controller or tap `BOOTSEL` anytime to reconnect.

---

## Key Features

- **Wireless Audio-Haptics & Impulse Triggers:** Hardware/firmware `0x36` (`0x91`+`0x90`+`0x92`) audio-to-haptics DSP, selectable packet modes (`0x36`, `0x32`, `0x31`), Xbox-style `0x26` Impulse Triggers in Xbox 360 mode, and configurable DSP thresholds/gains.
- **Dual USB Identity:** Instant switching between **DualSense** (`4ch USB Audio + HID`) and **Xbox 360 Wired XInput** (`1209:DB05`) via app sidebar or `2× BOOTSEL`.
- **Standalone Flash Storage + 1-Click Factory Reset:** CRC32-protected Pico flash settings persistence (`USB mode`, DSP, LED, deadzone, adaptive triggers) across reboots and `.uf2` updates, plus an offline bundled **`flash_nuke.uf2` (Nuke Pico Flash)** button with 2-step confirmation to wipe the Pico back to blank `RP2350`.
- **Live Controller Visualizer & Tester:** Real-time SVG DualSense & Xbox 360 controller overlays showing sticks, triggers, buttons, gyro/accelerometer axes, touchpad touchpoints, and radial stick deadzone (`0–25%`).
- **Adaptive Triggers:** `Follow Game (Auto)`, `Continuous Resistance`, `Weapon / Section`, `Machine Gun Vibration`, and `Bow Snap`.
- **Lighting, Themes & Companion Features:** RGB Lightbar + Player LEDs, 4 live UI themes (**Midnight Cobalt**, **Xbox Emerald**, **Cosmic Crimson**, **DualSense Frost**), Touchpad-to-Mouse, Gyro Mouse, bidirectional Windows Mic Mute sync, single-instance Windows system tray minimization, and Start with Windows.

---

## Branches

| Branch | Purpose |
| :--- | :--- |
| **`main`** | Active development branch — includes the hidden **Dev Console** tab (`Enable Developer Console & Diagnostics` / `Ctrl+Shift+D`), raw Pico CLI terminal, packet diagnostics, and latest in-progress changes. |
| **`release`** | Clean end-user release branch — omits Developer Console & dev toggles and replaces them with a simple **Update from Latest Release** button in **Firmware & Pico**; publishes automated Windows & firmware binaries to [GitHub Releases](https://github.com/Yamerooo-hd/SensaDS5Control/releases). |

---

## Repository Layout

- `firmware/` — Raspberry Pi Pico 2 W (`RP2350`) C++20 firmware (`SensaBridge.uf2`) + SRAM flash eraser (`flash_nuke.uf2`), built via GitHub Actions (`pico-sdk 2.3.0`, `TinyUSB`, `BTstack`)
- `app/` — Cross-platform **C# / .NET 8 + Avalonia UI 11** desktop suite (`SensaDS5Control.Core`, `SensaDS5Control.Platform.Windows`, `SensaDS5Control.App`) with offline bundled `.uf2` resources
- `packaging/windows/` — Inno Setup script (`SensaDS5Control.iss`) generating `SensaDS5Control-Setup-win-x64.exe` and `SensaDS5Control-Portable-win-x64.zip`
- `CREDITS.md` — Full attribution and third-party component details
- `LICENSE` — GNU Affero General Public License v3.0 (`AGPL-3.0`)
