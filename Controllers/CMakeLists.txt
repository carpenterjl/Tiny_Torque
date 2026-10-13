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
#                     -DCMAKE_TOOLCHAIN_FILE="$PWD/Controllers/cmake/arm-none-eabi.cmake"
#             A CubeMX / Pico SDK / ESP-IDF project add_subdirectory()s this
#             directory with TT_TARGET=embedded and links `opuslib` (the
#             vehicle controller) and/or `foclib` (a wheel's FOC driver).
set(TT_TARGET "sim" CACHE STRING "sim | host | embedded")
set_property(CACHE TT_TARGET PROPERTY STRINGS sim host embedded)
if(NOT TT_TARGET MATCHES "^(sim|host|embedded)$")
    message(FATAL_ERROR "TT_TARGET must be sim, host or embedded (got '${TT_TARGET}')")
endif()

# On Linux the static libraries end up inside the controller shared
# libraries, so their objects must be position-independent: GNU ld refuses a
# PC-relative reference from one object to a global in another (tt_route.c's
# tt_routes, since NAV-01) when making a shared object. MSVC has no such
# notion and lld lets it pass, so only the Ubuntu CI job sees it. Not for
# the MCU build, where -fPIC would cost code.
if(TT_TARGET STREQUAL "sim" AND NOT WIN32)
    set(CMAKE_POSITION_INDEPENDENT_CODE ON)
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
add_library(ttcore STATIC core/tt_params.c core/tt_alloc.c core/tt_nav.c core/tt_safety.c
            core/tt_wire.c core/tt_can.c core/tt_drvbus.c core/tt_soc.c
            core/tt_route.c core/tt_track.c core/tt_vplan.c core/routes_gen.c
            core/tt_clear.c core/tt_grid.c core/tt_detour.c
            core/params_opus_vector.c core/params_opus_vector_foc.c)
target_include_directories(ttcore PUBLIC core hal)
tt_core_warnings(ttcore)

# Opus Vector mission controller: dead-reckoned multi-phase manoeuvre on
# TtMeas/TtCmd. Portable (math.h, pid.h and the core), so the same sources
# build for a real MCU target.
add_library(opuslib STATIC opus_mission/opus_mission.c opus_mission/opus_fw.c
            opus_mission/opus_app.c)
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

# A wheel-motor node's firmware minus its HAL (HW_03_NODE): the FOC current
# loop, the supervisor that mirrors the twin's driver model, and the node's
# end of the CAN-FD drive bus (core/tt_can.h). A node image links this and
# supplies the G431 HAL (node/tt_node.h).
add_library(nodelib STATIC node/tt_node.c)
target_include_directories(nodelib PUBLIC node)
target_link_libraries(nodelib PUBLIC foclib ttcore)
tt_core_warnings(nodelib)

# MAIN's sensor and radio drivers, the portable halves (HW_02/04/05): byte
# streams and register bursts in, TtMeas fields out with the twin's units and
# conventions — CRSF/SBUS -> TtRc, ICM-42688 FIFO -> TtImu, VL53L5CX results
# -> TtTof, PAA5100 burst -> TtFlow, DW3000 timestamps -> TtUwb. The
# register-level halves are the board's.
add_library(drvlib STATIC drivers/tt_rc.c drivers/tt_icm42688.c drivers/tt_vl53l5.c
            drivers/tt_paa5100.c drivers/tt_uwb.c)
target_include_directories(drvlib PUBLIC drivers)
target_link_libraries(drvlib PUBLIC ttcore)
tt_core_warnings(drvlib)

# The firmware's end of the lockstep HIL link (HIL-03): the external-tick
# device loop and the Opus firmware behind it. Portable: an MCU image in HIL
# mode links this and feeds it its USB CDC bytes (targets/hil/opus_hil.h).
add_library(hillib STATIC targets/hil/hil_dev.c targets/hil/opus_hil.c)
target_include_directories(hillib PUBLIC targets/hil)
target_link_libraries(hillib PUBLIC opuslib)
tt_core_warnings(hillib)

# The on-target plant (HIL-06, "simulation in hardware"): a reduced car and
# the timed loop a board runs it in instead of its drivers. Portable: an MCU
# image built with it reports the firmware's CPU time per tick.
add_library(sihlib STATIC plant/tt_sih.c targets/sih/tt_sih_run.c)
target_include_directories(sihlib PUBLIC plant targets/sih)
target_link_libraries(sihlib PUBLIC opuslib)
tt_core_warnings(sihlib)

if(TT_TARGET STREQUAL "embedded")
    return()   # libraries only; the MCU project supplies main(), HAL and startup
endif()

# The Unity host's ABI frames as a board: binds the manifest, builds TtMeas,
# writes TtAct into actuator slots. Shared by the controller DLLs and the
# lockstep bridge, so both feed the firmware identically.
add_library(simhal STATIC targets/sim/sim_hal.c)
target_include_directories(simhal PUBLIC targets/sim hal)
target_link_libraries(simhal PUBLIC ttcore)
tt_core_warnings(simhal)

# Where the lockstep bridge and the host-built firmware images go (HIL-01/04).
# Outside Assets/ so Unity does not import them; gitignored.
set(TT_LINK_DIR "${CMAKE_SOURCE_DIR}/../UnitySim/Native"
    CACHE PATH "Destination for tt_bridge.exe and the PIL firmware images")

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
add_controller(opus_controller targets/sim/opus_main.c opuslib simhal)

# The same mission on the four-motor FOC twin -> opus_foc_controller.dll.
# Same source; only the parameter set differs, as a board config would.
add_controller(opus_foc_controller targets/sim/opus_main.c opuslib simhal)
if(TARGET opus_foc_controller)
    target_compile_definitions(opus_foc_controller PRIVATE OPUS_PARAMS=tt_params_opus_vector_foc)
endif()

# The same firmware as host processes in external-tick mode (HIL-03 without a
# board): the wire protocol over stdin/stdout. tt_bridge --exe runs them in
# lockstep with the sim; a run must reproduce the DLL's exactly.
function(add_pil name params)
    add_executable(${name} targets/hil/pil_main.c)
    target_link_libraries(${name} PRIVATE hillib)
    target_compile_definitions(${name} PRIVATE OPUS_PARAMS=${params} OPUS_PIL_NAME="${name}")
    if(TT_TARGET STREQUAL "sim")
        add_custom_command(TARGET ${name} POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E make_directory "${TT_LINK_DIR}"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:${name}>" "${TT_LINK_DIR}"
            COMMENT "Copying ${name} -> ${TT_LINK_DIR}")
    endif()
endfunction()
add_pil(opus_pil tt_params_opus_vector)
add_pil(opus_foc_pil tt_params_opus_vector_foc)

# The lockstep bridge (HIL-01/04): Unity <-> a DLL out of process, a PIL
# image, or a board on a serial port. Windows only for now.
if(WIN32)
    add_executable(tt_bridge tools/tt_bridge.c)
    target_link_libraries(tt_bridge PRIVATE simhal)
    if(TT_TARGET STREQUAL "sim")
        add_custom_command(TARGET tt_bridge POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E make_directory "${TT_LINK_DIR}"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:tt_bridge>" "${TT_LINK_DIR}"
            COMMENT "Copying tt_bridge -> ${TT_LINK_DIR}")
    endif()
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

# The clearance ahead from a multizone ToF (NAV-05), on synthetic frames.
add_executable(test_clear tests/test_clear.c)
target_link_libraries(test_clear PRIVATE ttcore)
add_test(NAME tt_clear_unit COMMAND test_clear)

# The local map and the detour planner (NAV-08): rays into a rolling grid,
# the offset profile, a lattice plan around a block on the oval.
add_executable(test_grid tests/test_grid.c)
target_link_libraries(test_grid PRIVATE ttcore)
add_test(NAME tt_grid_unit COMMAND test_grid)

# The navigation EKF (UWB + odometry + gyro) on a synthetic drive.
add_executable(test_nav tests/test_nav.c)
target_link_libraries(test_nav PRIVATE ttcore)
add_test(NAME tt_nav_unit COMMAND test_nav)

# Routes, the path tracker and the speed plan (NAV-01..03): every built-in
# route validates with the generator's CRC, projection through a figure-8's
# crossing, the plan's limits, both tracking laws on a kinematic car.
add_executable(test_route tests/test_route.c)
target_link_libraries(test_route PRIVATE ttcore)
add_test(NAME tt_route_track COMMAND test_route)

# The safety layer (FW-08) against injected faults (VAL-11): every fault the
# sim's FaultInjector can inject, on a synthetic car, with the expected
# response, plus no false trips on a clean drive.
add_executable(test_safety tests/test_safety.c tests/tt_fault.c)
target_include_directories(test_safety PRIVATE tests)
target_link_libraries(test_safety PRIVATE opuslib)
add_test(NAME tt_safety_faults COMMAND test_safety)

# Offline replay of a sim run's telemetry through the navigation EKF (VAL-03).
add_executable(nav_replay tools/nav_replay.c)
target_link_libraries(nav_replay PRIVATE ttcore)

# Replay a wire capture through the firmware and diff its commands (VAL-03).
add_executable(tt_replay tools/tt_replay.c)
target_link_libraries(tt_replay PRIVATE opuslib)

# The wire protocol and the firmware's end of the link (HIL-02/03): vectors,
# damaged frames, payload round trips, and a whole drive in lockstep through
# the wire, bit-identical to calling the firmware directly. Its wire stream
# is then replayed by tt_replay: identical with the same firmware, and the
# brushed parameter set on the FOC car's recording must show a difference.
add_executable(test_hil tests/test_hil.c)
target_link_libraries(test_hil PRIVATE hillib)
add_test(NAME hil_wire COMMAND test_hil "${CMAKE_CURRENT_BINARY_DIR}/hil_capture.ttw")
set_tests_properties(hil_wire PROPERTIES FIXTURES_SETUP hil_capture)
add_test(NAME hil_replay COMMAND tt_replay "${CMAKE_CURRENT_BINARY_DIR}/hil_capture.ttw")
set_tests_properties(hil_replay PROPERTIES FIXTURES_REQUIRED hil_capture
                     PASS_REGULAR_EXPRESSION "IDENTICAL")
add_test(NAME hil_replay_sees_change
         COMMAND tt_replay "${CMAKE_CURRENT_BINARY_DIR}/hil_capture.ttw" --params opus_vector --quiet)
set_tests_properties(hil_replay_sees_change PROPERTIES FIXTURES_REQUIRED hil_capture
                     PASS_REGULAR_EXPRESSION "DIFFERENT")

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

# The firmware on the on-target plant, on the PC (HIL-06): it must drive the
# manoeuvre and a route to their ends; the timing report is the board's job.
add_executable(opus_sih targets/sih/sih_main.c)
target_link_libraries(opus_sih PRIVATE sihlib)
add_test(NAME sih_manoeuvre COMMAND opus_sih --expect-done)
add_test(NAME sih_route_oval COMMAND opus_sih --route oval --expect-done)

# The CAN-FD drive bus (SYS-03): frame vectors and round trips, the bus load
# of the planned schedules, and MAIN's end (tt_drvbus: commands, STATUS ->
# TtMeas with timestamps on MAIN's clock, the arming check).
add_executable(test_can tests/test_can.c)
target_link_libraries(test_can PRIVATE ttcore)
add_test(NAME tt_can_bus COMMAND test_can)

# A wheel node on the PMSM plant, driven over the bus by MAIN's end: boot,
# encoder alignment, Iq following in the wheel frame, the limits, derates and
# trips the twin's driver model has, the watchdog, ESTOP, CFG.
add_executable(test_node tests/test_node.c)
target_link_libraries(test_node PRIVATE nodelib pmsmlib)
add_test(NAME tt_node_plant COMMAND test_node)

# The drivers against hand-built vectors and simulated physics (RC frames
# split anywhere in the stream, IMU FIFO packets, ToF zone orientation, flow
# scaling, DS-TWR with drifting clocks), and the pack SoC estimator against
# the twin's battery model.
add_executable(test_drivers tests/test_drivers.c)
target_link_libraries(test_drivers PRIVATE drvlib)
add_test(NAME tt_drivers COMMAND test_drivers)

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
