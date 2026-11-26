#include "runtime_fail_fast.h"

#include <cstdint>
#include "hardware/watchdog.h"
#include "pico.h"

extern "C" [[noreturn]] void sensa_runtime_panic(char const *message, ...) {
    (void)message;
    watchdog_enable(0, false);
    while (true) {
        tight_loop_contents();
    }
}

extern "C" void ds5_record_cyw43_rx_overflow(
    uint32_t previous_available,
    uint32_t published_count,
    uint32_t producer_index,
    uint32_t consumer_index
) {
    (void)previous_available;
    (void)published_count;
    (void)producer_index;
    (void)consumer_index;
    sensa_runtime_panic("cyw43_rx_overflow");
}

namespace std {

[[noreturn]] void __throw_bad_alloc() {
    sensa_runtime_panic("bad_alloc");
}

[[noreturn]] void __throw_bad_array_new_length() {
    sensa_runtime_panic("bad_array");
}

[[noreturn]] void __throw_length_error(char const *) {
    sensa_runtime_panic("length_error");
}

[[noreturn]] void __throw_out_of_range_fmt(char const *, ...) {
    sensa_runtime_panic("out_of_range");
}

} // namespace std
