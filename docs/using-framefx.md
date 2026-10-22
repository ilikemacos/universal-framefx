---
title: Using FrameFX
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [LarkquillAI](assistant.html) · [**💬 Chat with LarkquillAI**](chat/) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# Using FrameFX

The main window has a home page showing what's running, plus pages for Upscaling, Frame generation, Games, CPU, Advanced and About. Pick a source window and click **Apply**.

## Upscaling
- **CSR 1.3** (default) and **CSR 2.0** (edge-aware), FrameFX's own upscalers, with presets and a sharpness slider.
- **AMD FSR 1/2/3/4** and **Intel XeSS** where your GPU supports them.

## Frame generation
Settings → Frame generation offers:
- **Off**
- **CSR 1.3 frame generation (classic)**, the default
- **CSR 2.0 frame generation**, with Smooth pacing, Low-latency mode, Show cursor on generated frames and Lighter mode for weaker GPUs. Multipliers 2×–6×, 8×, 10× and 20× (20× is meant for 360 Hz+ displays).
- **CSR Flux BETA**, FrameFX's AI frame generation (experimental)
- **AMD FSR 3 frame generation**

Multipliers are 2×, 3×, 4× (8× advanced) for the others. Output is capped at your refresh rate and the multiplier is lowered automatically when needed. Frame generation never raises the game's real fps, adds about one captured frame of latency, steps down if the game's fps drops more than 5%, and pauses while the source already runs at ≥75% of your refresh rate.

## Presets and latency
Performance, Quality and Competitive presets, plus a latency budget that keeps added response time low.

## Apply to games only
FrameFX turns itself on for games and pauses on the desktop, browsers and normal apps.

## Ray-traced lighting (experimental)
Off by default. Adds extra light, reflections and contact shadows, including on cards without hardware ray tracing.

## CPU section
Off by default and remembered per game. Optional help while a game runs; it can't make the CPU faster than its hardware. Everything is undone when the game or FrameFX closes, or with Restore defaults. A quick benchmark compares before and after.

## Hotkeys (only while an output is running)
| Keys | Action |
|---|---|
| Ctrl+Alt+F | Hide / show the overlay |
| Ctrl+Alt+Q | Stop |
| Ctrl+Alt+C | Compare (FrameFX processing off) |

FrameFX shows its output fps next to the game's own fps, and warns when the game is in exclusive fullscreen.

## Anti-cheat safe mode
FrameFX recognizes games protected by anti-cheat (EasyAntiCheat, BattlEye, Riot Vanguard, FACEIT, Ricochet and others). For them it only captures the window, never touches the game, and leaves out CPU options that change priority or cores. The list is in Settings → Games, where you can add games or mark one as not protected.
