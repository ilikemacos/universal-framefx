# Preset assistant design (draft, for Mica's review)
Input: free text ("Cyberpunk on a 3060 laptop, want 120 fps, hate blur"). Output: valid GameProfile JSON keyed by exe + 2-4 line explanation.
1. Model: from-scratch char-level (or small BPE, ~1k vocab) decoder transformer, ~2-4M params, 4 layers, d=256, ctx 512, PyTorch CPU. No pretrained weights, no APIs.
   Target sequence = compact slot form (e.g. `game=cyberpunk2077.exe|preset=1|fg=1|mul=2|backend=0|res=0|...||why: ...`), not raw JSON — shorter, easier to learn; a deterministic formatter emits the JSON.
2. Validator (rule-based, guaranteed): parse slots → port of GameProfiles.Sanitize + hard rules (FSR4 only on RX 9000, multiplier legality per FgSelect, warn on anti-cheat exes). Constrained decoding: at each slot only legal values may be sampled. If parse fails → fall back to rule engine answer. So output is always valid.
3. Data: generator.py samples game (catalog with genre/HUD-heavy/competitive/anti-cheat/exe), GPU (tiered table), laptop/desktop, base fps, target fps, monitor Hz, prefs (blur/latency/sharpness/battery), then rules.md engine computes the preset + explanation. Requests rendered with many phrasings, typos, slang, missing fields. ~50-100k pairs; held-out split by unseen games/GPUs + unseen phrasing templates.
   Key rules seeded from tonight's results: CSR 1.3 FG (FgSelect=1) default, best+cheapest at 3x/4x; multiplier ≈ ceil(target/base) clamped to {2,3,4}; competitive/latency-sensitive → Preset 2 or FG off; HUD-heavy → note HUD mask; "hate blur" → avoid high multipliers / Spatial-heavy upscale; W18SWA/CsrAi FG not recommended yet (2x-only candidate, unshipped).
4. Eval: valid-preset rate (pre-validator), exact-slot match vs rule engine, per-slot accuracy, explanation sanity. 5. UI: CLI chat; tiny stdlib http.server page if cheap.
Training: nice 19, torch threads 2 until 00:00, then up to 8. Disk use < 300 MB.

## Defaults pending Mica (DPM) — change in rules.py / assist.py
1. Prints the entry; `--write COPY.json [--src original]` merges into a copy only (refuses paths containing AppData).
2. Coverage: 63 games, 35 GPUs (GTX 1050 Ti–RTX 40, RX 6000/7000/9000, Arc) in kb.py.
3. CPU tweaks / SSGI / RT off unless explicitly asked.
4. Competitive + anti-cheat games: FG Off + warning (never Competitive 8×).
5. CSR 1.3 preferred; FSR/XeSS only when asked; FSR 4 on RX 9000 for max quality.
## Model size (Mica, 22:57): ~1M params → GPT d=128, 5 layers, 4 heads, ctx 448, char vocab 98 = 1,061,504 params.
