#ifndef SENSA_BT_H
#define SENSA_BT_H

#include <cstdint>
#include "sensa_state.h"

namespace sensa {

bool bt_init();
void bt_poll();

void bt_start_pairing();
void bt_forget_all();
void bt_disconnect_now();
void bt_reconnect_bonded();
bool bt_start_sdp_discovery();

void bt_notify_host_output();
void bt_request_state_update();
void bt_trigger_test_impulse(uint8_t pct, uint32_t duration_ms);
void bt_submit_haptic_waveform(int8_t const block[64], HapticMode proto);
void bt_submit_audio_block(int8_t const haptics[64], uint8_t const *opus_200, bool has_opus, HapticMode proto);
void bt_set_override_led(uint8_t r, uint8_t g, uint8_t b);
void bt_clear_override_led();
void bt_set_mic_led(uint8_t mode);
void bt_set_trigger_override(uint8_t side, bool enable, uint8_t const data11[11]);
void bt_set_btmon(bool enable);
void bt_set_telemetry_stream(bool enable);

bool bt_copy_cached_feature(uint8_t report_id, uint8_t *buf, uint16_t cap, uint16_t &copied);
void bt_send_set_feature(uint8_t report_id, uint8_t const *buf, uint16_t len);
void bt_debug_dump();
void bt_trigger_unlock_sequence(uint8_t mode);

void bt_export_persisted_state(
    uint8_t &led_override, uint8_t &led_r, uint8_t &led_g, uint8_t &led_b, uint8_t &mic_led_mode,
    uint8_t &trig_r_en, uint8_t trig_r[11], uint8_t &trig_l_en, uint8_t trig_l[11]);
void bt_import_persisted_state(
    bool led_override, uint8_t led_r, uint8_t led_g, uint8_t led_b, uint8_t mic_led_mode,
    bool trig_r_en, uint8_t const trig_r[11], bool trig_l_en, uint8_t const trig_l[11]);

} // namespace sensa

#endif
