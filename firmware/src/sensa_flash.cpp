#include "sensa_flash.h"
#include "sensa_bt.h"
#include "sensa_console.h"
#include "sensa_state.h"

#include <cstddef>
#include <cstring>

#include "hardware/flash.h"
#include "hardware/regs/addressmap.h"
#include "hardware/watchdog.h"
#include "pico/flash.h"
#include "pico/time.h"

namespace sensa {
namespace {

constexpr uint32_t kFlashMagic = 0x53454E32u; // "SEN2"
constexpr uint16_t kFlashVersion = 1;
constexpr uint32_t kSaveDebounceUs = 1200000u;

// Reserve 6 sectors from end of flash:
// - sector -1: RP2350-E10 errata reservation
// - sectors -3..-2: BTstack TLV flash bank (8 KB)
// - sector -6: SensaBridge standalone config (4 KB)
constexpr uint32_t kConfigFlashOffset = PICO_FLASH_SIZE_BYTES - (6u * FLASH_SECTOR_SIZE);
static_assert((kConfigFlashOffset % FLASH_SECTOR_SIZE) == 0, "Config offset must align to flash sector");

struct __attribute__((packed)) PersistedConfig {
    uint32_t magic;
    uint16_t version;
    uint16_t payload_size;
    uint32_t seq;

    uint8_t usb_mode;
    uint8_t haptic_mode;
    uint8_t stick_deadzone_pct;
    uint8_t mic_led_mode;

    uint8_t led_override;
    uint8_t led_r;
    uint8_t led_g;
    uint8_t led_b;

    uint8_t noise_floor_pct;
    uint16_t gain_x10;
    uint8_t min_rumble;
    uint16_t hold_ms;
    uint16_t decay_ms;
    uint8_t include_speaker_bass;
    uint16_t speaker_bass_pct;
    uint8_t haptic_gain_pct;
    uint8_t impulse_mode;
    uint8_t impulse_strength_pct;

    uint8_t trig_r_override;
    uint8_t trig_r[11];
    uint8_t trig_l_override;
    uint8_t trig_l[11];

    uint32_t crc32;
};

static_assert(sizeof(PersistedConfig) <= FLASH_PAGE_SIZE, "PersistedConfig must fit in a single 256B flash page");

bool flash_fresh = true;
bool save_dirty = false;
uint32_t dirty_since_us = 0;
uint32_t current_seq = 0;
uint32_t last_saved_crc = 0;

uint32_t compute_cfg_crc(PersistedConfig const &cfg) {
    return crc32_seeded(reinterpret_cast<uint8_t const *>(&cfg), offsetof(PersistedConfig, crc32), 0x12345678u);
}

void populate_cfg_from_runtime(PersistedConfig &cfg, uint32_t seq) {
    std::memset(&cfg, 0, sizeof(cfg));
    cfg.magic = kFlashMagic;
    cfg.version = kFlashVersion;
    cfg.payload_size = static_cast<uint16_t>(sizeof(PersistedConfig));
    cfg.seq = seq;

    cfg.usb_mode = static_cast<uint8_t>(g_usb_mode);
    cfg.haptic_mode = static_cast<uint8_t>(g_haptic_mode);
    cfg.stick_deadzone_pct = g_stick_deadzone_pct;

    cfg.noise_floor_pct = g_dsp_config.noise_floor_pct;
    cfg.gain_x10 = g_dsp_config.gain_x10;
    cfg.min_rumble = g_dsp_config.min_rumble;
    cfg.hold_ms = g_dsp_config.hold_ms;
    cfg.decay_ms = g_dsp_config.decay_ms;
    cfg.include_speaker_bass = g_dsp_config.include_speaker_bass ? 1u : 0u;
    cfg.speaker_bass_pct = g_dsp_config.speaker_bass_pct;
    cfg.haptic_gain_pct = g_dsp_config.haptic_gain_pct;
    cfg.impulse_mode = static_cast<uint8_t>(g_dsp_config.impulse_mode);
    cfg.impulse_strength_pct = g_dsp_config.impulse_strength_pct;

    bt_export_persisted_state(
        cfg.led_override, cfg.led_r, cfg.led_g, cfg.led_b, cfg.mic_led_mode,
        cfg.trig_r_override, cfg.trig_r, cfg.trig_l_override, cfg.trig_l);

    cfg.crc32 = compute_cfg_crc(cfg);
}

struct FlashWriteCtx {
    uint32_t offset;
    uint8_t page[FLASH_PAGE_SIZE];
};

void __no_inline_not_in_flash_func(write_flash_cb)(void *param) {
    auto *ctx = static_cast<FlashWriteCtx *>(param);
    flash_range_erase(ctx->offset, FLASH_SECTOR_SIZE);
    flash_range_program(ctx->offset, ctx->page, FLASH_PAGE_SIZE);
}

uint8_t clamp_u8(uint8_t v, uint8_t lo, uint8_t hi) {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

uint16_t clamp_u16(uint16_t v, uint16_t lo, uint16_t hi) {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

} // namespace

bool config_load_from_flash(bool watchdog_usb_override) {
    auto const *stored = reinterpret_cast<PersistedConfig const *>(XIP_BASE + kConfigFlashOffset);
    PersistedConfig copy{};
    std::memcpy(&copy, stored, sizeof(copy));

    if (copy.magic != kFlashMagic || copy.version != kFlashVersion ||
        copy.payload_size != sizeof(PersistedConfig) || copy.crc32 != compute_cfg_crc(copy)) {
        flash_fresh = true;
        return false;
    }

    flash_fresh = false;
    current_seq = copy.seq;
    last_saved_crc = copy.crc32;

    if (!watchdog_usb_override && copy.usb_mode <= 1) {
        g_usb_mode = static_cast<UsbMode>(copy.usb_mode);
    }
    if (copy.haptic_mode <= static_cast<uint8_t>(HapticMode::ModeRawPass)) {
        g_haptic_mode = static_cast<HapticMode>(copy.haptic_mode);
    }
    g_stick_deadzone_pct = clamp_u8(copy.stick_deadzone_pct, 0, 25);

    g_dsp_config.noise_floor_pct = clamp_u8(copy.noise_floor_pct, 0, 50);
    g_dsp_config.gain_x10 = clamp_u16(copy.gain_x10, 1, 100);
    g_dsp_config.min_rumble = clamp_u8(copy.min_rumble, 0, 150);
    g_dsp_config.hold_ms = clamp_u16(copy.hold_ms, 10, 500);
    g_dsp_config.decay_ms = clamp_u16(copy.decay_ms, 10, 500);
    g_dsp_config.include_speaker_bass = copy.include_speaker_bass != 0;
    g_dsp_config.speaker_bass_pct = clamp_u16(copy.speaker_bass_pct, 0, 100);
    g_dsp_config.haptic_gain_pct = clamp_u8(copy.haptic_gain_pct, 25, 255);
    if (copy.impulse_mode <= 2) {
        g_dsp_config.impulse_mode = static_cast<ImpulseTriggerMode>(copy.impulse_mode);
    }
    g_dsp_config.impulse_strength_pct = clamp_u8(copy.impulse_strength_pct, 0, 100);

    bt_import_persisted_state(
        copy.led_override != 0, copy.led_r, copy.led_g, copy.led_b, copy.mic_led_mode,
        copy.trig_r_override != 0, copy.trig_r, copy.trig_l_override != 0, copy.trig_l);
    return true;
}

bool config_is_flash_fresh() {
    return flash_fresh;
}

void config_mark_dirty() {
    save_dirty = true;
    dirty_since_us = time_us_32();
}

void config_poll_save(uint32_t now_us) {
    if (!save_dirty) return;
    if (static_cast<int32_t>(now_us - dirty_since_us) < static_cast<int32_t>(kSaveDebounceUs)) return;
    config_save_now();
}

bool config_save_now() {
    save_dirty = false;

    // Avoid redundant sector erase/program if runtime state already matches stored flash (excluding seq)
    PersistedConfig probe{};
    populate_cfg_from_runtime(probe, current_seq);
    if (!flash_fresh && probe.crc32 == last_saved_crc) {
        return true;
    }

    PersistedConfig to_write{};
    populate_cfg_from_runtime(to_write, current_seq + 1);

    FlashWriteCtx ctx{};
    ctx.offset = kConfigFlashOffset;
    std::memset(ctx.page, 0xFF, sizeof(ctx.page));
    std::memcpy(ctx.page, &to_write, sizeof(to_write));

    watchdog_update();
    int rc = flash_safe_execute(write_flash_cb, &ctx, 500);
    watchdog_update();

    if (rc == PICO_OK) {
        current_seq++;
        last_saved_crc = to_write.crc32;
        flash_fresh = false;
        console_printf("[FLASH] Saved standalone config (seq=%lu usb=%s dz=%u%%)\n",
                       static_cast<unsigned long>(current_seq), usb_mode_name(g_usb_mode), g_stick_deadzone_pct);
        return true;
    }
    console_printf("[FLASH] Save failed rc=%d\n", rc);
    return false;
}

} // namespace sensa
