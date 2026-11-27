#ifndef SENSA_USB_DESCRIPTORS_H
#define SENSA_USB_DESCRIPTORS_H

#define UAC1_ENTITY_SPK_FEATURE_UNIT 0x02
#define UAC1_ENTITY_MIC_FEATURE_UNIT 0x05

#define GAMEPAD_ITF_NUM 0x03
#define CDC_COMM_ITF_NUM 0x04

#define XBOX_ITF_NUM 0x00
#define XBOX_CDC_COMM_ITF_NUM 0x01

#define XUSB360_EP_IN 0x81
#define XUSB360_EP_OUT 0x02

#define MS_OS_VENDOR_CODE_X360 0x21

#ifdef __cplusplus
extern "C" {
#endif
uint8_t sensa_active_usb_mode(void);
void sensa_get_unique_serial_hex32(char out33[33]);
#ifdef __cplusplus
}
#endif

#endif
