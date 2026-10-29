cmake_minimum_required(VERSION 3.20)
project(depthai-c VERSION 0.4.0 LANGUAGES CXX)

# DEPTHAI_CORE_DIR menunjuk paket depthai-core terpasang: rilis prebuilt Luxonis (Windows)
# atau hasil `cmake --install` dari sumber (Linux, lihat build.sh).
set(DEPTHAI_CORE_DIR "" CACHE PATH "Folder paket depthai-core terpasang")
if(NOT DEPTHAI_CORE_DIR OR NOT EXISTS "${DEPTHAI_CORE_DIR}/include/depthai")
    message(FATAL_ERROR "Set -DDEPTHAI_CORE_DIR ke paket depthai-core (lihat build.ps1 / build.sh).")
endif()

add_library(depthai-c SHARED src/depthai_c.cpp)
target_include_directories(depthai-c PUBLIC include)
target_compile_features(depthai-c PRIVATE cxx_std_17)
# Nama berkas harus depthai-c.dll / libdepthai-c.so — itulah yang dicari P/Invoke.
set_target_properties(depthai-c PROPERTIES CXX_VISIBILITY_PRESET hidden)

if(WIN32)
    # Target impor dibuat manual, bukan lewat find_package(depthai): depthaiConfig.cmake
    # rilis prebuilt mewajibkan OpenCV via find_dependency, padahal OpenCV-nya dikirim
    # terpisah dan shim ini tidak memakai API OpenCV. Definisi kompilasi disalin dari
    # depthaiTargets.cmake, kecuali makro OpenCV — makro itu hanya menyembunyikan fungsi
    # non-virtual dan include, sehingga layout kelas tetap sama.
    add_library(depthai_core_prebuilt SHARED IMPORTED)
    set_target_properties(depthai_core_prebuilt PROPERTIES
        IMPORTED_IMPLIB "${DEPTHAI_CORE_DIR}/lib/depthai-core.lib"
        IMPORTED_LOCATION "${DEPTHAI_CORE_DIR}/bin/depthai-core.dll"
        INTERFACE_INCLUDE_DIRECTORIES "${DEPTHAI_CORE_DIR}/include"
        INTERFACE_COMPILE_DEFINITIONS
            "DEPTHAI_TARGET_CORE;DEPTHAI_HAVE_BETA;DEPTHAI_HAVE_HOUSING_COORDINATES;DEPTHAI_MERGED_TARGET;DEPTHAI_HAVE_DYNAMIC_CALIBRATION_SUPPORT;DEPTHAI_ENABLE_PROTOBUF;DEPTHAI_XTENSOR_SUPPORT")
    target_link_libraries(depthai-c PRIVATE depthai_core_prebuilt)
else()
    # Di Linux depthai-core dibangun sendiri tanpa OpenCV, jadi config resminya bisa dipakai
    # dan definisi kompilasinya dijamin sama dengan build pustakanya.
    list(PREPEND CMAKE_PREFIX_PATH "${DEPTHAI_CORE_DIR}")
    find_package(depthai CONFIG REQUIRED)
    target_link_libraries(depthai-c PRIVATE depthai::core)

    # libdepthai-core.so dikirim di folder yang sama (runtimes/linux-x64/native), jadi
    # pustaka dicari relatif terhadap dirinya sendiri, bukan lewat LD_LIBRARY_PATH.
    set_target_properties(depthai-c PROPERTIES
        BUILD_RPATH "\$ORIGIN"
        INSTALL_RPATH "\$ORIGIN")
endif()

if(MSVC)
    # /EHsc: exception C++ tidak boleh lolos ke C; /bigobj: xtensor membuat banyak section.
    target_compile_options(depthai-c PRIVATE /EHsc /bigobj /W3 /utf-8)
    # Header depthai memakai fungsi yang ditandai deprecated oleh dirinya sendiri.
    target_compile_definitions(depthai-c PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX)
endif()
