# arm-none-eabi.cmake — cross-compile the portable core for a Cortex-M (FW-11).
#
#   cmake -S Controllers -B build-arm -DTT_TARGET=embedded \
#         -DCMAKE_TOOLCHAIN_FILE="$PWD/Controllers/cmake/arm-none-eabi.cmake"
#   cmake --build build-arm
#
# No board is chosen yet (decision D6), so the default is a generic Cortex-M4F:
# its FPU is single-precision only, which makes it the strictest common target
# — code that builds clean here (no double, no promotion) also suits the
# M7/M33 parts (STM32 G4/H5/U5/H7, RP2350). Pick a part with TT_MCU_FLAGS, e.g.
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
