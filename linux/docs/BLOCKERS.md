# Linux Windows-parity blockers (preview.2)

Evidence recorded 2026-10-23 on the build box.

## FSR2 / FSR3 / FSR4 (AMD FidelityFX)

* Windows ships **binary-redistributable** DX12 DLLs under AMD’s FidelityFX license.
* Tree checked: `/workspace/framefx/vendor/FidelityFX-SDK-2.3.0` — license is **binary form only**,
  no reverse engineering; upscalers package here is primarily headers (`ffx_upscale.h`) + docs,
  not a ready Linux Vulkan redistributable we can legally open-source as MIT source.
* **Decision:** do **not** claim FSR2+ on Linux until a vendor-allowed Vulkan redistributable
  (or MIT FSR1-only, already present as CSR 1.2) is integrated and tested.

## Intel XeSS

* Windows: `libxess.dll` redistributable.
* No Intel XeSS Linux/Vulkan SDK present in our trees.
* **Decision:** blocked / out of scope until Intel provides a Linux path we can ship legally.

## CSR AI upscale / SSGI

* CSR AI upscale: ported to Linux as Vulkan compute (`src/gpu/csr_ai_upscale.cpp`,
  `Models/csr_ai_x2.weights.json`, `src/shaders/csr_ai_*.comp`), with a CPU reference and numeric test.
* SSGI: ported to Linux as a Vulkan compute post effect (`src/gpu/ssgi.cpp`,
  `src/shaders/ssgi_*.comp`), with a CPU reference and numeric test. It is off by default,
  has a control-panel toggle plus strength slider, and `--ssgi` applies it on the offline upscale path.
* CSR AI FG remains **unavailable** on Linux: the Windows live frame-generation path has its own
  model/runtime pairing that has not been ported.

## LarkquillAI

* Windows has a local-permission gate + panel. Linux now has the same gate: a LarkquillAI button asks the same Yes/No question once, saves the answer to `larkquill-local-permission.json` under `$FRAMEFX_AGENT_DIR` or `~/.local/share/FrameFX/agent`, and shows "LarkquillAI is unavailable." after a No.
* **Decision:** no native chat panel yet. After a Yes the button opens LarkquillAI in the browser instead of faking a panel.

## DLSS

* NVIDIA closed; skipped unless an already-approved Linux path appears.

## Game Mode hardware

* Implementation + Lavapipe/selftest only this run. Deck Game Mode capture/present needs field results.
