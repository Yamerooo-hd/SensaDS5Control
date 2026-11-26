#include "sensa_usb.h"
#include "sensa_bt.h"
#include "sensa_console.h"
#include "sensa_state.h"
#include "usb_descriptors.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

#include "device/usbd_pvt.h"
#include "hardware/watchdog.h"
#include "pico/time.h"
#include "pico/unique_id.h"
#include "tusb.h"

extern "C" bool sensa_handle_ms_os_control(uint8_t rhport, uint8_t stage, tusb_control_request_t const *request);

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
pico_unique_board_id_t board_uid{};

void init_neutral_report() {
    std::memset(latest_input_report, 0, sizeof(latest_input_report));
    latest_input_report[0] = 0x80; // LX
    latest_input_report[1] = 0x80; // LY
    latest_input_report[2] = 0x80; // RX
    latest_input_report[3] = 0x80; // RY
    latest_input_report[6] = 0x01; // SeqNo
    latest_input_report[7] = 0x08; // DPad neutral
    latest_input_report[52] = 0x0A; // Neutral 100% until real BT battery updates
    latest_input_report[53] = 0x08;
}

void write_u16_le(uint8_t *p, uint16_t v) {
    p[0] = static_cast<uint8_t>(v & 0xFFu);
    p[1] = static_cast<uint8_t>((v >> 8) & 0xFFu);
}

void write_i16_le(uint8_t *p, int16_t v) {
    write_u16_le(p, static_cast<uint16_t>(v));
}

void write_u32_le(uint8_t *p, uint32_t v) {
    p[0] = static_cast<uint8_t>(v & 0xFFu);
    p[1] = static_cast<uint8_t>((v >> 8) & 0xFFu);
    p[2] = static_cast<uint8_t>((v >> 16) & 0xFFu);
    p[3] = static_cast<uint8_t>((v >> 24) & 0xFFu);
}

int16_t axis_u8_to_xusb(uint8_t value, bool invert) {
    if (value < 128) {
        int32_t span = invert ? 32767 : 32768;
        int32_t mag = ((128 - value) * span + 64) / 128;
        return static_cast<int16_t>(invert ? mag : -mag);
    }
    int32_t span = invert ? 32768 : 32767;
    int32_t mag = ((value - 128) * span + 63) / 127;
    return static_cast<int16_t>(invert ? -mag : mag);
}

// =========================================================================
// Custom TinyUSB Vendor Class Driver for Xbox 360 & Xbox Series [MS-GIPUSB]
// =========================================================================

uint8_t xbox_rhport = 0;
uint8_t xbox_ep_in = 0;
uint8_t xbox_ep_out = 0;
bool xbox_in_busy = false;
CFG_TUD_MEM_ALIGN uint8_t xbox_rx_buf[64]{};
CFG_TUD_MEM_ALIGN uint8_t xbox_tx_buf[64]{};

// [MS-GIPUSB] Xbox Series State Machine
enum class GipState : uint8_t {
    Idle = 0,
    Hello = 1,
    Metadata = 2,
    Active = 3,
};

GipState gip_state = GipState::Idle;
uint8_t gip_cmd_seq = 1;
uint8_t gip_data_seq = 1;
uint8_t gip_meta_frag_idx = 0;
bool gip_meta_waiting_ack = false;
bool gip_prev_guide = false;
uint32_t gip_last_hello_us = 0;
uint32_t gip_last_status_us = 0;
uint32_t gip_status_start_us = 0;
uint32_t gip_last_input_us = 0;
uint8_t gip_last_sent_14b[14]{};
uint8_t gip_device_id[8]{};

// Timed Direct Motor Command (0x09) expiry for Xbox Series
uint32_t gip_motor_stop_at_us = 0;
bool gip_motor_active = false;
uint8_t gip_cur_l_main = 0;
uint8_t gip_cur_r_main = 0;
uint8_t gip_cur_l_trig = 0;
uint8_t gip_cur_r_trig = 0;

// Official Microsoft [MS-GIPUSB] Section 2.2.2 compiled 182-byte Gamepad Metadata blob
// SupportsDeviceFirmwareVersions Major=1, Minor=0; Input 0x20 (14B), Output 0x09 (9B)
static constexpr uint8_t kGipMetadata[182] = {
    0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xB6, 0x00,
    0x77, 0x00, 0x16, 0x00, 0x1B, 0x00, 0x1C, 0x00, 0x23, 0x00, 0x29, 0x00, 0x46, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x06, 0x01, 0x02, 0x03,
    0x04, 0x06, 0x07, 0x05, 0x01, 0x04, 0x05, 0x06, 0x0A, 0x01, 0x1A, 0x00, 0x57, 0x69, 0x6E, 0x64,
    0x6F, 0x77, 0x73, 0x2E, 0x58, 0x62, 0x6F, 0x78, 0x2E, 0x49, 0x6E, 0x70, 0x75, 0x74, 0x2E, 0x47,
    0x61, 0x6D, 0x65, 0x70, 0x61, 0x64, 0x03, 0x56, 0xFF, 0x76, 0x97, 0xFD, 0x9B, 0x81, 0x45, 0xAD,
    0x45, 0xB6, 0x45, 0xBB, 0xA5, 0x26, 0xD6, 0x2C, 0x40, 0x2E, 0x08, 0xDF, 0x07, 0xE1, 0x45, 0xA5,
    0xAB, 0xA3, 0x12, 0x7A, 0xF1, 0x97, 0xB5, 0xE7, 0x1F, 0xF3, 0xB8, 0x86, 0x73, 0xE9, 0x40, 0xA9,
    0xF8, 0x2F, 0x21, 0x26, 0x3A, 0xCF, 0xB7, 0x02, 0x17, 0x00, 0x20, 0x0E, 0x00, 0x01, 0x00, 0x10,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17,
    0x00, 0x09, 0x09, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00
};

uint8_t next_seq(uint8_t &s) {
    uint8_t r = s++;
    if (s == 0) s = 1;
    if (r == 0) r = 1;
    return r;
}

bool xbox_arm_out(uint8_t rhport) {
    if (xbox_ep_out == 0) return false;
    return usbd_edpt_xfer(rhport, xbox_ep_out, xbox_rx_buf, sizeof(xbox_rx_buf), false);
}

bool xbox_send_pkt(uint8_t const *data, uint16_t len) {
    if (xbox_ep_in == 0 || xbox_in_busy || len == 0 || len > sizeof(xbox_tx_buf)) return false;
    std::memcpy(xbox_tx_buf, data, len);
    xbox_in_busy = usbd_edpt_xfer(xbox_rhport, xbox_ep_in, xbox_tx_buf, len, false);
    return xbox_in_busy;
}

uint8_t dpad_to_cardinal_mask(uint8_t dpad) {
    uint8_t mask = 0;
    if (dpad == 0 || dpad == 1 || dpad == 7) mask |= 0x01u; // Up
    if (dpad == 3 || dpad == 4 || dpad == 5) mask |= 0x02u; // Down
    if (dpad == 5 || dpad == 6 || dpad == 7) mask |= 0x04u; // Left
    if (dpad == 1 || dpad == 2 || dpad == 3) mask |= 0x08u; // Right
    return mask;
}

void push_xbox_motors_to_bt(uint8_t l_main, uint8_t r_main, uint8_t l_trig, uint8_t r_trig) {
    std::memset(g_host_output.payload, 0, sizeof(g_host_output.payload));
    g_host_output.payload[0] = 0x03u; // CompatibleVibration | HapticsSelect
    g_host_output.payload[2] = r_main;
    g_host_output.payload[3] = l_main;

    if (g_usb_mode == UsbMode::XboxSeries) {
        g_host_output.payload[0] |= 0x0Cu; // RightTriggerEffect | LeftTriggerEffect
        write_ds5_vibration_trig(g_host_output.payload + 10, r_trig);
        write_ds5_vibration_trig(g_host_output.payload + 21, l_trig);
        g_telemetry.last_r2_mode = r_trig ? 0x26 : 0x05;
        g_telemetry.last_l2_mode = l_trig ? 0x26 : 0x05;
    }

    g_host_output.len = 47;
    g_host_output.valid = true;
    g_host_output.last_rx_us = time_us_32();
    g_telemetry.usb_0x02_rx_count++;
    g_telemetry.last_game_right = r_main;
    g_telemetry.last_game_left = l_main;
    bt_notify_host_output();
}

void build_x360_input(uint8_t out20[20]) {
    std::memset(out20, 0, 20);
    out20[0] = 0x00;
    out20[1] = 0x14;
    uint8_t const *b = latest_input_report;
    uint16_t btns = dpad_to_cardinal_mask(b[7] & 0x0Fu);
    if (b[8] & 0x20u) btns |= 0x0010; // Options -> Start
    if (b[8] & 0x10u) btns |= 0x0020; // Create -> Back
    if (b[8] & 0x40u) btns |= 0x0040; // L3
    if (b[8] & 0x80u) btns |= 0x0080; // R3
    if (b[8] & 0x01u) btns |= 0x0100; // L1 -> LB
    if (b[8] & 0x02u) btns |= 0x0200; // R1 -> RB
    if (b[9] & 0x01u) btns |= 0x0400; // PS -> Guide
    if (b[7] & 0x20u) btns |= 0x1000; // Cross -> A
    if (b[7] & 0x40u) btns |= 0x2000; // Circle -> B
    if (b[7] & 0x10u) btns |= 0x4000; // Square -> X
    if (b[7] & 0x80u) btns |= 0x8000; // Triangle -> Y
    write_u16_le(out20 + 2, btns);
    out20[4] = b[4]; // LT
    out20[5] = b[5]; // RT
    write_i16_le(out20 + 6, axis_u8_to_xusb(b[0], false));
    write_i16_le(out20 + 8, axis_u8_to_xusb(b[1], true));
    write_i16_le(out20 + 10, axis_u8_to_xusb(b[2], false));
    write_i16_le(out20 + 12, axis_u8_to_xusb(b[3], true));
}

void build_gip_input_payload(uint8_t out14[14]) {
    std::memset(out14, 0, 14);
    uint8_t const *b = latest_input_report;
    uint8_t b0 = 0;
    uint8_t b1 = dpad_to_cardinal_mask(b[7] & 0x0Fu);
    if (b[8] & 0x20u) b0 |= 0x04u; // Options -> Menu
    if (b[8] & 0x10u) b0 |= 0x08u; // Create -> View
    if (b[7] & 0x20u) b0 |= 0x10u; // Cross -> A
    if (b[7] & 0x40u) b0 |= 0x20u; // Circle -> B
    if (b[7] & 0x10u) b0 |= 0x40u; // Square -> X
    if (b[7] & 0x80u) b0 |= 0x80u; // Triangle -> Y
    if (b[8] & 0x01u) b1 |= 0x10u; // L1 -> LB
    if (b[8] & 0x02u) b1 |= 0x20u; // R1 -> RB
    if (b[8] & 0x40u) b1 |= 0x40u; // L3
    if (b[8] & 0x80u) b1 |= 0x80u; // R3

    out14[0] = b0;
    out14[1] = b1;
    uint16_t lt = static_cast<uint16_t>((b[4] * 1023u) / 255u);
    uint16_t rt = static_cast<uint16_t>((b[5] * 1023u) / 255u);
    write_u16_le(out14 + 2, lt);
    write_u16_le(out14 + 4, rt);
    write_i16_le(out14 + 6, axis_u8_to_xusb(b[0], false));
    write_i16_le(out14 + 8, axis_u8_to_xusb(b[1], true));
    write_i16_le(out14 + 10, axis_u8_to_xusb(b[2], false));
    write_i16_le(out14 + 12, axis_u8_to_xusb(b[3], true));
}

bool send_gip_hello() {
    uint8_t pkt[32]{};
    pkt[0] = 0x02; // Hello Device
    pkt[1] = 0x20; // System single packet, no ACK
    pkt[2] = next_seq(gip_cmd_seq);
    pkt[3] = 0x1C; // 28B payload
    std::memcpy(pkt + 4, gip_device_id, 8);
    write_u16_le(pkt + 12, 0x1209); // VID (must match USB descriptor)
    write_u16_le(pkt + 14, 0xDB06); // PID (must match USB descriptor)
    write_u16_le(pkt + 16, 0x0001); // FW Major = 1 (matches Metadata[0x27]=1)
    write_u16_le(pkt + 18, 0x0000); // FW Minor = 0 (matches Metadata[0x28]=0)
    write_u16_le(pkt + 20, 0x0001); // Build
    write_u16_le(pkt + 22, 0x0000); // Revision
    pkt[24] = 0x01; pkt[25] = 0x00; // HW version
    pkt[26] = 0x01; pkt[27] = 0x00; // RF protocol 1.0
    pkt[28] = 0x01; pkt[29] = 0x00; // Security protocol 1.0
    pkt[30] = 0x01; pkt[31] = 0x00; // GIP 1.0
    return xbox_send_pkt(pkt, sizeof(pkt));
}

bool send_gip_next_metadata_fragment() {
    uint8_t pkt[64]{};
    static uint8_t meta_seq = 1;
    if (gip_meta_frag_idx == 0) {
        meta_seq = next_seq(gip_cmd_seq);
        pkt[0] = 0x04;
        pkt[1] = 0xF0; // First fragment + ACME
        pkt[2] = meta_seq;
        pkt[3] = 58;
        pkt[4] = 0x80u | (182u & 0x7Fu); // Total len LEB128 = 182 (0xB6)
        pkt[5] = 182u >> 7;              // 0x01
        std::memcpy(pkt + 6, kGipMetadata, 58);
        gip_meta_frag_idx = 1;
        gip_meta_waiting_ack = true;
        return xbox_send_pkt(pkt, 64);
    }
    if (gip_meta_frag_idx == 1) {
        pkt[0] = 0x04;
        pkt[1] = 0xA0; // Middle fragment
        pkt[2] = meta_seq;
        pkt[3] = 0x80u | 58u;
        pkt[4] = 0x00;
        pkt[5] = 58;
        std::memcpy(pkt + 6, kGipMetadata + 58, 58);
        gip_meta_frag_idx = 2;
        return xbox_send_pkt(pkt, 64);
    }
    if (gip_meta_frag_idx == 2) {
        pkt[0] = 0x04;
        pkt[1] = 0xA0; // Middle fragment
        pkt[2] = meta_seq;
        pkt[3] = 0x80u | 58u;
        pkt[4] = 0x00;
        pkt[5] = 116;
        std::memcpy(pkt + 6, kGipMetadata + 116, 58);
        gip_meta_frag_idx = 3;
        return xbox_send_pkt(pkt, 64);
    }
    if (gip_meta_frag_idx == 3) {
        pkt[0] = 0x04;
        pkt[1] = 0xB0; // Last fragment + ACME
        pkt[2] = meta_seq;
        pkt[3] = 8;
        pkt[4] = 0x80u | (174u & 0x7Fu);
        pkt[5] = 174u >> 7;
        std::memcpy(pkt + 6, kGipMetadata + 174, 8);
        gip_meta_frag_idx = 4;
        gip_meta_waiting_ack = true;
        return xbox_send_pkt(pkt, 14);
    }
    return false;
}

bool send_gip_status() {
    uint8_t pkt[8]{};
    pkt[0] = 0x03;
    pkt[1] = 0x20;
    pkt[2] = next_seq(gip_cmd_seq);
    pkt[3] = 0x04;
    pkt[4] = 0xEC; // Connected + Wired power/full
    pkt[5] = 0x00;
    pkt[6] = 0x00;
    pkt[7] = 0x00;
    return xbox_send_pkt(pkt, sizeof(pkt));
}

void xbox_driver_init(void) {
    xbox_rhport = 0;
    xbox_ep_in = 0;
    xbox_ep_out = 0;
    xbox_in_busy = false;
    gip_state = GipState::Idle;
    gip_meta_frag_idx = 0;
    gip_meta_waiting_ack = false;
    gip_motor_active = false;
}

bool xbox_driver_deinit(void) {
    xbox_driver_init();
    return true;
}

void xbox_driver_reset(uint8_t rhport) {
    (void)rhport;
    xbox_driver_init();
}

uint16_t xbox_driver_open(uint8_t rhport, tusb_desc_interface_t const *desc_itf, uint16_t max_len) {
    if (desc_itf->bInterfaceNumber != XBOX_ITF_NUM || desc_itf->bInterfaceClass != TUSB_CLASS_VENDOR_SPECIFIC) {
        return 0;
    }
    bool is_x360 = (g_usb_mode == UsbMode::Xbox360 && desc_itf->bInterfaceSubClass == 0x5D && desc_itf->bInterfaceProtocol == 0x01);
    bool is_xseries = (g_usb_mode == UsbMode::XboxSeries && desc_itf->bInterfaceSubClass == 0x47 && desc_itf->bInterfaceProtocol == 0xD0);
    if (!is_x360 && !is_xseries) {
        return 0;
    }

    uint8_t const *desc_end = reinterpret_cast<uint8_t const *>(desc_itf) + max_len;
    uint8_t const *desc = tu_desc_next(desc_itf);
    uint16_t consumed = static_cast<uint16_t>(desc - reinterpret_cast<uint8_t const *>(desc_itf));

    xbox_ep_in = 0;
    xbox_ep_out = 0;
    while (tu_desc_in_bounds(desc, desc_end)) {
        uint8_t dtype = tu_desc_type(desc);
        if (dtype == TUSB_DESC_INTERFACE || dtype == TUSB_DESC_INTERFACE_ASSOCIATION) break;
        if (dtype == TUSB_DESC_ENDPOINT) {
            auto const *ep = reinterpret_cast<tusb_desc_endpoint_t const *>(desc);
            if (ep->bmAttributes.xfer == TUSB_XFER_INTERRUPT) {
                TU_ASSERT(usbd_edpt_open(rhport, ep), 0);
                if (tu_edpt_dir(ep->bEndpointAddress) == TUSB_DIR_IN) xbox_ep_in = ep->bEndpointAddress;
                else xbox_ep_out = ep->bEndpointAddress;
            }
        }
        desc = tu_desc_next(desc);
        consumed = static_cast<uint16_t>(desc - reinterpret_cast<uint8_t const *>(desc_itf));
    }

    if (xbox_ep_in == 0 || xbox_ep_out == 0) return 0;

    xbox_rhport = rhport;
    xbox_in_busy = false;
    xbox_arm_out(rhport);

    if (is_x360) {
        uint8_t rpt[20]{};
        build_x360_input(rpt);
        xbox_send_pkt(rpt, sizeof(rpt));
    } else {
        uint32_t seed = time_us_32() ^ (static_cast<uint32_t>(board_uid.id[0]) << 24) ^
                        (static_cast<uint32_t>(board_uid.id[3]) << 16) ^
                        (static_cast<uint32_t>(board_uid.id[5]) << 8) ^ board_uid.id[7];
        gip_device_id[0] = static_cast<uint8_t>(seed & 0xFFu);
        gip_device_id[1] = static_cast<uint8_t>((seed >> 8) & 0xFFu);
        gip_device_id[2] = static_cast<uint8_t>((seed >> 16) & 0xFFu);
        gip_device_id[3] = static_cast<uint8_t>((seed >> 24) & 0xFFu);
        gip_device_id[4] = 0xFB;
        gip_device_id[5] = 0xFF;
        gip_device_id[6] = 0x00;
        gip_device_id[7] = 0x00;
        gip_state = GipState::Hello;
        gip_last_hello_us = time_us_32();
        send_gip_hello();
    }
    return consumed;
}

bool xbox_driver_control_xfer_cb(uint8_t rhport, uint8_t stage, tusb_control_request_t const *request) {
    return sensa_handle_ms_os_control(rhport, stage, request);
}

bool xbox_driver_xfer_cb(uint8_t rhport, uint8_t ep_addr, xfer_result_t result, uint32_t xferred_bytes) {
    if (ep_addr == xbox_ep_in) {
        xbox_in_busy = false;
        if (g_usb_mode == UsbMode::XboxSeries && gip_state == GipState::Metadata && !gip_meta_waiting_ack &&
            gip_meta_frag_idx < 4) {
            send_gip_next_metadata_fragment();
        }
        return true;
    }
    if (ep_addr != xbox_ep_out) return true;

    if (result == XFER_RESULT_SUCCESS && xferred_bytes > 0) {
        if (g_usb_mode == UsbMode::Xbox360) {
            if (xferred_bytes >= 8 && xbox_rx_buf[0] == 0x00 && xbox_rx_buf[1] == 0x08) {
                push_xbox_motors_to_bt(xbox_rx_buf[3], xbox_rx_buf[4], 0, 0);
            }
        } else if (g_usb_mode == UsbMode::XboxSeries && xferred_bytes >= 4) {
            uint8_t msg = xbox_rx_buf[0];
            if (msg == 0x01) { // ACK
                if (gip_state == GipState::Metadata) {
                    gip_meta_waiting_ack = false;
                    if (gip_meta_frag_idx < 4) {
                        send_gip_next_metadata_fragment();
                    } else {
                        gip_state = GipState::Active;
                        gip_status_start_us = time_us_32();
                        gip_last_status_us = gip_status_start_us;
                        send_gip_status();
                        console_printf("[GIP] Metadata ACKed -> Active\n");
                    }
                }
            } else if (msg == 0x04) { // Metadata Request
                gip_state = GipState::Metadata;
                gip_meta_frag_idx = 0;
                gip_meta_waiting_ack = false;
                console_printf("[GIP] Host Metadata Request -> streaming 182B blob\n");
                send_gip_next_metadata_fragment();
            } else if (msg == 0x05 && xferred_bytes >= 5) { // Set Device State
                uint8_t st = xbox_rx_buf[4];
                console_printf("[GIP] Set Device State = 0x%02X\n", st);
                if (st == 0x00) { // Start
                    gip_state = GipState::Active;
                    gip_status_start_us = time_us_32();
                    gip_last_status_us = gip_status_start_us;
                    send_gip_status();
                } else if (st == 0x01 || st == 0x04 || st == 0x05) { // Stop / Off / Quiesce
                    gip_motor_active = false;
                    gip_cur_l_main = gip_cur_r_main = gip_cur_l_trig = gip_cur_r_trig = 0;
                    push_xbox_motors_to_bt(0, 0, 0, 0);
                    if (st == 0x01) gip_state = GipState::Hello;
                } else if (st == 0x07) { // Reset
                    gip_motor_active = false;
                    gip_state = GipState::Hello;
                    gip_last_hello_us = time_us_32();
                    send_gip_hello();
                }
            } else if (msg == 0x09 && xferred_bytes >= 11 && xbox_rx_buf[4] == 0x00) {
                uint8_t bm = xbox_rx_buf[5];
                uint8_t lt = xbox_rx_buf[6];
                uint8_t rt = xbox_rx_buf[7];
                uint8_t lm = xbox_rx_buf[8];
                uint8_t rm = xbox_rx_buf[9];
                uint8_t dur = xbox_rx_buf[10];
                if (dur == 0) {
                    gip_cur_l_main = gip_cur_r_main = gip_cur_l_trig = gip_cur_r_trig = 0;
                    gip_motor_active = false;
                } else {
                    if (bm & 0x08u) gip_cur_l_trig = std::min<uint8_t>(lt, 100);
                    if (bm & 0x04u) gip_cur_r_trig = std::min<uint8_t>(rt, 100);
                    if (bm & 0x02u) gip_cur_l_main = static_cast<uint8_t>((std::min<uint8_t>(lm, 100) * 255u) / 100u);
                    if (bm & 0x01u) gip_cur_r_main = static_cast<uint8_t>((std::min<uint8_t>(rm, 100) * 255u) / 100u);
                    gip_motor_active = (gip_cur_l_main | gip_cur_r_main | gip_cur_l_trig | gip_cur_r_trig) != 0;
                    gip_motor_stop_at_us = time_us_32() + static_cast<uint32_t>(dur) * 10000u;
                }
                push_xbox_motors_to_bt(gip_cur_l_main, gip_cur_r_main, gip_cur_l_trig, gip_cur_r_trig);
            }
        }
    }
    xbox_arm_out(rhport);
    return true;
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
    if (!g_telemetry.usb_mounted) return;

    if (g_usb_mode == UsbMode::DualSense) {
        if (!tud_hid_ready() || hid_in_flight || !input_dirty) return;
        if (tud_hid_report(0x01, latest_input_report, sizeof(latest_input_report))) {
            input_dirty = false;
            hid_in_flight = true;
        }
        return;
    }

    if (g_usb_mode == UsbMode::Xbox360) {
        if (xbox_in_busy || xbox_ep_in == 0 || !input_dirty) return;
        uint8_t rpt[20]{};
        build_x360_input(rpt);
        if (xbox_send_pkt(rpt, sizeof(rpt))) input_dirty = false;
        return;
    }

    if (g_usb_mode == UsbMode::XboxSeries) {
        uint32_t now = time_us_32();
        if (gip_state == GipState::Hello) {
            if (!xbox_in_busy && static_cast<int32_t>(now - gip_last_hello_us) >= 500000) {
                gip_last_hello_us = now;
                send_gip_hello();
            }
            return;
        }
        if (gip_state != GipState::Active || xbox_in_busy) return;

        uint32_t status_interval = (static_cast<uint32_t>(now - gip_status_start_us) < 10000000u) ? 1000000u : 20000000u;
        if (static_cast<int32_t>(now - gip_last_status_us) >= static_cast<int32_t>(status_interval)) {
            gip_last_status_us = now;
            if (send_gip_status()) return;
        }

        bool guide_now = (latest_input_report[9] & 0x01u) != 0;
        if (guide_now != gip_prev_guide) {
            uint8_t gpkt[6] = {0x07, 0x20, next_seq(gip_cmd_seq), 0x02, static_cast<uint8_t>(guide_now ? 1 : 0), 0x5B};
            if (xbox_send_pkt(gpkt, sizeof(gpkt))) {
                gip_prev_guide = guide_now;
            }
            return;
        }

        uint8_t p14[14]{};
        build_gip_input_payload(p14);
        bool changed = std::memcmp(p14, gip_last_sent_14b, 14) != 0;
        if ((changed || input_dirty) && static_cast<int32_t>(now - gip_last_input_us) >= 4000) {
            uint8_t gp[18]{};
            gp[0] = 0x20;
            gp[1] = 0x00;
            gp[2] = next_seq(gip_data_seq);
            gp[3] = 0x0E;
            std::memcpy(gp + 4, p14, 14);
            if (xbox_send_pkt(gp, sizeof(gp))) {
                std::memcpy(gip_last_sent_14b, p14, 14);
                gip_last_input_us = now;
                input_dirty = false;
            }
        }
    }
}

} // namespace

void usb_init() {
    init_neutral_report();
    pico_get_unique_board_id(&board_uid);
    mac_seed[0] = static_cast<uint8_t>((board_uid.id[0] & 0xFEu) | 0x02u);
    std::memcpy(mac_seed + 1, board_uid.id + 1, 5);

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
    if (g_usb_mode == UsbMode::XboxSeries && gip_motor_active &&
        static_cast<int32_t>(time_us_32() - gip_motor_stop_at_us) >= 0) {
        gip_motor_active = false;
        gip_cur_l_main = gip_cur_r_main = gip_cur_l_trig = gip_cur_r_trig = 0;
        push_xbox_motors_to_bt(0, 0, 0, 0);
    }
    try_send_hid_input();
}

} // namespace sensa

// ---- TinyUSB C Exports & Callbacks ----

extern "C" uint8_t sensa_active_usb_mode(void) {
    return static_cast<uint8_t>(sensa::g_usb_mode);
}

extern "C" void sensa_get_unique_serial_hex32(char out33[33]) {
    static constexpr char kHex[] = "0123456789ABCDEF";
    for (int i = 0; i < 16; ++i) {
        uint8_t b = sensa::board_uid.id[i % 8] ^ static_cast<uint8_t>((i >= 8) ? 0xA5u : 0x00u);
        out33[i * 2] = kHex[(b >> 4) & 0x0Fu];
        out33[i * 2 + 1] = kHex[b & 0x0Fu];
    }
    out33[32] = '\0';
}

extern "C" usbd_class_driver_t const *usbd_app_driver_get_cb(uint8_t *driver_count) {
    static usbd_class_driver_t const drivers[] = {
        {
            .name = "SENSA_XBOX",
            .init = sensa::xbox_driver_init,
            .deinit = sensa::xbox_driver_deinit,
            .reset = sensa::xbox_driver_reset,
            .open = sensa::xbox_driver_open,
            .control_xfer_cb = sensa::xbox_driver_control_xfer_cb,
            .xfer_cb = sensa::xbox_driver_xfer_cb,
            .xfer_isr = nullptr,
            .sof = nullptr,
        }
    };
    *driver_count = 1;
    return drivers;
}

extern "C" void tud_mount_cb(void) {
    sensa::g_telemetry.usb_mounted = true;
    sensa::hid_in_flight = false;
    sensa::input_dirty = true;
    sensa::console_printf("[USB] Mounted (%s + CDC Console)\n", sensa::usb_mode_name(sensa::g_usb_mode));
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
