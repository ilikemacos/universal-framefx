# Preset rules (implemented in rules.py `decide`; DPM = default pending Mica)
Sources: windows/src/UniversalFrameFX/GameProfiles.cs, VendorPath.cs, FramePipeline.cs, AntiCheat.cs, README 1.3.1; Oct 11 eval (csrai-fg/eval-fixes-oct11.md, devset/score_dev.md).
1. Base fps: if user states current fps, use it; else estimate = GPU score × (0.75 laptop) × res factor (1080 1.0 / 1440 0.65 / 4K 0.32 / unknown 0.8) × 6 / game_cost^1.1. Rough, editable in kb.py.
2. Target fps: stated, else 144 for competitive games, 120 otherwise.
3. Preset: Quality if "max quality" asked (and not battery), or desktop GPU score ≥45 with base ≥ target/2; else Performance. Never Competitive (DPM 4).
4. Frame gen OFF + warning for competitive or anti-cheat games (catalog from AntiCheat.cs) (DPM 4); OFF if user prioritises latency; OFF if base ≥ 0.9×target.
5. Otherwise FG = CSR 1.3 (FgSelect 1): best and cheapest at 3x/4x per Oct 11 eval (~0.12 ms est., less flicker). W18SWA/CSR AI FG never recommended (unshipped, 2x-only candidate). FSR 3 FG only if asked.
6. Multiplier = ceil(target/base) clamped to 2..4; "hate blur/ghosting" caps at 2x. Base <35 fps → artifact warning. HUD-heavy games → HUD shimmer note.
7. Upscaler: CSR 1.3 (Backend 0) by default (DPM 5). FSR1/2/3, XeSS, CSR 1.2 only if explicitly asked. FSR 4 (Backend 6) only on RX 9000 and when asked for FSR4 or max quality; otherwise explain unavailability.
8. Output res from stated monitor (1080→4, 1440→1, 4K→2), else Auto.
9. SSGI / ray-traced lighting / CPU boost only when explicitly asked (DPM 3). RT replaces SSGI.
10. Validator mirrors GameProfiles.Sanitize legal values; any invalid model output → rule engine answer from a keyword extractor.
