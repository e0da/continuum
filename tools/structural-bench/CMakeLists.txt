cmake_minimum_required(VERSION 3.24)
project(ContinuumStructuralBench VERSION 1.0 LANGUAGES CXX)

file(REAL_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../../artifacts" artifact_root)
file(REAL_PATH "${CMAKE_BINARY_DIR}" binary_root)
cmake_path(IS_PREFIX artifact_root "${binary_root}" NORMALIZE in_artifacts)
if(NOT in_artifacts OR binary_root STREQUAL artifact_root)
  message(FATAL_ERROR "Build below this repository's ignored artifacts directory.")
endif()
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()
if(NOT CMAKE_BUILD_TYPE STREQUAL "Release" OR CMAKE_CONFIGURATION_TYPES)
  message(FATAL_ERROR "Use a single-configuration generator with CMAKE_BUILD_TYPE=Release.")
endif()

include(FetchContent)
set(DOUBLE_PRECISION ON CACHE BOOL "Double world positions" FORCE)
set(CROSS_PLATFORM_DETERMINISTIC ON CACHE BOOL "Deterministic compiler settings" FORCE)
set(OVERRIDE_CXX_FLAGS OFF CACHE BOOL "Preserve CMake Release flags" FORCE)
set(INTERPROCEDURAL_OPTIMIZATION OFF CACHE BOOL "No LTO" FORCE)
set(CPP_EXCEPTIONS_ENABLED ON CACHE BOOL "Permit consumer exceptions" FORCE)
set(FLOATING_POINT_EXCEPTIONS_ENABLED OFF CACHE BOOL "No FP traps" FORCE)
set(USE_ASSERTS OFF CACHE BOOL "Release assertions off" FORCE)
set(GENERATE_DEBUG_SYMBOLS OFF CACHE BOOL "No debug symbols" FORCE)
set(ENABLE_ALL_WARNINGS OFF CACHE BOOL "Upstream default warnings" FORCE)
set(ENABLE_INSTALL OFF CACHE BOOL "No installation" FORCE)
set(JPH_BUILD_SHARED_LIBS OFF CACHE BOOL "Static Jolt" FORCE)
foreach(option TARGET_UNIT_TESTS TARGET_HELLO_WORLD TARGET_PERFORMANCE_TEST TARGET_SAMPLES TARGET_VIEWER
    JPH_USE_DX12 JPH_USE_VK JPH_USE_MTL JPH_USE_CPU_COMPUTE
    DEBUG_RENDERER_IN_DEBUG_AND_RELEASE DEBUG_RENDERER_IN_DISTRIBUTION
    PROFILER_IN_DEBUG_AND_RELEASE PROFILER_IN_DISTRIBUTION)
  set(${option} OFF CACHE BOOL "Not required by standalone fixture" FORCE)
endforeach()
FetchContent_Declare(Jolt
  URL https://codeload.github.com/jrouwe/JoltPhysics/tar.gz/e77f175595e64cb44218cc9d9d56fc365ad0e36a
  URL_HASH SHA256=1f32328fb763135de10a244568d6ccb2ed9b1e6593fafe6dc6db5b2719d330bd
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_SUBDIR Build)
FetchContent_MakeAvailable(Jolt)

add_executable(continuum-structural main.cpp)
target_link_libraries(continuum-structural PRIVATE Jolt)
target_compile_features(continuum-structural PRIVATE cxx_std_17)
target_compile_definitions(continuum-structural PRIVATE
  CONTINUUM_COMPILER="${CMAKE_CXX_COMPILER_ID}-${CMAKE_CXX_COMPILER_VERSION}")
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
  target_compile_options(continuum-structural PRIVATE -Wall -Wextra -Werror)
endif()
find_package(Python3 REQUIRED COMPONENTS Interpreter)
enable_testing()
add_test(NAME structural-report COMMAND "${Python3_EXECUTABLE}"
  "${CMAKE_CURRENT_SOURCE_DIR}/test_report.py" "$<TARGET_FILE:continuum-structural>")
