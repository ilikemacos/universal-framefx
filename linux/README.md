# Universal-FrameFX for Linux (desktop)

Native Linux **Vulkan + Wayland** desktop app. Capture is `xdg-desktop-portal`
ScreenCast (PipeWire). Present via SDL2 Vulkan. Upscalers: **CSR 1.2**
(EASU + RCAS) and **CSR 2.0** (upsample + optional temporal accum + CAS).
Frame generation: **CSR 2.0 InterpFast**, 2x/3x (4x when refresh / source fps
>= 4). Present is FIFO vsync-paced. Added latency is one source frame
(`HoldDelay`).

Version: **1.4.1-linux-preview.1**

Product identity: **desktop Linux** (Wayland-first). Steam Deck **Desktop
Mode** is a supported host, not exclusive branding. This is not a SteamOS /
Game Mode product.

## Honest limits

* **Desktop session only.** Game Mode / gamescope is not supported.
* Capture-only: no process injection, no anti-cheat interaction.
* Frame generation (CSR 2.0 InterpFast) adds ~1 source frame of latency.
* DMA-BUF zero-copy import is best-effort. If the compositor/driver does not
  offer a matching modifier, the app falls back to a memfd/CPU upload.
* fp16 compose is used when `shaderFloat16` is present; otherwise fp32.
* **No temporal vendor FSR2 / FSR3 / FSR4 / XeSS** in this Linux preview
  (those paths are Windows vendor-SDK only where redistributable).
* This preview was numerically verified on **Lavapipe** (software Vulkan).
  Visual quality and live FG on real hardware (RADV / ANV / NVK, portal
  picker + compositor) still needs hardware testing — do not treat Lavapipe
  pass as a hardware quality claim.

## Requirements

* Recent Linux desktop with **Wayland** (preferred) or X11 via SDL2, plus
  `xdg-desktop-portal` ScreenCast + **PipeWire**
* Vulkan 1.2 (Mesa; do not bundle a GPU driver)
* SDL2 (bundled in the AppImage / zip)

## Build

```bash
# Debian/Ubuntu-like:
sudo apt-get install -y libvulkan-dev mesa-vulkan-drivers glslc \
  libsdl2-dev libpipewire-0.3-dev cmake ninja-build \
  libsystemd-dev spirv-tools pkg-config

./scripts/build.sh          # → build/ufx-linux, build/shaders/*.spv
./scripts/package-appimage.sh
```

## CLI

* `--version` — prints `Universal-FrameFX 1.4.1-linux-preview.1`
* `--selftest` — headless GPU self-test (Lavapipe OK; exit 0 = pass)
* `--offline-fg IN OUT [--fgx N] [--fast] [--score] [--save-every K]` — CSR 2.0 FG on a PNG sequence
* `--offline-upscale IN OUT [--mode csr1|csr2|bilinear] [--temporal] [--sharpness S]` — upscaler PSNR
* `easu_rcas_numeric_test`, `csr2_numeric_test`, `fg2_numeric_test`,
  `pacing_numeric_test`, `affine_numeric_test`

```bash
export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
./build/ufx-linux --selftest
./build/gen_synthetic /tmp/ufx-syn
./build/ufx-linux --offline-fg /tmp/ufx-syn /tmp/ufx-fg2 --fgx 2 --score
./build/ufx-linux --offline-upscale /tmp/ufx-syn /tmp/ufx-up-csr2 --mode csr2
./build/easu_rcas_numeric_test
./tests/portal_capability_probe.sh
```

## License

MIT (see LICENSE). AMD FSR1 MIT notice (EASU/RCAS), Dear ImGui MIT, stb MIT:
NOTICE.md. Engine C++/shaders in this tree are MIT with the app.
