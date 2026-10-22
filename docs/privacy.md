---
title: Privacy and safety
---
[Home](./) · [Install](install.html) · [Using FrameFX](using-framefx.html) · [Presets & Beta7](presets.html) · [LarkquillAI](assistant.html) · [**💬 Chat with LarkquillAI**](chat/) · [Privacy & safety](privacy.html) · [FAQ](faq.html) · [Changelog](changelog.html)

# Privacy and safety

- FrameFX makes no network connections apart from the updater ([Updater.cs](https://github.com/ilikemacos/universal-framefx/blob/main/Updater.cs)). No analytics, telemetry, crash uploads, accounts or ads.
- The update check is one HTTPS GET of `latest.json` that sends only your IP, standard headers and a `Universal-FrameFX/<version>` User-Agent. Turn off "Automatically check for updates" and FrameFX makes no requests unless you click "Check now".
- Non-HTTPS update URLs are refused. Downloads are checked against the published SHA-256.
- FrameFX needs no admin rights; some CPU power options may ask for approval.
- The closed engine contains no networking code.
- The website version of WrenbyAI sends your chat over the internet to generate replies, and its web searches go through the site's server; the in-app version runs locally and only goes online for web search/fetch.

Security reports: see [SECURITY.md](https://github.com/ilikemacos/universal-framefx/blob/main/SECURITY.md).
