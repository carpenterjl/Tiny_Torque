cmake_minimum_required(VERSION 3.15)
project(AIHWSimControllers C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

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

# Per-vehicle portable control logic.
add_library(controllib STATIC diffdrive_pid/diffdrive_control.c)
target_include_directories(controllib PUBLIC diffdrive_pid)
target_link_libraries(controllib PUBLIC pidlib)

add_library(carlib STATIC car_pid/car_control.c)
target_include_directories(carlib PUBLIC car_pid)
target_link_libraries(carlib PUBLIC pidlib)

# Opus Vector mission controller: dead-reckoned multi-phase manoeuvre. Portable
# (only math.h + pid.h), so the same sources build for a real MCU target.
add_library(opuslib STATIC opus_mission/opus_mission.c)
target_include_directories(opuslib PUBLIC opus_mission)
target_link_libraries(opuslib PUBLIC pidlib)

# Helper: build a SHARED controller DLL from one sim-target source, emit exactly
# "<name>.dll", and copy it into Unity after each build.
function(add_controller name source)
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

# ABI v2 configurable-sensor demo      -> car_sensors_controller.dll
# Standalone (only needs controller_api.h) so it doubles as a v2 reference.
add_controller(car_sensors_controller car_sensors/car_sensors.c)
