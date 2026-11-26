#ifndef SENSA_STATE_H
#define SENSA_STATE_H

#include <array>
#include <cstddef>
#include <cstdint>

#include "pico.h"

namespace sensa {

enum class UsbMode : uint8_t {
    DualSense = 0,   // 054C:0CE6 DualSense (4ch Audio + HID + CDC)
    Xbox360 = 1,     // 045E:028E Xbox 360 Wired XInput + CDC
    XboxSeries = 2,  // 045E:0B12 Experimental Xbox Series [MS-GIPUSB] + Impulse Triggers + CDC
};

inline char const *usb_mode_name(UsbMode m) {
    switch (m) {
        case UsbMode::DualSense: return "DualSense (054C:0CE6)";
        case UsbMode::Xbox360: return "Xbox 360 XInput (1209:DB05)";
        case UsbMode::XboxSeries: return "Xbox Series GIP [Experimental] (1209:DB06)";
        default: return "unknown";
    }
}

constexpr uint32_t kUsbModeScratchMagic = 0x53454E53u; // "SENS"

enum class HapticMode : uint8_t {
    Mode31Synth = 0,     // 0x31 (78B): Audio SimFx + Game 0x02 rumble merged cleanly
    Mode32SAxense = 1,   // 0x32 (142B): Pure SAxense audio-haptic blocks (zero 0x31 HapticsSelect)
    Mode36Haptics = 2,   // 0x36 (398B): Pure ds5haptics block + silence Opus (zero 0x31)
    Mode39Probe = 3,     // 0x39 (547B): Extended BT audio/haptic packet probe
    Mode31Hybrid32 = 4,  // Sends 0x32 audio-haptic waveform + 0x31 triggers/LEDs (no HapticsSelect)
    ModeRawPass = 5,     // 0x31 (78B): Raw USB 0x02 passthrough only (no audio synthesis)
};

inline char const *haptic_mode_name(HapticMode mode) {
    switch (mode) {
        case HapticMode::Mode31Synth: return "31 (SimFx + Game 0x02)";
        case HapticMode::Mode32SAxense: return "32 (Pure SAxense 142B)";
        case HapticMode::Mode36Haptics: return "36 (Pure ds5haptics 398B)";
        case HapticMode::Mode39Probe: return "39 (Long 547B Probe)";
        case HapticMode::Mode31Hybrid32: return "hybrid (0x32 Haptics + 0x31 Triggers/LED)";
        case HapticMode::ModeRawPass: return "raw (USB 0x02 Passthrough Only)";
        default: return "unknown";
    }
}

enum class ImpulseTriggerMode : uint8_t {
    Off = 0,         // Disable synthesized impulse triggers (normal L2/R2 or manual presets)
    Always = 1,      // Game rumble drives L2/R2 0x26 trigger vibration whenever rumbling
    WhenPulled = 2,  // Only drives L2/R2 0x26 trigger vibration while that trigger is squeezed
};

struct DspConfig {
    // Tuned defaults baked from live-tuning session (Control Resonant):
    // floor=8% eliminates idle buzz, gain=3.0x, min=10 (overcomes stiction without buzz),
    // hold=25ms (sharp cues), decay=55ms (clean tail)
    uint8_t noise_floor_pct = 8;       // Ignore ch3/ch4 signal below 8.0% peak
    uint16_t gain_x10 = 30;            // 3.0x overall scaling on raw haptic delta
    uint8_t min_rumble = 10;           // Minimum motor drive once triggered (overcomes motor stiction)
    uint16_t hold_ms = 25;             // Sustain window per impact
    uint16_t decay_ms = 55;            // Decay window after hold
    bool include_speaker_bass = true;  // Fold ch0/ch1 sub-bass transients subtly when ch3/4 present or strong explosion
    uint16_t speaker_bass_pct = 20;    // 0..100% weight of low-freq speaker energy above 25% threshold
    uint8_t haptic_gain_pct = 150;     // PCM waveform scale for 0x36/0x39 (ds5-untethered default 1.5x + tanh)
    ImpulseTriggerMode impulse_mode = ImpulseTriggerMode::Always; // Xbox 360-style L2/R2 impulse triggers from rumble
    uint8_t impulse_strength_pct = 85;                            // 0..100% intensity for synthesized 0x26 triggers
};

struct HostOutputState {
    bool valid = false;
    uint8_t payload[63]{}; // Common DualSense output payload (USB report 0x02 bytes [1..47+])
    uint16_t len = 0;
    uint32_t last_rx_us = 0;
};

struct SdpSummary {
    bool queried = false;
    bool hid_done = false;
    bool pnp_done = false;
    uint8_t hid_status = 0xFF;
    uint8_t pnp_status = 0xFF;
    uint16_t pnp_vid = 0;
    uint16_t pnp_pid = 0;
    uint16_t pnp_version = 0;
    uint16_t hid_desc_len = 0;
};

struct Telemetry {
    bool usb_mounted = false;
    bool usb_speaker_streaming = false;
    bool usb_mic_streaming = false;
    uint16_t usb_volume_pct = 100;
    bool usb_muted = false;

    bool bt_acl_connected = false;
    bool bt_control_ready = false;
    bool bt_interrupt_ready = false;
    bool bt_pairing_active = false;
    bool bt_has_bonded_key = false;
    uint8_t bt_addr[6]{};
    char bt_name[48]{};

    uint32_t usb_audio_frames = 0;
    uint32_t usb_0x02_rx_count = 0;
    uint32_t bt_0x31_rx_count = 0;
    uint32_t bt_0x01_rx_count = 0;
    uint32_t bt_0x31_tx_count = 0;
    uint32_t bt_0x32_tx_count = 0;
    uint32_t bt_0x36_tx_count = 0;
    uint32_t bt_0x39_tx_count = 0;
    uint32_t l2cap_fail_count = 0;

    uint16_t peak_audio_l_pct = 0;
    uint16_t peak_audio_r_pct = 0;
    uint16_t peak_haptic_l_pct = 0;
    uint16_t peak_haptic_r_pct = 0;
    uint8_t last_game_right = 0;
    uint8_t last_game_left = 0;
    uint8_t last_synth_right = 0;
    uint8_t last_synth_left = 0;
    uint8_t last_sent_right = 0;
    uint8_t last_sent_left = 0;
    uint8_t last_r2_mode = 0;
    uint8_t last_l2_mode = 0;

    SdpSummary sdp{};
};

// Shared runtime singletons (defined in main.cpp)
extern UsbMode g_usb_mode;
extern HapticMode g_haptic_mode;
extern DspConfig g_dsp_config;
extern Telemetry g_telemetry;
extern HostOutputState g_host_output;

// CRC32 with Sony BT seeds (0xA2 for reports, 0x53 for SET_FEATURE)
inline constexpr uint32_t crc32_table_entry(uint32_t index) {
    for (unsigned bit = 0; bit < 8; ++bit) {
        index = (index >> 1) ^ (0xEDB88320u & -(index & 1u));
    }
    return index;
}

inline constexpr auto make_crc32_table() {
    std::array<uint32_t, 256> table{};
    for (uint32_t i = 0; i < 256; ++i) {
        table[i] = crc32_table_entry(i);
    }
    return table;
}

inline constexpr std::array<uint32_t, 256> __not_in_flash("crc32_lookup_table")
kCrc32Table = make_crc32_table();

__force_inline uint32_t crc32_seeded(uint8_t const *data, size_t size, uint32_t seed) {
    uint32_t crc = ~seed;
    while (size--) {
        crc = (crc >> 8) ^ kCrc32Table[(crc ^ *data++) & 0xFFu];
    }
    return ~crc;
}

__force_inline void write_bt_report_crc(uint8_t *report_after_a2, size_t report_len) {
    if (report_len < 4) return;
    // 0xEADA2D49 == crc32_seeded({0xA2}, 1, 0)
    uint32_t crc = crc32_seeded(report_after_a2, report_len - 4, 0xEADA2D49u);
    report_after_a2[report_len - 4] = static_cast<uint8_t>(crc & 0xFFu);
    report_after_a2[report_len - 3] = static_cast<uint8_t>((crc >> 8) & 0xFFu);
    report_after_a2[report_len - 2] = static_cast<uint8_t>((crc >> 16) & 0xFFu);
    report_after_a2[report_len - 1] = static_cast<uint8_t>((crc >> 24) & 0xFFu);
}

inline void write_bt_feature_crc(uint8_t *report_after_53, size_t report_len) {
    if (report_len < 4) return;
    // 0x2060EFC3 == crc32_seeded({0x53}, 1, 0)
    uint32_t crc = crc32_seeded(report_after_53, report_len - 4, 0x2060EFC3u);
    report_after_53[report_len - 4] = static_cast<uint8_t>(crc & 0xFFu);
    report_after_53[report_len - 3] = static_cast<uint8_t>((crc >> 8) & 0xFFu);
    report_after_53[report_len - 2] = static_cast<uint8_t>((crc >> 16) & 0xFFu);
    report_after_53[report_len - 1] = static_cast<uint8_t>((crc >> 24) & 0xFFu);
}

// Encode Xbox-style trigger vibration intensity (0..100%) into 11-byte DualSense 0x26 trigger block.
inline void write_ds5_vibration_trig(uint8_t dst[11], uint8_t pct) {
    for (int i = 0; i < 11; ++i) dst[i] = 0;
    if (pct == 0) {
        dst[0] = 0x05; // Reset / Off
        return;
    }
    int fp_raw = (static_cast<int>(pct) * 7 + 99) / 100;
    uint8_t fp = static_cast<uint8_t>(fp_raw < 1 ? 1 : (fp_raw > 7 ? 7 : fp_raw));
    uint8_t freq = static_cast<uint8_t>(18 + (static_cast<int>(pct) * 38) / 100); // 18..56 Hz
    uint32_t packed30 = 0;
    for (int z = 0; z < 10; ++z) {
        packed30 |= (static_cast<uint32_t>(fp & 0x07u) << (z * 3));
    }
    dst[0] = 0x26; // Official DualSense Vibration trigger mode
    dst[1] = 0xFF; // Zones 0..7 enabled
    dst[2] = 0x03; // Zones 8..9 enabled
    dst[3] = static_cast<uint8_t>(packed30 & 0xFFu);
    dst[4] = static_cast<uint8_t>((packed30 >> 8) & 0xFFu);
    dst[5] = static_cast<uint8_t>((packed30 >> 16) & 0xFFu);
    dst[6] = static_cast<uint8_t>((packed30 >> 24) & 0xFFu);
    dst[9] = freq;
}

} // namespace sensa

#endif
