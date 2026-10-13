# FrameFX per-game preset schema (source: ufx-csrai @ 9234d99, branch feature/csr-flux-v1)

A "preset" = one **GameProfile** entry in `%APPDATA%\Universal-FrameFX\ui.json` → `Profiles` (dict keyed by
lowercase exe name, e.g. `"cyberpunk2077.exe"`). Refs: windows/src/UniversalFrameFX/GameProfiles.cs (class + Sanitize),
MainForm.cs:2104-2175 (UiState, PathOf, Load migrations), CaptureUiProfile MainForm.cs:1320, ApplyProfileToUi :1360.
JSON is System.Text.Json default (PascalCase property names). Every load goes through `GameProfiles.Sanitize`, so the
validator must replicate it exactly.

| Field | Type / range | Meaning | Sanitize rule |
|---|---|---|---|
| Preset | 0 Performance, 1 Quality, 2 Competitive | Pipeline preset. Perf = CSR 1.3 low-cost fused path (README 1.3.1); Quality = 1.3 pipeline w/ hardware motion, full-res masks; Competitive = lean FG passes (FramePipeline.cs:133-136), Summary() says it always runs FG 8× | not 1/2 → 0 |
| FrameGen | bool | derived | = FgSelect != Off |
| FgSelect | 0 Off, 1 Csr13, 2 Csr20, 3 Fsr3, 4 CsrAi (VendorPath.cs:11) | frame-gen engine; -1 = unsaved | outside 0..4 → Csr13 if FrameGen else Off |
| FgMultiplier | 0 (Auto), 2,3,4,8; Csr20/CsrAi also 5,6,10,20 | output frames per source frame | GameProfiles.Mul: else → 4 |
| Ssgi | bool | experimental screen-space GI | – |
| SsgiPreset | 0 Auto, 1 GTX 1050 Ti, 2 GTX 980 Ti | SSGI cost tier | not 1/2 → 0 |
| SsgiTemporal | bool, default false (Ssgi.cs:17) | steadier lighting in motion | – |
| Ssrt | bool | experimental ray-traced lighting, replaces SSGI | – |
| SsrtPreset | 0/1/2 as SSGI | | not 1/2 → 0 |
| SsrtTemporal | bool, default false (Ssrt.cs:14) | | – |
| Backend | 0 Temporal(CSR 1.3), 1 Spatial(CSR 1.2), 2 Bilinear, 3 Fsr1, 4 Fsr2, 5 Fsr3, 6 Fsr4 (RX 9000 only), 7 XeSS, 8 Csr20, 9 CsrAi (FramePipeline.cs:7) | upscaler | undefined → 0 |
| Res | 0 Auto, 1 1440p, 2 4K, 3 Source(1:1), 4 1080p (OutputForm.cs:25) | output resolution | outside 0..4 → 0 |
| Cpu | CpuBoostSettings (CpuBoost.cs:6-31): PowerPlan, MinProcessorState100, DisableCoreParking, BoostMode 0/1/2, HighPriority (never Realtime), PreferFastCores, AvoidSmtSiblings, TimerResolution1ms, AllPhysicalCores, LowerFrameFxPriority, BackgroundProcesses[string], DisablePowerThrottling | per-game CPU tweaks; all off by default | CpuBoost.Sanitize (BoostMode not 1/2 → 0) |
| FgV2HudMask / W / H | string? / int 64 / int 36 | HUD mask for CSR 2.0 FG | W,H ≤0 → 64/36 |

Global-only (NOT per-profile, UiState): LatencyBudget (default on), FpsCap, FixedPacing, GpuChoice, FgV2Vsync/Extrap/LowResGen/Cursor.
Related: AntiCheat.cs has a built-in catalog of ~35 anti-cheat games (Valorant/Vanguard, Fortnite/EAC, CS2/VAC, ...) → assistant must warn on these.
