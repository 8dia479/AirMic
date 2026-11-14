# Optional Linux -> Windows x64 compile check. NOT a substitute for MSVC/device tests.
# cmake -S . -B build/win-cross -G Ninja \
#   -DCMAKE_TOOLCHAIN_FILE=cmake/zig-windows.cmake -DZIG_EXECUTABLE=/path/to/zig \
#   -DBUILD_TESTING=OFF -DCMAKE_BUILD_TYPE=Release
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR AMD64)
list(APPEND CMAKE_TRY_COMPILE_PLATFORM_VARIABLES ZIG_EXECUTABLE)
if(NOT ZIG_EXECUTABLE)
    find_program(ZIG_EXECUTABLE zig REQUIRED)
endif()
set(CMAKE_CXX_COMPILER "${ZIG_EXECUTABLE}" CACHE FILEPATH "Zig executable")
set(CMAKE_CXX_COMPILER_ARG1 "c++ -target x86_64-windows-gnu" CACHE STRING "Zig C++ cross driver")
# Compiler driver performs the link. Host ar/ranlib can index COFF archives.
set(CMAKE_RC_COMPILER llvm-rc CACHE FILEPATH "No resources are compiled by AirMic")
