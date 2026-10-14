---
title: FrameFX Assistant
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [FrameFX Assistant](assistant.html) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# FrameFX Assistant

The FrameFX Assistant is a chat assistant for FrameFX. It answers general questions, builds presets with the Beta7 pipeline and, in the app, can take actions for you, always with your confirmation.

## Try it on the web
[preset-ai-chat.vercel.app](https://preset-ai-chat.vercel.app) runs the Assistant on a cloud AI. Plain preset requests are handled instantly in your browser; chat and multi-step requests go to the cloud. It can build presets and copy them to your clipboard for FrameFX. Replies are limited in length.

## In the app (in development)
> The in-app Assistant is in development and not in a released build yet.

It runs **fully on your PC** (first-run download of about 1.9 GB, SHA-256 checked) using your GPU, with context sized to your GPU's free memory (up to 128k tokens) and a 256k-token budget per chat; long chats are summarized.

### Tools
| Tool | What it does | Needs your OK? |
|---|---|---|
| Web search | Searches the web (DuckDuckGo by default) | No |
| Fetch page | Reads a public web page as text | No |
| Get FrameFX state | Reads what's running | No |
| Make preset | Proposes a Beta7 preset | No |
| Apply preset | Saves a game profile, showing Before/After | **Yes** |
| Change setting | Changes an allowed setting, Before/After | **Yes** |
| Start / stop FrameFX | Starts or stops the output | **Yes** |
| Launch app | Starts a program you picked | **Yes** |

### Safety
- There is no shell tool. Launch app only starts `.exe` files you added with your own file picker, with no arguments, and refuses command shells and script hosts.
- Confirm dialogs default to Cancel; Enter doesn't confirm and closing the dialog means no. A declined action isn't retried.
- Frame generation can't be turned on for anti-cheat-protected games.
- Web content can only reach read-only tools; every change needs your click.
- At most 8 steps per request.
