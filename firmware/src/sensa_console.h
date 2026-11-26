#ifndef SENSA_CONSOLE_H
#define SENSA_CONSOLE_H

#include <cstdint>

namespace sensa {

void console_init();
void console_poll();
void console_printf(char const *fmt, ...) __attribute__((format(printf, 1, 2)));
void console_hexdump(char const *tag, uint8_t const *data, uint16_t len);
void console_print_status();
void console_print_help();

} // namespace sensa

#endif
