#include "tusb.h"
#include "usb_descriptors.h"
#include <string.h>

#define AUDIO_AND_HID_BASE_LEN (8 + 227)
#define CONFIG_TOTAL_LEN (AUDIO_AND_HID_BASE_LEN + TUD_CDC_DESC_LEN)

#define XUSB360_ITF_LEN (9 + 17 + 7 + 7)
#define XUSB360_CONFIG_TOTAL_LEN (9 + XUSB360_ITF_LEN + TUD_CDC_DESC_LEN)

enum {
    STRID_LANGID = 0,
    STRID_MANUFACTURER,
    STRID_PRODUCT,
    STRID_CDC,
    STRID_SERIAL,
    STRID_MS_MFG,
    STRID_X360_PROD,
};

static tusb_desc_device_t const desc_device_ds5 = {
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,
    .bcdUSB             = 0x0200,
    .bDeviceClass       = TUSB_CLASS_MISC,
    .bDeviceSubClass    = MISC_SUBCLASS_COMMON,
    .bDeviceProtocol    = MISC_PROTOCOL_IAD,
    .bMaxPacketSize0    = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor           = 0x054C,
    .idProduct          = 0x0CE6,
    .bcdDevice          = 0x0157,
    .iManufacturer      = STRID_MANUFACTURER,
    .iProduct           = STRID_PRODUCT,
    .iSerialNumber      = 0x00,
    .bNumConfigurations = 0x01
};

static tusb_desc_device_t const desc_device_x360 = {
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,
    .bcdUSB             = 0x0200,
    .bDeviceClass       = TUSB_CLASS_MISC,
    .bDeviceSubClass    = MISC_SUBCLASS_COMMON,
    .bDeviceProtocol    = MISC_PROTOCOL_IAD,
    .bMaxPacketSize0    = CFG_TUD_ENDPOINT0_SIZE,
    // Composite-safe VID/PID so Windows loads usbccgp (XUSB10 on MI_00 + CDC COM on MI_01)
    .idVendor           = 0x1209,
    .idProduct          = 0xDB05,
    .bcdDevice          = 0x0157,
    .iManufacturer      = STRID_MS_MFG,
    .iProduct           = STRID_X360_PROD,
    .iSerialNumber      = STRID_SERIAL,
    .bNumConfigurations = 0x01
};

uint8_t const *tud_descriptor_device_cb(void) {
    uint8_t mode = sensa_active_usb_mode();
    if (mode == 1) return (uint8_t const *)&desc_device_x360;
    return (uint8_t const *)&desc_device_ds5;
}

// 289-byte DualSense USB HID Report Descriptor
static uint8_t const desc_hid_report_ds[289] = {
    0x05, 0x01, // Usage Page (Generic Desktop Ctrls)
    0x09, 0x05, // Usage (Game Pad)
    0xA1, 0x01, // Collection (Application)
    0x85, 0x01, //   Report ID (1)
    0x09, 0x30, //   Usage (X)
    0x09, 0x31, //   Usage (Y)
    0x09, 0x32, //   Usage (Z)
    0x09, 0x35, //   Usage (Rz)
    0x09, 0x33, //   Usage (Rx)
    0x09, 0x34, //   Usage (Ry)
    0x15, 0x00, //   Logical Minimum (0)
    0x26, 0xFF, 0x00, //   Logical Maximum (255)
    0x75, 0x08, //   Report Size (8)
    0x95, 0x06, //   Report Count (6)
    0x81, 0x02, //   Input
    0x06, 0x00, 0xFF, //   Usage Page (Vendor Defined 0xFF00)
    0x09, 0x20, //   Usage (0x20)
    0x95, 0x01, //   Report Count (1)
    0x81, 0x02, //   Input
    0x05, 0x01, //   Usage Page (Generic Desktop Ctrls)
    0x09, 0x39, //   Usage (Hat switch)
    0x15, 0x00, //   Logical Minimum (0)
    0x25, 0x07, //   Logical Maximum (7)
    0x35, 0x00, //   Physical Minimum (0)
    0x46, 0x3B, 0x01, //   Physical Maximum (315)
    0x65, 0x14, //   Unit (Eng Rot:Angular Pos)
    0x75, 0x04, //   Report Size (4)
    0x95, 0x01, //   Report Count (1)
    0x81, 0x42, //   Input (Null State)
    0x65, 0x00, //   Unit (None)
    0x05, 0x09, //   Usage Page (Button)
    0x19, 0x01, //   Usage Minimum (1)
    0x29, 0x0F, //   Usage Maximum (15)
    0x15, 0x00, //   Logical Minimum (0)
    0x25, 0x01, //   Logical Maximum (1)
    0x75, 0x01, //   Report Size (1)
    0x95, 0x0F, //   Report Count (15)
    0x81, 0x02, //   Input
    0x06, 0x00, 0xFF, //   Usage Page (Vendor Defined 0xFF00)
    0x09, 0x21, //   Usage (0x21)
    0x95, 0x0D, //   Report Count (13)
    0x81, 0x02, //   Input
    0x06, 0x00, 0xFF, //   Usage Page (Vendor Defined 0xFF00)
    0x09, 0x22, //   Usage (0x22)
    0x15, 0x00, //   Logical Minimum (0)
    0x26, 0xFF, 0x00, //   Logical Maximum (255)
    0x75, 0x08, //   Report Size (8)
    0x95, 0x34, //   Report Count (52)
    0x81, 0x02, //   Input
    0x85, 0x02, //   Report ID (2)
    0x09, 0x23, //   Usage (0x23)
    0x95, 0x2F, //   Report Count (47)
    0x91, 0x02, //   Output
    0x85, 0x05, //   Report ID (5)
    0x09, 0x33, //   Usage (0x33)
    0x95, 0x28, //   Report Count (40)
    0xB1, 0x02, //   Feature
    0x85, 0x08, //   Report ID (8)
    0x09, 0x34, //   Usage (0x34)
    0x95, 0x2F, //   Report Count (47)
    0xB1, 0x02, //   Feature
    0x85, 0x09, //   Report ID (9)
    0x09, 0x24, //   Usage (0x24)
    0x95, 0x13, //   Report Count (19)
    0xB1, 0x02, //   Feature
    0x85, 0x0A, //   Report ID (10)
    0x09, 0x25, //   Usage (0x25)
    0x95, 0x1A, //   Report Count (26)
    0xB1, 0x02, //   Feature
    0x85, 0x0B, //   Report ID (11)
    0x09, 0x41, //   Usage (0x41)
    0x95, 0x29, //   Report Count (41)
    0xB1, 0x02, //   Feature
    0x85, 0x0C, //   Report ID (12)
    0x09, 0x42, //   Usage (0x42)
    0x95, 0x29, //   Report Count (41)
    0xB1, 0x02, //   Feature
    0x85, 0x20, //   Report ID (32)
    0x09, 0x26, //   Usage (0x26)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0x21, //   Report ID (33)
    0x09, 0x27, //   Usage (0x27)
    0x95, 0x04, //   Report Count (4)
    0xB1, 0x02, //   Feature
    0x85, 0x22, //   Report ID (34)
    0x09, 0x40, //   Usage (0x40)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0x80, //   Report ID (-128)
    0x09, 0x28, //   Usage (0x28)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0x81, //   Report ID (-127)
    0x09, 0x29, //   Usage (0x29)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0x82, //   Report ID (-126)
    0x09, 0x2A, //   Usage (0x2A)
    0x95, 0x09, //   Report Count (9)
    0xB1, 0x02, //   Feature
    0x85, 0x83, //   Report ID (-125)
    0x09, 0x2B, //   Usage (0x2B)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0x84, //   Report ID (-124)
    0x09, 0x2C, //   Usage (0x2C)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0x85, //   Report ID (-123)
    0x09, 0x2D, //   Usage (0x2D)
    0x95, 0x02, //   Report Count (2)
    0xB1, 0x02, //   Feature
    0x85, 0xA0, //   Report ID (-96)
    0x09, 0x2E, //   Usage (0x2E)
    0x95, 0x01, //   Report Count (1)
    0xB1, 0x02, //   Feature
    0x85, 0xE0, //   Report ID (-32)
    0x09, 0x2F, //   Usage (0x2F)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0xF0, //   Report ID (-16)
    0x09, 0x30, //   Usage (0x30)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0xF1, //   Report ID (-15)
    0x09, 0x31, //   Usage (0x31)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0xF2, //   Report ID (-14)
    0x09, 0x32, //   Usage (0x32)
    0x95, 0x0F, //   Report Count (15)
    0xB1, 0x02, //   Feature
    0x85, 0xF4, //   Report ID (-12)
    0x09, 0x35, //   Usage (0x35)
    0x95, 0x3F, //   Report Count (63)
    0xB1, 0x02, //   Feature
    0x85, 0xF5, //   Report ID (-11)
    0x09, 0x36, //   Usage (0x36)
    0x95, 0x03, //   Report Count (3)
    0xB1, 0x02, //   Feature
    0xC0        // End Collection
};

uint8_t const *tud_hid_descriptor_report_cb(uint8_t itf) {
    (void)itf;
    return desc_hid_report_ds;
}

static uint8_t const desc_configuration[] = {
    // --- Configuration Descriptor ---
    0x09, 0x02,
    CONFIG_TOTAL_LEN & 0xFF, (CONFIG_TOTAL_LEN >> 8) & 0xFF,
    0x06, // bNumInterfaces: 0..2 Audio, 3 HID, 4..5 CDC
    0x01, // bConfigurationValue
    0x00, // iConfiguration
    0xE0, // SELF-POWERED + REMOTE-WAKEUP
    0xFA, // 500mA

    // --- Interface Association Descriptor (Audio Control + Streaming OUT/IN: Itf 0..2) ---
    0x08, TUSB_DESC_INTERFACE_ASSOCIATION, 0x00, 0x03, 0x01, 0x00, 0x00, 0x00,

    // --- Interface 0.0: Audio Control ---
    0x09, 0x04, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
    // AC Header (wTotalLength = 73)
    0x0A, 0x24, 0x01, 0x00, 0x01, 0x49, 0x00, 0x02, 0x01, 0x02,
    // Input Terminal 1 (USB Streaming -> 4ch Speaker+Haptics)
    0x0C, 0x24, 0x02, 0x01, 0x01, 0x01, 0x06, 0x04, 0x33, 0x00, 0x00, 0x00,
    // Feature Unit 2 (Speaker Volume + Mute)
    0x0C, 0x24, 0x06, UAC1_ENTITY_SPK_FEATURE_UNIT, 0x01, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00,
    // Output Terminal 3 (Speaker)
    0x09, 0x24, 0x03, 0x03, 0x01, 0x03, 0x04, UAC1_ENTITY_SPK_FEATURE_UNIT, 0x00,
    // Input Terminal 4 (Headset Mic)
    0x0C, 0x24, 0x02, 0x04, 0x02, 0x04, 0x03, 0x01, 0x00, 0x00, 0x00, 0x00,
    // Feature Unit 5 (Mic Volume + Mute)
    0x09, 0x24, 0x06, UAC1_ENTITY_MIC_FEATURE_UNIT, 0x04, 0x01, 0x03, 0x00, 0x00,
    // Output Terminal 6 (USB Streaming IN)
    0x09, 0x24, 0x03, 0x06, 0x01, 0x01, 0x01, UAC1_ENTITY_MIC_FEATURE_UNIT, 0x00,

    // --- Interface 1.0: Audio Streaming OUT Alt 0 ---
    0x09, 0x04, 0x01, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00,
    // --- Interface 1.1: Audio Streaming OUT Alt 1 ---
    0x09, 0x04, 0x01, 0x01, 0x01, 0x01, 0x02, 0x00, 0x00,
    0x07, 0x24, 0x01, 0x01, 0x01, 0x01, 0x00, // AS General -> Terminal 1
    0x0B, 0x24, 0x02, 0x01, 0x04, 0x02, 0x10, 0x01, 0x80, 0xBB, 0x00, // 4ch 16b 48kHz
    0x09, 0x05, 0x01, 0x09, 0x88, 0x01, 0x01, 0x00, 0x00, // EP 0x01 Iso Adaptive 392B
    0x07, 0x25, 0x01, 0x00, 0x00, 0x00, 0x00,

    // --- Interface 2.0: Audio Streaming IN Alt 0 ---
    0x09, 0x04, 0x02, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00,
    // --- Interface 2.1: Audio Streaming IN Alt 1 ---
    0x09, 0x04, 0x02, 0x01, 0x01, 0x01, 0x02, 0x00, 0x00,
    0x07, 0x24, 0x01, 0x06, 0x01, 0x01, 0x00, // AS General -> Terminal 6
    0x0B, 0x24, 0x02, 0x01, 0x01, 0x02, 0x10, 0x01, 0x80, 0xBB, 0x00, // 1ch 16b 48kHz
    0x09, 0x05, 0x82, 0x05, 0x60, 0x00, 0x01, 0x00, 0x00, // EP 0x82 Iso Async 96B
    0x07, 0x25, 0x01, 0x00, 0x00, 0x00, 0x00,

    // --- Interface 3.0: HID DualSense Controller ---
    0x09, 0x04, GAMEPAD_ITF_NUM, 0x00, 0x02, 0x03, 0x00, 0x00, 0x00,
    0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, 0x21, 0x01, // HID 1.11, 289B report desc
    0x07, 0x05, 0x84, 0x03, 0x40, 0x00, 0x01, // EP 0x84 IN 64B 1ms
    0x07, 0x05, 0x03, 0x03, 0x40, 0x00, 0x01, // EP 0x03 OUT 64B 1ms

    // --- Interfaces 4 & 5: USB CDC ACM Diagnostic Console ---
    TUD_CDC_DESCRIPTOR(CDC_COMM_ITF_NUM, STRID_CDC, 0x87, 8, 0x06, 0x86, 64)
};

TU_VERIFY_STATIC(sizeof(desc_configuration) == CONFIG_TOTAL_LEN, "Incorrect config descriptor length");

uint8_t const *tud_descriptor_configuration_cb(uint8_t index);

static uint8_t const desc_config_x360[] = {
    0x09, 0x02,
    XUSB360_CONFIG_TOTAL_LEN & 0xFF, (XUSB360_CONFIG_TOTAL_LEN >> 8) & 0xFF,
    0x03, // bNumInterfaces: 0 X360, 1..2 CDC
    0x01, 0x00, 0xA0, 0xFA,

    // --- Interface 0: Xbox 360 Controller (XUSB 1.0) ---
    0x09, 0x04, XBOX_ITF_NUM, 0x00, 0x02, 0xFF, 0x5D, 0x01, STRID_X360_PROD,
    // XUSB class-specific 17-byte descriptor (EP IN 0x81 20B, EP OUT 0x02 8B)
    0x11, 0x21, 0x00, 0x01,
    0x01, 0x25, XUSB360_EP_IN, 0x14,
    0x00, 0x00, 0x00, 0x00,
    0x13, XUSB360_EP_OUT, 0x08, 0x00, 0x00,
    // EP IN 0x81 (32B, 4ms)
    0x07, 0x05, XUSB360_EP_IN, 0x03, 0x20, 0x00, 0x04,
    // EP OUT 0x02 (32B, 8ms)
    0x07, 0x05, XUSB360_EP_OUT, 0x03, 0x20, 0x00, 0x08,

    // --- Interfaces 1 & 2: USB CDC ACM Diagnostic Console ---
    TUD_CDC_DESCRIPTOR(XBOX_CDC_COMM_ITF_NUM, STRID_CDC, 0x87, 8, 0x06, 0x86, 64)
};
TU_VERIFY_STATIC(sizeof(desc_config_x360) == XUSB360_CONFIG_TOTAL_LEN, "Incorrect X360 config length");

uint8_t const *tud_descriptor_configuration_cb(uint8_t index) {
    (void)index;
    return (sensa_active_usb_mode() == 1) ? desc_config_x360 : desc_configuration;
}

static char const *string_desc_arr[] = {
    (const char[]){0x09, 0x04},             // 0: English (0x0409)
    "Sony Interactive Entertainment",       // 1: Manufacturer (DS5)
    "DualSense Wireless Controller",        // 2: Product (DS5)
    "SensaBridge Diagnostic Console",       // 3: CDC Interface
    "",                                     // 4: Dynamic 32-hex Serial
    "Microsoft Corporation",                // 5: Manufacturer (Xbox)
    "Xbox 360 Controller for Windows",      // 6: Product (X360)
};

static uint16_t _desc_str[64];

static uint8_t const desc_ms_os_compat_x360[40] = {
    0x28, 0x00, 0x00, 0x00, 0x00, 0x01, 0x04, 0x00, 0x01,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    XBOX_ITF_NUM, 0x01,
    'X', 'U', 'S', 'B', '1', '0', 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
};

bool sensa_handle_ms_os_control(uint8_t rhport, uint8_t stage, tusb_control_request_t const *request) {
    if (request == NULL || request->bmRequestType_bit.type != TUSB_REQ_TYPE_VENDOR) {
        return false;
    }
    if (stage != CONTROL_STAGE_SETUP) {
        return true;
    }
    if (sensa_active_usb_mode() == 1 && request->bRequest == MS_OS_VENDOR_CODE_X360 &&
        request->wIndex == 4 && request->bmRequestType_bit.direction == TUSB_DIR_IN) {
        uint16_t len = request->wLength < sizeof(desc_ms_os_compat_x360) ? request->wLength : sizeof(desc_ms_os_compat_x360);
        return tud_control_xfer(rhport, request, (void *)(uintptr_t)desc_ms_os_compat_x360, len);
    }
    return false;
}

bool tud_vendor_control_xfer_cb(uint8_t rhport, uint8_t stage, tusb_control_request_t const *request) {
    return sensa_handle_ms_os_control(rhport, stage, request);
}

uint16_t const *tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
    (void)langid;
    size_t chr_count = 0;

    if (index == 0xEE) {
        if (sensa_active_usb_mode() != 1) return NULL;
        char const msft[7] = {'M', 'S', 'F', 'T', '1', '0', '0'};
        for (size_t i = 0; i < 7; ++i) _desc_str[1 + i] = (uint16_t)msft[i];
        _desc_str[8] = MS_OS_VENDOR_CODE_X360;
        _desc_str[0] = (uint16_t)((TUSB_DESC_STRING << 8) | (2 * 8 + 2));
        return _desc_str;
    }

    if (index == STRID_LANGID) {
        memcpy(&_desc_str[1], string_desc_arr[0], 2);
        chr_count = 1;
    } else if (index == STRID_SERIAL) {
        char serial_buf[33];
        sensa_get_unique_serial_hex32(serial_buf);
        chr_count = 32;
        for (size_t i = 0; i < chr_count; ++i) {
            _desc_str[1 + i] = (uint16_t)serial_buf[i];
        }
    } else {
        if (index >= sizeof(string_desc_arr) / sizeof(string_desc_arr[0])) {
            return NULL;
        }
        char const *str = string_desc_arr[index];
        chr_count = strlen(str);
        if (chr_count > 63) chr_count = 63;
        for (size_t i = 0; i < chr_count; ++i) {
            _desc_str[1 + i] = (uint16_t)str[i];
        }
    }
    _desc_str[0] = (uint16_t)((TUSB_DESC_STRING << 8) | (2 * chr_count + 2));
    return _desc_str;
}
