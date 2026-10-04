# Contributing to Universal-FrameFX

Thanks for helping! This repository is the open-source (MIT) Universal-FrameFX app: the UI, settings, game
detection, window capture setup, overlay window, updater and installer. The image-processing engine is closed
source and isn't part of this repository, so changes to upscaling or frame-generation quality can't be made here.
Bug reports about them are still welcome as issues.

## Reporting bugs

Open an issue at https://github.com/ilikemacos/universal-framefx/issues with:

- the FrameFX version (shown under the title in the main window, or `Universal-FrameFX.exe --version`)
- Windows version and GPU
- what you did, what you expected, and what happened
- relevant lines from `%LOCALAPPDATA%\Universal-FrameFX\crash.log` or `updates\update.log` (check them for
  anything private first)

Security problems: please don't open a public issue. See [SECURITY.md](SECURITY.md).

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Keep changes focused; one fix or feature per pull request.
3. Make sure it builds: `dotnet build -c Release` (on Linux/macOS add `-p:EnableWindowsTargeting=true`).
   The GitHub Actions build must pass.
4. If you change anything that touches the network, files, other processes or the desktop, update the
   "What FrameFX does and doesn't do on your PC" section of the README in the same pull request. That section
   must always match the code.
5. Don't add telemetry, analytics, ads or new network endpoints.

By contributing you agree that your contribution is licensed under the MIT licence in [LICENSE](LICENSE).
