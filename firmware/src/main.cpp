#include "sensa_bt.h"
#include "sensa_console.h"
#include "sensa_dsp.h"
#include "sensa_flash.h"
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

UsbMode g_usb_mode = UsbMode::DualSense;
HapticMode g_haptic_mode = HapticMode::Mode36Haptics;
DspConfig g_dsp_config{};
Telemetry g_telemetry{};
HostOutputState g_host_output{};
uint8_t g_stick_deadzone_pct = 4;

namespace {

void __no_inline_not_in_flash_func(read_bootsel_cb)(void *param) {
    bool *pressed = static_cast<bool *>(param);
    constexpr uint kCsPin = 1;
    hw_write_masked(&ioqspi_hw->io[kCsPin].ctrl,
                    GPIO_OVERRIDE_LOW << IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_LSB,
                    IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_BITS);
    for (int i = 0; i < 200; ++i) __asm volatile("nop");
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

void trigger_usb_mode_switch_reboot() {
    UsbMode next = (g_usb_mode == UsbMode::DualSense) ? UsbMode::Xbox360 : UsbMode::DualSense;
    g_usb_mode = next;
    config_save_now();
    watchdog_hw->scratch[0] = kUsbModeScratchMagic;
    watchdog_hw->scratch[1] = static_cast<uint32_t>(next);
    console_printf("[BTN] 2x BOOTSEL -> Switching to %s & rebooting...\n", usb_mode_name(next));
    console_poll();
    sleep_ms(60);
    watchdog_reboot(0, 0, 0);
}

void poll_bootsel_and_led(uint32_t now_us) {
    constexpr uint32_t kIdleBlinkTimeoutUs = 12000000u; // Stop blinking after 12s while waiting
    static uint32_t last_btn_us = 0;
    static uint32_t down_since_us = 0;
    static bool was_down = false;
    static bool hold_fired = false;
    static uint8_t click_count = 0;
    static uint32_t last_click_us = 0;

    static uint32_t last_led_us = 0;
    static uint32_t idle_blink_until_us = 0;
    static bool led_on = false;
    static bool led_init = false;
    static bool was_ready = false;
    static bool was_connecting = false;

    if (static_cast<uint32_t>(now_us - last_btn_us) >= 75000u) {
        last_btn_us = now_us;
        bool down = read_bootsel_button();
        if (down && !was_down) {
            down_since_us = now_us;
            hold_fired = false;
        } else if (down && was_down && !hold_fired &&
                   static_cast<uint32_t>(now_us - down_since_us) >= 2000000u) {
            hold_fired = true;
            click_count = 0;
            console_printf("[BTN] Hold 2s -> Entering USB BOOTSEL firmware mode...\n");
            console_poll();
            for (int i = 0; i < 6; ++i) {
                cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, (i & 1) == 0);
                sleep_ms(45);
            }
            reset_usb_boot(0, 0);
        } else if (!down && was_down && !hold_fired) {
            if (static_cast<uint32_t>(now_us - down_since_us) < 600000u) {
                click_count++;
                last_click_us = now_us;
            }
        }
        was_down = down;
    }

    if (click_count > 0 && static_cast<uint32_t>(now_us - last_click_us) >= 500000u) {
        uint8_t n = click_count;
        click_count = 0;
        idle_blink_until_us = now_us + kIdleBlinkTimeoutUs;
        if (n == 1) {
            if (g_telemetry.bt_acl_connected) bt_disconnect_now();
            else if (g_telemetry.bt_has_bonded_key) bt_reconnect_bonded();
            else bt_start_pairing();
        } else if (n == 2) {
            trigger_usb_mode_switch_reboot();
        } else if (n == 3) {
            console_printf("[BTN] 3x BOOTSEL -> Pairing Scan (Share+PS on Sensa)\n");
            bt_start_pairing();
        } else if (n >= 4) {
            console_printf("[BTN] 4x BOOTSEL -> Forget bonded keys & Re-Pair\n");
            bt_forget_all();
        }
    }

    bool ready = g_telemetry.bt_interrupt_ready;
    bool pairing = g_telemetry.bt_pairing_active;
    bool connecting = g_telemetry.bt_connecting;

    if (!led_init || (was_ready && !ready) || (!was_connecting && connecting)) {
        led_init = true;
        idle_blink_until_us = now_us + kIdleBlinkTimeoutUs;
    }
    was_ready = ready;
    was_connecting = connecting;

    if (ready) {
        if (!led_on) {
            led_on = true;
            cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, true);
        }
    } else if (pairing || static_cast<int32_t>(idle_blink_until_us - now_us) > 0) {
        uint32_t period_us = pairing ? 150000u : 700000u;
        if (static_cast<uint32_t>(now_us - last_led_us) >= period_us) {
            last_led_us = now_us;
            led_on = !led_on;
            cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, led_on);
        }
    } else if (led_on) {
        led_on = false;
        cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, false);
    }
}

} // namespace
} // namespace sensa

int main() {
    bool watchdog_usb_override = false;
    if (watchdog_hw->scratch[0] == sensa::kUsbModeScratchMagic) {
        uint32_t m = watchdog_hw->scratch[1];
        if (m <= 1) {
            sensa::g_usb_mode = static_cast<sensa::UsbMode>(m);
            watchdog_usb_override = true;
        }
    }

    board_init();
    sensa::config_load_from_flash(watchdog_usb_override);
    sensa::console_init();
    sensa::usb_init();
    board_init_after_tusb();
    sensa::dsp_init();

    if (!sensa::bt_init()) {
        return 1;
    }

    watchdog_enable(1500, true);

    while (true) {
        uint32_t now_us = time_us_32();
        watchdog_update();
        sensa::bt_poll();
        sensa::usb_poll();
        sensa::dsp_poll();
        sensa::console_poll();
        sensa::poll_bootsel_and_led(now_us);
        sensa::config_poll_save(now_us);
    }
}
