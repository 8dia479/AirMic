cmake_minimum_required(VERSION 3.22.1)
project(AirMicAndroid VERSION 0.1.0 LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(oboe REQUIRED CONFIG)
add_subdirectory(../shared "${CMAKE_CURRENT_BINARY_DIR}/shared")
add_library(airmic SHARED cpp/audio_engine.cpp cpp/jni_bridge.cpp)
target_include_directories(airmic PRIVATE cpp)
target_compile_options(airmic PRIVATE -Wall -Wextra -Wpedantic)
target_link_libraries(airmic PRIVATE airmic_core oboe::oboe android log)
# Android's default 16 KB page compatibility is required for current arm64 devices.
target_link_options(airmic PRIVATE "-Wl,-z,max-page-size=16384")
