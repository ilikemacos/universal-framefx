# Universal-FrameFX for Linux (desktop + Game Mode path)

Native Linux **Vulkan + Wayland** app. Capture: `xdg-desktop-portal` ScreenCast
(PipeWire) on Desktop Mode; under **gamescope / SteamOS Game Mode**, prefers the
gamescope PipeWire video node, then portal. Present via SDL2 Vulkan.
Upscalers: **CSR 1.2** (EASU + RCAS) and **CSR 2.0**. Frame generation:
**CSR 2.0 InterpFast** 2x/3x (4x when refresh/source fps >= 4).

Version: **1.4.1-linux-preview.4**

## Game Mode (experimental)

* Detects gamescope via `GAMESCOPE_WAYLAND_DISPLAY` / desktop session vars.
* Capture preference in Game Mode: **gamescope PipeWire node** → portal.
* Helper: `scripts/ufx-gamescope-launch.sh` (nested gamescope or passthrough).
* Docs: `docs/GAMEMODE.md`. Hardware Deck QA **not** done on this preview box
  (Lavapipe only) — treat as an experimental path, not a quality claim.

## Honest limits

* CSR AI / FSR2+ / XeSS / LarkquillAI / DLSS: **not** on Linux yet — see `docs/BLOCKERS.md`.
* Capture-only: no process injection, no anti-cheat interaction.
* Frame generation adds ~1 source frame of latency (`HoldDelay`).
* DMA-BUF zero-copy is best-effort; else memfd/CPU upload.
* Numerically verified on **Lavapipe**. Live FG on RADV/ANV/NVK still needs hardware testing.

## Requirements

* Wayland (preferred) or X11 via SDL2, `xdg-desktop-portal` + **PipeWire**, Vulkan 1.2, SDL2
* Game Mode: gamescope exporting a PipeWire video node and/or a ScreenCast portal backend

## Build

```bash
sudo apt-get install -y libvulkan-dev mesa-vulkan-drivers glslc \
  libsdl2-dev libpipewire-0.3-dev cmake ninja-build \
  libsystemd-dev spirv-tools pkg-config

./scripts/build.sh
./scripts/package-appimage.sh
```

## CLI

* `--version` — `Universal-FrameFX 1.4.1-linux-preview.4`
* `--selftest` — headless GPU self-test (Lavapipe OK)
* `--session-info` — print Desktop vs Game Mode detection
* `--capture-fail-soft` — if capture fails, continue with null (black) capture
* `--offline-fg` / `--offline-upscale` — PNG sequence tools
* Numeric tests: `easu_rcas_numeric_test`, `csr2_numeric_test`, `fg2_numeric_test`, …

```bash
export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
./build/ufx-linux --selftest
./build/ufx-linux --session-info
UFX_FORCE_GAMEMODE=1 ./build/ufx-linux --session-info
```

## Troubleshooting

If the app opens and closes instantly (e.g. CachyOS/Arch + GNOME), check
`~/.local/share/universal-framefx/last-run.log`. If the AppImage never starts at all,
install `fuse2` or run it without FUSE:

```bash
./Universal-FrameFX-*.AppImage --appimage-extract-and-run
```

See `docs/TROUBLESHOOT.md` for Vulkan driver, portal and PipeWire checks.

## License

MIT (see LICENSE). AMD FSR1 MIT notice (EASU/RCAS), Dear ImGui MIT, stb MIT:
NOTICE.md.
