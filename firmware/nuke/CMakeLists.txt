cmake_minimum_required(VERSION 3.13)

set(CMAKE_C_STANDARD 11)
set(PICO_BOARD pico2_w CACHE STRING "Board type")

include(../pico_sdk_import.cmake)

project(pico_flash_nuke C ASM)

pico_sdk_init()

add_executable(pico_flash_nuke
    nuke.c
)

# Must execute completely from SRAM because it erases the entire flash chip.
pico_set_binary_type(pico_flash_nuke no_flash)

target_link_libraries(pico_flash_nuke
    pico_stdlib
    hardware_flash
)

pico_add_extra_outputs(pico_flash_nuke)
