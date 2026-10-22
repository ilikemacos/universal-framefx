---
title: Install
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [LarkquillAI](assistant.html) · [**💬 Chat with LarkquillAI**](chat/) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# Install

Current version: **v1.4.1 Beta10** ([release page](https://github.com/ilikemacos/universal-framefx/releases/tag/v1.4.1-beta.10)).

## PowerShell (recommended)
Per-user, no admin. The script checks the download's SHA-256 before installing.

```powershell
irm https://github.com/ilikemacos/universal-framefx/releases/download/v1.4.1-beta.10/install.ps1 | iex
```

The script is [`install.ps1`](https://github.com/ilikemacos/universal-framefx/releases/download/v1.4.1-beta.10/install.ps1) (source: [`installer/install.ps1`](https://github.com/ilikemacos/universal-framefx/blob/main/installer/install.ps1)), so you can read it first. It installs to `%LOCALAPPDATA%\Programs\Universal-FrameFX` and adds a Start menu shortcut. Run it with `-Uninstall` to remove FrameFX.

## Zip
1. Download [Universal-FrameFX-1.4.1-beta.10-win-x64.zip](https://github.com/ilikemacos/universal-framefx/releases/download/v1.4.1-beta.10/Universal-FrameFX-1.4.1-beta.10-win-x64.zip) from the [Beta10 release](https://github.com/ilikemacos/universal-framefx/releases/tag/v1.4.1-beta.10).
2. Unzip anywhere and run **Universal-FrameFX.exe**.
3. The exe isn't code-signed, so SmartScreen may warn on first launch ("More info" → "Run anyway"). The zip's SHA-256 is in [Universal-FrameFX-1.4.1-beta.10-win-x64.zip.sha256](https://github.com/ilikemacos/universal-framefx/releases/download/v1.4.1-beta.10/Universal-FrameFX-1.4.1-beta.10-win-x64.zip.sha256). Check it with `Get-FileHash Universal-FrameFX-1.4.1-beta.10-win-x64.zip`.
4. The standalone [Universal-FrameFX.exe](https://github.com/ilikemacos/universal-framefx/releases/download/v1.4.1-beta.10/Universal-FrameFX.exe) is also attached to the release.

A separate ROG Ally / Ally X edition with Ally-tuned defaults stays on **v1.4.1 Beta4**, on [chopstickshq.com/universal-framefx](https://chopstickshq.com/universal-framefx/).

## Updates
FrameFX checks `latest.json` at startup and every 6 hours while "Automatically check for updates" is on. "Install updates automatically" is off by default.
