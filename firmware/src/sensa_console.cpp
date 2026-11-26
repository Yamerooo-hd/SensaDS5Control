#include "sensa_console.h"
#include "sensa_bt.h"
#include "sensa_dsp.h"
#include "sensa_state.h"

#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include "hardware/watchdog.h"
#include "pico/bootrom.h"
#include "pico/time.h"
#include "tusb.h"

namespace sensa {
namespace {

constexpr size_t kTxRingSize = 4096;
char tx_ring[kTxRingSize];
size_t tx_head = 0;
size_t tx_tail = 0;
bool was_connected = false;

char cmd_buf[128];
size_t cmd_len = 0;

void ring_push(char c) {
    size_t next = (tx_head + 1) % kTxRingSize;
    if (next == tx_tail) {
        tx_tail = (tx_tail + 1) % kTxRingSize; // Drop oldest byte
    }
    tx_ring[tx_head] = c;
    tx_head = next;
}

void ring_push_str(char const *s, size_t len) {
    for (size_t i = 0; i < len; ++i) {
        if (s[i] == '\n') {
            ring_push('\r');
        }
        ring_push(s[i]);
    }
}

bool eq_ci(char const *a, char const *b) {
    while (*a && *b) {
        char ca = *a;
        char cb = *b;
        if (ca >= 'A' && ca <= 'Z') ca += 32;
        if (cb >= 'A' && cb <= 'Z') cb += 32;
        if (ca != cb) return false;
        ++a;
        ++b;
    }
    return *a == 0 && *b == 0;
}

void handle_command(char *line) {
    char *tokens[14]{};
    int argc = 0;
    char *p = line;
    while (*p && argc < 14) {
        while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') ++p;
        if (!*p) break;
        tokens[argc++] = p;
        while (*p && *p != ' ' && *p != '\t' && *p != '\r' && *p != '\n') ++p;
        if (*p) *p++ = '\0';
    }
    if (argc == 0) return;

    char const *cmd = tokens[0];
    if (eq_ci(cmd, "help") || eq_ci(cmd, "?")) {
        console_print_help();
    } else if (eq_ci(cmd, "status") || eq_ci(cmd, "s")) {
        console_print_status();
    } else if (eq_ci(cmd, "sdp")) {
        if (!bt_start_sdp_discovery()) {
            console_printf("[SDP] Cannot run: controller ACL not connected\n");
        }
    } else if (eq_ci(cmd, "dbg")) {
        bt_debug_dump();
    } else if (eq_ci(cmd, "unlock")) {
        uint8_t m = argc >= 2 ? static_cast<uint8_t>(strtol(tokens[1], nullptr, 0)) : 0;
        bt_trigger_unlock_sequence(m);
    } else if (eq_ci(cmd, "btmon") && argc >= 2) {
        bool en = atoi(tokens[1]) != 0;
        bt_set_btmon(en);
        console_printf("[MON] %s\n", en ? "ON" : "OFF");
    } else if (eq_ci(cmd, "tele") && argc >= 2) {
        bool en = atoi(tokens[1]) != 0;
        bt_set_telemetry_stream(en);
        console_printf("[TELE] %s\n", en ? "ON (30Hz)" : "OFF");
    } else if (eq_ci(cmd, "micled") && argc >= 2) {
        uint8_t m = static_cast<uint8_t>(strtol(tokens[1], nullptr, 0));
        bt_set_mic_led(m);
        console_printf("[MICLED] mode=%u\n", m);
    } else if (eq_ci(cmd, "trig") && argc >= 3) {
        uint8_t side = eq_ci(tokens[1], "r") ? 0 : (eq_ci(tokens[1], "l") ? 1 : 2);
        if (eq_ci(tokens[2], "auto")) {
            bt_set_trigger_override(side, false, nullptr);
            console_printf("[TRIG] side=%u -> AUTO (game)\n", side);
        } else {
            uint8_t d[11]{};
            for (int i = 0; i < 11 && (2 + i) < argc; ++i) {
                d[i] = static_cast<uint8_t>(strtol(tokens[2 + i], nullptr, 0));
            }
            bt_set_trigger_override(side, true, d);
            console_printf("[TRIG] side=%u mode=0x%02X p1=0x%02X p2=0x%02X p3=0x%02X\n",
                           side, d[0], d[1], d[2], d[3]);
        }
    } else if (eq_ci(cmd, "mode") && argc >= 2) {
        char const *m = tokens[1];
        if (eq_ci(m, "31")) g_haptic_mode = HapticMode::Mode31Synth;
        else if (eq_ci(m, "32")) g_haptic_mode = HapticMode::Mode32SAxense;
        else if (eq_ci(m, "36")) g_haptic_mode = HapticMode::Mode36Haptics;
        else if (eq_ci(m, "39")) g_haptic_mode = HapticMode::Mode39Probe;
        else if (eq_ci(m, "hybrid")) g_haptic_mode = HapticMode::Mode31Hybrid32;
        else if (eq_ci(m, "raw")) g_haptic_mode = HapticMode::ModeRawPass;
        else {
            console_printf("[MODE] Unknown mode '%s' (use 31|32|36|39|hybrid|raw)\n", m);
            return;
        }
        console_printf("[MODE] Switched live to: %s\n", haptic_mode_name(g_haptic_mode));
    } else if (eq_ci(cmd, "pulse")) {
        int r = argc >= 2 ? atoi(tokens[1]) : 180;
        int l = argc >= 3 ? atoi(tokens[2]) : r;
        int ms = argc >= 4 ? atoi(tokens[3]) : 350;
        r = r < 0 ? 0 : (r > 255 ? 255 : r);
        l = l < 0 ? 0 : (l > 255 ? 255 : l);
        ms = ms < 20 ? 20 : (ms > 5000 ? 5000 : ms);
        dsp_trigger_test_rumble(static_cast<uint8_t>(r), static_cast<uint8_t>(l), static_cast<uint32_t>(ms));
        console_printf("[TEST] 0x31 pulse R=%d L=%d for %d ms\n", r, l, ms);
    } else if (eq_ci(cmd, "test32") || eq_ci(cmd, "test36") || eq_ci(cmd, "test39")) {
        int amp = argc >= 2 ? atoi(tokens[1]) : 100;
        int hz = argc >= 3 ? atoi(tokens[2]) : 90;
        int ms = argc >= 4 ? atoi(tokens[3]) : 600;
        amp = amp < 1 ? 1 : (amp > 127 ? 127 : amp);
        hz = hz < 15 ? 15 : (hz > 400 ? 400 : hz);
        ms = ms < 30 ? 30 : (ms > 5000 ? 5000 : ms);
        HapticMode proto = eq_ci(cmd, "test32") ? HapticMode::Mode32SAxense
                         : eq_ci(cmd, "test36") ? HapticMode::Mode36Haptics
                                                : HapticMode::Mode39Probe;
        dsp_trigger_test_wave(proto, static_cast<uint8_t>(amp), static_cast<uint16_t>(hz), static_cast<uint32_t>(ms));
        console_printf("[TEST] Pure %s sine wave amp=%d/127 freq=%dHz for %d ms\n",
                       haptic_mode_name(proto), amp, hz, ms);
    } else if (eq_ci(cmd, "tune") && argc >= 3) {
        char const *k = tokens[1];
        int v = atoi(tokens[2]);
        if (eq_ci(k, "floor")) g_dsp_config.noise_floor_pct = static_cast<uint8_t>(v < 0 ? 0 : (v > 50 ? 50 : v));
        else if (eq_ci(k, "gain")) g_dsp_config.gain_x10 = static_cast<uint16_t>(v < 1 ? 1 : (v > 100 ? 100 : v));
        else if (eq_ci(k, "min")) g_dsp_config.min_rumble = static_cast<uint8_t>(v < 0 ? 0 : (v > 150 ? 150 : v));
        else if (eq_ci(k, "hold")) g_dsp_config.hold_ms = static_cast<uint16_t>(v < 10 ? 10 : (v > 500 ? 500 : v));
        else if (eq_ci(k, "decay")) g_dsp_config.decay_ms = static_cast<uint16_t>(v < 10 ? 10 : (v > 500 ? 500 : v));
        else if (eq_ci(k, "hgain")) g_dsp_config.haptic_gain_pct = static_cast<uint8_t>(v < 25 ? 25 : (v > 255 ? 255 : v));
        else {
            console_printf("[TUNE] Keys: floor | gain (x10) | min | hold | decay | hgain\n");
            return;
        }
        console_printf("[TUNE] floor=%u%% gain=%.1fx min=%u hold=%ums decay=%ums hgain=%u%%\n",
                       g_dsp_config.noise_floor_pct, g_dsp_config.gain_x10 / 10.0f,
                       g_dsp_config.min_rumble, g_dsp_config.hold_ms, g_dsp_config.decay_ms,
                       g_dsp_config.haptic_gain_pct);
    } else if (eq_ci(cmd, "led") && argc >= 2) {
        if (eq_ci(tokens[1], "auto")) {
            bt_clear_override_led();
            console_printf("[LED] Released to game (AUTO)\n");
        } else if (argc >= 4) {
            uint8_t r = static_cast<uint8_t>(atoi(tokens[1]));
            uint8_t g = static_cast<uint8_t>(atoi(tokens[2]));
            uint8_t b = static_cast<uint8_t>(atoi(tokens[3]));
            bt_set_override_led(r, g, b);
            console_printf("[LED] Sent R=%u G=%u B=%u\n", r, g, b);
        } else {
            console_printf("[LED] Usage: led <r> <g> <b> | led auto\n");
        }
    } else if (eq_ci(cmd, "usbmode")) {
        if (n < 2) {
            console_printf("[USBMODE] Active: %s (usage: usbmode <ds5|x360|xseries>)\n", usb_mode_name(g_usb_mode));
        } else {
            UsbMode target = g_usb_mode;
            bool ok = true;
            if (eq_ci(tokens[1], "ds5")) target = UsbMode::DualSense;
            else if (eq_ci(tokens[1], "x360")) target = UsbMode::Xbox360;
            else if (eq_ci(tokens[1], "xseries")) target = UsbMode::XboxSeries;
            else ok = false;
            if (!ok) {
                console_printf("[USBMODE] Unknown '%s'. Use ds5, x360, or xseries.\n", tokens[1]);
            } else {
                watchdog_hw->scratch[0] = kUsbModeScratchMagic;
                watchdog_hw->scratch[1] = static_cast<uint32_t>(target);
                console_printf("[USBMODE] Switching USB identity to %s -> rebooting...\n", usb_mode_name(target));
                console_poll();
                sleep_ms(60);
                watchdog_reboot(0, 0, 0);
            }
        }
    } else if (eq_ci(cmd, "pair")) {
        bt_start_pairing();
    } else if (eq_ci(cmd, "forget")) {
        bt_forget_all();
    } else if (eq_ci(cmd, "disconnect")) {
        bt_disconnect_now();
    } else if (eq_ci(cmd, "reboot")) {
        console_printf("[SYS] Rebooting...\n");
        console_poll();
        sleep_ms(30);
        watchdog_reboot(0, 0, 0);
    } else if (eq_ci(cmd, "bootsel")) {
        console_printf("[SYS] Entering USB BOOTSEL...\n");
        console_poll();
        sleep_ms(30);
        reset_usb_boot(0, 0);
    } else {
        console_printf("[CLI] Unknown command '%s'. Type 'help'.\n", cmd);
    }
}

} // namespace

void console_init() {
    tx_head = 0;
    tx_tail = 0;
    cmd_len = 0;
}

void console_printf(char const *fmt, ...) {
    char buf[256];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n > 0) {
        size_t len = static_cast<size_t>(n);
        if (len >= sizeof(buf)) len = sizeof(buf) - 1;
        ring_push_str(buf, len);
    }
}

void console_hexdump(char const *tag, uint8_t const *data, uint16_t len) {
    console_printf("%s (%u bytes):\n", tag, len);
    for (uint16_t off = 0; off < len; off += 16) {
        char line[96];
        int pos = snprintf(line, sizeof(line), "  %04X:", off);
        for (uint16_t i = 0; i < 16 && (off + i) < len; ++i) {
            pos += snprintf(line + pos, sizeof(line) - pos, " %02X", data[off + i]);
        }
        line[pos++] = '\n';
        ring_push_str(line, static_cast<size_t>(pos));
    }
}

void console_print_help() {
    console_printf("\n=== SensaBridge Ground-Up Pico 2 W Console ===\n");
    console_printf("Commands:\n");
    console_printf("  status              - Show live BT, USB audio, SDP & haptic stats\n");
    console_printf("  sdp                 - Query Cosmic Byte Sensa SDP HID descriptor + PnP ID\n");
    console_printf("  mode <31|32|36|39|hybrid|raw> - Switch haptics output mode live\n");
    console_printf("  pulse <R> <L> <ms>  - Test 0x31 classic dual-motor pulse (e.g. pulse 200 200 400)\n");
    console_printf("  test32 <amp> <hz> <ms> - Test pure 0x32 SAxense sine wave (zero 0x31 interference)\n");
    console_printf("  test36 <amp> <hz> <ms> - Test pure 0x36 ds5haptics wave (zero 0x31 interference)\n");
    console_printf("  test39 <amp> <hz> <ms> - Test 0x39 long frame wave\n");
    console_printf("  tune <floor|gain|min|hold|decay|hgain> <val>\n");
    console_printf("  led <r> <g> <b> | auto - RGB lightbar override / auto\n");
    console_printf("  micled <0|1|2>      - Mic Mute LED (0=off, 1=solid, 2=pulse)\n");
    console_printf("  trig <R|L|both> <auto|mode p0..p9> - Adaptive trigger override\n");
    console_printf("  usbmode <ds5|x360|xseries> - Switch USB identity & soft-reboot\n");
    console_printf("  tele <0|1> | btmon <0|1> | unlock <0..4> | dbg\n");
    console_printf("  pair | forget | disconnect | reboot | bootsel\n\n");
}

void console_print_status() {
    auto const &t = g_telemetry;
    console_printf("\n--- SensaBridge Status ---\n");
    console_printf("Mode: %s | USBID: %s\n", haptic_mode_name(g_haptic_mode), usb_mode_name(g_usb_mode));
    console_printf("USB: mounted=%d spkStream=%d micStream=%d vol=%u%% mute=%d audioFrames=%lu usb0x02=%lu\n",
                   t.usb_mounted ? 1 : 0, t.usb_speaker_streaming ? 1 : 0, t.usb_mic_streaming ? 1 : 0,
                   t.usb_volume_pct, t.usb_muted ? 1 : 0,
                   static_cast<unsigned long>(t.usb_audio_frames),
                   static_cast<unsigned long>(t.usb_0x02_rx_count));
    console_printf("BT:  acl=%d ctrl=%d intr=%d pair=%d bonded=%d mac=%02X:%02X:%02X:%02X:%02X:%02X name='%s'\n",
                   t.bt_acl_connected ? 1 : 0, t.bt_control_ready ? 1 : 0, t.bt_interrupt_ready ? 1 : 0,
                   t.bt_pairing_active ? 1 : 0, t.bt_has_bonded_key ? 1 : 0,
                   t.bt_addr[0], t.bt_addr[1], t.bt_addr[2], t.bt_addr[3], t.bt_addr[4], t.bt_addr[5],
                   t.bt_name);
    console_printf("SDP: queried=%d PnP=%04X:%04X v%04X HIDDescLen=%u\n",
                   t.sdp.queried ? 1 : 0, t.sdp.pnp_vid, t.sdp.pnp_pid, t.sdp.pnp_version, t.sdp.hid_desc_len);
    console_printf("PKT: rx0x31=%lu rx0x01=%lu tx0x31=%lu tx0x32=%lu tx0x36=%lu tx0x39=%lu fail=%lu\n",
                   static_cast<unsigned long>(t.bt_0x31_rx_count),
                   static_cast<unsigned long>(t.bt_0x01_rx_count),
                   static_cast<unsigned long>(t.bt_0x31_tx_count),
                   static_cast<unsigned long>(t.bt_0x32_tx_count),
                   static_cast<unsigned long>(t.bt_0x36_tx_count),
                   static_cast<unsigned long>(t.bt_0x39_tx_count),
                   static_cast<unsigned long>(t.l2cap_fail_count));
    console_printf("DSP: peakAudio(L=%u.%u%% R=%u.%u%%) peakHaptic(L=%u.%u%% R=%u.%u%%)\n",
                   t.peak_audio_l_pct / 10, t.peak_audio_l_pct % 10,
                   t.peak_audio_r_pct / 10, t.peak_audio_r_pct % 10,
                   t.peak_haptic_l_pct / 10, t.peak_haptic_l_pct % 10,
                   t.peak_haptic_r_pct / 10, t.peak_haptic_r_pct % 10);
    console_printf("OUT: game(R=%u L=%u) synth(R=%u L=%u) sent(R=%u L=%u) trig(R=0x%02X L=0x%02X)\n\n",
                   t.last_game_right, t.last_game_left,
                   t.last_synth_right, t.last_synth_left,
                   t.last_sent_right, t.last_sent_left,
                   t.last_r2_mode, t.last_l2_mode);
}

void console_poll() {
    if (!tud_inited()) return;

    bool connected = tud_cdc_connected();
    if (connected && !was_connected) {
        was_connected = true;
        console_print_help();
        console_print_status();
    } else if (!connected && was_connected) {
        was_connected = false;
    }

    // Drain outgoing log ring into TinyUSB CDC TX FIFO
    uint32_t avail = tud_cdc_write_available();
    if (avail > 0 && tx_tail != tx_head) {
        char chunk[64];
        size_t n = 0;
        while (n < sizeof(chunk) && n < avail && tx_tail != tx_head) {
            chunk[n++] = tx_ring[tx_tail];
            tx_tail = (tx_tail + 1) % kTxRingSize;
        }
        if (n > 0) {
            tud_cdc_write(chunk, n);
            tud_cdc_write_flush();
        }
    }

    // Read incoming commands
    while (tud_cdc_available() > 0) {
        uint8_t ch_byte = 0;
        if (tud_cdc_read(&ch_byte, 1) != 1) break;
        char c = static_cast<char>(ch_byte);
        if (c == '\r' || c == '\n') {
            if (cmd_len > 0) {
                cmd_buf[cmd_len] = '\0';
                console_printf("> %s\n", cmd_buf);
                handle_command(cmd_buf);
                cmd_len = 0;
            }
        } else if (c == '\b' || c == 127) {
            if (cmd_len > 0) cmd_len--;
        } else if (cmd_len + 1 < sizeof(cmd_buf)) {
            cmd_buf[cmd_len++] = c;
        }
    }
}

} // namespace sensa
