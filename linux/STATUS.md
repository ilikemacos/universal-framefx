# Status — 1.4.1-linux-preview.3 (box, 2026-10-23 America/Vancouver)

- Plan: `plans/linux-preview3-silent-exit.md`
- Silent-exit fixes for CachyOS/GNOME: fatal log + SDL message box, GPU
  pick (graphics+compute+present, prefer discrete), AppDir shader paths,
  Wayland composite-alpha fallback, AppRun/run.sh log capture (tee on TTY)
- Docs: `docs/TROUBLESHOOT.md` (fuse2, Vulkan drivers, GNOME portal, PipeWire)
- CSR AI upscale and SSGI are ported (Vulkan compute, CPU reference + numeric test).
  SSGI is off by default; toggle + strength in the panel, `--ssgi` for offline upscale.
- Unavailable: FSR2+ / XeSS / CSR AI FG.
- Game Mode: still experimental (unchanged path; hardware unverified)
- Blockers: `docs/BLOCKERS.md` (FSR2+/XeSS/CSR AI FG/Larkquill/DLSS)
- Build: Release OK → `build/ufx-linux`
- `--version` / `--selftest` (Lavapipe): PASS
- Numeric: easu_rcas, csr2, csr_ai, fg2, ssgi: PASS
- Artifacts under `dist/`:
  - AppImage sha256 `9bce5abfd77d6cf284c5d856a1513830eb4ba879902a7189ceb729a7940d6687`
  - zip sha256 `1a9753bb72ef3a018455a6cf8c45a0eccabf139fe50fd7fffd4cea60f8260371`
  - tar.gz sha256 `2b5118246c848437e70c24f4e6dde1f039483312f9b6a3d058ae027f14d2b725`
