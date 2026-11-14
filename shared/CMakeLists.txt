add_library(airmic_core STATIC
    protocol/packet.cpp
    audio/jitter_buffer.cpp
    audio/playout.cpp
    dsp/processor.cpp
)
target_compile_features(airmic_core PUBLIC cxx_std_20)
target_include_directories(airmic_core PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
set_target_properties(airmic_core PROPERTIES POSITION_INDEPENDENT_CODE ON)
if(MSVC)
    target_compile_options(airmic_core PRIVATE /W4 /permissive- /utf-8)
else()
    target_compile_options(airmic_core PRIVATE -Wall -Wextra -Wpedantic -Wconversion -Wshadow)
endif()
if(AIRMIC_SANITIZER AND NOT MSVC)
    if(AIRMIC_SANITIZER STREQUAL "address")
        set(_san "address,undefined")
    elseif(AIRMIC_SANITIZER MATCHES "^(undefined|thread)$")
        set(_san "${AIRMIC_SANITIZER}")
    else()
        message(FATAL_ERROR "Unknown AIRMIC_SANITIZER=${AIRMIC_SANITIZER}")
    endif()
    target_compile_options(airmic_core PUBLIC -fsanitize=${_san} -fno-omit-frame-pointer)
    target_link_options(airmic_core PUBLIC -fsanitize=${_san})
endif()
