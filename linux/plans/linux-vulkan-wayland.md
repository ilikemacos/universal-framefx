# Universal-FrameFX — Linux desktop (Vulkan/Wayland) + open-source engine

**Owner (this box):** Grok Bot / Wyne engine-release lane  
**Product:** Universal-FrameFX for Linux (desktop) — Wayland-first  
**Version target:** `1.4.1-linux-preview.1` (aligned with Windows `v1.4.1-beta.10` era)  
**Worktree:** `/workspace/ufx-linux-desktop/`  
**Best source copied from:** `/workspace/ufx-csrai/linux/` (`1.4.1-steamos-preview.2`)  
**Public repo:** https://github.com/ilikemacos/universal-framefx (publish as **ilikemacos** only)  
**Anti-goals:** SteamOS-only rebrand as the product; Game Mode/gamescope claims; fake FSR2/3/4/XeSS labels; shipping closed Windows engine DLLs on Linux; Cursor cloud-agent publishes.

---

## 1. Product identity & versioning

| Item | Value |
|------|--------|
| Product name | Universal-FrameFX for Linux (desktop) |
| Binary | `ufx-linux` |
| Version string | `1.4.1-linux-preview.1` (`src/ufx_version.h`) |
| Capture | xdg-desktop-portal ScreenCast → PipeWire |
| Present | SDL2 + Vulkan swapchain (Wayland-first; X11 free via SDL if available) |
| Supported host note | Steam Deck **Desktop Mode** OK; not Deck-/SteamOS-exclusive branding |
| Not supported | SteamOS Game Mode, gamescope session |

---

## 2. Source scaffold (done / do)

```bash
# Already: copy /workspace/ufx-csrai/linux/ → /workspace/ufx-linux-desktop/
# Rename project/binary ufx-steamos → ufx-linux (CMakeLists, scripts, usage strings)
# Bump UFX_VERSION_STRING → 1.4.1-linux-preview.1
# Rewrite README/NOTICE for desktop Linux product + honest limits
./scripts/build.sh
export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
./build/ufx-linux --version
./build/ufx-linux --selftest
./build/easu_rcas_numeric_test
./build/csr2_numeric_test
./build/fg2_numeric_test
./build/pacing_numeric_test
./build/affine_numeric_test
./scripts/package-appimage.sh   # or tarball fallback under dist/
```

Files touched for rebrand: `CMakeLists.txt`, `src/ufx_version.h`, `src/ui/main.cpp`,
`src/ui/control_panel.cpp`, `src/offline_*.cpp`, `src/selftest.cpp`,
`scripts/build.sh`, `scripts/package-appimage.sh`, `README.md`, `NOTICE.md`.

---

## 3. Capture / present stack

* **Wayland-first:** SDL2 creates Vulkan window; compositor is Wayland when `WAYLAND_DISPLAY` is set.
* **Capture:** `src/capture/portal_screencast.cpp` — xdg-desktop-portal ScreenCast + PipeWire; DMA-BUF import best-effort (`src/gpu/vk_dmabuf_import.cpp`), else memfd/CPU upload.
* **X11:** no extra code path required if SDL2 provides it; do not add X11-only capture.
* **Requirements to document:** `xdg-desktop-portal`, PipeWire, Vulkan 1.2 ICD (Mesa), SDL2.

---

## 4. Feature matrix vs Windows Beta10 (honest)

| Feature | Windows Beta10 | Linux `1.4.1-linux-preview.1` |
|---------|----------------|-------------------------------|
| CSR 1.2 EASU+RCAS | Yes | Yes (Vulkan compute) |
| CSR 2.0 upscale (+ temporal accum + CAS) | Yes | Yes |
| CSR 2.0 InterpFast FG 2x/3x/(4x) | Yes | Yes |
| CSR AI upscale / CSR AI FG | Yes (ONNX/weights) | **Not in this Linux tree** — no ONNX runtime wired; honest absence |
| AMD FSR1 (MIT shaders) | Yes | Yes (EASU/RCAS lineage; labeled CSR 1.2 / FSR1 MIT notice) |
| AMD FSR2/3/4 (vendor SDK) | Yes (DLL) | **Out of scope** this preview |
| Intel XeSS | Yes (DLL) | **Out of scope** this preview |
| NV OF / hardware motion | Windows | **Out of scope** |
| SSGI / SSRT native helper | Windows C++/HLSL | **Not packaged in Linux preview** (source exists under windows/ssgi for OSS publish) |
| Capture | Desktop Duplication / WGC | Portal + PipeWire |
| Game Mode / gamescope | N/A | **Not supported** |
| Verification this run | — | Lavapipe + numeric tests only |

---

## 5. Build, test, package

**Deps:** `libvulkan-dev` `glslc` `libsdl2-dev` `libpipewire-0.3-dev` `cmake` `ninja-build` `libsystemd-dev` `mesa-vulkan-drivers` `spirv-tools` `pkg-config` `g++`

**Release build:** `./scripts/build.sh` → `build/ufx-linux`, `build/shaders/*.spv`

**Gate before any GitHub release:**
1. `./build/ufx-linux --version` prints `Universal-FrameFX 1.4.1-linux-preview.1`
2. `VK_ICD_FILENAMES=.../lvp_icd.json ./build/ufx-linux --selftest` exit 0
3. Numeric tests exit 0
4. AppImage **or** runnable tarball under `dist/`

**Release (later, report-first):** proposed tag `v1.4.1-linux-preview.1`, asset name `Universal-FrameFX-1.4.1-linux-preview.1-linux-x86_64.{AppImage,tar.gz}`. Push branch optional; **no release publish until green + Mica/parent OK**. Publish only as **ilikemacos**.

---

## 6. README / NOTICE messaging rules

* Product = **Linux desktop Vulkan/Wayland**, not SteamOS-exclusive.
* State Lavapipe verification scope; no hardware quality claim without RADV/ANV/etc. test.
* No temporal vendor FSR2/3/4/XeSS labels unless truly wired and tested.
* No Game Mode / gamescope support claims.
* Keep AMD FSR1 MIT, ImGui, stb notices in NOTICE.md.

---

## 7. Open-source the FrameFX engine (Mica add-on)

### 7.1 License preference

* **Default for first-party engine source:** **MIT** (match app `LICENSE`, Chopsticks HQ 2026).
* **Third-party:** keep vendor licenses in-tree under `vendor/licenses/` / NOTICE — do **not** relicense AMD/Intel SDKs as MIT.
* FSR1 EASU/RCAS shader lineage: MIT (AMD FidelityFX FSR1) — already attributed.
* AMD FidelityFX SDK prebuilt DX12 DLLs: **binary redistributable only** (AMD license) — not open source.
* Intel XeSS `libxess.dll`: **binary redistributable only** (Intel Simplified Software License) — not open source.
* Model weights / ONNX: **separate** — see inventory; Wrenby-owned training artifacts need explicit license note before claiming MIT on weights.

### 7.2 Repo visibility (what moves public under ilikemacos/universal-framefx)

| Path on box (canonical private) | Proposed public layout | Action |
|---------------------------------|------------------------|--------|
| `/workspace/ufx-csrai/windows/src/UniversalFrameFX/*.cs` (+ `Shaders/`) | `windows/src/UniversalFrameFX/` or replace stub `Engine/` with real sources | **Open-source now** (MIT) |
| `/workspace/ufx-csrai/windows/ssgi/` (C++/HLSL) | `windows/ssgi/` | **Open-source now** (MIT) |
| `/workspace/ufx-linux-desktop/` (this tree) | `linux/` | **Open-source now** (MIT) — already first-party |
| `/workspace/ufx-csrai/macos/` | `macos/` (if/when polished) | Open when buildable; not blocking Linux preview |
| `/workspace/universal-framefx-pub/Engine/*` stubs (`EngineNotIncludedException`) | Delete stubs; wire `FrameEngine.Create` / consumers to real `Gpu` + `FramePipeline` (or keep thin MIT facade that constructs real types) | Replace stubs |
| `windows/vendor/bin/*.dll` | `windows/vendor/bin/` + licenses | **Redistributable binaries only** — not source |
| `Models/*.onnx`, `*.weights.json` | `Models/` with `Models/LICENSE` or NOTICE section | **Not MIT by default** — document; ask Wrenby for weight license text |

### 7.3 OSS inventory table (closed artifacts found)

| Artifact | Location(s) | Classification |
|----------|-------------|----------------|
| Real Windows engine C# (`Gpu.cs`, `FramePipeline.cs`, `FgV2*.cs`, `Fsr1.cs`, `Csr2Upscale.cs`, `CsrAi*.cs`, …) | `/workspace/ufx-csrai/windows/src/UniversalFrameFX/` | **Open-source now** (first-party; replace public stubs) |
| Windows shaders HLSL | `.../Shaders/` incl. `CsrAi.hlsl`, `CsrAiFg.hlsl` | **Open-source now** (code); runtime still needs weights for AI paths |
| SSGI/SSRT native | `/workspace/ufx-csrai/windows/ssgi/` | **Open-source now** |
| Linux Vulkan engine (C++/GLSL/SPIR-V build) | this worktree / ufx-csrai/linux | **Open-source now** |
| Public stubs `EngineNotIncludedException`, `FrameEngine.Create` throw | `/workspace/universal-framefx-pub/Engine/` | **Remove / replace** with real buildable paths |
| `amd_fidelityfx_dx12.dll`, `amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll` | `*/windows/vendor/bin/`, FidelityFX-SDK PrebuiltSignedDLL | **Third-party SDK redistributable only** (AMD license) |
| `libxess.dll` (+ dx11 variant in framefx/vendor) | vendor bins | **Third-party SDK redistributable only** (Intel) |
| Vendor license texts | `windows/vendor/licenses/*` | Keep & ship with binaries |
| `csr_ai_x2_fp16.onnx`, `csr_ai_x2_fp32.onnx` | `UniversalFrameFX/Models/` | **Wrenby-owned / AI artifact** — needs license note; do not claim MIT without confirmation |
| `csr_ai_x2.weights.json`, `csr_ai_fg.weights.json` | same | **Wrenby-owned weights** — license question for Wrenby |
| Preset assistant ONNX (`preset_*.onnx`, `preset_beta7.onnx`) | pub docs/chat, b6/b7, framefx-assistant | **Wrenby/assistant** — separate from engine FG; license note |
| Closed Windows **release** zips / publish trees with only bins | `/workspace/framefx/*.zip`, `ufx-fg8x/publish-*` | Binaries; source of truth is pcsrc/ufx-csrai — do not treat zip as OSS |

**Wrenby needed this pass?** **No** for Linux preview compile/selftest (no AI weights on Linux path). **Yes (doc only)** before advertising CSR AI weights as MIT or shipping them under the app LICENSE — ask Wrenby for a one-paragraph weight license (or “weights All Rights Reserved / research only”).

### 7.4 Replace `EngineNotIncludedException` / stub `FrameEngine.Create`

Private tree does **not** use `FrameEngine.Create`; it exposes concrete `Gpu` + `FramePipeline` (+ FG/upscale types). Public tree stubs those behind `EngineNotIncludedException`.

**Plan:**
1. Copy `/workspace/ufx-csrai/windows/src/UniversalFrameFX/` into public checkout (or sync via ilikemacos branch) under MIT.
2. Either:
   - **A (preferred):** implement `FrameEngine.Create` as a thin MIT factory that returns a real engine wrapper around existing types; delete throw-only stubs in `EngineTypes.cs` / `EngineContract.cs`, **or**
   - **B:** remove `Engine/` stub facade and point `OutputForm` at the real types (larger API churn).
3. Vendor DLLs stay optional load-at-runtime; app must run CSR paths without them.
4. AI paths: if weights absent, keep **honest disable/stub** (already a product rule) — compile without requiring Wrenby files.
5. Wyne owns engine release mechanics; report-first before pushing public engine drop.

**This run scope for OSS:** document inventory + fold into plan; Linux build/selftest first. Optional follow-up in same worktree: draft `plans/oss-engine-publish.md` checklist and a local mirror folder `/workspace/ufx-linux-desktop/oss-staging/` listing files to publish — **no force-push / no release** until parent approves.

### 7.5 Question for Wrenby (document only; do not block)

> Please confirm license for `Models/csr_ai_*.onnx`, `csr_ai_*.weights.json`, and `csr_ai_fg.weights.json`: MIT with the app, separate research license, or do-not-redistribute? Engine C#/HLSL that loads them can still go MIT either way.

---

## 8. Gaps / risks

* Lavapipe ≠ hardware quality; portal picker untested on this headless box.
* AppImage tooling (`linuxdeploy`) may be missing → ship `.tar.gz` runnable stage if needed.
* Windows vendor FSR2+/XeSS cannot become “open source”; only redistributable.
* Public stub replacement is a separate git publish from Linux AppImage; coordinate so README no longer says “engine not included” once sources land.
* CSR AI on Linux not in preview.1 — do not list as shipping.

## 9. Success criteria (this run)

- [x] Plan file exists (this document) with Linux + OSS sections
- [x] `/workspace/ufx-linux-desktop/` builds `ufx-linux`
- [x] `--version` and `--selftest` (Lavapipe) exit 0
- [x] README desktop Linux + honest limits
- [x] Report: version, artifacts, test summary, gaps vs Beta10, Wrenby needs (weights license only) — see parent final message


## Weight license decision (Mica via Nr. Wrenby, 2026-10-23 America/Vancouver)

**MIT with the app** for all FrameFX model artifacts Wrenby owns:
- `Models/csr_ai_*.onnx`
- `Models/csr_ai_*.weights.json`
- `Models/csr_ai_fg.weights.json`
- Preset-assistant ONNXes

Same as Universal-FrameFX `LICENSE`; redistribute freely. Advertise under the app LICENSE. Engine C#/HLSL/C++ MIT is fine alongside. No separate research / do-not-redistribute restriction. Wrenby ask closed.
