cmake_minimum_required(VERSION 3.16)
project(ts_voice_forwarder C)

# ── Toolchain: MinGW cross-compile (Windows x64 DLL from Linux) ──────────
# These are set here only as a fallback if not passed via -DCMAKE_TOOLCHAIN_FILE.
# Prefer using:  cmake -DCMAKE_TOOLCHAIN_FILE=../toolchain-mingw64.cmake ..
if(NOT CMAKE_CROSSCOMPILING AND NOT WIN32)
    set(CMAKE_SYSTEM_NAME Windows)
    set(CMAKE_C_COMPILER  x86_64-w64-mingw32-gcc)
    set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)
endif()

# ── Sources & includes ────────────────────────────────────────────────────
add_library(ts_voice_forwarder SHARED
    ../plugin.c
)

target_include_directories(ts_voice_forwarder PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../include
)

# ── Link Winsock (cross-compile: static to avoid runtime DLL deps) ────────
target_link_libraries(ts_voice_forwarder
    ws2_32
)

# Remove the default "lib" prefix so TS3 sees "ts_voice_forwarder.dll"
set_target_properties(ts_voice_forwarder PROPERTIES
    PREFIX ""
    SUFFIX ".dll"
)

# ── Compiler flags ────────────────────────────────────────────────────────
target_compile_options(ts_voice_forwarder PRIVATE
    -O2
    -Wall
    -Wno-unused-parameter
)

# ── Optional: copy the output DLL into a staging directory ───────────────
# Override with:  cmake -DDEPLOY_DIR=/mnt/c/Users/Pc/AppData/Roaming/TS3Client/plugins ..
if(DEFINED DEPLOY_DIR)
    add_custom_command(TARGET ts_voice_forwarder POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy
            $<TARGET_FILE:ts_voice_forwarder>
            "${DEPLOY_DIR}/ts_voice_forwarder.dll"
        COMMENT "Deploying DLL to ${DEPLOY_DIR}"
    )
endif()
