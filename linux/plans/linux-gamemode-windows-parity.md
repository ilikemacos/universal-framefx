# Universal-FrameFX Linux Game Mode + Windows Parity Plan

**Version Target:** 1.4.1-linux-preview.2  
**Current:** 1.4.1-linux-preview.1 (Desktop Mode portal+PipeWire; CSR 1.2, CSR 2.0, InterpFast FG)  
**Repository:** ilikemacos/universal-framefx `linux/` branch  
**Environment:** Lavapipe software Vulkan only — hardware/Game Mode testing unverified

---

## 1. Game Mode Architecture & Chosen Approach

### Architecture Context

**gamescope constraints:**
- Dedicated Wayland compositor, not a window manager
- Nested session model: runs as Wayland client OR root compositor
- SteamOS Game Mode: sets `GAMESCOPE_WAYLAND_DISPLAY`, `XDG_CURRENT_DESKTOP=gamescope`, `STEAM_DECK=1`
- xdg-desktop-portal ScreenCast backend availability **not guaranteed** in gamescope nested sessions
- Monitor capture from nested gamescope client → outer desktop session may work on some portal implementations
- Direct game window capture from within gamescope: requires game cooperation or compositor hooks (not portal)

**Failure modes in preview.1:**
- Portal ScreenCast silently fails or refuses connection under gamescope
- No Game Mode detection → same Desktop Mode path attempted → black screen or hang
- No user-actionable diagnostics

### Evaluated Options

**Option A: Block Game Mode entirely**  
- Pro: Honest about unsupported state  
- Con: No path forward, users cannot experiment on hardware  

**Option B: PipeWire node direct attach (bypass portal)**  
- Pro: May work when portal fails  
- Con: Requires enumerating nodes, guessing target (portal abstracts this), security/permission model unclear, high research cost for this slice  

**Option C: Gamescope launch wrapper + portal with graceful degradation**  
- Pro: Ships real Game Mode detection + helper tooling; works for Desktop→nested gamescope workflow; clear error path  
- Con: Unverified on hardware; portal may still fail  

### ✅ CHOSEN APPROACH (Option C)

**Detect Game Mode:**
```bash
GAMESCOPE_WAYLAND_DISPLAY set
XDG_CURRENT_DESKTOP=gamescope
STEAM_DECK=1 + gamescope process
```

**Capture strategy (preference order) — research update 2026-10-23:**
1. **If Game Mode / gamescope detected:** attach to gamescope's PipeWire video source
   (`gamescope:out_0` / node.name matching `gamescope`) on the default PipeWire remote
   (proven pattern used by Game Mode streamers; SteamOS may also expose a gamescope
   ScreenCast portal backend — try portal second).
2. **Else (Desktop Mode):** portal ScreenCast → PipeWire (existing path).
3. **If Game Mode and both PW-direct + portal fail:** actionable status + `docs/GAMEMODE.md`;
   `--capture-fail-soft` continues with null capture; else exit 78.

**Ship:**
- `scripts/ufx-gamescope-launch.sh`: Wrapper for nested gamescope invocation
  - Use case 1: Desktop Mode → launch UFX in nested gamescope window for testing
  - Use case 2: Steam non-Steam game entry template for Deck users
  - Sets up env, attempts portal from outer session if nested
- `docs/GAMEMODE.md`: Document exact usage, env vars, known working/failing configs, unverified status
- In-app status: If Game Mode detected, show "⚠️ Game Mode (experimental, unverified)" in UI

**Rationale:**
- Provides **real code path** for Game Mode, not vaporware
- Users on hardware can experiment and report findings
- Clear failure modes with actionable errors (not silent hangs)
- Launcher script enables Desktop→nested gamescope workflow immediately
- Does NOT claim "full support" — labels as experimental/unverified
- Future: PipeWire node fallback (Option B) remains as next iteration if portal insufficient

---

## 2. Windows→Linux Port Backlog (Prioritized)

### MUST (preview.2 blockers)

| Feature | Windows Beta10 Status | Linux preview.1 | Scope for preview.2 | Blocker |
|---------|---------------------|----------------|---------------------|---------|
| **Game Mode path** | N/A (Windows) | None | Ship detection + launcher + docs (Option C above) | Unverified without hardware; ship experimental path |
| **CSR AI (ONNX Runtime)** | CPU + DirectML | None | Wire ONNX Runtime CPU + Vulkan EP if redistributable libs exist; else document blocker | License check; Vulkan EP build availability |
| **Backend exposure in UI** | Hidden "Auto" | Hidden | Show actual backend: "PipeWire/DMA-BUF", "PipeWire/memcpy", "Game Mode (experimental)" | Transparency |
| **Null capture mode** | N/A | Crashes | `--capture-fail-soft` allows app to run without capture (for testing/Game Mode degrade) | Fail-safe for Game Mode |

### SHOULD (preview.2 targets if not blocked)

| Feature | Windows Beta10 | Linux preview.1 | Scope for preview.2 | Status |
|---------|---------------|----------------|---------------------|--------|
| **FSR2 Vulkan** | D3D11/12 via AMD SDK | None | Port via AMD FidelityFX SDK Vulkan path **only if** redistributable; no proprietary blobs | **RESEARCH REQUIRED**: Check SDK license for Vulkan libs |
| **XeSS Linux** | XeSS 1.3 D3D | None | **BLOCKED** until Intel releases Linux XeSS SDK | Deferred to later preview |
| **SSGI** | Custom compute | None | Port **only if** portable Vulkan compute; else defer | Assess complexity |
| **LarkquillAI panel** | Beta UI | Omitted | **SKIP** until real Linux panel implementation exists | No fake UI |

### LATER (post-preview.2)

- DLSS (requires NVIDIA Linux SDK; Lavapipe irrelevant)
- Game overlay mode (requires compositor hooks or Vulkan layer; research needed)
- HDR passthrough (needs HDR display on test hardware)
- Full compositor overlay / libei input injection

---

## 3. Version Bump Proposal

**From:** `1.4.1-linux-preview.1`  
**To:** `1.4.1-linux-preview.2`

**Rationale:**
- Patch-level preview increment for experimental Game Mode path
- Signals incomplete but forward progress
- Not `1.5.0` until Game Mode verified on hardware OR Windows parity substantially closed

**Tagging:**
```bash
git tag -a v1.4.1-linux-preview.2 -m "Game Mode experimental support + CSR AI wiring (unverified)"
```

---

## 4. Success Criteria & Out-of-Scope

### ✅ Success Criteria (preview.2)

1. **Game Mode detection active**: Env vars correctly identify gamescope sessions
2. **Launch script ships**: `scripts/ufx-gamescope-launch.sh` runs UFX in nested gamescope without crash
3. **Portal failure is non-silent**: Logs actionable error + docs link; `--capture-fail-soft` allows degrade
4. **CSR AI wired OR blocker documented**: Either ONNX Runtime Vulkan EP integrated, OR `docs/BLOCKERS.md` explains why not (license, lib unavailability)
5. **UI backend transparency**: Users see "PipeWire/DMA-BUF" or "Game Mode (experimental)" not "Auto"
6. **Docs honest**: `GAMEMODE.md` explicitly states "unverified on hardware; Lavapipe testing only"
7. **Selftest passes on Lavapipe**: Existing portal+PipeWire path unchanged; Game Mode path degrades gracefully
8. **No fake features**: LarkquillAI remains omitted; FSR2/XeSS only shipped if legally redistributable + working

### 🚫 Explicitly Out-of-Scope (preview.2)

- **Hardware verification**: Deck/gamescope testing deferred to users or future hardware access
- **Full Game Mode overlay**: Not claiming parity with Windows overlay mode
- **DLSS**: Requires NVIDIA Linux SDK + hardware
- **XeSS**: Blocked on Intel Linux release
- **LarkquillAI UI**: No panel implementation on Linux yet
- **Full Deck hardware QA**: Lavapipe-only verification this run
- **HDR**: No HDR display on test box
- **FSR2 if proprietary**: Will not ship if AMD SDK Vulkan path requires non-redistributable blobs

---

## 5. Implementation Checklist (This Run)

### Phase 1: Game Mode Detection & Infrastructure

- [ ] Add Game Mode detection to `src/platform/linux/session_detect.c`:
  - [ ] Check `GAMESCOPE_WAYLAND_DISPLAY` env var
  - [ ] Check `XDG_CURRENT_DESKTOP=gamescope`
  - [ ] Check `STEAM_DECK + gamescope` process
  - [ ] Return enum: `SESSION_DESKTOP`, `SESSION_GAMEMODE`, `SESSION_UNKNOWN`
- [ ] Wire detection into capture init path (`src/capture/pipewire_capture.c`):
  - [ ] Log detected session type at startup
  - [ ] Set internal `is_gamemode` flag
- [ ] Add `--capture-fail-soft` CLI flag:
  - [ ] If set + portal fails → continue with null capture backend
  - [ ] If unset + portal fails → exit 78 with error message
- [ ] Implement null capture backend (`src/capture/null_capture.c`):
  - [ ] Returns black frames or no-op on poll
  - [ ] Allows app to run for testing

### Phase 2: Gamescope Launch Tooling

- [ ] Write `scripts/ufx-gamescope-launch.sh`:
  ```bash
  #!/bin/bash
  # Launch UFX in nested gamescope for Desktop Mode testing
  # or as template for Steam non-Steam game entry
  ```
  - [ ] Detect if already in gamescope (avoid double-nesting)
  - [ ] Set recommended gamescope flags: `-w 1920 -h 1080 -W 1920 -H 1080 -f`
  - [ ] Pass through UFX CLI args
  - [ ] Exec `gamescope -- ./universal-framefx "$@"`
- [ ] Write `docs/GAMEMODE.md`:
  - [ ] Explain gamescope architecture
  - [ ] Document env vars detection uses
  - [ ] Usage: Desktop Mode nested launch
  - [ ] Usage: Steam non-Steam game template
  - [ ] Unverified status warning (Lavapipe only)
  - [ ] Troubleshooting: portal failures, `--capture-fail-soft` usage

### Phase 3: CSR AI (ONNX Runtime)

- [ ] **Research**: Check ONNX Runtime Vulkan Execution Provider availability:
  - [ ] License: MIT or compatible?
  - [ ] Redistributable libs: Can we ship `.so` or must user install?
  - [ ] Build from source feasible in CI?
- [ ] **If feasible**:
  - [ ] Add ONNX Runtime dependency to `CMakeLists.txt`
  - [ ] Wire Vulkan EP into `src/ai/csr_onnx.cpp` (port from Windows DirectML path)
  - [ ] Fallback to CPU EP if Vulkan EP init fails
  - [ ] Selftest with Lavapipe (expect slow perf, but functional)
- [ ] **If blocked**:
  - [ ] Document in `docs/BLOCKERS.md`:
    - Reason (license, lib unavailability, build complexity)
    - Upstream issue links if applicable
    - Fallback: users must build with system ONNX Runtime

### Phase 4: UI Backend Transparency

- [ ] Modify `src/ui/status_display.c`:
  - [ ] Replace "Auto" with actual backend string:
    - `"PipeWire/DMA-BUF"` if DMA-BUF active
    - `"PipeWire/memcpy"` if fallback copy
    - `"Game Mode (experimental)"` if gamescope detected
    - `"Null (--capture-fail-soft)"` if null backend
- [ ] Add warning icon `⚠️` for experimental backends in UI

### Phase 5: FSR2 (Conditional)

- [ ] **Research**: AMD FidelityFX SDK Vulkan path:
  - [ ] Clone SDK, check `LICENSE.txt`
  - [ ] Locate Vulkan FSR2 sample/libs
  - [ ] Verify redistributability (no proprietary `.so` blobs required)
- [ ] **If redistributable**:
  - [ ] Port FSR2 Vulkan from Windows D3D path
  - [ ] Integrate into `src/upscale/fsr2_vk.cpp`
  - [ ] Selftest on Lavapipe (expect initialization, maybe crashes due to SW limitations)
- [ ] **If blocked**: Document in `BLOCKERS.md`

### Phase 6: Testing & Polish

- [ ] Selftest on Lavapipe:
  - [ ] Desktop Mode (existing): portal+PipeWire still works
  - [ ] Game Mode path: detection triggers, portal fails gracefully, `--capture-fail-soft` works
  - [ ] Launcher script: nested gamescope launches without crash
- [ ] Update `README.md`:
  - [ ] Add Game Mode section: "Experimental (unverified on hardware)"
  - [ ] Link to `docs/GAMEMODE.md`
  - [ ] CSR AI status (working or blocked)
  - [ ] FSR2 status (working or blocked)
- [ ] Update `CHANGELOG.md`:
  ```markdown
  ## [1.4.1-linux-preview.2] - 2026-10-23
  ### Added
  - Game Mode detection (experimental, unverified)
  - scripts/ufx-gamescope-launch.sh for nested gamescope testing
  - --capture-fail-soft flag for graceful portal failure
  - Backend transparency in UI (shows actual capture method)
  - CSR AI ONNX Runtime Vulkan EP [if shipped] / documented blocker [if blocked]
  - FSR2 Vulkan upscaling [if shipped] / documented blocker [if blocked]
  
  ### Changed
  - Portal ScreenCast failures now log actionable errors (not silent)
  
  ### Known Issues
  - Game Mode support tested on Lavapipe only; hardware behavior unverified
  - XeSS blocked on Intel Linux SDK release
  - LarkquillAI panel omitted (no Linux implementation)
  ```
- [ ] Version bump: Update `CMakeLists.txt` and `version.h`:
  ```c
  #define UFX_VERSION "1.4.1-linux-preview.2"
  ```

---

## 6. Feature Matrix

| Feature | Windows Beta10 | preview.1 (current) | preview.2 (target) | Notes |
|---------|---------------|--------------------|--------------------|-------|
| **Capture** | DXGI Desktop Duplication | xdg-desktop-portal ScreenCast + PipeWire | Same + Game Mode detection | preview.2 adds gamescope env detection |
| **Zero-copy** | D3D11 textures | DMA-BUF (best-effort) | Same | Lavapipe: memcpy fallback |
| **Present** | D3D11 | SDL2 + Vulkan | Same | |
| **CSR 1.2 FG** | ✅ | ✅ | ✅ | Already shipped |
| **CSR 2.0 FG** | ✅ | ✅ | ✅ | Already shipped |
| **InterpFast FG** | ✅ | ✅ | ✅ | Already shipped |
| **CSR AI** | ✅ (CPU + DirectML) | ❌ | 🔶 (ONNX Vulkan EP or blocked) | Research dependent |
| **FSR2** | ✅ (D3D11/12) | ❌ | 🔶 (Vulkan or blocked) | License/redist dependent |
| **FSR3 FG** | ✅ | ⏸️ Deferred | ⏸️ Deferred | Low priority |
| **XeSS** | ✅ (1.3 D3D) | ❌ | ❌ (blocked) | Intel Linux SDK unreleased |
| **DLSS** | ✅ | ❌ | ❌ (deferred) | Requires NVIDIA Linux SDK + HW |
| **SSGI** | ✅ | ❌ | 🔶 (if portable compute) | Complexity assessment needed |
| **LarkquillAI panel** | ✅ | ❌ (omitted) | ❌ (omitted) | No fake UI; needs real impl |
| **Game Mode / overlay** | N/A (Desktop WM) | ❌ | 🧪 (experimental path) | Unverified; gamescope detection only |
| **UI backend display** | Hidden "Auto" | Hidden "Auto" | ✅ Shows actual backend | Transparency |
| **Capture degrade mode** | N/A | Crashes | ✅ `--capture-fail-soft` | Fail-safe |

**Legend:**  
✅ Shipped  
🔶 Conditional (research/license dependent)  
🧪 Experimental (unverified)  
❌ Blocked  
⏸️ Deferred  

---

## 7. Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Portal fails in all gamescope configs | High | Medium | Ship `--capture-fail-soft` + document PipeWire node fallback as next step |
| ONNX Runtime Vulkan EP unavailable/proprietary | Medium | Medium | Document blocker; ship CPU-only as fallback or defer CSR AI |
| FSR2 Vulkan requires proprietary blobs | Medium | Low | Document blocker; users still have InterpFast FG |
| Lavapipe crashes on new code paths | Medium | High | Extensive null-check; catch Vulkan validation errors; fail gracefully |
| Users expect "full support" from "preview.2" | Low | Medium | Clear labeling: "experimental", "unverified", docs explicit about Lavapipe-only testing |
| Launch script breaks on non-SteamOS distros | Medium | Low | Test on generic Arch/Ubuntu; document gamescope install prereq |

---

## 8. Post-preview.2 Roadmap (Not This Run)

1. **Hardware verification pass**: Test on real Deck in Game Mode; iterate on findings
2. **PipeWire node direct attach**: Fallback capture when portal unavailable (research needed)
3. **Game overlay mode**: Vulkan layer or compositor hook for in-game HUD (major research)
4. **XeSS**: Revisit when Intel ships Linux SDK
5. **DLSS**: Revisit when NVIDIA Linux SDK integrated + hardware available
6. **LarkquillAI**: Real panel implementation for Linux (not fake UI)
7. **HDR**: Requires HDR display + PipeWire HDR metadata support
8. **Version 1.5.0-linux-preview.1**: When Game Mode verified + major Windows parity closed

---

## 9. Documentation Deliverables

- `docs/GAMEMODE.md`: Architecture, usage, unverified status, troubleshooting
- `docs/BLOCKERS.md`: CSR AI, FSR2, XeSS blockers with upstream links
- `README.md`: Updated feature matrix + Game Mode section
- `CHANGELOG.md`: preview.2 additions
- `scripts/ufx-gamescope-launch.sh`: Commented wrapper with usage examples

---

**Next Actions:**
1. Implement Game Mode detection (Phase 1)
2. Research ONNX Runtime + FSR2 licenses (Phase 3, 5)
3. Ship launcher script + docs (Phase 2)
4. Selftest on Lavapipe (Phase 6)
5. Tag `v1.4.1-linux-preview.2` when checklist complete
