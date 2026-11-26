#include "sensa_dsp.h"
#include "sensa_bt.h"

#include <algorithm>
#include <cmath>
#include <cstring>

#include "pico/time.h"
#include "tusb.h"

namespace sensa {
namespace {

constexpr uint32_t kUsbReadChunkFrames = 48; // 1 ms @ 48 kHz
int16_t usb_pcm_rx[kUsbReadChunkFrames * 4];
int16_t usb_mic_zeros[48]{};

// 2-stage 1000 Hz IIR low-pass (matching ds5-untethered HapticsConverter.cs) + 3 kHz downsampling
constexpr float kLpfAlpha = 0.1227f; // 1 - exp(-2*pi*1000/48000)
float lpf1_l = 0.0f, lpf2_l = 0.0f;
float lpf1_r = 0.0f, lpf2_r = 0.0f;
float decimate_sum_l = 0.0f;
float decimate_sum_r = 0.0f;
uint8_t building_block[64]{};
uint8_t building_pair_idx = 0;
uint8_t decimate_accum_count = 0;

int8_t ready_block[64]{};
bool ready_block_valid = false;
uint32_t last_wave_block_us = 0;
uint32_t last_haptic_sound_us = 0;

// Peak telemetry (1000 = 100.0%, i.e., tenths of percent)
uint16_t cur_peak_audio_l = 0;
uint16_t cur_peak_audio_r = 0;
uint16_t cur_peak_haptic_l = 0;
uint16_t cur_peak_haptic_r = 0;
uint32_t last_peak_decay_us = 0;

// Dual-envelope SimFx transient + sustain detector
float env_fast_l = 0.0f;
float env_fast_r = 0.0f;
float env_slow_l = 0.0f;
float env_slow_r = 0.0f;
float synth_level_l = 0.0f;
float synth_level_r = 0.0f;
uint32_t hold_until_l_us = 0;
uint32_t hold_until_r_us = 0;
uint32_t last_audio_rx_us = 0;
uint32_t last_simfx_step_us = 0;

// Manual test pulse / sine wave state
uint8_t test_pulse_r = 0;
uint8_t test_pulse_l = 0;
uint32_t test_pulse_until_us = 0;

bool test_wave_active = false;
HapticMode test_wave_proto = HapticMode::Mode32SAxense;
uint8_t test_wave_amp = 100;
uint16_t test_wave_freq_hz = 90;
uint32_t test_wave_until_us = 0;
float test_wave_phase = 0.0f;
HapticMode ready_override_proto = HapticMode::Mode31Synth;
bool ready_has_override = false;

inline int8_t clamp_i8(int32_t v) {
    if (v > 127) return 127;
    if (v < -128) return -128;
    return static_cast<int8_t>(v);
}

inline uint8_t clamp_u8(float v) {
    if (v <= 0.0f) return 0;
    if (v >= 255.0f) return 255;
    return static_cast<uint8_t>(v + 0.5f);
}

void push_haptic_pair(int8_t l, int8_t r) {
    building_block[building_pair_idx * 2] = l;
    building_block[building_pair_idx * 2 + 1] = r;
    building_pair_idx++;
    if (building_pair_idx >= 32) {
        std::memcpy(ready_block, building_block, 64);
        ready_block_valid = true;
        ready_has_override = false;
        building_pair_idx = 0;
    }
}

void process_frames(int16_t const *frames, uint32_t count, uint32_t now_us) {
    int32_t block_max_al = 0, block_max_ar = 0, block_max_hl = 0, block_max_hr = 0;

    for (uint32_t i = 0; i < count; ++i) {
        int32_t al = frames[i * 4 + 0];
        int32_t ar = frames[i * 4 + 1];
        int32_t hl = frames[i * 4 + 2];
        int32_t hr = frames[i * 4 + 3];

        int32_t abs_al = al < 0 ? -al : al;
        int32_t abs_ar = ar < 0 ? -ar : ar;
        int32_t abs_hl = hl < 0 ? -hl : hl;
        int32_t abs_hr = hr < 0 ? -hr : hr;
        if (abs_al > block_max_al) block_max_al = abs_al;
        if (abs_ar > block_max_ar) block_max_ar = abs_ar;
        if (abs_hl > block_max_hl) block_max_hl = abs_hl;
        if (abs_hr > block_max_hr) block_max_hr = abs_hr;

        // 2-stage 1000 Hz IIR low-pass + 16x decimation -> 3 kHz (ds5-untethered HapticsConverter)
        if ((hl | hr) != 0 || lpf1_l != 0.0f || lpf2_l != 0.0f || lpf1_r != 0.0f || lpf2_r != 0.0f) {
            float fl = static_cast<float>(hl) / 32768.0f;
            float fr = static_cast<float>(hr) / 32768.0f;
            lpf1_l += kLpfAlpha * (fl - lpf1_l);
            lpf2_l += kLpfAlpha * (lpf1_l - lpf2_l);
            lpf1_r += kLpfAlpha * (fr - lpf1_r);
            lpf2_r += kLpfAlpha * (lpf1_r - lpf2_r);
            decimate_sum_l += lpf2_l;
            decimate_sum_r += lpf2_r;
            if ((hl | hr) == 0) {
                if (std::fabs(lpf1_l) < 1e-6f) lpf1_l = 0.0f;
                if (std::fabs(lpf2_l) < 1e-6f) lpf2_l = 0.0f;
                if (std::fabs(lpf1_r) < 1e-6f) lpf1_r = 0.0f;
                if (std::fabs(lpf2_r) < 1e-6f) lpf2_r = 0.0f;
            }
        }
        decimate_accum_count++;

        if (decimate_accum_count >= 16) {
            int8_t out_l = 0, out_r = 0;
            if (decimate_sum_l != 0.0f || decimate_sum_r != 0.0f) {
                float level = static_cast<float>(g_dsp_config.haptic_gain_pct) * 0.01f;
                float vl = std::tanh((decimate_sum_l / 16.0f) * level) * 127.0f;
                float vr = std::tanh((decimate_sum_r / 16.0f) * level) * 127.0f;
                out_l = clamp_i8(static_cast<int32_t>(vl));
                out_r = clamp_i8(static_cast<int32_t>(vr));
            }
            push_haptic_pair(out_l, out_r);
            decimate_sum_l = 0.0f;
            decimate_sum_r = 0.0f;
            decimate_accum_count = 0;
        }
    }

    if (block_max_hl > 16 || block_max_hr > 16) {
        last_haptic_sound_us = now_us;
    }

    auto to_tenths = [](int32_t peak) -> uint16_t {
        uint32_t p = (static_cast<uint32_t>(peak) * 1000u) / 32767u;
        return static_cast<uint16_t>(p > 1000u ? 1000u : p);
    };
    uint16_t al_pct = to_tenths(block_max_al);
    uint16_t ar_pct = to_tenths(block_max_ar);
    uint16_t hl_pct = to_tenths(block_max_hl);
    uint16_t hr_pct = to_tenths(block_max_hr);
    if (al_pct > cur_peak_audio_l) cur_peak_audio_l = al_pct;
    if (ar_pct > cur_peak_audio_r) cur_peak_audio_r = ar_pct;
    if (hl_pct > cur_peak_haptic_l) cur_peak_haptic_l = hl_pct;
    if (hr_pct > cur_peak_haptic_r) cur_peak_haptic_r = hr_pct;

    // Normalize 0..1
    float norm_hl = static_cast<float>(block_max_hl) / 32768.0f;
    float norm_hr = static_cast<float>(block_max_hr) / 32768.0f;
    if (g_dsp_config.include_speaker_bass && g_dsp_config.speaker_bass_pct > 0) {
        float norm_al = static_cast<float>(block_max_al) / 32768.0f;
        float norm_ar = static_cast<float>(block_max_ar) / 32768.0f;
        if (norm_al > 0.25f) norm_hl = std::max(norm_hl, (norm_al - 0.25f) * (g_dsp_config.speaker_bass_pct / 100.0f));
        if (norm_ar > 0.25f) norm_hr = std::max(norm_hr, (norm_ar - 0.25f) * (g_dsp_config.speaker_bass_pct / 100.0f));
    }

    // Dual-envelope adaptive SimFx detector
    // L/R asymmetry: right motor = fast transient attack (crisp hits),
    //                left motor  = slow heavy impacts only (deep rumble).
    // This creates a felt difference between sharp and heavy events.
    float floor_thr = static_cast<float>(g_dsp_config.noise_floor_pct) * 0.01f;
    float gain = static_cast<float>(g_dsp_config.gain_x10) * 0.1f;

    // Right motor: fast-attack, responds to transient delta (sharp cues)
    auto update_right = [&](float mag, float &fast_e, float &slow_e, float &out_lvl, uint32_t &hold_until) {
        fast_e = fast_e * 0.65f + mag * 0.35f;   // faster attack
        slow_e = slow_e * 0.96f + mag * 0.04f;
        float delta = fast_e - slow_e * 0.80f;   // more sensitive to transients
        float eff = std::max(mag * 0.80f, delta * 1.60f);
        if (eff >= floor_thr && eff > 0.002f) {
            float drive = (eff - floor_thr * 0.5f) * gain * 255.0f;
            float target = std::max(drive, static_cast<float>(g_dsp_config.min_rumble));
            if (target > 255.0f) target = 255.0f;
            if (target >= out_lvl) {
                out_lvl = target;
                // Right motor hold is shorter (crisp feel)
                hold_until = now_us + static_cast<uint32_t>(g_dsp_config.hold_ms) * 1000u;
            }
        }
    };

    // Left motor: slow-attack, responds only to sustained heavy signal (heavy hits / explosions)
    auto update_left = [&](float mag, float &fast_e, float &slow_e, float &out_lvl, uint32_t &hold_until) {
        fast_e = fast_e * 0.80f + mag * 0.20f;   // slower attack = less jitter
        slow_e = slow_e * 0.97f + mag * 0.03f;
        float delta = fast_e - slow_e * 0.90f;
        float eff = std::max(mag * 0.90f, delta * 1.20f);
        // Higher floor for left motor — only heavy impacts get through
        float left_floor = floor_thr * 1.6f;
        if (eff >= left_floor && eff > 0.004f) {
            float drive = (eff - left_floor * 0.5f) * gain * 200.0f;  // softer cap
            float target = std::max(drive, static_cast<float>(g_dsp_config.min_rumble));
            if (target > 200.0f) target = 200.0f;
            if (target >= out_lvl) {
                out_lvl = target;
                // Left motor hold is longer (sustained heavy rumble)
                hold_until = now_us + static_cast<uint32_t>(g_dsp_config.hold_ms) * 2000u;
            }
        }
    };

    // Use average of L+R haptic channels for each motor:
    // right motor = average of both (all hits), left = heavier hits only
    float norm_avg = (norm_hl + norm_hr) * 0.5f;
    update_right(norm_avg, env_fast_r, env_slow_r, synth_level_r, hold_until_r_us);
    update_left(norm_avg, env_fast_l, env_slow_l, synth_level_l, hold_until_l_us);
    last_audio_rx_us = now_us;
}

} // namespace

void dsp_init() {
    std::memset(building_block, 0, sizeof(building_block));
    std::memset(ready_block, 0, sizeof(ready_block));
    std::memset(usb_mic_zeros, 0, sizeof(usb_mic_zeros));
}

void dsp_trigger_test_rumble(uint8_t right, uint8_t left, uint32_t duration_ms) {
    test_pulse_r = right;
    test_pulse_l = left;
    test_pulse_until_us = time_us_32() + duration_ms * 1000u;
}

void dsp_trigger_test_wave(HapticMode proto, uint8_t amp, uint16_t freq_hz, uint32_t duration_ms) {
    test_wave_active = true;
    test_wave_proto = proto;
    test_wave_amp = amp;
    test_wave_freq_hz = freq_hz;
    test_wave_until_us = time_us_32() + duration_ms * 1000u;
    test_wave_phase = 0.0f;
    last_wave_block_us = 0;
}

void dsp_get_synth_motors(uint8_t &right_out, uint8_t &left_out) {
    uint32_t now_us = time_us_32();
    if (static_cast<int32_t>(now_us - test_pulse_until_us) < 0) {
        right_out = test_pulse_r;
        left_out = test_pulse_l;
        return;
    }
    if (!g_telemetry.usb_speaker_streaming ||
        (last_audio_rx_us != 0 && static_cast<uint32_t>(now_us - last_audio_rx_us) > 250000u)) {
        right_out = 0;
        left_out = 0;
        return;
    }
    right_out = clamp_u8(synth_level_r);
    left_out = clamp_u8(synth_level_l);
}

bool dsp_is_haptics_active() {
    if (test_wave_active) return true;
    if (!g_telemetry.usb_speaker_streaming || last_haptic_sound_us == 0) return false;
    return static_cast<uint32_t>(time_us_32() - last_haptic_sound_us) <= 300000u;
}

bool dsp_take_haptic_block(int8_t out_block[64], HapticMode &override_proto) {
    if (!ready_block_valid) return false;
    ready_block_valid = false;
    bool has_override = ready_has_override;
    override_proto = has_override ? ready_override_proto : g_haptic_mode;
    ready_has_override = false;
    if (!has_override && !dsp_is_haptics_active()) {
        return false;
    }
    std::memcpy(out_block, ready_block, 64);
    return true;
}

void dsp_poll() {
    uint32_t now_us = time_us_32();

    // Feed USB Mic TX endpoint when host is reading mic
    if (g_telemetry.usb_mic_streaming && tud_inited()) {
        tu_fifo_t *in_ff = tud_audio_n_get_ep_in_ff(0);
        if (in_ff != nullptr && tu_fifo_remaining(in_ff) >= sizeof(usb_mic_zeros)) {
            tud_audio_n_write(0, usb_mic_zeros, sizeof(usb_mic_zeros));
        }
    }

    // Drain incoming 4ch 48kHz PCM from USB Audio OUT
    while (tud_inited() && tud_audio_available() > 0) {
        uint32_t bytes = tud_audio_read(usb_pcm_rx, sizeof(usb_pcm_rx));
        uint32_t frames = bytes / (4u * sizeof(int16_t));
        if (frames == 0) break;
        g_telemetry.usb_audio_frames += frames;
        process_frames(usb_pcm_rx, frames, now_us);
    }

    // Step SimFx envelope decay every ~2ms
    if (last_simfx_step_us == 0 || static_cast<uint32_t>(now_us - last_simfx_step_us) >= 2000u) {
        uint32_t dt_us = last_simfx_step_us == 0 ? 2000u : static_cast<uint32_t>(now_us - last_simfx_step_us);
        last_simfx_step_us = now_us;
        float dt_ms = static_cast<float>(dt_us) * 0.001f;
        float decay_step = (255.0f / static_cast<float>(g_dsp_config.decay_ms > 5 ? g_dsp_config.decay_ms : 5)) * dt_ms;

        if (static_cast<int32_t>(now_us - hold_until_l_us) >= 0) {
            synth_level_l = std::max(0.0f, synth_level_l - decay_step);
        }
        if (static_cast<int32_t>(now_us - hold_until_r_us) >= 0) {
            synth_level_r = std::max(0.0f, synth_level_r - decay_step);
        }
        if (!g_telemetry.usb_speaker_streaming) {
            synth_level_l = 0.0f;
            synth_level_r = 0.0f;
        }
    }

    // Decay telemetry peaks every 100ms
    if (static_cast<uint32_t>(now_us - last_peak_decay_us) >= 100000u) {
        last_peak_decay_us = now_us;
        g_telemetry.peak_audio_l_pct = cur_peak_audio_l;
        g_telemetry.peak_audio_r_pct = cur_peak_audio_r;
        g_telemetry.peak_haptic_l_pct = cur_peak_haptic_l;
        g_telemetry.peak_haptic_r_pct = cur_peak_haptic_r;
        cur_peak_audio_l = static_cast<uint16_t>((cur_peak_audio_l * 7u) / 10u);
        cur_peak_audio_r = static_cast<uint16_t>((cur_peak_audio_r * 7u) / 10u);
        cur_peak_haptic_l = static_cast<uint16_t>((cur_peak_haptic_l * 7u) / 10u);
        cur_peak_haptic_r = static_cast<uint16_t>((cur_peak_haptic_r * 7u) / 10u);
    }

    // Generate synthetic pure 0x32 / 0x36 / 0x39 sine block every 10.67 ms when requested
    if (test_wave_active) {
        if (static_cast<int32_t>(now_us - test_wave_until_us) >= 0) {
            test_wave_active = false;
        } else if (last_wave_block_us == 0 || static_cast<uint32_t>(now_us - last_wave_block_us) >= 10667u) {
            last_wave_block_us = now_us;
            constexpr float kTwoPi = 6.2831853f;
            float phase_inc = kTwoPi * static_cast<float>(test_wave_freq_hz) / 3000.0f;
            for (int i = 0; i < 32; ++i) {
                int8_t v = clamp_i8(static_cast<int32_t>(std::sin(test_wave_phase) * static_cast<float>(test_wave_amp)));
                ready_block[i * 2] = v;
                ready_block[i * 2 + 1] = v;
                test_wave_phase += phase_inc;
                if (test_wave_phase >= kTwoPi) test_wave_phase -= kTwoPi;
            }
            ready_block_valid = true;
            ready_override_proto = test_wave_proto;
            ready_has_override = true;
        }
    }
}

} // namespace sensa
