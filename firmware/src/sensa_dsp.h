#ifndef SENSA_DSP_H
#define SENSA_DSP_H

#include <cstdint>
#include "sensa_state.h"

namespace sensa {

void dsp_init();
void dsp_poll();

void dsp_trigger_test_rumble(uint8_t right, uint8_t left, uint32_t duration_ms);
void dsp_trigger_test_wave(HapticMode proto, uint8_t amp, uint16_t freq_hz, uint32_t duration_ms);
void dsp_trigger_test_speaker(uint32_t duration_ms);
void dsp_push_bt_mic_opus(uint8_t const *opus_bytes, uint16_t len);

// Called by sensa_bt when fetching current haptic synth & audio/speaker waveform blocks
void dsp_get_synth_motors(uint8_t &right_out, uint8_t &left_out);
bool dsp_has_active_test_rumble();
bool dsp_take_haptic_block(int8_t out_block[64], HapticMode &override_proto);
bool dsp_take_audio_block(int8_t out_haptics[64], uint8_t out_opus[200], bool &has_opus, HapticMode &override_proto);
bool dsp_is_haptics_active();
bool dsp_is_speaker_active();
bool dsp_is_speaker_test_active();

} // namespace sensa

#endif
