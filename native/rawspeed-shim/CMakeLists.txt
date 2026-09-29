cmake_minimum_required(VERSION 3.22)

# Static CRT on Windows: the DLL must load on a clean machine without the VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded" CACHE STRING "")

project(rawspeed_shim CXX)

set(RAWSPEED_SRC "" CACHE PATH "Path to a darktable-org/rawspeed checkout")
if(NOT EXISTS "${RAWSPEED_SRC}/CMakeLists.txt")
  message(FATAL_ERROR "Set -DRAWSPEED_SRC=<path to rawspeed checkout>")
endif()

find_package(Git REQUIRED)
execute_process(COMMAND "${GIT_EXECUTABLE}" -C "${RAWSPEED_SRC}" rev-parse --short=8 HEAD
                OUTPUT_VARIABLE RAWSPEED_COMMIT OUTPUT_STRIP_TRAILING_WHITESPACE COMMAND_ERROR_IS_FATAL ANY)
execute_process(COMMAND "${GIT_EXECUTABLE}" -C "${RAWSPEED_SRC}" describe --tags --always --long
                OUTPUT_VARIABLE RAWSPEED_DESCRIBE OUTPUT_STRIP_TRAILING_WHITESPACE COMMAND_ERROR_IS_FATAL ANY)
message(STATUS "rawspeed ${RAWSPEED_COMMIT} (${RAWSPEED_DESCRIBE})")

# RawSpeed options: library only, portable codegen, no OpenMP runtime to redistribute.
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(BUILD_BENCHMARKING OFF CACHE BOOL "" FORCE)
set(BUILD_FUZZERS OFF CACHE BOOL "" FORCE)
set(BUILD_DOCS OFF CACHE BOOL "" FORCE)
set(BINARY_PACKAGE_BUILD ON CACHE BOOL "" FORCE)
set(WITH_OPENMP OFF CACHE BOOL "" FORCE)
set(USE_XMLLINT OFF CACHE BOOL "" FORCE)
set(RAWSPEED_ENABLE_WERROR OFF CACHE BOOL "" FORCE)
set(RAWSPEED_ENABLE_DEBUG_INFO OFF CACHE BOOL "" FORCE)

add_subdirectory("${RAWSPEED_SRC}" rawspeed EXCLUDE_FROM_ALL)

add_library(rawspeed_shim SHARED rawspeed_shim.cpp rawspeed_shim.h)
target_link_libraries(rawspeed_shim PRIVATE rawspeed)
file(CONFIGURE OUTPUT "${CMAKE_BINARY_DIR}/generated/rawspeed_shim_version.h"
  CONTENT "#define RAWSPEED_SHIM_VERSION \"rawspeed ${RAWSPEED_COMMIT} (${RAWSPEED_DESCRIBE})\"\n")
target_include_directories(rawspeed_shim PRIVATE "${CMAKE_BINARY_DIR}/generated")
set_target_properties(rawspeed_shim PROPERTIES
  OUTPUT_NAME rawspeed
  PREFIX ""
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN ON
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out/lib")

if(UNIX AND NOT APPLE)
  target_link_options(rawspeed_shim PRIVATE
    -static-libstdc++ -static-libgcc -Wl,--exclude-libs,ALL -Wl,--no-undefined -Wl,--gc-sections)
endif()

file(CONFIGURE OUTPUT "${CMAKE_BINARY_DIR}/out/commit.txt" CONTENT "${RAWSPEED_COMMIT}\n")
add_custom_command(TARGET rawspeed_shim POST_BUILD
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${RAWSPEED_SRC}/data/cameras.xml" "${CMAKE_BINARY_DIR}/out/cameras.xml"
  VERBATIM)
