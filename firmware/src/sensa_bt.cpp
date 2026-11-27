#include "sensa_bt.h"
#include "sensa_console.h"
#include "sensa_dsp.h"
#include "sensa_usb.h"

#include <algorithm>
#include <cstring>

#include "btstack.h"
#include "classic/sdp_client.h"
#include "classic/sdp_util.h"
#include "pico/cyw43_arch.h"
#include "pico/time.h"

namespace sensa {
namespace {

constexpr uint16_t kPsmHidControl = 0x0011;
constexpr uint16_t kPsmHidInterrupt = 0x0013;
constexpr uint16_t kMtuControl = 672;
constexpr uint16_t kMtuInterrupt = 672;
constexpr uint16_t kAttrHidDescriptorList = 0x0206;

btstack_packet_callback_registration_t hci_cb_reg{};
btstack_packet_callback_registration_t l2cap_cb_reg{};

hci_con_handle_t acl_handle = HCI_CON_HANDLE_INVALID;
bd_addr_t peer_addr{};
bool peer_addr_valid = false;
bd_addr_t bonded_addr{};
bool has_bonded_addr = false;

uint16_t hid_ctrl_cid = 0;
uint16_t hid_intr_cid = 0;
uint16_t hid_ctrl_pending = 0;
uint16_t hid_intr_pending = 0;
bool hid_ctrl_ready = false;
bool hid_intr_ready = false;
bool hid_session_announced = false;
bool rx_0x31_seen = false;
bool security_ready = false;
uint8_t last_intr_rx[20]{};
uint16_t last_intr_rx_size = 0;

bool pairing_window = false;
uint32_t pairing_deadline_us = 0;
bool inquiry_active = false;
bool outbound_connecting = false;

// Outgoing TX state
uint8_t bt_seq_nibble = 0;
uint8_t bt_audio_ctr = 0;
bool intr_can_send_req = false;
bool ctrl_can_send_req = false;

bool state_send_pending = false;
bool deferred_state_flush = false;
uint32_t last_state_send_us = 0;

bool impulse_test_active = false;
uint8_t impulse_test_pct = 85;
uint32_t impulse_test_until_us = 0;
bool prev_imp_l_active = false;
bool prev_imp_r_active = false;
uint8_t last_imp_sent_l = 0;
uint8_t last_imp_sent_r = 0;
uint32_t last_dynamic_check_us = 0;

bool audio_block_pending = false;
int8_t pending_audio_block[64]{};
HapticMode pending_audio_proto = HapticMode::Mode32SAxense;

bool led_override_active = false;
uint8_t led_override_r = 0;
uint8_t led_override_g = 0;
uint8_t led_override_b = 255;
uint8_t mic_led_override_mode = 0; // 0=off, 1=solid, 2=pulse

bool trig_r_override_active = false;
uint8_t trig_r_override[11]{};
bool trig_l_override_active = false;
uint8_t trig_l_override[11]{};

bool btmon_enabled = false;
bool tele_enabled = false;
uint32_t last_tele_us = 0;
uint8_t last_bt_body[63]{};
uint8_t last_mon_raw[75]{};
uint16_t last_mon_len = 0;
uint32_t last_mon_us = 0;

bool send_547_once = false;

// Control queue (GET_FEATURE 0x43/0x4B, SET_REPORT 0x52, SET_FEATURE 0x53)
constexpr uint8_t kCtrlQueueCap = 8;
struct CtrlPkt {
    uint8_t bytes[80]{};
    uint16_t len = 0;
};
CtrlPkt ctrl_q[kCtrlQueueCap]{};
uint8_t ctrl_q_head = 0;
uint8_t ctrl_q_tail = 0;

// Feature report cache
struct FeatureEntry {
    bool valid = false;
    uint8_t id = 0;
    uint8_t data[65]{};
    uint16_t len = 0;
};
FeatureEntry feat_cache[12]{};

// SDP Discovery state
enum class SdpStage : uint8_t { Idle = 0, QueryHid = 1, QueryPnp = 2 };
SdpStage sdp_stage = SdpStage::Idle;
bool sdp_auto_pending = false;
uint32_t sdp_auto_at_us = 0;
uint8_t sdp_attr_buf[1024]{};
uint16_t sdp_attr_len = 0;
uint16_t sdp_cur_attr_id = 0;

// Reconnect & controller-owned L2CAP timing
bool boot_reconnect_tried = false;
uint32_t state_working_us = 0;
bool rx_intr_logged = false;
bool hid_remote_initiated = false;
uint32_t security_ready_us = 0;
bool hid_local_fallback_pending = false;
uint32_t hid_open_ctrl_done_us = 0;
uint8_t unlock_boot_retries_left = 0;

// Spaced feature prefetch queue (avoids choking clone HID Control endpoint)
constexpr uint8_t kPrefetchCap = 6;
uint8_t prefetch_ids[kPrefetchCap]{};
uint8_t prefetch_count = 0;
uint8_t prefetch_idx = 0;
uint32_t prefetch_next_us = 0;

void enqueue_ctrl(uint8_t const *data, uint16_t len) {
    if (len > sizeof(ctrl_q[0].bytes)) return;
    uint8_t next = static_cast<uint8_t>((ctrl_q_head + 1) % kCtrlQueueCap);
    if (next == ctrl_q_tail) return;
    std::memcpy(ctrl_q[ctrl_q_head].bytes, data, len);
    ctrl_q[ctrl_q_head].len = len;
    ctrl_q_head = next;
    if (hid_ctrl_ready && hid_ctrl_cid != 0 && !ctrl_can_send_req) {
        ctrl_can_send_req = true;
        l2cap_request_can_send_now_event(hid_ctrl_cid);
    }
}

void store_feature(uint8_t report_id, uint8_t const *data_with_id, uint16_t len) {
    FeatureEntry *slot = nullptr;
    for (auto &e : feat_cache) {
        if (e.valid && e.id == report_id) { slot = &e; break; }
        if (!e.valid && !slot) slot = &e;
    }
    if (!slot) return;
    uint16_t n = std::min<uint16_t>(len, sizeof(slot->data));
    std::memcpy(slot->data, data_with_id, n);
    slot->len = n;
    slot->id = report_id;
    slot->valid = true;
}

void refresh_bonded_key() {
    btstack_link_key_iterator_t it;
    has_bonded_addr = false;
    if (gap_link_key_iterator_init(&it)) {
        bd_addr_t addr;
        link_key_t key;
        link_key_type_t type;
        if (gap_link_key_iterator_get_next(&it, addr, key, &type)) {
            bd_addr_copy(bonded_addr, addr);
            has_bonded_addr = true;
        }
        gap_link_key_iterator_done(&it);
    }
    g_telemetry.bt_has_bonded_key = has_bonded_addr;
}

void request_intr_tx() {
    if (hid_intr_ready && hid_intr_cid != 0 && !intr_can_send_req) {
        intr_can_send_req = true;
        l2cap_request_can_send_now_event(hid_intr_cid);
    }
}

void decode_and_log_hid_descriptor(uint8_t const *attr_val, uint16_t attr_len) {
    // Attribute 0x0206 is DES{ DES{ UINT8(0x22), STRING(hid_desc) } }
    if (attr_len < 6) {
        console_hexdump("[SDP] Raw HID attr too short", attr_val, attr_len);
        return;
    }
    uint8_t const *raw = nullptr;
    uint16_t raw_len = 0;

    // Walk nested DES elements to locate the inner descriptor byte string (type == DE_STRING == 4)
    de_type_t top_type = de_get_element_type(attr_val);
    if (top_type == DE_DES) {
        uint32_t top_hdr = de_get_header_size(attr_val);
        uint32_t pos = top_hdr;
        while (pos + 2 < attr_len) {
            de_type_t item_type = de_get_element_type(attr_val + pos);
            uint32_t item_len = de_get_len_safe(attr_val + pos, attr_len - pos);
            if (item_len == 0) break;
            if (item_type == DE_DES) {
                uint32_t sub_hdr = de_get_header_size(attr_val + pos);
                uint32_t sub_pos = pos + sub_hdr;
                uint32_t sub_end = pos + item_len;
                while (sub_pos + 2 <= sub_end) {
                    de_type_t t = de_get_element_type(attr_val + sub_pos);
                    uint32_t l = de_get_len_safe(attr_val + sub_pos, sub_end - sub_pos);
                    if (l == 0) break;
                    if (t == DE_STRING) {
                        uint32_t h = de_get_header_size(attr_val + sub_pos);
                        raw = attr_val + sub_pos + h;
                        raw_len = static_cast<uint16_t>(l - h);
                        break;
                    }
                    sub_pos += l;
                }
            }
            if (raw != nullptr) break;
            pos += item_len;
        }
    }

    if (!raw || raw_len == 0) {
        console_hexdump("[SDP] Unparsed HID attr 0x0206", attr_val, attr_len);
        return;
    }

    g_telemetry.sdp.hid_desc_len = raw_len;
    console_hexdump("[SDP] Cosmic Byte Sensa BT HID Report Descriptor", raw, raw_len);

    // Quick HID Report ID summary parser
    console_printf("[SDP] Parsed Report IDs:\n");
    uint8_t cur_id = 0;
    uint32_t cur_size = 0;
    uint32_t cur_count = 0;
    uint32_t acc_bits = 0;
    uint8_t last_id = 0;
    for (uint16_t i = 0; i < raw_len; ) {
        uint8_t b = raw[i];
        if (b == 0xFE) {
            if (i + 1 >= raw_len) break;
            i = static_cast<uint16_t>(i + 3 + raw[i + 1]);
            continue;
        }
        uint8_t sz_code = b & 0x03u;
        uint8_t dsz = (sz_code == 3) ? 4 : sz_code;
        if (i + 1 + dsz > raw_len) break;
        uint32_t val = 0;
        for (uint8_t k = 0; k < dsz; ++k) {
            val |= static_cast<uint32_t>(raw[i + 1 + k]) << (k * 8);
        }
        uint8_t type = (b >> 2) & 0x03u;
        uint8_t tag = (b >> 4) & 0x0Fu;
        if (type == 1) { // Global
            if (tag == 7) cur_size = val;
            else if (tag == 8) {
                cur_id = static_cast<uint8_t>(val);
                if (cur_id != last_id) {
                    acc_bits = 0;
                    last_id = cur_id;
                }
            } else if (tag == 9) cur_count = val;
        } else if (type == 0) { // Main
            if (tag == 8 || tag == 9 || tag == 11) {
                acc_bits += cur_size * cur_count;
                char const *kind = (tag == 8) ? "IN " : (tag == 9 ? "OUT" : "FEA");
                console_printf("  %s ID=0x%02X (+%lu bits -> total %lu bytes)\n",
                               kind, cur_id,
                               static_cast<unsigned long>(cur_size * cur_count),
                               static_cast<unsigned long>((acc_bits + 7) / 8));
            }
        }
        i = static_cast<uint16_t>(i + 1 + dsz);
    }
}

void sdp_callback(uint8_t packet_type, uint16_t channel, uint8_t *packet, uint16_t size) {
    (void)packet_type;
    (void)channel;
    (void)size;
    uint8_t event = hci_event_packet_get_type(packet);

    if (event == SDP_EVENT_QUERY_ATTRIBUTE_VALUE || event == SDP_EVENT_QUERY_ATTRIBUTE_BYTE) {
        uint16_t attr_id = sdp_event_query_attribute_byte_get_attribute_id(packet);
        uint16_t total_len = sdp_event_query_attribute_byte_get_attribute_length(packet);
        uint16_t offset = sdp_event_query_attribute_byte_get_data_offset(packet);
        uint8_t byte_val = sdp_event_query_attribute_byte_get_data(packet);

        if (sdp_stage == SdpStage::QueryHid && attr_id == kAttrHidDescriptorList) {
            if (offset == 0) {
                sdp_cur_attr_id = attr_id;
                sdp_attr_len = std::min<uint16_t>(total_len, sizeof(sdp_attr_buf));
            }
            if (offset < sizeof(sdp_attr_buf)) {
                sdp_attr_buf[offset] = byte_val;
            }
            if (offset + 1 == total_len && sdp_attr_len > 0) {
                decode_and_log_hid_descriptor(sdp_attr_buf, sdp_attr_len);
            }
        } else if (sdp_stage == SdpStage::QueryPnp && (attr_id >= 0x0201 && attr_id <= 0x0205)) {
            if (offset == 0) {
                sdp_cur_attr_id = attr_id;
                sdp_attr_len = std::min<uint16_t>(total_len, sizeof(sdp_attr_buf));
            }
            if (offset < sizeof(sdp_attr_buf)) {
                sdp_attr_buf[offset] = byte_val;
            }
            if (offset + 1 == total_len && sdp_attr_len >= 3 && de_get_element_type(sdp_attr_buf) == DE_UINT) {
                uint32_t h = de_get_header_size(sdp_attr_buf);
                uint16_t v = 0;
                if (sdp_attr_len >= h + 2) {
                    v = static_cast<uint16_t>((sdp_attr_buf[h] << 8) | sdp_attr_buf[h + 1]);
                }
                if (attr_id == 0x0201) g_telemetry.sdp.pnp_vid = v;
                else if (attr_id == 0x0202) g_telemetry.sdp.pnp_pid = v;
                else if (attr_id == 0x0203) g_telemetry.sdp.pnp_version = v;
            }
        }
    } else if (event == SDP_EVENT_QUERY_COMPLETE) {
        uint8_t status = sdp_event_query_complete_get_status(packet);
        if (sdp_stage == SdpStage::QueryHid) {
            g_telemetry.sdp.hid_done = true;
            g_telemetry.sdp.hid_status = status;
            console_printf("[SDP] HID Service query complete status=0x%02X\n", status);
            // Chain to PnP Info (0x1200)
            sdp_stage = SdpStage::QueryPnp;
            uint8_t st = sdp_client_query_uuid16(&sdp_callback, peer_addr, BLUETOOTH_SERVICE_CLASS_PNP_INFORMATION);
            if (st != ERROR_CODE_SUCCESS) {
                sdp_stage = SdpStage::Idle;
                console_printf("[SDP] PnP query start failed 0x%02X\n", st);
            }
        } else if (sdp_stage == SdpStage::QueryPnp) {
            g_telemetry.sdp.pnp_done = true;
            g_telemetry.sdp.pnp_status = status;
            g_telemetry.sdp.queried = true;
            sdp_stage = SdpStage::Idle;
            console_printf("[SDP] PnP query complete status=0x%02X -> VID=0x%04X PID=0x%04X Ver=0x%04X\n",
                           status, g_telemetry.sdp.pnp_vid, g_telemetry.sdp.pnp_pid, g_telemetry.sdp.pnp_version);
        }
    }
}

void compute_dynamic_impulse_pct(uint8_t &out_l_pct, uint8_t &out_r_pct) {
    uint32_t now_us = time_us_32();
    if (impulse_test_active && static_cast<int32_t>(now_us - impulse_test_until_us) < 0) {
        out_l_pct = impulse_test_pct;
        out_r_pct = impulse_test_pct;
        return;
    }

    if (g_dsp_config.impulse_mode == ImpulseTriggerMode::Off ||
        g_dsp_config.impulse_strength_pct == 0 ||
        !g_host_output.valid) {
        out_l_pct = 0;
        out_r_pct = 0;
        return;
    }

    uint8_t gr = g_host_output.payload[2];
    uint8_t gl = g_host_output.payload[3];
    if ((gr | gl) == 0) {
        out_l_pct = 0;
        out_r_pct = 0;
        return;
    }

    // Blend XInput L (low-freq) & R (high-freq) motors so games using only 1 motor still pulse both triggers naturally
    uint8_t raw_l = std::max<uint8_t>(gl, static_cast<uint8_t>((gr * 3u) / 4u));
    uint8_t raw_r = std::max<uint8_t>(gr, static_cast<uint8_t>((gl * 3u) / 4u));

    auto scale_side = [](uint8_t raw, uint8_t pull_depth) -> uint8_t {
        if (raw < 10) return 0;
        uint32_t pct = (static_cast<uint32_t>(raw) * 100u + 127u) / 255u;
        pct = (pct * g_dsp_config.impulse_strength_pct + 50u) / 100u;
        if (g_dsp_config.impulse_mode == ImpulseTriggerMode::WhenPulled) {
            if (pull_depth < 20) return 0;
            uint32_t pull_scale = 35u + ((static_cast<uint32_t>(pull_depth - 20u) * 65u) / 235u);
            pct = (pct * pull_scale + 50u) / 100u;
        }
        return static_cast<uint8_t>(std::clamp<uint32_t>(pct, 12u, 100u));
    };

    out_l_pct = scale_side(raw_l, last_bt_body[4]);
    out_r_pct = scale_side(raw_r, last_bt_body[5]);
}

// Populate common 47-byte/63-byte DualSense output block shared by 0x31 and 0x90 subpacket in 0x36/0x39
void fill_state_block(uint8_t p[63], bool allow_synth_motors, bool haptics_audio_mode) {
    if (g_host_output.valid && g_host_output.len > 0) {
        uint16_t n = std::min<uint16_t>(g_host_output.len, 47);
        std::memcpy(p, g_host_output.payload, n);
    }

    uint8_t game_r = g_host_output.valid ? p[2] : 0;
    uint8_t game_l = g_host_output.valid ? p[3] : 0;
    uint8_t synth_r = 0, synth_l = 0;
    if (allow_synth_motors) {
        dsp_get_synth_motors(synth_r, synth_l);
    }
    g_telemetry.last_synth_right = synth_r;
    g_telemetry.last_synth_left = synth_l;

    uint8_t out_r = game_r;
    uint8_t out_l = game_l;
    if (allow_synth_motors) {
        if (synth_r > out_r) out_r = synth_r;
        if (synth_l > out_l) out_l = synth_l;
    }

    if (haptics_audio_mode) {
        // ds5-untethered SetHapticsMode: clear rumble bits, enable PowerSaveControl|HapticsLowPass|MotorPowerControl (0x62)
        p[0] &= static_cast<uint8_t>(~(0x01u | 0x02u));
        p[1] |= 0x62u;
        p[2] = 0;
        p[3] = 0;
        p[9] = 0;  // PowerSave = 0
        p[36] = 0; // MotorPowerReduction = 0
    } else if ((out_r | out_l) != 0) {
        p[0] |= 0x03u; // CompatibleVibration | HapticsSelect
        p[2] = out_r;
        p[3] = out_l;
        p[38] |= 0x04u; // EnableImprovedRumbleEmulation
    } else {
        p[0] = static_cast<uint8_t>((p[0] | 0x01u) & ~0x02u);
        p[2] = 0;
        p[3] = 0;
    }

    g_telemetry.last_sent_right = p[2];
    g_telemetry.last_sent_left = p[3];

    // Mic Mute LED: driven by mic_led_override_mode (0=off, 1=solid, 2=pulse)
    p[1] |= 0x01u; // MicMuteLedControlEnable
    p[8] = mic_led_override_mode;

    uint8_t imp_l_pct = 0, imp_r_pct = 0;
    compute_dynamic_impulse_pct(imp_l_pct, imp_r_pct);
    last_imp_sent_l = imp_l_pct;
    last_imp_sent_r = imp_r_pct;

    if (trig_r_override_active) {
        p[0] |= 0x04u;
        std::memcpy(p + 10, trig_r_override, 11);
        g_telemetry.last_r2_mode = trig_r_override[0];
        prev_imp_r_active = false;
    } else if (g_usb_mode != UsbMode::DualSense || (p[0] & 0x04u) == 0) {
        if (imp_r_pct > 0) {
            p[0] |= 0x04u;
            write_ds5_vibration_trig(p + 10, imp_r_pct);
            g_telemetry.last_r2_mode = 0x26;
            prev_imp_r_active = true;
        } else if (prev_imp_r_active) {
            p[0] |= 0x04u;
            write_ds5_vibration_trig(p + 10, 0);
            g_telemetry.last_r2_mode = 0x05;
            prev_imp_r_active = false;
        }
    }

    if (trig_l_override_active) {
        p[0] |= 0x08u;
        std::memcpy(p + 21, trig_l_override, 11);
        g_telemetry.last_l2_mode = trig_l_override[0];
        prev_imp_l_active = false;
    } else if (g_usb_mode != UsbMode::DualSense || (p[0] & 0x08u) == 0) {
        if (imp_l_pct > 0) {
            p[0] |= 0x08u;
            write_ds5_vibration_trig(p + 21, imp_l_pct);
            g_telemetry.last_l2_mode = 0x26;
            prev_imp_l_active = true;
        } else if (prev_imp_l_active) {
            p[0] |= 0x08u;
            write_ds5_vibration_trig(p + 21, 0);
            g_telemetry.last_l2_mode = 0x05;
            prev_imp_l_active = false;
        }
    }

    if (led_override_active) {
        p[1] |= 0x04u; // Lightbar control enable
        p[44] = led_override_r;
        p[45] = led_override_g;
        p[46] = led_override_b;
    }
}

void build_0x31_packet(uint8_t out_pkt[79], bool allow_synth_motors, bool clear_haptics_select) {
    std::memset(out_pkt, 0, 79);
    out_pkt[0] = 0xA2;
    uint8_t *r = out_pkt + 1;
    r[0] = 0x31;
    r[1] = static_cast<uint8_t>((bt_seq_nibble++ & 0x0Fu) << 4);
    r[2] = 0x10; // BT output tag
    fill_state_block(r + 3, allow_synth_motors, clear_haptics_select);
    write_bt_report_crc(r, 78);
}

void build_0x32_packet(uint8_t out_pkt[143], int8_t const haptics[64]) {
    std::memset(out_pkt, 0, 143);
    out_pkt[0] = 0xA2;
    uint8_t *r = out_pkt + 1;
    r[0] = 0x32;
    r[1] = static_cast<uint8_t>((bt_seq_nibble++ & 0x0Fu) << 4);
    r[2] = 0x91; r[3] = 0x07;
    r[4] = 0xFE; r[5] = 0x00; r[6] = 0x00; r[7] = 0x00; r[8] = 0x00; r[9] = 0x20;
    r[10] = bt_audio_ctr++;
    r[11] = 0x92; r[12] = 64;
    std::memcpy(r + 13, haptics, 64);
    write_bt_report_crc(r, 142);
}

void build_long_haptic_packet(uint8_t report_id, uint16_t payload_len, uint8_t *out_pkt, int8_t const haptics[64]) {
    std::memset(out_pkt, 0, payload_len + 1u);
    out_pkt[0] = 0xA2;
    uint8_t *r = out_pkt + 1;
    r[0] = report_id;
    r[1] = static_cast<uint8_t>((bt_seq_nibble++ & 0x0Fu) << 4);
    r[2] = 0x91; r[3] = 0x07;
    r[4] = 0xFE; r[5] = 0x00; r[6] = 0x00; r[7] = 0x00; r[8] = 0x00; r[9] = 0x20;
    r[10] = bt_audio_ctr++;
    r[11] = 0x90; r[12] = 63;
    fill_state_block(r + 13, false, true);
    r[76] = 0x92; r[77] = 64;
    std::memcpy(r + 78, haptics, 64);
    write_bt_report_crc(r, payload_len);
}

void build_0x36_packet(uint8_t out_pkt[399], int8_t const haptics[64]) {
    build_long_haptic_packet(0x36, 398, out_pkt, haptics);
}

void build_0x39_packet(uint8_t out_pkt[548], int8_t const haptics[64]) {
    build_long_haptic_packet(0x39, 547, out_pkt, haptics);
}

void l2cap_packet_handler(uint8_t packet_type, uint16_t channel, uint8_t *packet, uint16_t size);

void open_hid_channels() {
    if (acl_handle == HCI_CON_HANDLE_INVALID || !security_ready) return;
    if (hid_remote_initiated) return;
    if (!hid_ctrl_ready && hid_ctrl_cid == 0 && hid_ctrl_pending == 0) {
        uint8_t st = l2cap_create_channel(&l2cap_packet_handler, peer_addr, kPsmHidControl, kMtuControl, &hid_ctrl_pending);
        console_printf("[BT] Open HID Control -> status=0x%02X cid=0x%04X\n", st, hid_ctrl_pending);
    } else if (hid_ctrl_ready && !hid_intr_ready && hid_intr_cid == 0 && hid_intr_pending == 0) {
        uint8_t st = l2cap_create_channel(&l2cap_packet_handler, peer_addr, kPsmHidInterrupt, kMtuInterrupt, &hid_intr_pending);
        console_printf("[BT] Open HID Interrupt -> status=0x%02X cid=0x%04X\n", st, hid_intr_pending);
    }
}

void on_hid_session_ready() {
    console_printf("[BT] HID Control+Interrupt READY (initiator=%s)! Prefetching features (no 0x71)\n",
                   hid_remote_initiated ? "controller" : "pico");
    hid_local_fallback_pending = false;
    static constexpr uint8_t kReqIds[4] = {0x20, 0x09, 0x22, 0x05};
    prefetch_count = 4;
    prefetch_idx = 0;
    for (uint8_t i = 0; i < 4; ++i) prefetch_ids[i] = kReqIds[i];
    prefetch_next_us = time_us_32() + 40000u;
    unlock_boot_retries_left = 4;
    last_state_send_us = time_us_32();
    usb_set_bt_ready(true);
}

void reset_session_state() {
    acl_handle = HCI_CON_HANDLE_INVALID;
    hid_ctrl_cid = 0;
    hid_intr_cid = 0;
    hid_ctrl_pending = 0;
    hid_intr_pending = 0;
    hid_ctrl_ready = false;
    hid_intr_ready = false;
    hid_session_announced = false;
    hid_remote_initiated = false;
    hid_local_fallback_pending = false;
    hid_open_ctrl_done_us = 0;
    security_ready_us = 0;
    rx_intr_logged = false;
    rx_0x31_seen = false;
    security_ready = false;
    prefetch_count = 0;
    prefetch_idx = 0;
    prefetch_next_us = 0;
    unlock_boot_retries_left = 0;
    last_mon_len = 0;
    std::memset(last_bt_body, 0, sizeof(last_bt_body));
    intr_can_send_req = false;
    ctrl_can_send_req = false;
    audio_block_pending = false;
    state_send_pending = false;
    deferred_state_flush = false;
    impulse_test_active = false;
    prev_imp_l_active = false;
    prev_imp_r_active = false;
    last_imp_sent_l = 0;
    last_imp_sent_r = 0;
    outbound_connecting = false;
    sdp_stage = SdpStage::Idle;
    sdp_auto_pending = false;
    ctrl_q_head = 0;
    ctrl_q_tail = 0;
    for (auto &f : feat_cache) f.valid = false;
    g_telemetry.bt_acl_connected = false;
    g_telemetry.bt_control_ready = false;
    g_telemetry.bt_interrupt_ready = false;
    g_telemetry.bt_connecting = false;
    usb_set_bt_ready(false);
}

void convert_short_0x01_to_ds5(uint8_t const *in9, uint8_t out63[63]) {
    std::memset(out63, 0, 63);
    out63[0] = in9[0]; // LX
    out63[1] = in9[1]; // LY
    out63[2] = in9[2]; // RX
    out63[3] = in9[3]; // RY
    out63[4] = in9[7]; // L2
    out63[5] = in9[8]; // R2
    out63[6] = 0x01;
    out63[7] = in9[4];
    out63[8] = in9[5];
    out63[9] = in9[6];
    out63[52] = 0x0A; // Avoid fake 5% low-battery OS/Steam popup before 0x31 unlocks
    out63[53] = 0x08;
}

void l2cap_packet_handler(uint8_t packet_type, uint16_t channel, uint8_t *packet, uint16_t size) {
    if (packet_type == L2CAP_DATA_PACKET) {
        if (channel == hid_intr_cid && size > 2) {
            if (!rx_intr_logged) {
                rx_intr_logged = true;
                console_printf("[BT] First Intr RX id=0x%02X len=%u\n", packet[1], size);
            }
            uint16_t copy_dbg = std::min<uint16_t>(size, sizeof(last_intr_rx));
            std::memcpy(last_intr_rx, packet, copy_dbg);
            last_intr_rx_size = size;
            if (packet[1] == 0x31 && (packet[2] & 0x02u) == 0 && size >= 66) {
                rx_0x31_seen = true;
                g_telemetry.bt_0x31_rx_count++;
                std::memcpy(last_bt_body, packet + 3, 63);
                usb_submit_bt_input_report(packet + 3, 63);
            } else if (packet[1] == 0x01 && size >= 11) {
                g_telemetry.bt_0x01_rx_count++;
                convert_short_0x01_to_ds5(packet + 2, last_bt_body);
                usb_submit_bt_input_report(last_bt_body, 63);
            }
            if (btmon_enabled) {
                uint16_t mlen = std::min<uint16_t>(size, sizeof(last_mon_raw));
                uint32_t now_mon = time_us_32();
                bool diff = (mlen != last_mon_len);
                if (!diff) {
                    // Check button/vendor/tail bytes (skip seq, sticks, IMU, touchpad, and timestamp[50..54])
                    if (packet[1] == 0x01) {
                        for (uint16_t i = 6; i < mlen && i <= 10; ++i) {
                            if (i != 8 && packet[i] != last_mon_raw[i]) { diff = true; break; }
                        }
                    } else if (packet[1] == 0x31) {
                        for (uint16_t i = 9; i <= 15 && i < mlen; ++i) {
                            if (packet[i] != last_mon_raw[i]) { diff = true; break; }
                        }
                        if (!diff && mlen > 45 && packet[45] != last_mon_raw[45]) diff = true;
                        if (!diff && mlen > 49 && packet[49] != last_mon_raw[49]) diff = true;
                        for (uint16_t i = 55; i < mlen && !diff; ++i) {
                            if (packet[i] != last_mon_raw[i]) { diff = true; break; }
                        }
                    }
                }
                if (diff && (last_mon_len == 0 || static_cast<int32_t>(now_mon - last_mon_us) >= 25000)) {
                    std::memcpy(last_mon_raw, packet, mlen);
                    last_mon_len = mlen;
                    last_mon_us = now_mon;
                    console_printf("[MON] len=%u id=0x%02X b9..15:", size, packet[1]);
                    for (uint16_t i = 9; i <= 15 && i < mlen; ++i) console_printf(" %02X", packet[i]);
                    if (mlen > 45) {
                        console_printf(" | t45..49:");
                        for (uint16_t i = 45; i <= 49 && i < mlen; ++i) console_printf(" %02X", packet[i]);
                    }
                    if (mlen > 55) {
                        console_printf(" | t55+:");
                        for (uint16_t i = 55; i < mlen; ++i) console_printf(" %02X", packet[i]);
                    }
                    console_printf("\n");
                }
            }
            return;
        }
        if (channel == hid_ctrl_cid && size >= 1) {
            uint8_t hdr = packet[0];
            if ((hdr & 0xF0u) == 0xA0u && size >= 3) {
                store_feature(packet[1], packet + 1, static_cast<uint16_t>(size - 1));
                console_printf("[BT] Cached Feature 0x%02X (%u bytes)\n", packet[1], size - 1);
                if (packet[1] == 0x05) {
                    // Sensa requires a 0x31 SetHapticsMode output after Feature 0x05 to switch into full 0x31 input reports
                    state_send_pending = true;
                    request_intr_tx();
                }
            } else {
                console_printf("[BT] HID Ctrl rx len=%u b0=0x%02X\n", size, hdr);
            }
            return;
        }
        console_printf("[BT] Other L2CAP RX cid=0x%04X (ctrl=0x%04X intr=0x%04X) len=%u\n",
                       channel, hid_ctrl_cid, hid_intr_cid, size);
        return;
    }

    if (packet_type != HCI_EVENT_PACKET) return;
    uint8_t event = hci_event_packet_get_type(packet);

    if (event == L2CAP_EVENT_CAN_SEND_NOW) {
        uint16_t cid = l2cap_event_can_send_now_get_local_cid(packet);
        if (cid == hid_ctrl_cid) {
            ctrl_can_send_req = false;
            if (ctrl_q_tail != ctrl_q_head) {
                CtrlPkt const &pkt = ctrl_q[ctrl_q_tail];
                uint8_t st = l2cap_send(hid_ctrl_cid, const_cast<uint8_t *>(pkt.bytes), pkt.len);
                if (st == 0) {
                    ctrl_q_tail = static_cast<uint8_t>((ctrl_q_tail + 1) % kCtrlQueueCap);
                } else {
                    g_telemetry.l2cap_fail_count++;
                }
                if (ctrl_q_tail != ctrl_q_head) {
                    ctrl_can_send_req = true;
                    l2cap_request_can_send_now_event(hid_ctrl_cid);
                }
            }
            return;
        }

        if (cid == hid_intr_cid) {
            intr_can_send_req = false;
            if (audio_block_pending) {
                audio_block_pending = false;
                uint8_t buf[548];
                uint16_t len = 0;
                if (pending_audio_proto == HapticMode::Mode32SAxense ||
                    pending_audio_proto == HapticMode::Mode31Hybrid32) {
                    build_0x32_packet(buf, pending_audio_block);
                    len = 143;
                    g_telemetry.bt_0x32_tx_count++;
                } else if (pending_audio_proto == HapticMode::Mode36Haptics) {
                    build_0x36_packet(buf, pending_audio_block);
                    len = 399;
                    g_telemetry.bt_0x36_tx_count++;
                    state_send_pending = false; // 0x36 carries full 0x90 state
                    last_state_send_us = time_us_32();
                } else if (pending_audio_proto == HapticMode::Mode39Probe) {
                    build_0x39_packet(buf, pending_audio_block);
                    len = 548;
                    g_telemetry.bt_0x39_tx_count++;
                    state_send_pending = false; // 0x39 carries full 0x90 state
                    last_state_send_us = time_us_32();
                }
                if (len > 0) {
                    uint8_t st = l2cap_send(hid_intr_cid, buf, len);
                    if (st != 0) g_telemetry.l2cap_fail_count++;
                }
            } else if (state_send_pending) {
                state_send_pending = false;
                deferred_state_flush = false;
                uint8_t pkt548[548]{};
                bool test_rumble_now = dsp_has_active_test_rumble();
                bool synth = (g_haptic_mode == HapticMode::Mode31Synth) || test_rumble_now;
                bool haptics_mode_0x31 = !rx_0x31_seen ||
                                         (g_haptic_mode == HapticMode::Mode31Hybrid32) ||
                                         ((g_haptic_mode == HapticMode::Mode36Haptics || g_haptic_mode == HapticMode::Mode39Probe) &&
                                          dsp_is_haptics_active() && !test_rumble_now);
                build_0x31_packet(pkt548, synth, haptics_mode_0x31);
                uint16_t tx_len = send_547_once ? 548 : 79;
                send_547_once = false;
                uint8_t st = l2cap_send(hid_intr_cid, pkt548, tx_len);
                if (st == 0) {
                    g_telemetry.bt_0x31_tx_count++;
                    last_state_send_us = time_us_32();
                } else {
                    g_telemetry.l2cap_fail_count++;
                }
            }
            if (audio_block_pending || state_send_pending) {
                request_intr_tx();
            }
            return;
        }
        return;
    }

    switch (event) {
        case L2CAP_EVENT_INCOMING_CONNECTION: {
            uint16_t psm = l2cap_event_incoming_connection_get_psm(packet);
            uint16_t lcid = l2cap_event_incoming_connection_get_local_cid(packet);
            if (!security_ready) {
                console_printf("[BT] Decline incoming L2CAP psm=0x%04X before security\n", psm);
                l2cap_decline_connection(lcid);
                break;
            }
            if (psm == kPsmHidControl && hid_ctrl_cid == 0 && hid_ctrl_pending == 0) {
                hid_remote_initiated = true;
                hid_local_fallback_pending = false;
                hid_ctrl_pending = lcid;
                console_printf("[BT] Incoming HID Control cid=0x%04X -> accept\n", lcid);
                l2cap_accept_connection(lcid);
            } else if (psm == kPsmHidInterrupt && hid_intr_cid == 0 && hid_intr_pending == 0) {
                hid_remote_initiated = true;
                hid_local_fallback_pending = false;
                hid_intr_pending = lcid;
                console_printf("[BT] Incoming HID Interrupt cid=0x%04X -> accept\n", lcid);
                l2cap_accept_connection(lcid);
            } else {
                l2cap_decline_connection(lcid);
            }
            break;
        }
        case L2CAP_EVENT_CHANNEL_OPENED: {
            uint8_t st = l2cap_event_channel_opened_get_status(packet);
            uint16_t psm = l2cap_event_channel_opened_get_psm(packet);
            uint16_t lcid = l2cap_event_channel_opened_get_local_cid(packet);
            if (st != 0) {
                console_printf("[BT] L2CAP open failed psm=0x%04X status=0x%02X\n", psm, st);
                if (psm == kPsmHidControl) hid_ctrl_pending = 0;
                if (psm == kPsmHidInterrupt) hid_intr_pending = 0;
                break;
            }
            if (psm == kPsmHidControl) {
                hid_ctrl_cid = lcid;
                hid_ctrl_pending = 0;
                hid_ctrl_ready = true;
                hid_open_ctrl_done_us = time_us_32();
                g_telemetry.bt_control_ready = true;
                if (!hid_remote_initiated) open_hid_channels();
            } else if (psm == kPsmHidInterrupt) {
                hid_intr_cid = lcid;
                hid_intr_pending = 0;
                hid_intr_ready = true;
                g_telemetry.bt_interrupt_ready = true;
            }
            if (hid_ctrl_ready && hid_intr_ready && !hid_session_announced) {
                hid_session_announced = true;
                on_hid_session_ready();
            }
            break;
        }
        case L2CAP_EVENT_CHANNEL_CLOSED: {
            uint16_t lcid = l2cap_event_channel_closed_get_local_cid(packet);
            if (lcid == hid_ctrl_cid) { hid_ctrl_cid = 0; hid_ctrl_ready = false; g_telemetry.bt_control_ready = false; }
            if (lcid == hid_intr_cid) { hid_intr_cid = 0; hid_intr_ready = false; g_telemetry.bt_interrupt_ready = false; }
            if (!hid_ctrl_ready || !hid_intr_ready) usb_set_bt_ready(false);
            break;
        }
        default:
            break;
    }
}

void hci_packet_handler(uint8_t packet_type, uint16_t channel, uint8_t *packet, uint16_t size) {
    (void)channel;
    (void)size;
    if (packet_type != HCI_EVENT_PACKET) return;
    uint8_t event = hci_event_packet_get_type(packet);

    switch (event) {
        case BTSTACK_EVENT_STATE:
            if (btstack_event_state_get_state(packet) == HCI_STATE_WORKING) {
                state_working_us = time_us_32();
                gap_set_page_scan_activity(0x0012, 0x0012);
                gap_set_page_scan_type(PAGE_SCAN_MODE_INTERLACED);
                refresh_bonded_key();
                console_printf("[BT] HCI Working! bonded=%d\n", has_bonded_addr ? 1 : 0);
                if (has_bonded_addr) {
                    std::memcpy(g_telemetry.bt_addr, bonded_addr, 6);
                    gap_connectable_control(1);
                } else {
                    bt_start_pairing();
                }
            }
            break;

        case GAP_EVENT_INQUIRY_RESULT: {
            if (!pairing_window || outbound_connecting || acl_handle != HCI_CON_HANDLE_INVALID) break;
            uint32_t cod = gap_event_inquiry_result_get_class_of_device(packet);
            bd_addr_t addr;
            gap_event_inquiry_result_get_bd_addr(packet, addr);
            char name_buf[48]{};
            if (gap_event_inquiry_result_get_name_available(packet)) {
                uint8_t nlen = gap_event_inquiry_result_get_name_len(packet);
                uint8_t copy_n = std::min<uint8_t>(nlen, sizeof(name_buf) - 1);
                std::memcpy(name_buf, gap_event_inquiry_result_get_name(packet), copy_n);
            }
            uint32_t major = (cod >> 8) & 0x1Fu;
            console_printf("[BT] Inquiry found %s CoD=0x%06lX name='%s'\n",
                           bd_addr_to_str(addr), static_cast<unsigned long>(cod), name_buf);
            // Major Device Class 5 = Peripheral (Gamepads/Controllers)
            if (major == 5) {
                uint8_t page_rep = gap_event_inquiry_result_get_page_scan_repetition_mode(packet);
                uint16_t clk_off = gap_event_inquiry_result_get_clock_offset(packet);
                uint16_t valid_clk = (clk_off != 0) ? static_cast<uint16_t>((clk_off & 0x7FFFu) | 0x8000u) : 0;
                gap_inquiry_stop();
                inquiry_active = false;
                bd_addr_copy(peer_addr, addr);
                peer_addr_valid = true;
                std::memcpy(g_telemetry.bt_addr, addr, 6);
                std::memcpy(g_telemetry.bt_name, name_buf, sizeof(g_telemetry.bt_name));
                outbound_connecting = true;
                uint8_t st = hci_send_cmd(
                    &hci_create_connection,
                    addr,
                    hci_usable_acl_packet_types(),
                    page_rep,
                    0,
                    valid_clk,
                    1
                );
                console_printf("[BT] Connecting to %s -> status=0x%02X\n", bd_addr_to_str(addr), st);
            }
            break;
        }

        case GAP_EVENT_INQUIRY_COMPLETE:
            inquiry_active = false;
            if (pairing_window && acl_handle == HCI_CON_HANDLE_INVALID && !outbound_connecting) {
                gap_inquiry_start(8);
                inquiry_active = true;
            }
            break;

        case HCI_EVENT_CONNECTION_COMPLETE: {
            outbound_connecting = false;
            g_telemetry.bt_connecting = false;
            uint8_t st = hci_event_connection_complete_get_status(packet);
            if (st != 0) {
                console_printf("[BT] ACL connect failed status=0x%02X\n", st);
                if (pairing_window && !inquiry_active) {
                    gap_inquiry_start(8);
                    inquiry_active = true;
                } else if (has_bonded_addr) {
                    gap_connectable_control(1);
                }
                break;
            }
            acl_handle = hci_event_connection_complete_get_connection_handle(packet);
            hci_event_connection_complete_get_bd_addr(packet, peer_addr);
            peer_addr_valid = true;
            std::memcpy(g_telemetry.bt_addr, peer_addr, 6);
            g_telemetry.bt_acl_connected = true;
            pairing_window = false;
            g_telemetry.bt_pairing_active = false;
            gap_connectable_control(0);
            gap_inquiry_stop();
            inquiry_active = false;
            console_printf("[BT] ACL Connected %s handle=0x%04X -> requesting Level 2 security\n",
                           bd_addr_to_str(peer_addr), acl_handle);
            gap_request_security_level(acl_handle, LEVEL_2);
            break;
        }

        case HCI_EVENT_LINK_KEY_REQUEST: {
            bd_addr_t addr;
            hci_event_link_key_request_get_bd_addr(packet, addr);
            link_key_t key;
            link_key_type_t type;
            if (gap_get_link_key_for_bd_addr(addr, key, &type)) {
                hci_send_cmd(&hci_link_key_request_reply, addr, key);
            } else {
                hci_send_cmd(&hci_link_key_request_negative_reply, addr);
            }
            break;
        }

        case HCI_EVENT_PIN_CODE_REQUEST: {
            bd_addr_t addr;
            hci_event_pin_code_request_get_bd_addr(packet, addr);
            uint8_t pin[16]{};
            std::memcpy(pin, "0000", 4);
            hci_send_cmd(&hci_pin_code_request_reply, addr, 4, pin);
            break;
        }

        case HCI_EVENT_USER_CONFIRMATION_REQUEST: {
            // Auto-accepted by gap_ssp_set_auto_accept(true)
            console_printf("[BT] SSP User Confirmation auto-accepted\n");
            break;
        }

        case HCI_EVENT_LINK_KEY_NOTIFICATION:
            refresh_bonded_key();
            gap_set_bondable_mode(0);
            console_printf("[BT] Link key bonded!\n");
            break;

        case GAP_EVENT_SECURITY_LEVEL: {
            uint8_t lvl = gap_event_security_level_get_security_level(packet);
            if (lvl >= LEVEL_2 && !security_ready) {
                security_ready = true;
                security_ready_us = time_us_32();
                hid_local_fallback_pending = true;
                console_printf("[BT] Security LEVEL_%u -> waiting 650ms for controller HID channels\n", lvl);
            }
            break;
        }

        case HCI_EVENT_ENCRYPTION_CHANGE:
            if (hci_event_encryption_change_get_encryption_enabled(packet) != 0 && !security_ready) {
                security_ready = true;
                security_ready_us = time_us_32();
                hid_local_fallback_pending = true;
                console_printf("[BT] Encryption ON -> waiting 650ms for controller HID channels\n");
            }
            break;

        case HCI_EVENT_AUTHENTICATION_COMPLETE: {
            uint8_t st = hci_event_authentication_complete_get_status(packet);
            if (st == ERROR_CODE_PIN_OR_KEY_MISSING && peer_addr_valid) {
                console_printf("[BT] Controller rejected link key -> dropping key and re-pairing\n");
                gap_drop_link_key_for_bd_addr(peer_addr);
                refresh_bonded_key();
                gap_disconnect(acl_handle);
            }
            break;
        }

        case HCI_EVENT_DISCONNECTION_COMPLETE:
            console_printf("[BT] ACL Disconnected\n");
            reset_session_state();
            if (has_bonded_addr) {
                gap_connectable_control(1);
            } else {
                bt_start_pairing();
            }
            break;

        default:
            break;
    }
}

void initiate_bonded_connection(char const *tag) {
    bd_addr_copy(peer_addr, bonded_addr);
    peer_addr_valid = true;
    outbound_connecting = true;
    g_telemetry.bt_connecting = true;
    uint8_t st = hci_send_cmd(
        &hci_create_connection,
        bonded_addr,
        hci_usable_acl_packet_types(),
        2,
        0,
        0,
        1
    );
    console_printf("[BT] %s %s -> status=0x%02X\n", tag, bd_addr_to_str(bonded_addr), st);
}

} // namespace

bool bt_init() {
    if (cyw43_arch_init() != 0) {
        return false;
    }
    cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, false);

    l2cap_cb_reg.callback = &l2cap_packet_handler;
    l2cap_add_event_handler(&l2cap_cb_reg);
    sdp_init();
    l2cap_register_service(&l2cap_packet_handler, kPsmHidControl, kMtuControl, LEVEL_2);
    l2cap_register_service(&l2cap_packet_handler, kPsmHidInterrupt, kMtuInterrupt, LEVEL_2);
    l2cap_init();
    gap_set_link_supervision_timeout(2400); // 1.5s supervision timeout for crisp unplug disconnect

    gap_ssp_set_enable(true);
    gap_secure_connections_enable(true);
    gap_ssp_set_io_capability(SSP_IO_CAPABILITY_DISPLAY_YES_NO);
    gap_ssp_set_authentication_requirement(SSP_IO_AUTHREQ_MITM_PROTECTION_NOT_REQUIRED_GENERAL_BONDING);
    gap_ssp_set_auto_accept(true);
    gap_set_bondable_mode(0);
    hci_set_master_slave_policy(HCI_ROLE_MASTER);
    gap_connectable_control(0);
    gap_discoverable_control(0);

    hci_cb_reg.callback = &hci_packet_handler;
    hci_add_event_handler(&hci_cb_reg);
    hci_power_control(HCI_POWER_ON);
    return true;
}

void bt_start_pairing() {
    pairing_window = true;
    pairing_deadline_us = time_us_32() + 45000000u;
    g_telemetry.bt_pairing_active = true;
    gap_set_bondable_mode(1);
    gap_connectable_control(1);
    if (acl_handle != HCI_CON_HANDLE_INVALID) {
        gap_disconnect(acl_handle);
        return;
    }
    if (!inquiry_active) {
        uint8_t st = gap_inquiry_start(8);
        inquiry_active = (st == ERROR_CODE_SUCCESS);
        console_printf("[BT] Pairing scan started (status=0x%02X) - hold Share+PS on Sensa\n", st);
    }
}

void bt_forget_all() {
    gap_delete_all_link_keys();
    refresh_bonded_key();
    console_printf("[BT] Cleared all bonded link keys\n");
    bt_start_pairing();
}

void bt_disconnect_now() {
    pairing_window = false;
    g_telemetry.bt_pairing_active = false;
    gap_inquiry_stop();
    inquiry_active = false;
    if (outbound_connecting && peer_addr_valid) {
        hci_send_cmd(&hci_create_connection_cancel, peer_addr);
        outbound_connecting = false;
        g_telemetry.bt_connecting = false;
    }
    if (acl_handle != HCI_CON_HANDLE_INVALID) {
        gap_disconnect(acl_handle);
        console_printf("[BT] Disconnecting controller ACL...\n");
    }
}

void bt_reconnect_bonded() {
    if (acl_handle != HCI_CON_HANDLE_INVALID || outbound_connecting || pairing_window) return;
    refresh_bonded_key();
    if (!has_bonded_addr) {
        console_printf("[BT] No bonded controller found -> starting pairing scan\n");
        bt_start_pairing();
        return;
    }
    initiate_bonded_connection("Reconnecting bonded");
}

bool bt_start_sdp_discovery() {
    if (acl_handle == HCI_CON_HANDLE_INVALID || !peer_addr_valid) return false;
    if (sdp_stage != SdpStage::Idle) return true;
    g_telemetry.sdp = SdpSummary{};
    sdp_stage = SdpStage::QueryHid;
    uint8_t st = sdp_client_query_uuid16(&sdp_callback, peer_addr, BLUETOOTH_SERVICE_CLASS_HUMAN_INTERFACE_DEVICE_SERVICE);
    console_printf("[SDP] Starting HID Service query -> status=0x%02X\n", st);
    if (st != ERROR_CODE_SUCCESS) {
        sdp_stage = SdpStage::Idle;
        return false;
    }
    return true;
}

void bt_notify_host_output() {
    if (!hid_intr_ready) return;
    // While 0x36/0x39 audio-haptics is actively streaming, its 0x90 subpacket already carries
    // triggers/LEDs every 10.67 ms — flooding 0x31 at 450 Hz resets/kills the 0x36 haptic stream!
    if ((g_haptic_mode == HapticMode::Mode36Haptics || g_haptic_mode == HapticMode::Mode39Probe) &&
        dsp_is_haptics_active() && !dsp_has_active_test_rumble()) {
        return;
    }
    // Cap 0x31 rate so L2CAP never floods, but defer instead of dropping stop/state packets!
    uint32_t now_us = time_us_32();
    uint32_t min_gap_us = (g_usb_mode != UsbMode::DualSense) ? 8000u : 20000u;
    if (!led_override_active && static_cast<int32_t>(now_us - last_state_send_us) < static_cast<int32_t>(min_gap_us)) {
        deferred_state_flush = true;
        return;
    }
    deferred_state_flush = false;
    state_send_pending = true;
    request_intr_tx();
}

void bt_request_state_update() {
    if (!hid_intr_ready) return;
    deferred_state_flush = false;
    state_send_pending = true;
    request_intr_tx();
}

void bt_trigger_test_impulse(uint8_t pct, uint32_t duration_ms) {
    impulse_test_pct = static_cast<uint8_t>(std::clamp<int>(pct, 5, 100));
    impulse_test_until_us = time_us_32() + duration_ms * 1000u;
    impulse_test_active = true;
    bt_request_state_update();
}

void bt_submit_haptic_waveform(int8_t const block[64], HapticMode proto) {
    if (!hid_intr_ready) return;
    std::memcpy(pending_audio_block, block, 64);
    pending_audio_proto = proto;
    audio_block_pending = true;
    request_intr_tx();
}

void bt_set_override_led(uint8_t r, uint8_t g, uint8_t b) {
    led_override_active = true;
    led_override_r = r;
    led_override_g = g;
    led_override_b = b;
    state_send_pending = true;
    request_intr_tx();
}

void bt_clear_override_led() {
    led_override_active = false;
    state_send_pending = true;
    request_intr_tx();
}

void bt_set_mic_led(uint8_t mode) {
    mic_led_override_mode = (mode > 2) ? 2 : mode;
    state_send_pending = true;
    request_intr_tx();
}

void bt_set_trigger_override(uint8_t side, bool enable, uint8_t const data11[11]) {
    if (side == 0 || side == 2) {
        trig_r_override_active = enable;
        if (enable && data11) std::memcpy(trig_r_override, data11, 11);
    }
    if (side == 1 || side == 2) {
        trig_l_override_active = enable;
        if (enable && data11) std::memcpy(trig_l_override, data11, 11);
    }
    state_send_pending = true;
    request_intr_tx();
}

void bt_set_btmon(bool enable) {
    btmon_enabled = enable;
    last_mon_len = 0;
}

void bt_set_telemetry_stream(bool enable) {
    tele_enabled = enable;
    last_tele_us = time_us_32();
}

bool bt_copy_cached_feature(uint8_t report_id, uint8_t *buf, uint16_t cap, uint16_t &copied) {
    copied = 0;
    for (auto const &e : feat_cache) {
        if (e.valid && e.id == report_id && e.len > 1) {
            uint16_t n = std::min<uint16_t>(static_cast<uint16_t>(e.len - 1), cap);
            std::memcpy(buf, e.data + 1, n);
            copied = n;
            return true;
        }
    }
    if (hid_ctrl_ready) {
        uint8_t req[2] = {0x43, report_id};
        enqueue_ctrl(req, 2);
    }
    return false;
}

void bt_send_set_feature(uint8_t report_id, uint8_t const *buf, uint16_t len) {
    if (!hid_ctrl_ready || !buf || len < 4 || len > 62) return;
    uint8_t pkt[66]{};
    pkt[0] = 0x53;
    pkt[1] = report_id;
    std::memcpy(pkt + 2, buf, len);
    write_bt_feature_crc(pkt + 1, static_cast<size_t>(len + 1));
    enqueue_ctrl(pkt, static_cast<uint16_t>(len + 2));
}

void bt_debug_dump() {
    console_printf("[DBG] last_intr_rx size=%u:", last_intr_rx_size);
    uint16_t n = std::min<uint16_t>(last_intr_rx_size, sizeof(last_intr_rx));
    for (uint16_t i = 0; i < n; ++i) console_printf(" %02X", last_intr_rx[i]);
    console_printf("\n");
    for (auto const &f : feat_cache) {
        if (!f.valid) continue;
        console_printf("[DBG] feat 0x%02X (%uB):", f.id, f.len);
        uint16_t fn = std::min<uint16_t>(f.len, 16);
        for (uint16_t i = 0; i < fn; ++i) console_printf(" %02X", f.data[i]);
        console_printf("\n");
    }
}

void bt_trigger_unlock_sequence(uint8_t mode) {
    if (!hid_ctrl_ready || !hid_intr_ready) {
        console_printf("[BT] Unlock skipped: channels not ready\n");
        return;
    }
    if (mode == 1) {
        uint8_t req5_len[4] = {0x4B, 0x05, 0x29, 0x00};
        enqueue_ctrl(req5_len, 4);
        state_send_pending = true;
        request_intr_tx();
        console_printf("[BT] unlock 1: 0x4B 0x05 (41B) + 0x31\n");
    } else if (mode == 2) {
        uint8_t pkt[80]{};
        pkt[0] = 0x52; // SET_REPORT | Output
        uint8_t tmp79[79];
        build_0x31_packet(tmp79, false, true);
        std::memcpy(pkt + 1, tmp79 + 1, 78);
        enqueue_ctrl(pkt, 79);
        console_printf("[BT] unlock 2: SET_REPORT 0x52 0x31 (79B) on Ctrl\n");
    } else if (mode == 3) {
        send_547_once = true;
        state_send_pending = true;
        request_intr_tx();
        console_printf("[BT] unlock 3: 0xA2 0x31 padded 548B on Intr\n");
    } else if (mode == 4) {
        uint8_t req5_547[4] = {0x4B, 0x05, 0x23, 0x02};
        enqueue_ctrl(req5_547, 4);
        send_547_once = true;
        state_send_pending = true;
        request_intr_tx();
        console_printf("[BT] unlock 4: 0x4B 0x05 (547B) + 0x31 548B\n");
    } else {
        uint8_t set_proto = 0x71;
        enqueue_ctrl(&set_proto, 1);
        uint8_t req5[2] = {0x43, 0x05};
        enqueue_ctrl(req5, 2);
        state_send_pending = true;
        request_intr_tx();
        console_printf("[BT] unlock 0: 0x71 + 0x43 0x05 + 0x31\n");
    }
}

void bt_poll() {
    cyw43_arch_poll();
    uint32_t now_us = time_us_32();

    // Auto-connect to bonded controller if it hasn't initiated ACL within 2.0s after boot
    if (!boot_reconnect_tried && state_working_us != 0 &&
        static_cast<uint32_t>(now_us - state_working_us) > 2000000u &&
        has_bonded_addr && acl_handle == HCI_CON_HANDLE_INVALID && !pairing_window) {
        boot_reconnect_tried = true;
        initiate_bonded_connection("Auto-connecting bonded");
    }

    // Fallback to local L2CAP open if controller didn't initiate HID channels in 650ms (or Interrupt in 1.2s)
    if (security_ready && (!hid_ctrl_ready || !hid_intr_ready)) {
        if (hid_local_fallback_pending && !hid_ctrl_ready &&
            static_cast<int32_t>(now_us - security_ready_us) >= 650000) {
            hid_local_fallback_pending = false;
            hid_remote_initiated = false;
            open_hid_channels();
        } else if (hid_remote_initiated && hid_ctrl_ready && !hid_intr_ready &&
                   hid_intr_pending == 0 &&
                   static_cast<int32_t>(now_us - hid_open_ctrl_done_us) >= 1200000) {
            hid_remote_initiated = false;
            open_hid_channels();
        }
    }

    if (pairing_window && static_cast<int32_t>(now_us - pairing_deadline_us) >= 0) {
        pairing_window = false;
        g_telemetry.bt_pairing_active = false;
        gap_inquiry_stop();
        inquiry_active = false;
        gap_set_bondable_mode(0);
        console_printf("[BT] Pairing scan timed out\n");
    }

    if (sdp_auto_pending && hid_intr_ready && static_cast<int32_t>(now_us - sdp_auto_at_us) >= 0) {
        sdp_auto_pending = false;
        bt_start_sdp_discovery();
    }

    if (!hid_intr_ready) return;

    // Spaced Feature prefetch (0x20, 0x09, 0x22, 0x05)
    if (prefetch_idx < prefetch_count && static_cast<int32_t>(now_us - prefetch_next_us) >= 0) {
        uint8_t req[2] = {0x43, prefetch_ids[prefetch_idx++]};
        enqueue_ctrl(req, 2);
        prefetch_next_us = now_us + 45000u;
    }

    // Pull next 64B DSP waveform block whenever ready (works even before rx0x31 unlocks)
    int8_t block[64];
    HapticMode wave_proto = g_haptic_mode;
    if (dsp_take_haptic_block(block, wave_proto)) {
        if (wave_proto == HapticMode::Mode32SAxense ||
            wave_proto == HapticMode::Mode36Haptics ||
            wave_proto == HapticMode::Mode39Probe ||
            wave_proto == HapticMode::Mode31Hybrid32) {
            bt_submit_haptic_waveform(block, wave_proto);
        }
    }

    // Re-sample clock after synchronous L2CAP callbacks inside bt_submit_haptic_waveform to prevent uint32 underflow
    now_us = time_us_32();

    // Bounded initial 0x31 SetHapticsMode unlock retries (every 1s, max 4 times) when idle
    if (!rx_0x31_seen && unlock_boot_retries_left > 0 && !dsp_is_haptics_active() &&
        static_cast<int32_t>(now_us - last_state_send_us) >= 1000000) {
        unlock_boot_retries_left--;
        state_send_pending = true;
        request_intr_tx();
    }

    // Reset Session state clears these too — make sure reset_session_state keeps deferred/impulse clean
    if (impulse_test_active && static_cast<int32_t>(now_us - impulse_test_until_us) >= 0) {
        impulse_test_active = false;
        bt_request_state_update();
    }

    // Flush any rate-limited USB 0x02 / Xbox rumble update once cooldown has passed
    uint32_t min_gap_us = (g_usb_mode != UsbMode::DualSense) ? 8000u : 20000u;
    if (deferred_state_flush && static_cast<int32_t>(now_us - last_state_send_us) >= static_cast<int32_t>(min_gap_us)) {
        deferred_state_flush = false;
        state_send_pending = true;
        request_intr_tx();
    }

    // Keep manual 0x31 classic rumble / impulse test alive and update WhenPulled dynamic triggers smoothly
    if (static_cast<int32_t>(now_us - last_dynamic_check_us) >= 25000) {
        last_dynamic_check_us = now_us;
        uint8_t imp_l = 0, imp_r = 0;
        compute_dynamic_impulse_pct(imp_l, imp_r);
        int dl = static_cast<int>(imp_l) - static_cast<int>(last_imp_sent_l);
        int dr = static_cast<int>(imp_r) - static_cast<int>(last_imp_sent_r);
        bool imp_edge = (imp_l == 0) != (last_imp_sent_l == 0) || (imp_r == 0) != (last_imp_sent_r == 0);
        bool imp_changed = imp_edge || (dl < -7 || dl > 7) || (dr < -7 || dr > 7);

        if (dsp_has_active_test_rumble() || impulse_test_active || imp_changed) {
            state_send_pending = true;
            request_intr_tx();
        }
    }

    // Periodic 0x31 update at ~100 Hz while rumble active (or hybrid trigger updates)
    uint32_t interval_us = (g_haptic_mode == HapticMode::Mode31Synth) ? 10000u : 50000u;
    if (static_cast<int32_t>(now_us - last_state_send_us) >= static_cast<int32_t>(interval_us)) {
        if (g_haptic_mode == HapticMode::Mode31Synth || g_haptic_mode == HapticMode::Mode31Hybrid32) {
            uint8_t sr = 0, sl = 0;
            dsp_get_synth_motors(sr, sl);
            bool active_synth = (sr | sl) != 0;
            bool active_game = (g_telemetry.last_game_right | g_telemetry.last_game_left) != 0;
            if (active_synth || active_game || state_send_pending ||
                static_cast<uint32_t>(now_us - last_state_send_us) >= 100000u) {
                state_send_pending = true;
                request_intr_tx();
            }
        }
    }

    // Structured 30 Hz telemetry stream for SensaDS5Control desktop app
    if (tele_enabled && static_cast<int32_t>(now_us - last_tele_us) >= 33333) {
        last_tele_us = now_us;
        auto rd_i16 = [](uint8_t const *p) -> int {
            return static_cast<int>(static_cast<int16_t>(p[0] | (p[1] << 8)));
        };
        uint8_t const *b = last_bt_body;
        uint8_t t0dn = ((b[32] & 0x80u) == 0 && rx_0x31_seen) ? 1 : 0;
        uint16_t t0x = static_cast<uint16_t>(b[33] | ((b[34] & 0x0Fu) << 8));
        uint16_t t0y = static_cast<uint16_t>((b[34] >> 4) | (b[35] << 4));
        uint8_t t1dn = ((b[36] & 0x80u) == 0 && rx_0x31_seen) ? 1 : 0;
        uint16_t t1x = static_cast<uint16_t>(b[37] | ((b[38] & 0x0Fu) << 8));
        uint16_t t1y = static_cast<uint16_t>((b[38] >> 4) | (b[39] << 4));
        uint8_t bat = std::min<uint8_t>(b[52] & 0x0Fu, 10) * 10;
        uint8_t chg = (b[52] >> 4) & 0x0Fu;
        console_printf("TELE,%u,%u,%u,%u,%u,%u,%u,%u,%u,%u,%u,%d,%d,%d,%d,%d,%d,%u,%u,%u,%u,%u,%u,%u,%u,%u,%u,%u,%lu,%u,%u,%u,%u,%u,%u\n",
                       rx_0x31_seen ? 1u : 0u,
                       b[0], b[1], b[2], b[3], b[4], b[5],
                       b[7], b[8], b[9], b[10],
                       rd_i16(b + 15), rd_i16(b + 17), rd_i16(b + 19),
                       rd_i16(b + 21), rd_i16(b + 23), rd_i16(b + 25),
                       t0dn, t0x, t0y, t1dn, t1x, t1y,
                       bat, chg,
                       g_telemetry.peak_haptic_l_pct, g_telemetry.peak_haptic_r_pct,
                       static_cast<unsigned>(g_haptic_mode),
                       static_cast<unsigned long>(g_telemetry.bt_0x36_tx_count),
                       g_dsp_config.haptic_gain_pct,
                       mic_led_override_mode,
                       static_cast<unsigned>(g_usb_mode),
                       static_cast<unsigned>(g_dsp_config.impulse_mode),
                       g_dsp_config.impulse_strength_pct,
                       g_telemetry.bt_interrupt_ready ? 1u : 0u);
    }
}

} // namespace sensa
