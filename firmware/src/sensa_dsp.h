#ifndef SENSA_DSP_H
#define SENSA_DSP_H

#include <cstdint>
#include "sensa_state.h"

namespace sensa {

void dsp_init();
void dsp_poll();

void dsp_trigger_test_rumble(uint8_t right, uint8_t left, uint32_t duration_ms);
void dsp_trigger_test_wave(HapticMode proto, uint8_t amp, uint16_t freq_hz, uint32_t duration_ms);

// Called by sensa_bt when fetching current haptic synth & audio waveform blocks
void dsp_get_synth_motors(uint8_t &right_out, uint8_t &left_out);
bool dsp_take_haptic_block(int8_t out_block[64], HapticMode &override_proto);
bool dsp_is_haptics_active();

} // namespace sensa

#endif
