#!/usr/bin/env bash
# Builds rawspeed.so (linux-x64) inside an ubuntu:22.04 container (glibc 2.35 baseline):
#
#   docker run --rm \
#     -v <SimdRaw>:/simdraw:ro -v <rawspeed>:/rawspeed:ro -v <out>:/out \
#     ubuntu:22.04 bash /simdraw/native/rawspeed-shim/build-linux-x64.sh
#
# pugixml / zlib / libjpeg-turbo come from vcpkg (static, PIC) pinned to the same port versions as the Windows build,
# libstdc++/libgcc are linked statically, so the .so only depends on glibc.
set -euo pipefail

VCPKG_COMMIT="${VCPKG_COMMIT:-8e8dfb4ba483886936ded5ca201b500b8d8b0096}"
export DEBIAN_FRONTEND=noninteractive

apt-get update -qq
apt-get install -y -qq --no-install-recommends \
  ca-certificates git curl zip unzip tar pkg-config gcc-12 g++-12 make ninja-build cmake nasm python3 >/dev/null
export CC=gcc-12 CXX=g++-12

git config --global --add safe.directory '*'

if [ ! -x /opt/vcpkg/vcpkg ]; then
  git clone -q https://github.com/microsoft/vcpkg /opt/vcpkg
  git -C /opt/vcpkg checkout -q "$VCPKG_COMMIT"
  /opt/vcpkg/bootstrap-vcpkg.sh -disableMetrics >/dev/null
fi
/opt/vcpkg/vcpkg install pugixml zlib libjpeg-turbo --triplet x64-linux --clean-after-build

cmake -G Ninja -S /simdraw/native/rawspeed-shim -B /tmp/build \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_CXX_COMPILER=g++-12 \
  -DCMAKE_TOOLCHAIN_FILE=/opt/vcpkg/scripts/buildsystems/vcpkg.cmake \
  -DVCPKG_TARGET_TRIPLET=x64-linux \
  -DRAWSPEED_SRC=/rawspeed
cmake --build /tmp/build

strip --strip-unneeded /tmp/build/out/rawspeed.so
cp /tmp/build/out/rawspeed.so /tmp/build/out/cameras.xml /tmp/build/out/commit.txt /out/
echo "--- exported symbols"
nm -D --defined-only /out/rawspeed.so | grep ' T '
echo "--- dependencies"
readelf -d /out/rawspeed.so | grep NEEDED
echo "--- max GLIBC symbol version"
objdump -T /out/rawspeed.so | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1
