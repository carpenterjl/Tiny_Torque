# arm-none-eabi.cmake — cross-compile the portable core for a Cortex-M (FW-11).
#
#   cmake -S Controllers -B build-arm -DTT_TARGET=embedded \
#         -DCMAKE_TOOLCHAIN_FILE="$PWD/Controllers/cmake/arm-none-eabi.cmake"
#   cmake --build build-arm
#
# The car has two MCUs (D6, Hardware/HW_00_SYSTEM.html): the wheel nodes'
# STM32G431 (Cortex-M4F) and MAIN's STM32H723 (Cortex-M7). The default is the
# M4F: its FPU is single-precision only, which makes it the strictest — code
# that builds clean here (no double, no promotion) also suits the M7. CI
# builds both; MAIN's flags are
#   -DTT_MCU_FLAGS="-mcpu=cortex-m7 -mthumb -mfpu=fpv5-d16 -mfloat-abi=hard"
set(CMAKE_SYSTEM_NAME Generic)
set(CMAKE_SYSTEM_PROCESSOR arm)

set(CMAKE_C_COMPILER arm-none-eabi-gcc)
set(CMAKE_AR arm-none-eabi-ar)
set(CMAKE_RANLIB arm-none-eabi-ranlib)

# There is no OS to link a test program against.
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)

set(TT_MCU_FLAGS "-mcpu=cortex-m4 -mthumb -mfpu=fpv4-sp-d16 -mfloat-abi=hard"
    CACHE STRING "CPU/FPU flags for the target MCU")

# -fsingle-precision-constant: an unsuffixed literal is a float, so a missed
# `f` cannot silently drag a whole expression into software double (FW-04).
set(CMAKE_C_FLAGS_INIT
    "${TT_MCU_FLAGS} -ffunction-sections -fdata-sections -fsingle-precision-constant")

set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
