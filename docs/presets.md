---
title: Presets and Beta7
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [WyneAI](assistant.html) · [**💬 Chat with WyneAI**](chat/) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# AI presets (Beta7)

> Beta7 presets are being prepared for an upcoming beta and may not be in your build yet.

Describe what you want in plain words, for example *"Cyberpunk on my 3060, 1440p, make it quieter"*, and FrameFX builds a preset: game, GPU, upscaler, frame generation, fps and target.

## Model-first pipeline
1. **The preset model fills every field** from your request, including follow-ups like "what about 4K", "actually I have a 4070" or "make it quieter".
2. **Rules act as a safety check.** They drop invalid or out-of-range values (fps/target outside 10–500, target above fps, features you didn't ask for, game or GPU names that don't match a known one), and fill in only fields the model left empty or invalid.
3. **Anti-cheat is always enforced.** For anti-cheat-protected games, frame generation is turned off and you get a warning.

The model runs locally and is small (about 1 million parameters).

## Accuracy
On sealed test sets the team never trained on:

| Test | Beta7 model-first | Previous default | Rules only |
|---|---|---|---|
| 85 single requests | 0.965 | 0.929 | 0.918 |
| 50 single requests | 0.94 | — | — |
| 40 chat turns | 0.825 | 0.825 | 0.825 |
