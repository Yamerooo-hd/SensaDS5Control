#ifndef SENSA_FLASH_H
#define SENSA_FLASH_H

#include <cstdint>

namespace sensa {

// Loads standalone settings from internal RP2350 flash (returns true if valid config existed).
// Sets watchdog_loaded_usb_mode = true if a soft-reboot scratch override took precedence.
bool config_load_from_flash(bool watchdog_usb_override);

// True when flash had no saved SensaBridge config yet (first boot after fresh .uf2 flash).
bool config_is_flash_fresh();

// Marks settings modified so config_poll_save() writes once after a 1.2s quiet debounce.
void config_mark_dirty();

// Flushes debounced config changes safely to flash during main loop idle time.
void config_poll_save(uint32_t now_us);

// Immediately persists current settings to flash if dirty or differing from stored CRC.
bool config_save_now();

} // namespace sensa

#endif
