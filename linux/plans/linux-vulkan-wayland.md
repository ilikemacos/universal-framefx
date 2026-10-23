# Universal-FrameFX — Linux desktop (Vulkan/Wayland) + open-source engine

**Product:** Universal-FrameFX for Linux (desktop) — Wayland-first  
**Version:** `1.4.1-linux-preview.1` (aligned with Windows `v1.4.1-beta.10` era)  
**Public repo:** https://github.com/ilikemacos/universal-framefx  
**Anti-goals:** SteamOS-only branding; Game Mode/gamescope claims; fake FSR2/3/4/XeSS labels; treating vendor GPU DLLs as MIT source.

## Product identity

| Item | Value |
|------|--------|
| Capture | xdg-desktop-portal ScreenCast → PipeWire |
| Present | SDL2 + Vulkan (Wayland-first) |
| Upscale | CSR 1.2 (EASU+RCAS), CSR 2.0 |
| Frame gen | CSR 2.0 InterpFast (2×/3×; 4× when refresh/source allows) |
| Host note | Steam Deck **Desktop Mode** OK; not Deck-exclusive |
| Not supported | SteamOS Game Mode, gamescope session; vendor FSR2+/XeSS on Linux preview |

## Open-source inventory (MIT)

| Area | In-tree path | Notes |
|------|--------------|--------|
| Windows engine C#/HLSL | repo root (`Gpu.cs`, `FramePipeline.cs`, `Shaders/`, …) | MIT |
| First-party models | `Models/`, `PresetAi/Models/` | MIT with app LICENSE |
| SSGI sources | `ssgi/` | MIT; optional native build |
| Linux port | `linux/` | MIT |
| AMD FidelityFX / Intel XeSS **DLLs** | release zips / local `vendor/bin` (gitignored) | Binary redistributable only — see `licenses/` |

## Weight license decision (Mica via Nr. Wrenby, 2026-10-23 America/Vancouver)

**All FrameFX model artifacts are MIT with the app LICENSE:**

- `Models/csr_ai_*.onnx`
- `Models/csr_ai_*.weights.json`
- `Models/csr_ai_fg.weights.json`
- Preset-assistant ONNXes (`preset_assistant_v1.onnx`, `preset_beta7.onnx`, `preset_g14.onnx`, etc.)

Advertise under the app LICENSE. Engine C#/HLSL/C++ MIT alongside. Do **not** relicense AMD/Intel SDKs as MIT.

## Build / verify (Linux)

See [linux/README.md](../README.md). Preview artifacts: GitHub Release `v1.4.1-linux-preview.1`.
