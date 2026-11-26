#ifndef SENSA_USB_H
#define SENSA_USB_H

#include <cstdint>

namespace sensa {

void usb_init();
void usb_poll();

void usb_set_bt_ready(bool ready);
void usb_submit_bt_input_report(uint8_t const *report_63b, uint16_t len);

} // namespace sensa

#endif
