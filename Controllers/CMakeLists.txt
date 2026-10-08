cmake_minimum_required(VERSION 3.15)
project(AIHWSimControllers C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# What to build (FW-11):
#   sim       the Unity controller DLLs, the host tests and UserScripts (default)
#   host      the portable libraries and host tests only — no DLL copy, no
#             UserScripts; what the 32-bit CI build uses
#   embedded  the portable libraries only, for an MCU toolchain:
#               cmake -S Controllers -B build-arm -DTT_TARGET=embedded \
#                     -DCMAKE_TOOLCHAIN_FILE=Controllers/cmake/arm-none-eabi.cmake
#             A CubeMX / Pico SDK / ESP-IDF project add_subdirectory()s this
#             directory with TT_TARGET=embedded and links `opuslib` (the
#             vehicle controller) and/or `foclib` (a wheel's FOC driver).
set(TT_TARGET "sim" CACHE STRING "sim | host | embedded")
set_property(CACHE TT_TARGET PROPERTY STRINGS sim host embedded)
if(NOT TT_TARGET MATCHES "^(sim|host|embedded)$")
    message(FATAL_ERROR "TT_TARGET must be sim, host or embedded (got '${TT_TARGET}')")
endif()

# Firmware hygiene for the portable core (FW-04). GCC/Clang only; MSVC has no
# equivalent of -Wdouble-promotion. -ffast-math is deliberately absent: it
# deletes the NaN checks the core relies on. Warnings are errors on the
# embedded build, where a stray double costs a software-float call.
function(tt_core_warnings target)
    if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
        target_compile_options(${target} PRIVATE
            -Wall -Wextra -Wdouble-promotion -Wfloat-conversion -Wshadow
            -fno-math-errno -ffp-contract=off)
        if(TT_TARGET STREQUAL "embedded")
            target_compile_options(${target} PRIVATE -Werror)
        endif()
    endif()
endfunction()

# Where Unity looks for native plugins (64-bit).
#
# CACHE, and load-bearing: the game's in-game "Build & Reload" overrides this via
# build.ps1 -PluginDir so a player build copies the DLL next to the RUNNING game
# instead of into the repo it was compiled from. Turning this into a plain set()
# would make that feature silently do nothing.
set(UNITY_PLUGIN_DIR "${CMAKE_SOURCE_DIR}/../UnitySim/Assets/Plugins/x86_64"
    CACHE PATH "Destination for the built controller DLLs")

# Shared control math (PID). Kept in its own lib so each controller links one
# copy without duplicate-symbol clashes.
add_library(pidlib STATIC common/pid.c)
target_include_directories(pidlib PUBLIC common)
tt_core_warnings(pidlib)

# The portable core's shared pieces: TtMeas/TtCmd types, the parameter store
# and its generated defaults (Tools/gen_params.js), the log schema, and the
# board interface every non-sim target implements (hal/tt_hal.h).
add_library(ttcore STATIC core/tt_params.c core/tt_alloc.c
            core/params_opus_vector.c core/params_opus_vector_foc.c)
target_include_directories(ttcore PUBLIC core hal)
tt_core_warnings(ttcore)

# Opus Vector mission controller: dead-reckoned multi-phase manoeuvre on
# TtMeas/TtCmd. Portable (math.h, pid.h and the core), so the same sources
# build for a real MCU target.
add_library(opuslib STATIC opus_mission/opus_mission.c)
target_include_directories(opuslib PUBLIC opus_mission)
target_link_libraries(opuslib PUBLIC pidlib ttcore)
tt_core_warnings(opuslib)
if(NOT MSVC)
    target_link_libraries(opuslib PUBLIC m)
    target_link_libraries(ttcore PUBLIC m)
endif()

# Field-oriented current control for one wheel motor (FW-10): what runs in
# each FOC driver's ADC ISR. Portable float32 C with no core dependencies, so
# it cross-compiles with the rest; Unity simulates only the closed current
# loop, so this is exercised by test_foc_pmsm against plant/ on the host.
add_library(foclib STATIC foc/foc.c)
target_include_directories(foclib PUBLIC foc)
tt_core_warnings(foclib)
if(NOT MSVC)
    target_link_libraries(foclib PUBLIC m)
endif()

if(TT_TARGET STREQUAL "embedded")
    return()   # libraries only; the MCU project supplies main(), HAL and startup
endif()

# Per-vehicle portable control logic.
add_library(controllib STATIC diffdrive_pid/diffdrive_control.c)
target_include_directories(controllib PUBLIC diffdrive_pid)
target_link_libraries(controllib PUBLIC pidlib)

add_library(carlib STATIC car_pid/car_control.c)
target_include_directories(carlib PUBLIC car_pid)
target_link_libraries(carlib PUBLIC pidlib)

# Helper: build a SHARED controller DLL from one sim-target source, emit exactly
# "<name>.dll", and copy it into Unity after each build. Sim target only.
function(add_controller name source)
    if(NOT TT_TARGET STREQUAL "sim")
        return()
    endif()
    add_library(${name} SHARED ${source})
    target_include_directories(${name} PRIVATE hal)
    target_link_libraries(${name} PRIVATE ${ARGN})
    set_target_properties(${name} PROPERTIES PREFIX "" OUTPUT_NAME "${name}")
    if(MINGW)
        # Unity's LoadLibrary has no way to resolve libgcc_s / libwinpthread
        # from the compiler's bin directory, so a dynamically-linked MinGW DLL
        # fails to load with a bare "LoadLibrary failed". Link the runtime in.
        target_link_options(${name} PRIVATE -static -static-libgcc)
    endif()
    add_custom_command(TARGET ${name} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${UNITY_PLUGIN_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:${name}>" "${UNITY_PLUGIN_DIR}/${name}.dll"
        COMMENT "Copying ${name}.dll -> ${UNITY_PLUGIN_DIR}")
endfunction()

# Differential-drive robot controller  -> controller.dll
add_controller(controller     targets/sim/sim_main.c controllib)

# Four-wheel car speed controller      -> car_controller.dll
add_controller(car_controller targets/sim/car_main.c carlib)

# Opus Vector autonomous mission       -> opus_controller.dll
add_controller(opus_controller targets/sim/opus_main.c opuslib)

# The same mission on the four-motor FOC twin -> opus_foc_controller.dll.
# Same source; only the parameter set differs, as a board config would.
add_controller(opus_foc_controller targets/sim/opus_main.c opuslib)
if(TARGET opus_foc_controller)
    target_compile_definitions(opus_foc_controller PRIVATE OPUS_PARAMS=tt_params_opus_vector_foc)
endif()

# ABI v2 configurable-sensor demo      -> car_sensors_controller.dll
# Standalone (only needs controller_api.h) so it doubles as a v2 reference.
add_controller(car_sensors_controller car_sensors/car_sensors.c)

# ───────────────────────────────── Tests ─────────────────────────────────────
#
# Host-side checks, no Unity needed:
#   cmake -S . -B build && cmake --build build
#   ctest --test-dir build --output-on-failure        (add -C Release for VS)
# The test executables are not copied anywhere; only the DLLs above are.
enable_testing()

# Opus mission against a simplified plant, with tolerance asserts, at 100 and
# 500 Hz.
add_executable(test_mission opus_mission/test_mission.c)
target_link_libraries(test_mission PRIVATE opuslib)
add_test(NAME opus_mission_bench COMMAND test_mission)

# The core's shared pieces: parameter store, torque -> Iq, log schema.
add_executable(test_core tests/test_core.c)
target_link_libraries(test_core PRIVATE opuslib)
add_test(NAME tt_core_unit COMMAND test_core)

# Host-only dq PMSM + inverter model: the plant the FOC is tested against.
add_library(pmsmlib STATIC plant/pmsm_model.c)
target_include_directories(pmsmlib PUBLIC plant)
tt_core_warnings(pmsmlib)
if(NOT MSVC)
    target_link_libraries(pmsmlib PUBLIC m)
endif()

# FOC current loop on the PMSM plant: transforms, SVPWM, Iq step response,
# torque and PLL speed on a loaded rotor, the voltage ceiling, alignment.
add_executable(test_foc_pmsm tests/test_foc_pmsm.c)
target_link_libraries(test_foc_pmsm PRIVATE foclib pmsmlib)
add_test(NAME foc_pmsm COMMAND test_foc_pmsm)

# The UserScripts helper header (PID clamps, wheel radius, manifest fallback).
add_executable(test_tt_controller tests/test_tt_controller.c)
target_include_directories(test_tt_controller PRIVATE
    hal "${CMAKE_SOURCE_DIR}/../UserScripts/lib")
add_test(NAME tt_controller_unit COMMAND test_tt_controller)

if(NOT MSVC)
    target_link_libraries(test_tt_controller PRIVATE m)
endif()

# ─────────────────────── User controllers (UserScripts/) ──────────────────────
#
# Everything above is the game's own firmware. Everything below belongs to
# whoever is playing it: each folder under UserScripts/ holding at least one .c
# becomes its own DLL, named after the folder.
#
# GLOBBED, not listed. "Write a new controller" has to be "copy a folder" —
# the in-game Build button cannot edit this file for the player, and somebody
# writing C for the first time should not have to learn CMake in the same hour.
# CONFIGURE_DEPENDS is what makes a NEW folder show up without a manual
# re-configure: it re-runs the glob at build time and re-configures when the
# answer changed. Ninja, Makefiles and Visual Studio all honour it; if some
# future generator does not, deleting Controllers/build/ has the same effect.
#
# Kept in sync with UserScriptCatalog.cs on the C# side, which lists the same
# folders for the in-game picker by the same rules — a folder the game offers to
# build and CMake declines to build would be a dead button.
set(USER_SCRIPTS_DIR "${CMAKE_SOURCE_DIR}/../UserScripts"
    CACHE PATH "Player-written controllers, one DLL per subfolder")

if(TT_TARGET STREQUAL "sim" AND IS_DIRECTORY "${USER_SCRIPTS_DIR}")
    file(GLOB _tt_user_dirs CONFIGURE_DEPENDS "${USER_SCRIPTS_DIR}/*")
    foreach(_tt_dir ${_tt_user_dirs})
        if(NOT IS_DIRECTORY "${_tt_dir}")
            continue()
        endif()
        get_filename_component(_tt_name "${_tt_dir}" NAME)

        # lib/ is the shared helper header, not a controller.
        if(_tt_name STREQUAL "lib")
            continue()
        endif()

        # The folder name becomes a CMake target AND a DLL file name, so it has
        # to survive both. Skipping with a message beats failing the configure
        # and taking every other controller down with it.
        if(NOT _tt_name MATCHES "^[A-Za-z0-9_]+$")
            message(STATUS "UserScripts/${_tt_name}: skipped — use letters, digits and _ only")
            continue()
        endif()
        if(TARGET ${_tt_name})
            message(WARNING "UserScripts/${_tt_name}: skipped — a controller of that name already exists")
            continue()
        endif()

        file(GLOB _tt_user_src CONFIGURE_DEPENDS "${_tt_dir}/*.c")
        if(NOT _tt_user_src)
            continue()   # a folder of notes and headers is not a controller
        endif()

        # pidlib comes along so a user controller can #include "pid.h" and use
        # the same PID the game's own controllers do. tt_controller.h needs no
        # linking at all — it is header-only — but the include path does.
        add_controller(${_tt_name} "${_tt_user_src}" pidlib)
        target_include_directories(${_tt_name} PRIVATE
            "${USER_SCRIPTS_DIR}/lib" "${_tt_dir}")
        message(STATUS "User controller: ${_tt_name} -> ${_tt_name}.dll")
    endforeach()
endif()
