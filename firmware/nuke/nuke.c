#include "hardware/flash.h"
#include "pico/bootrom.h"
#include "pico/stdlib.h"

int main(void) {
    // Erase the entire QSPI flash chip (code, filesystems, BTstack TLV, and settings)
    // and reboot cleanly straight back into RP2350 USB BOOTSEL mode.
    flash_range_erase(0, PICO_FLASH_SIZE_BYTES);
    reset_usb_boot(0, 0);
    return 0;
}
