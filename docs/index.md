---
title: Overview
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [WyneAI](assistant.html) · [**💬 Chat with WyneAI**](chat/) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# Universal-FrameFX

> **[💬 Chat with WyneAI →](chat/)** WyneAI is a coding assistant (also FrameFX): it writes and runs code in your browser, makes files, and builds game presets. Works anywhere GitHub works; if the AI server is blocked it falls back to basic mode.

Universal-FrameFX is a **free real-time upscaler and frame generator for Windows 10/11**. It captures a game or app window through Windows' own capture API and draws a sharper, smoother picture in a click-through overlay. Nothing is injected into the game.

- No account, no telemetry. The only network request is the update check.
- Upscaling with FrameFX's own CSR options, or AMD FSR 1/2/3/4 and Intel XeSS where your GPU supports them.
- Frame generation: CSR 1.3 (classic, default), CSR 2.0 and CSR Flux BETA (AI), plus AMD FSR 3 frame generation.
- "Apply to games only", Performance / Quality / Competitive presets, and an anti-cheat safe mode.
- AI presets (Beta7) and the **WyneAI** (in development).

**Requires:** Windows 10 version 2004 (build 19041) or later, 64-bit, a DirectX 11 GPU.

Official site and downloads: [chopstickshq.com/universal-framefx](https://chopstickshq.com/universal-framefx/)

## Open source, with one closed part
The app (UI, game detection, capture setup, overlay, updater, installer) is MIT-licensed and in this repository. The image-processing engine (upscaling, frame generation, image effects) is proprietary and not in the repository. Building from source gives the app with a placeholder engine.
