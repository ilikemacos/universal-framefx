#!/usr/bin/env bash
# Launch Universal-FrameFX under gamescope (nested) or directly if already in Game Mode.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
resolve_bin() {
  if [[ -n "${UFX_BIN:-}" && -x "${UFX_BIN}" ]]; then
    printf '%s\n' "${UFX_BIN}"
    return
  fi
  for cand in \
    "${SCRIPT_DIR}/ufx-linux" \
    "${SCRIPT_DIR}/../build/ufx-linux" \
    "${SCRIPT_DIR}/../ufx-linux" \
    "$(command -v ufx-linux 2>/dev/null || true)"; do
    if [[ -n "${cand}" && -x "${cand}" ]]; then
      printf '%s\n' "${cand}"
      return
    fi
  done
  echo "ufx-gamescope-launch: ufx-linux not found (set UFX_BIN)" >&2
  exit 127
}

BIN="$(resolve_bin)"
echo "Universal-FrameFX Game Mode launcher (experimental; see docs/GAMEMODE.md)" >&2
echo "Binary: ${BIN}" >&2

if [[ -n "${GAMESCOPE_WAYLAND_DISPLAY:-}" ]]; then
  echo "Already in gamescope — exec ufx-linux directly." >&2
  exec "${BIN}" "$@"
fi

if ! command -v gamescope >/dev/null 2>&1; then
  echo "gamescope not on PATH — running ufx-linux on the current session." >&2
  exec "${BIN}" "$@"
fi

# Nested gamescope client; -f fullscreen. Adaptive sync when supported.
exec gamescope -f --adaptive-sync -- "${BIN}" "$@"
