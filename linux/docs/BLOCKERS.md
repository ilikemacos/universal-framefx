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

## CSR AI upscale / CSR AI FG

* Windows live path: `Models/csr_ai_x2.weights.json` + HLSL compute (`CsrAi.hlsl`), not the tiny
  ONNX stubs as the primary engine (`csr_ai_x2_fp16.onnx` ~5KB).
* Linux tree has CSR 1.2 / CSR 2.0 / InterpFast Vulkan compute; **no** weights→GLSL conv pipeline yet.
* System `libonnxruntime` is available on Debian, but wiring ORT alone would not match the Windows
  HLSL weight path without a real port.
* **Decision:** ship Models in the public repo (already OSS); Linux CSR AI labeled unavailable in UI
  until a real Vulkan/CPU weight path lands. Not a fake “CSR AI” menu item that no-ops as quality.

## LarkquillAI

* Windows has a local-permission gate + panel. Linux now has the same gate: a LarkquillAI button asks the same Yes/No question once, saves the answer to `larkquill-local-permission.json` under `$FRAMEFX_AGENT_DIR` or `~/.local/share/FrameFX/agent`, and shows "LarkquillAI is unavailable." after a No.
* **Decision:** no native chat panel yet. After a Yes the button opens LarkquillAI in the browser instead of faking a panel.

## DLSS

* NVIDIA closed; skipped unless an already-approved Linux path appears.

## Game Mode hardware

* Implementation + Lavapipe/selftest only this run. Deck Game Mode capture/present needs field results.
