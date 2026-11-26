# Credits & Licenses

`SensaDS5Control` is a standalone monorepo containing the Raspberry Pi Pico 2 W wireless bridge firmware (`/firmware`) and the cross-platform control suite (`/app`).

## Firmware (`/firmware`)
Licensed under **GNU Affero General Public License v3.0 (AGPL-3.0)** (see `LICENSE`) due to the included RP2350 CYW43 PIO-SPI driver patches and linker overrides:

- **SundayMoments / DS5_Bridge** (`AGPL-3.0`)
  - `firmware/cmake/patch-cyw43.cmake`, `firmware/cmake/patches/*.inc`, and `firmware/cmake/linker/rp2350-core0-stack/*.incl`
  - Audited Pico SDK 2.3.0 CYW43 HCI/PIO-SPI transport patches and stack memory guards.
- **awalol / DS5Dongle** (`MIT`)
  - `firmware/src/ram_mem.c` — SRAM-resident copy helpers for the audio path.
  - DualSense USB Audio + HID descriptor layout foundation in `firmware/src/usb_descriptors.c`.
- **Midzer00 / ds5-untethered** (`GPL-3.0-or-later`)
  - Protocol and DSP insights referenced during development: `Flag1 |= 0x62` (`PowerSaveControl | HapticsLowPass | MotorPowerControl`), `0x20` BT audio buffer depth, and the 2-stage 1000 Hz LPF + `tanh` 48 kHz → 3 kHz haptics conversion design.
- **Third-Party SDKs (fetched at build time by CI):**
  - Raspberry Pi `pico-sdk` (`BSD-3-Clause`)
  - `TinyUSB` (`MIT`)
  - BlueKitchen `BTstack` (`BSD-3-Clause`)

## Original Work
- Ground-up `SensaBridge` firmware (`src/main.cpp`, `src/sensa_bt.cpp`, `src/sensa_dsp.cpp`, `src/sensa_usb.cpp`, `src/sensa_console.cpp`, `src/sensa_state.h`, `src/runtime_fail_fast.cpp`) including Cosmic Byte Sensa (`PS5_JG11`) wireless `0x36` (`0x91`+`0x90`+`0x92`) audio-haptic protocol support.
- `SensaDS5Control` desktop application (`/app`).
