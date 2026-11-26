#include "sensa_usb.h"
#include "sensa_bt.h"
#include "sensa_console.h"
#include "sensa_state.h"
#include "usb_descriptors.h"

#include <algorithm>
#include <cstring>

#include "hardware/watchdog.h"
#include "pico/time.h"
#include "pico/unique_id.h"
#include "tusb.h"

namespace sensa {
namespace {

uint8_t latest_input_report[63]{
    0x80, 0x80, 0x80, 0x80, 0x00, 0x00, 0x01, 0x08
};
bool input_dirty = false;
bool hid_in_flight = false;

bool bt_ready = false;
bool usb_attached = false;

uint8_t mac_seed[6] = {0x00, 0xA5, 0x19, 0xF6, 0x0B, 0x02};

void init_neutral_report() {
    std::memset(latest_input_report, 0, sizeof(latest_input_report));
    latest_input_report[0] = 0x80; // LX
    latest_input_report[1] = 0x80; // LY
    latest_input_report[2] = 0x80; // RX
    latest_input_report[3] = 0x80; // RY
    latest_input_report[6] = 0x01; // SeqNo
    latest_input_report[7] = 0x08; // DPad neutral
}

void write_u16_le(uint8_t *p, uint16_t v) {
    p[0] = static_cast<uint8_t>(v & 0xFFu);
    p[1] = static_cast<uint8_t>((v >> 8) & 0xFFu);
}

void write_u32_le(uint8_t *p, uint32_t v) {
    p[0] = static_cast<uint8_t>(v & 0xFFu);
    p[1] = static_cast<uint8_t>((v >> 8) & 0xFFu);
    p[2] = static_cast<uint8_t>((v >> 16) & 0xFFu);
    p[3] = static_cast<uint8_t>((v >> 24) & 0xFFu);
}

uint16_t synthetic_dualsense_feature(uint8_t report_id, uint8_t *buf, uint16_t reqlen) {
    uint8_t rpt[64]{};
    rpt[0] = report_id;
    uint16_t total_len = 0;

    switch (report_id) {
        case 0x03: // Capabilities
            rpt[2] = 0x28;
            rpt[4] = 0x02 | 0x04 | 0x08 | 0x40;
            rpt[20] = 0x01 | 0x80;
            total_len = 48;
            break;
        case 0x05: { // Calibration
            static constexpr int16_t kCal[17] = {
                0, 0, 0,
                1024, -1024, 1024, -1024, 1024, -1024,
                64, 64,
                8192, -8192, 8192, -8192, 8192, -8192
            };
            for (int i = 0; i < 17; ++i) {
                write_u16_le(rpt + 1 + i * 2, static_cast<uint16_t>(kCal[i]));
            }
            total_len = 41;
            break;
        }
        case 0x09: // Pairing MAC
            std::memcpy(rpt + 1, mac_seed, 6);
            total_len = 20;
            break;
        case 0x20: // Firmware Info
            std::memcpy(rpt + 1, "Jul  4 2025", 11);
            std::memcpy(rpt + 12, "10:10:32", 8);
            write_u16_le(rpt + 20, 0x0002);
            write_u16_le(rpt + 22, 0x0004);
            write_u32_le(rpt + 24, 0x00000617u);
            write_u32_le(rpt + 28, 0x0110002au);
            write_u16_le(rpt + 44, 0x0630);
            write_u32_le(rpt + 48, 0x0001003cu);
            write_u32_le(rpt + 52, 0x0002000au);
            write_u32_le(rpt + 56, 0x00000006u);
            total_len = 64;
            break;
        default:
            std::memset(buf, 0, reqlen);
            return reqlen;
    }
    uint16_t payload_len = static_cast<uint16_t>(total_len - 1);
    uint16_t copy_len = std::min<uint16_t>(payload_len, reqlen);
    std::memcpy(buf, rpt + 1, copy_len);
    return copy_len;
}

void try_send_hid_input() {
    if (!g_telemetry.usb_mounted || !tud_hid_ready() || hid_in_flight || !input_dirty) {
        return;
    }
    if (tud_hid_report(0x01, latest_input_report, sizeof(latest_input_report))) {
        input_dirty = false;
        hid_in_flight = true;
    }
}

} // namespace

void usb_init() {
    init_neutral_report();
    pico_unique_board_id_t bid{};
    pico_get_unique_board_id(&bid);
    mac_seed[0] = static_cast<uint8_t>((bid.id[0] & 0xFEu) | 0x02u);
    std::memcpy(mac_seed + 1, bid.id + 1, 5);

    tusb_rhport_init_t dev_init = {
        .role = TUSB_ROLE_DEVICE,
        .speed = TUSB_SPEED_FULL,
    };
    tusb_init(0, &dev_init);
    usb_attached = true;
}

void usb_set_bt_ready(bool ready) {
    bt_ready = ready;
    if (!ready) {
        init_neutral_report();
        input_dirty = true;
    }
}

void usb_submit_bt_input_report(uint8_t const *report_63b, uint16_t len) {
    if (!report_63b || len < sizeof(latest_input_report)) return;
    std::memcpy(latest_input_report, report_63b, sizeof(latest_input_report));
    input_dirty = true;
    try_send_hid_input();
}

void usb_poll() {
    tud_task();
    try_send_hid_input();
}

} // namespace sensa

// ---- TinyUSB C Callbacks ----

extern "C" void tud_mount_cb(void) {
    sensa::g_telemetry.usb_mounted = true;
    sensa::hid_in_flight = false;
    sensa::input_dirty = true;
    sensa::console_printf("[USB] Mounted (DualSense Audio+HID + CDC Console)\n");
}

extern "C" void tud_umount_cb(void) {
    sensa::g_telemetry.usb_mounted = false;
    sensa::g_telemetry.usb_speaker_streaming = false;
    sensa::g_telemetry.usb_mic_streaming = false;
    sensa::hid_in_flight = false;
}

extern "C" void tud_suspend_cb(bool remote_wakeup_en) {
    (void)remote_wakeup_en;
    sensa::g_telemetry.usb_speaker_streaming = false;
    sensa::g_telemetry.usb_mic_streaming = false;
}

extern "C" void tud_resume_cb(void) {
}

extern "C" void tud_hid_report_complete_cb(uint8_t instance, uint8_t const *report, uint16_t len) {
    (void)instance;
    (void)report;
    (void)len;
    sensa::hid_in_flight = false;
    sensa::try_send_hid_input();
}

extern "C" uint16_t tud_hid_get_report_cb(
    uint8_t itf,
    uint8_t report_id,
    hid_report_type_t report_type,
    uint8_t *buffer,
    uint16_t reqlen
) {
    (void)itf;
    if (report_type == HID_REPORT_TYPE_INPUT && report_id == 0x01) {
        uint16_t n = std::min<uint16_t>(reqlen, sizeof(sensa::latest_input_report));
        std::memcpy(buffer, sensa::latest_input_report, n);
        return n;
    }
    if (report_type != HID_REPORT_TYPE_FEATURE || buffer == nullptr) {
        return 0;
    }
    uint16_t copied = 0;
    if (sensa::bt_copy_cached_feature(report_id, buffer, reqlen, copied)) {
        return copied;
    }
    return sensa::synthetic_dualsense_feature(report_id, buffer, reqlen);
}

extern "C" void tud_hid_set_report_cb(
    uint8_t itf,
    uint8_t report_id,
    hid_report_type_t report_type,
    uint8_t const *buffer,
    uint16_t bufsize
) {
    (void)itf;
    (void)report_type;
    if (buffer == nullptr || bufsize == 0) return;

    // USB Output Report 0x02 comes either on EP OUT with report_id==0 & buffer[0]==0x02,
    // or via control SET_REPORT(Output) with report_id==0x02 and buffer starting at payload[0].
    uint8_t const *payload = nullptr;
    uint16_t payload_len = 0;
    if (report_id == 0 && buffer[0] == 0x02 && bufsize > 1) {
        payload = buffer + 1;
        payload_len = static_cast<uint16_t>(bufsize - 1);
    } else if (report_id == 0x02) {
        payload = buffer;
        payload_len = bufsize;
    }

    if (payload != nullptr) {
        uint16_t copy_len = std::min<uint16_t>(payload_len, sizeof(sensa::g_host_output.payload));
        std::memcpy(sensa::g_host_output.payload, payload, copy_len);
        sensa::g_host_output.len = copy_len;
        sensa::g_host_output.valid = true;
        sensa::g_host_output.last_rx_us = time_us_32();
        sensa::g_telemetry.usb_0x02_rx_count++;
        if (copy_len >= 4) {
            sensa::g_telemetry.last_game_right = payload[2];
            sensa::g_telemetry.last_game_left = payload[3];
        }
        if (copy_len >= 12) sensa::g_telemetry.last_r2_mode = payload[10];
        if (copy_len >= 23) sensa::g_telemetry.last_l2_mode = payload[21];
        sensa::bt_notify_host_output();
        return;
    }

    if (report_id == 0x80 || report_id == 0x60 || report_id == 0x61 || report_id == 0x62) {
        sensa::bt_send_set_feature(report_id, buffer, bufsize);
    }
}

extern "C" bool tud_audio_set_itf_cb(uint8_t rhport, tusb_control_request_t const *p_request) {
    (void)rhport;
    uint8_t itf = tu_u16_low(p_request->wIndex);
    uint8_t alt = tu_u16_low(p_request->wValue);
    if (itf == 1) {
        sensa::g_telemetry.usb_speaker_streaming = (alt != 0);
        sensa::console_printf("[AUDIO] Speaker streaming: %s\n", alt ? "ON (4ch 48kHz)" : "OFF");
    } else if (itf == 2) {
        sensa::g_telemetry.usb_mic_streaming = (alt != 0);
    }
    return true;
}

extern "C" bool tud_audio_set_req_entity_cb(uint8_t rhport, tusb_control_request_t const *p_request, uint8_t *buf) {
    (void)rhport;
    uint8_t entity_id = tu_u16_high(p_request->wIndex);
    uint8_t ctrl_sel = tu_u16_high(p_request->wValue);
    if (entity_id == UAC1_ENTITY_SPK_FEATURE_UNIT) {
        if (ctrl_sel == AUDIO10_FU_CTRL_MUTE && p_request->bRequest == AUDIO10_CS_REQ_SET_CUR) {
            sensa::g_telemetry.usb_muted = (buf[0] != 0);
            return true;
        }
        if (ctrl_sel == AUDIO10_FU_CTRL_VOLUME && p_request->bRequest == AUDIO10_CS_REQ_SET_CUR) {
            int16_t raw = static_cast<int16_t>(tu_unaligned_read16(buf));
            // Range -100dB (-25600) .. 0dB (0) -> map linearly 0..100%
            int32_t pct = ((static_cast<int32_t>(raw) + 25600) * 100) / 25600;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            sensa::g_telemetry.usb_volume_pct = static_cast<uint16_t>(pct);
            return true;
        }
    } else if (entity_id == UAC1_ENTITY_MIC_FEATURE_UNIT) {
        return true;
    }
    return true;
}

extern "C" bool tud_audio_get_req_entity_cb(uint8_t rhport, tusb_control_request_t const *p_request) {
    uint8_t entity_id = tu_u16_high(p_request->wIndex);
    uint8_t ctrl_sel = tu_u16_high(p_request->wValue);
    if (entity_id != UAC1_ENTITY_SPK_FEATURE_UNIT && entity_id != UAC1_ENTITY_MIC_FEATURE_UNIT) {
        return false;
    }
    if (ctrl_sel == AUDIO10_FU_CTRL_MUTE) {
        uint8_t mute_val = (entity_id == UAC1_ENTITY_SPK_FEATURE_UNIT && sensa::g_telemetry.usb_muted) ? 1 : 0;
        return tud_audio_buffer_and_schedule_control_xfer(rhport, p_request, &mute_val, 1);
    }
    if (ctrl_sel == AUDIO10_FU_CTRL_VOLUME) {
        int16_t min_v = (entity_id == UAC1_ENTITY_SPK_FEATURE_UNIT) ? -25600 : 0;
        int16_t max_v = (entity_id == UAC1_ENTITY_SPK_FEATURE_UNIT) ? 0 : 12288;
        int16_t res_v = (entity_id == UAC1_ENTITY_SPK_FEATURE_UNIT) ? 256 : 0x007a;
        int16_t cur_v = (entity_id == UAC1_ENTITY_SPK_FEATURE_UNIT)
            ? static_cast<int16_t>(-25600 + (static_cast<int32_t>(sensa::g_telemetry.usb_volume_pct) * 25600) / 100)
            : max_v;
        switch (p_request->bRequest) {
            case AUDIO10_CS_REQ_GET_CUR: return tud_audio_buffer_and_schedule_control_xfer(rhport, p_request, &cur_v, 2);
            case AUDIO10_CS_REQ_GET_MIN: return tud_audio_buffer_and_schedule_control_xfer(rhport, p_request, &min_v, 2);
            case AUDIO10_CS_REQ_GET_MAX: return tud_audio_buffer_and_schedule_control_xfer(rhport, p_request, &max_v, 2);
            case AUDIO10_CS_REQ_GET_RES: return tud_audio_buffer_and_schedule_control_xfer(rhport, p_request, &res_v, 2);
            default: return false;
        }
    }
    return false;
}
