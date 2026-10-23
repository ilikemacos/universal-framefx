#!/bin/bash
# Build + check everything for the SSGI module on Linux (no Windows needed):
#   1. fxc (real Windows SDK fxc.exe under Wine): every shader entry point, SM5.0 -> build/cso/*.cso
#   2. native g++ -std=c++20: importance/worker unit test (+ optional TSAN run: ./build.sh tsan)
#   3. mingw-w64 g++ -std=c++20 cross-compile: ssgi.dll (D3D11 pipeline + C API, real d3d11.h) and the test as .exe
set -e
cd "$(dirname "$0")"
mkdir -p build
bash tests/fxc-check-ssgi.sh
CXXFLAGS="-std=c++20 -O2 -msse2 -Wall -Wextra -Wpedantic"
g++ $CXXFLAGS -pthread -o build/importance_test tests/importance_test.cpp src/ssgi_importance.cpp src/ssgi_worker.cpp
echo "native g++ $(g++ -dumpversion): build/importance_test OK"
if [ "${1:-}" = tsan ]; then
  g++ -std=c++20 -O1 -g -pthread -fsanitize=thread -o build/importance_test_tsan tests/importance_test.cpp src/ssgi_importance.cpp src/ssgi_worker.cpp
fi
MINGW=x86_64-w64-mingw32-g++-posix
$MINGW $CXXFLAGS -shared -static -o build/ssgi.dll src/ssgi_importance.cpp src/ssgi_worker.cpp src/ssgi_pipeline.cpp src/ssgi_capi.cpp
$MINGW $CXXFLAGS -static -o build/importance_test.exe tests/importance_test.cpp src/ssgi_importance.cpp src/ssgi_worker.cpp
echo "mingw $($MINGW -dumpversion): build/ssgi.dll, build/importance_test.exe OK"
