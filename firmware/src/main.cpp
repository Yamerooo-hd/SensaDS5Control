#include "sensa_bt.h"
#include "sensa_console.h"
#include "sensa_dsp.h"
#include "sensa_state.h"
#include "sensa_usb.h"

#include "bsp/board_api.h"
#include "hardware/gpio.h"
#include "hardware/structs/ioqspi.h"
#include "hardware/structs/sio.h"
#include "hardware/watchdog.h"
#include "pico/bootrom.h"
#include "pico/cyw43_arch.h"
#include "pico/flash.h"
#include "pico/stdlib.h"

namespace sensa {

HapticMode g_haptic_mode = HapticMode::Mode36Haptics;
DspConfig g_dsp_config{};
Telemetry g_telemetry{};
HostOutputState g_host_output{};

namespace {

void __no_inline_not_in_flash_func(read_bootsel_cb)(void *param) {
    bool *pressed = static_cast<bool *>(param);
    constexpr uint kCsPin = 1;
    hw_write_masked(&ioqspi_hw->io[kCsPin].ctrl,
                    GPIO_OVERRIDE_LOW << IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_LSB,
                    IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_BITS);
    for (int i = 0; i < 1000; ++i) __asm volatile("nop");
    *pressed = !(sio_hw->gpio_hi_in & SIO_GPIO_HI_IN_QSPI_CSN_BITS);
    hw_write_masked(&ioqspi_hw->io[kCsPin].ctrl,
                    GPIO_OVERRIDE_NORMAL << IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_LSB,
                    IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_BITS);
}

bool read_bootsel_button() {
    bool pressed = false;
    flash_safe_execute(read_bootsel_cb, &pressed, 50);
    return pressed;
}

void poll_bootsel_and_led(uint32_t now_us) {
    static uint32_t last_btn_us = 0;
    static uint32_t down_since_us = 0;
    static bool was_down = false;
    static bool hold_fired = false;
    static uint8_t click_count = 0;
    static uint32_t last_click_us = 0;

    if (static_cast<uint32_t>(now_us - last_btn_us) >= 50000u) {
        last_btn_us = now_us;
        bool down = read_bootsel_button();
        if (down && !was_down) {
            down_since_us = now_us;
            hold_fired = false;
        } else if (down && was_down && !hold_fired &&
                   static_cast<uint32_t>(now_us - down_since_us) >= 1500000u) {
            hold_fired = true;
            click_count = 0;
            bt_forget_all();
        } else if (!down && was_down && !hold_fired) {
            if (static_cast<uint32_t>(now_us - down_since_us) < 500000u) {
                click_count++;
                last_click_us = now_us;
            }
        }
        was_down = down;
    }

    if (click_count > 0 && static_cast<uint32_t>(now_us - last_click_us) >= 450000u) {
        uint8_t n = click_count;
        click_count = 0;
        if (n == 1) {
            if (g_telemetry.bt_acl_connected) bt_disconnect_now();
            else bt_start_pairing();
        } else if (n == 2) {
            watchdog_reboot(0, 0, 0);
        } else if (n >= 3) {
            reset_usb_boot(0, 0);
        }
    }

    static uint32_t last_led_us = 0;
    static bool led_on = false;
    uint32_t period_us = g_telemetry.bt_interrupt_ready ? 0
                       : g_telemetry.bt_pairing_active ? 150000u
                                                       : 700000u;
    if (period_us == 0) {
        if (!led_on) {
            led_on = true;
            cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, true);
        }
    } else if (static_cast<uint32_t>(now_us - last_led_us) >= period_us) {
        last_led_us = now_us;
        led_on = !led_on;
        cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, led_on);
    }
}

} // namespace
} // namespace sensa

int main() {
    board_init();
    sensa::console_init();
    sensa::usb_init();
    board_init_after_tusb();
    sensa::dsp_init();

    if (!sensa::bt_init()) {
        return 1;
    }

    watchdog_enable(1500, true);

    while (true) {
        watchdog_update();
        sensa::bt_poll();
        sensa::usb_poll();
        sensa::dsp_poll();
        sensa::console_poll();
        sensa::poll_bootsel_and_led(time_us_32());
    }
}
