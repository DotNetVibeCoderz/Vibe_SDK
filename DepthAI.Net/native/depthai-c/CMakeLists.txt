cmake_minimum_required(VERSION 3.20)
project(depthai-c VERSION 0.2.0 LANGUAGES CXX)

# DEPTHAI_CORE_DIR menunjuk paket depthai-core terpasang (rilis prebuilt Luxonis atau
# hasil `cmake --install` sendiri), berisi include/, lib/, dan bin/.
set(DEPTHAI_CORE_DIR "" CACHE PATH "Folder paket depthai-core terpasang")
if(NOT DEPTHAI_CORE_DIR OR NOT EXISTS "${DEPTHAI_CORE_DIR}/include/depthai")
    message(FATAL_ERROR "Set -DDEPTHAI_CORE_DIR ke paket depthai-core (lihat build.ps1).")
endif()

# Target impor dibuat manual, bukan lewat find_package(depthai): depthaiConfig.cmake
# rilis prebuilt mewajibkan OpenCV via find_dependency, padahal OpenCV ter-link statis
# di dalam DLL-nya dan shim ini tidak memakai API OpenCV. Definisi kompilasi disalin
# dari depthaiTargets.cmake, kecuali makro OpenCV — makro itu hanya menyembunyikan
# fungsi non-virtual dan include, sehingga layout kelas tetap sama.
add_library(depthai_core_prebuilt SHARED IMPORTED)
if(WIN32)
    set_target_properties(depthai_core_prebuilt PROPERTIES
        IMPORTED_IMPLIB "${DEPTHAI_CORE_DIR}/lib/depthai-core.lib"
        IMPORTED_LOCATION "${DEPTHAI_CORE_DIR}/bin/depthai-core.dll")
else()
    set_target_properties(depthai_core_prebuilt PROPERTIES
        IMPORTED_LOCATION "${DEPTHAI_CORE_DIR}/lib/libdepthai-core.so")
endif()
set_target_properties(depthai_core_prebuilt PROPERTIES
    INTERFACE_INCLUDE_DIRECTORIES "${DEPTHAI_CORE_DIR}/include"
    INTERFACE_COMPILE_DEFINITIONS
        "DEPTHAI_TARGET_CORE;DEPTHAI_HAVE_BETA;DEPTHAI_HAVE_HOUSING_COORDINATES;DEPTHAI_MERGED_TARGET;DEPTHAI_HAVE_DYNAMIC_CALIBRATION_SUPPORT;DEPTHAI_ENABLE_PROTOBUF;DEPTHAI_XTENSOR_SUPPORT")

add_library(depthai-c SHARED src/depthai_c.cpp)
target_include_directories(depthai-c PUBLIC include)
target_link_libraries(depthai-c PRIVATE depthai_core_prebuilt)
target_compile_features(depthai-c PRIVATE cxx_std_17)
# Nama berkas harus depthai-c.dll / libdepthai-c.so — itulah yang dicari P/Invoke.
set_target_properties(depthai-c PROPERTIES CXX_VISIBILITY_PRESET hidden)

if(MSVC)
    # /EHsc: exception C++ tidak boleh lolos ke C; /bigobj: xtensor membuat banyak section.
    target_compile_options(depthai-c PRIVATE /EHsc /bigobj /W3 /utf-8)
    # Header depthai memakai fungsi yang ditandai deprecated oleh dirinya sendiri.
    target_compile_definitions(depthai-c PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX)
endif()
