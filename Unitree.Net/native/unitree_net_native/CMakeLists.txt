cmake_minimum_required(VERSION 3.20)
project(unitree_net_native VERSION 0.1.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

# ---------------------------------------------------------------------------
# Cyclone DDS
#
# The shim uses the serdata API (dds_writecdr / dds_takecdr) to move already-encoded CDR, which is
# available from Cyclone DDS 0.10 onward. Point CMAKE_PREFIX_PATH at your Cyclone install, or at the
# one bundled with unitree_sdk2.
# ---------------------------------------------------------------------------
find_package(CycloneDDS REQUIRED)

# ---------------------------------------------------------------------------
# IDL code generation
#
# UNITREE_IDL_DIR must point at the directory holding Unitree's .idl files. In a unitree_sdk2 checkout
# these live under thirdparty/ or idl/ depending on the release; see native/README.md.
# ---------------------------------------------------------------------------
set(UNITREE_IDL_DIR "" CACHE PATH "Directory containing the Unitree .idl files")

if(NOT UNITREE_IDL_DIR OR NOT EXISTS "${UNITREE_IDL_DIR}")
  message(FATAL_ERROR
    "UNITREE_IDL_DIR is not set or does not exist.\n"
    "Configure with: cmake -DUNITREE_IDL_DIR=/path/to/unitree_sdk2/idl ..")
endif()

file(GLOB_RECURSE UNITREE_IDL_FILES "${UNITREE_IDL_DIR}/*.idl")

if(NOT UNITREE_IDL_FILES)
  message(FATAL_ERROR "No .idl files found under ${UNITREE_IDL_DIR}")
endif()

set(GENERATED_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${GENERATED_DIR}")

# idlc emits one .c/.h pair per IDL file, carrying the dds_topic_descriptor_t symbols the shim
# registers in find_descriptor().
idlc_generate(TARGET unitree_idl
              FILES ${UNITREE_IDL_FILES}
              WARNINGS no-implicit-extensibility
              INCLUDES "${UNITREE_IDL_DIR}")

# ---------------------------------------------------------------------------
# The shim itself
# ---------------------------------------------------------------------------
add_library(unitree_net_native SHARED
  src/unitree_net_native.cpp)

target_include_directories(unitree_net_native
  PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>
  PRIVATE
    "${GENERATED_DIR}")

target_link_libraries(unitree_net_native
  PRIVATE
    unitree_idl
    CycloneDDS::ddsc)

target_compile_definitions(unitree_net_native PRIVATE UNITREE_NET_NATIVE_EXPORTS)

if(MSVC)
  target_compile_options(unitree_net_native PRIVATE /W4 /permissive-)
else()
  target_compile_options(unitree_net_native PRIVATE -Wall -Wextra -Wpedantic)
endif()

# Keep the output name stable across platforms so .NET's default probing finds it:
# unitree_net_native.dll / libunitree_net_native.so / libunitree_net_native.dylib
set_target_properties(unitree_net_native PROPERTIES
  OUTPUT_NAME "unitree_net_native"
  VERSION ${PROJECT_VERSION}
  SOVERSION ${PROJECT_VERSION_MAJOR})

include(GNUInstallDirs)
install(TARGETS unitree_net_native
  RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
  LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
  ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR})
install(FILES include/unitree_net_native.h DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
