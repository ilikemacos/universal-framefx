#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
cmake --preset release
cmake --build --preset release -j"$(nproc)"
echo "built: $ROOT/build/ufx-linux"
echo "shaders: $ROOT/build/shaders"
if command -v spirv-val >/dev/null 2>&1; then
  for s in "$ROOT"/build/shaders/*.spv; do
    spirv-val "$s"
    echo "spirv-val OK $(basename "$s")"
  done
fi
