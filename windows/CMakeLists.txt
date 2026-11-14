cmake_minimum_required(VERSION 3.22)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(AirMicWindows VERSION 0.1.0 LANGUAGES CXX)
    set(CMAKE_CXX_STANDARD 20)
    set(CMAKE_CXX_STANDARD_REQUIRED ON)
    set(CMAKE_CXX_EXTENSIONS OFF)
    add_subdirectory(../shared "${CMAKE_CURRENT_BINARY_DIR}/shared")
endif()
if(NOT WIN32)
    message(FATAL_ERROR "AirMic receiver requires Windows 10/11 (WASAPI and Winsock2)")
endif()
find_package(Threads REQUIRED)
add_executable(airmic_receiver
    src/main.cpp
    src/devices.cpp
    src/udp_receiver.cpp
    src/wasapi_renderer.cpp
)
target_include_directories(airmic_receiver PRIVATE include)
target_compile_features(airmic_receiver PRIVATE cxx_std_20)
target_compile_definitions(airmic_receiver PRIVATE
    WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE WINVER=0x0A00)
if(MSVC)
    target_compile_definitions(airmic_receiver PRIVATE _WIN32_WINNT=0x0A00)
endif()
target_link_libraries(airmic_receiver PRIVATE airmic_core Threads::Threads
    ws2_32 ole32 uuid avrt propsys shell32)
if(MSVC)
    target_compile_options(airmic_receiver PRIVATE /W4 /permissive- /utf-8 /EHsc)
elseif(MINGW)
    # Zig supplies a Windows 8.1 default; override it without macro redefinition.
    target_compile_options(airmic_receiver PRIVATE -U_WIN32_WINNT -D_WIN32_WINNT=0x0A00 -Wall -Wextra -Wpedantic)
    target_link_options(airmic_receiver PRIVATE -static -static-libgcc -static-libstdc++)
endif()
install(TARGETS airmic_receiver RUNTIME DESTINATION .)
