cmake_minimum_required(VERSION 3.13)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

set(PICO_BOARD pico2_w CACHE STRING "Board type")
set(SYS_CLOCK_KHZ_VALUE 150000 CACHE STRING "RP2350 system clock in kHz")
set(CYW43_PIO_CLOCK_DIV_VALUE 2 CACHE STRING "CYW43 PIO clock divider")

include(pico_sdk_import.cmake)

project(sensa-bridge C CXX ASM)

set(TINYUSB_OPT_OS OPT_OS_NONE CACHE STRING "TinyUSB bare-metal OS abstraction" FORCE)

pico_sdk_init()

# Keep the audited CYW43 PIO-SPI / HCI continuation patches from Pico SDK 2.3.0
include("${CMAKE_CURRENT_SOURCE_DIR}/cmake/patch-cyw43.cmake")
set(SENSA_CYW43_GENERATED_DIR "${CMAKE_BINARY_DIR}/generated/sensa-cyw43")
set(SENSA_SDK_CYW43_DIR "${PICO_SDK_PATH}/src/rp2_common/pico_cyw43_driver")
ds5_generate_cyw43_hci_transport(
    "${SENSA_SDK_CYW43_DIR}/btstack_hci_transport_cyw43.c"
    "${SENSA_CYW43_GENERATED_DIR}/btstack_hci_transport_cyw43.c"
)
ds5_generate_cyw43_shared_bus(
    "${SENSA_SDK_CYW43_DIR}/cybt_shared_bus/cybt_shared_bus.c"
    "${SENSA_CYW43_GENERATED_DIR}/cybt_shared_bus.c"
)
ds5_generate_cyw43_payload(
    "${PICO_CYW43_DRIVER_PATH}/src/cyw43_ll.c"
    "${SENSA_SDK_CYW43_DIR}/cyw43_bus_pio_spi.c"
    "${SENSA_SDK_CYW43_DIR}/cybt_shared_bus/cybt_shared_bus_driver.c"
    "${SENSA_CYW43_GENERATED_DIR}"
)
ds5_replace_sdk_interface_source(pico_btstack_hci_transport_cyw43
    "${SENSA_SDK_CYW43_DIR}/btstack_hci_transport_cyw43.c"
    "${SENSA_CYW43_GENERATED_DIR}/btstack_hci_transport_cyw43.c")
ds5_replace_sdk_interface_source(cybt_shared_bus
    "${SENSA_SDK_CYW43_DIR}/cybt_shared_bus/cybt_shared_bus.c"
    "${SENSA_CYW43_GENERATED_DIR}/cybt_shared_bus.c")
ds5_replace_sdk_interface_source(cybt_shared_bus
    "${SENSA_SDK_CYW43_DIR}/cybt_shared_bus/cybt_shared_bus_driver.c"
    "${SENSA_CYW43_GENERATED_DIR}/cybt_shared_bus_driver.c")
ds5_replace_sdk_interface_source(cyw43_driver
    "${PICO_CYW43_DRIVER_PATH}/src/cyw43_ll.c"
    "${SENSA_CYW43_GENERATED_DIR}/cyw43_ll.c")
ds5_replace_sdk_interface_source(cyw43_driver_picow
    "${SENSA_SDK_CYW43_DIR}/cyw43_bus_pio_spi.c"
    "${SENSA_CYW43_GENERATED_DIR}/cyw43_bus_pio_spi.c")

add_executable(sensa-bridge
    src/main.cpp
    src/sensa_bt.cpp
    src/sensa_console.cpp
    src/sensa_dsp.cpp
    src/sensa_usb.cpp
    src/usb_descriptors.c
    src/ram_mem.c
    src/runtime_fail_fast.cpp
)

set_source_files_properties(src/ram_mem.c PROPERTIES COMPILE_OPTIONS
    "-fno-builtin;-fno-tree-loop-distribute-patterns"
)

target_compile_options(sensa-bridge PRIVATE
    -fsigned-char
    -Wall
    -Wextra
    $<$<COMPILE_LANGUAGE:CXX>:-fno-exceptions>
    $<$<COMPILE_LANGUAGE:CXX>:-fno-rtti>
    $<$<COMPILE_LANGUAGE:CXX>:-fno-unwind-tables>
    $<$<COMPILE_LANGUAGE:CXX>:-fno-use-cxa-atexit>
)

target_compile_definitions(sensa-bridge PRIVATE
    CYW43_LWIP=0
    PICO_CXX_ENABLE_EXCEPTIONS=0
    PICO_CYW43_LOGGING_ENABLED=0
    PICO_PANIC_FUNCTION=sensa_runtime_panic
    PICO_STACK_SIZE=7168
    PICO_CORE1_STACK_SIZE=0
    PICO_USE_STACK_GUARDS=1
    CFG_TUSB_OS=OPT_OS_NONE
    SYS_CLOCK_KHZ=${SYS_CLOCK_KHZ_VALUE}
    CYW43_PIO_CLOCK_DIV_INT=${CYW43_PIO_CLOCK_DIV_VALUE}
)

pico_add_linker_script_override_path(sensa-bridge
    "${CMAKE_CURRENT_SOURCE_DIR}/cmake/linker/rp2350-core0-stack"
    FILES memory_scratch.incl section_extra_post_platform_end.incl
)

pico_set_program_name(sensa-bridge "SensaBridge")
pico_set_program_version(sensa-bridge "0.1.0")

pico_enable_stdio_uart(sensa-bridge 0)
pico_enable_stdio_usb(sensa-bridge 0)

target_include_directories(sensa-bridge PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_link_libraries(sensa-bridge
    pico_stdlib
    hardware_watchdog
    hardware_timer
    pico_rand
    pico_btstack_classic
    pico_cyw43_arch_poll
    pico_btstack_cyw43
    tinyusb_device
    tinyusb_board
)

pico_add_extra_outputs(sensa-bridge)
