#!/usr/bin/env bash
# Membangun libdepthai-c.so untuk linux-x64 dan menyiapkan runtime-nya di
# artifacts/native/linux-x64.
#
# Luxonis tidak merilis paket C++ prebuilt untuk Linux, jadi depthai-core dibangun dari
# sumber (vcpkg bawaannya mengurus dependensi). Konfigurasinya dirampingkan: OpenCV,
# dynamic calibration, protobuf, curl, dan AprilTag dimatikan karena shim tidak memakainya,
# dan OpenCV+ffmpeg adalah bagian build yang paling lama. Firmware perangkat tetap
# di-embed. Hasil install di-cache di .deps, jadi build berikutnya hanya mengompilasi shim.
#
# Butuh: cmake >= 3.24, ninja, gcc >= 12, git, curl, zip/unzip, pkg-config, libudev-dev,
# dan patchelf. Di Ubuntu:
#   sudo apt-get install ninja-build g++-12 pkg-config libudev-dev patchelf zip unzip
#
# Pemakaian:  native/depthai-c/build.sh   (versi lewat env DEPTHAI_VERSION, bawaan 3.11.0)
set -euo pipefail

DEPTHAI_VERSION="${DEPTHAI_VERSION:-3.11.0}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
deps="$here/.deps"
src="$deps/depthai-core-$DEPTHAI_VERSION-src"
install="$deps/depthai-core-$DEPTHAI_VERSION-linux-x64"
build="$here/.build-linux"
stage="$repo/artifacts/native/linux-x64"
jobs="$(nproc)"

mkdir -p "$deps" "$stage"

# --- depthai-core dari sumber --------------------------------------------------------
if [ ! -f "$install/lib/cmake/depthai/depthaiConfig.cmake" ]; then
    if [ ! -d "$src/.git" ]; then
        git clone --depth 1 --branch "v$DEPTHAI_VERSION" --recurse-submodules --shallow-submodules \
            https://github.com/luxonis/depthai-core.git "$src"
    fi

    cmake -S "$src" -B "$src/build" -G Ninja \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=ON \
        -DCMAKE_INSTALL_PREFIX="$install" \
        -DDEPTHAI_OPENCV_SUPPORT=OFF \
        -DDEPTHAI_XTENSOR_SUPPORT=ON \
        -DDEPTHAI_DYNAMIC_CALIBRATION_SUPPORT=OFF \
        -DDEPTHAI_ENABLE_PROTOBUF=OFF \
        -DDEPTHAI_ENABLE_CURL=OFF \
        -DDEPTHAI_ENABLE_APRIL_TAG=OFF \
        -DDEPTHAI_ENABLE_MP4V2=OFF \
        -DDEPTHAI_ENABLE_BACKWARD=OFF \
        -DDEPTHAI_ENABLE_LIBUSB=ON \
        -DDEPTHAI_BUILD_EXAMPLES=OFF \
        -DDEPTHAI_BUILD_TESTS=OFF \
        -DDEPTHAI_BUILD_PYTHON=OFF \
        -DDEPTHAI_CLANG_FORMAT=OFF \
        "-DVCPKG_INSTALL_OPTIONS=--clean-buildtrees-after-build;--clean-packages-after-build"
    cmake --build "$src/build" --target install --parallel "$jobs"
fi

# --- Shim ------------------------------------------------------------------------------
cmake -S "$here" -B "$build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DDEPTHAI_CORE_DIR="$install"
cmake --build "$build" --parallel "$jobs"

# --- Kumpulkan runtime -----------------------------------------------------------------
# Hanya pustaka yang berasal dari prefix install yang ikut dikirim; pustaka sistem (glibc,
# libstdc++, libudev) diambil dari mesin pengguna. Tiap berkas disalin dengan nama
# SONAME-nya (NuGet tidak menyimpan symlink) dan RUNPATH-nya diarahkan ke $ORIGIN.
rm -f "$stage"/*.so*
cp -L "$build/libdepthai-c.so" "$stage/"

queue=("$stage/libdepthai-c.so")
while [ "${#queue[@]}" -gt 0 ]; do
    current="${queue[0]}"
    queue=("${queue[@]:1}")
    while read -r name path; do
        case "$path" in
            "$install"/*)
                if [ ! -f "$stage/$name" ]; then
                    cp -L "$path" "$stage/$name"
                    queue+=("$stage/$name")
                fi
                ;;
        esac
    done < <(LD_LIBRARY_PATH="$install/lib:$install/lib64" ldd "$current" | awk '/=> \//{print $1, $3}')
done

for lib in "$stage"/*.so*; do
    patchelf --set-rpath '$ORIGIN' "$lib"
done

# Tanpa LD_LIBRARY_PATH semua dependensi harus tetap ditemukan — itulah kondisi di mesin pengguna.
if ldd "$stage/libdepthai-c.so" | grep -q "not found"; then
    ldd "$stage/libdepthai-c.so"
    echo "Ada dependensi yang tidak ditemukan." >&2
    exit 1
fi

echo "Runtime linux-x64 siap di $stage"
ls -la "$stage"
echo "Dependensi sistem yang diharapkan ada di mesin pengguna:"
ldd "$stage/libdepthai-c.so" "$stage"/libdepthai-core*.so* | awk '/=> \//{print "  " $1}' | sort -u
