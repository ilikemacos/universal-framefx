---
title: FAQ
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [LarkquillAI](assistant.html) · [**💬 Chat with LarkquillAI**](chat/) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# FAQ

**Does frame generation raise my game's real fps?** No. It adds generated frames to what you see and adds about one captured frame of latency.

**Will it get me banned?** FrameFX doesn't inject into games; it captures the window. For anti-cheat-protected games it uses anti-cheat safe mode, and presets and the Assistant keep frame generation off for them. Always follow each game's rules.

**Why does SmartScreen warn me?** The exe isn't code-signed. Check the SHA-256 on the site, then "More info" → "Run anyway".

**Exclusive fullscreen?** FrameFX warns you; use borderless/windowed mode.

**Can I build it myself?** Yes. The full MIT engine is in this repository. On Windows use `dotnet build -c Release` (optional vendor DLLs under `vendor/bin`). Linux: see `linux/README.md`.

**Which AI does the Assistant use?** It's WrenbyAI, made for FrameFX and trained on a 2021 ROG Zephyrus G14. In the app it runs locally on your GPU; the website version needs an internet connection.

**My GPU is weak.** Try CSR 2.0's Lighter mode or the Performance preset. Ray-traced lighting works even without hardware RT.
