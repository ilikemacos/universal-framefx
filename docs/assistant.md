---
title: LarkquillAI
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [LarkquillAI](assistant.html) · [**💬 Chat with LarkquillAI**](chat/) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# LarkquillAI

LarkquillAI is the assistant built into FrameFX since **v1.4.1 Beta10**. It answers questions about FrameFX and builds, applies or changes presets for your games. FrameFX checks every change and asks you to confirm it.

## Set up
1. Install [v1.4.1 Beta10](https://github.com/ilikemacos/universal-framefx/releases/tag/v1.4.1-beta.10).
2. Get a free API key at [larkquillai.vercel.app/api](https://larkquillai.vercel.app/api).
3. Paste it into the LarkquillAI panel once. It's stored encrypted for your Windows account.

No key or no internet? The panel falls back to the offline preset model.

## Models
| Model | Plan |
|---|---|
| Quill Wren (fast) | Hobby (free) |
| Quill Scribe (default) | Hobby (free) |
| Quill Sage | Pro |
| Quill Sovereign | Max |

Locked models show an Upgrade link. You can also chat on the web at [larkquillai.vercel.app](https://larkquillai.vercel.app).

## LarkquillAI Local (experimental)
The old local assistant (formerly WrenbyAI) is now LarkquillAI Local. It runs on your PC with your GPU.

## Tools
| Tool | What it does | Needs your OK? |
|---|---|---|
| Get FrameFX state | Reads what's running | No |
| Make preset | Proposes a preset | No |
| Apply preset | Saves a game profile, showing Before/After | **Yes** |
| Change setting | Changes an allowed setting, Before/After | **Yes** |
| Start / stop FrameFX | Starts or stops the output | **Yes** |

## Safety
- Confirm dialogs default to Cancel; closing the dialog means no.
- Frame generation can't be turned on for anti-cheat-protected games.
- Every change needs your click.
