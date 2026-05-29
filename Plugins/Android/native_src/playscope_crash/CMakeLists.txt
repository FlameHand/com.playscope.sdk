cmake_minimum_required(VERSION 3.18.1)
project(playscope_crash LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(playscope_crash SHARED playscope_crash.cpp)

target_compile_options(playscope_crash PRIVATE
    -Wall -Wextra -Werror
    -fno-exceptions -fno-rtti
    -funwind-tables -fasynchronous-unwind-tables
    -fvisibility=hidden
    -Os
    -fdata-sections -ffunction-sections
)

target_link_options(playscope_crash PRIVATE
    -Wl,--gc-sections
    -Wl,--exclude-libs,ALL
)

target_link_libraries(playscope_crash PRIVATE log)
