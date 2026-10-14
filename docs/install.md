---
title: Install
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [FrameFX Assistant](assistant.html) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# Install

## PowerShell (recommended)
Per-user, no admin. The script checks the download's SHA-256 before installing.

```powershell
irm https://chopstickshq.com/universal-framefx/install.ps1 | iex
```

The script is [`installer/install.ps1`](https://github.com/ilikemacos/universal-framefx/blob/main/installer/install.ps1), so you can read it first. It installs to `%LOCALAPPDATA%\Programs\Universal-FrameFX` and adds a Start menu shortcut. Run it with `-Uninstall` to remove FrameFX.

## Zip
1. Download the .zip from [chopstickshq.com/universal-framefx](https://chopstickshq.com/universal-framefx/).
2. Unzip anywhere and run **Universal-FrameFX.exe**.
3. The exe isn't code-signed, so SmartScreen may warn on first launch ("More info" → "Run anyway"). The zip's SHA-256 is published on the site and in [latest.json](https://chopstickshq.com/universal-framefx/latest.json).

A separate ROG Ally / Ally X edition with Ally-tuned defaults is on the same site.

## Updates
FrameFX checks `latest.json` at startup and every 6 hours while "Automatically check for updates" is on. "Install updates automatically" is off by default.
