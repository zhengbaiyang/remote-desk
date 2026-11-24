cmake_minimum_required(VERSION 3.20)
if(POLICY CMP0141)
    cmake_policy(SET CMP0141 NEW)
    set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT "Embedded" CACHE STRING "" FORCE)
endif()
project(RemoteDeskStreamCore LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(THIRD_PARTY_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party")
set(ML_DIR "${THIRD_PARTY_DIR}/moonlight-common-c")
set(OPUS_DIR "${THIRD_PARTY_DIR}/opus")

if(NOT EXISTS "${ML_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "Missing third_party/moonlight-common-c. Run native/fetch_deps.ps1 first.")
endif()
if(NOT EXISTS "${OPUS_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "Missing third_party/opus. Run native/fetch_deps.ps1 first.")
endif()

# 1. Opus static library
set(OPUS_BUILD_SHARED_LIBRARY OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_PKG_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_CMAKE_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
add_subdirectory("${OPUS_DIR}" opus_build EXCLUDE_FROM_ALL)

# 2. Moonlight custom ENet submodule
set(ENET_NO_INSTALL ON CACHE BOOL "" FORCE)
add_subdirectory("${ML_DIR}/enet" enet_build EXCLUDE_FROM_ALL)

# 3. moonlight-common-c sources (substituting PlatformCrypto.c with Windows CNG PlatformCryptoBcrypt.c)
file(GLOB ML_SRC_FILES "${ML_DIR}/src/*.c")
list(FILTER ML_SRC_FILES EXCLUDE REGEX "PlatformCrypto\\.c$")

set(ML_ALL_SOURCES
    ${ML_SRC_FILES}
    "${CMAKE_CURRENT_SOURCE_DIR}/PlatformCryptoBcrypt.c"
    "${ML_DIR}/nanors/rs.c"
    "${ML_DIR}/nanors/deps/obl/oblas_common.c"
    "${ML_DIR}/nanors/deps/obl/oblas_lite.c"
)

add_library(moonlight_core STATIC ${ML_ALL_SOURCES})
target_include_directories(moonlight_core
    PUBLIC "${ML_DIR}/src"
    PRIVATE "${ML_DIR}/nanors" "${ML_DIR}/nanors/deps" "${ML_DIR}/nanors/deps/obl"
)
target_compile_definitions(moonlight_core PRIVATE NDEBUG HAS_SOCKLEN_T _CRT_SECURE_NO_WARNINGS)
if(MSVC)
    target_compile_options(moonlight_core PRIVATE /W3 /wd4100 /wd4232 /wd4244 /wd4267 /wd5105 /experimental:c11atomics)
endif()
target_link_libraries(moonlight_core PUBLIC enet ws2_32 winmm bcrypt)

# 4. RemoteDesk.StreamCore.dll
add_library(RemoteDesk.StreamCore SHARED
    stream_core_api.h
    d3d11_mf_video.h
    d3d11_mf_video.cpp
    opus_wasapi_audio.h
    opus_wasapi_audio.cpp
    stream_core.cpp
)
target_compile_definitions(RemoteDesk.StreamCore PRIVATE
    RD_STREAMCORE_EXPORTS
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    _CRT_SECURE_NO_WARNINGS
)
if(MSVC)
    target_compile_options(RemoteDesk.StreamCore PRIVATE /utf-8 /W3)
endif()
target_link_libraries(RemoteDesk.StreamCore PRIVATE
    moonlight_core
    opus
    d3d11
    dxgi
    mfplat
    mfuuid
    ole32
    oleaut32
    ws2_32
    winmm
    bcrypt
)
set_target_properties(RemoteDesk.StreamCore PROPERTIES
    OUTPUT_NAME "RemoteDesk.StreamCore"
    PREFIX ""
)
