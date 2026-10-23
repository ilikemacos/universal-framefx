# Contributing to Universal-FrameFX

Thanks for helping! This repository is the open-source (MIT) Universal-FrameFX project: the Windows app and
image-processing engine, first-party `Models/` weights/ONNX files, the Linux Vulkan/Wayland port under `linux/`,
and optional SSGI sources under `ssgi/`.

Optional AMD FidelityFX / Intel XeSS **GPU DLLs** are third-party binary redistributables (see NOTICE.md). Do not
commit those DLLs or relicense them as MIT.

## Reporting bugs

Open an issue at https://github.com/ilikemacos/universal-framefx/issues with:

- the FrameFX version (shown under the title in the main window, or `Universal-FrameFX.exe --version`)
- Windows version and GPU (or Linux distro / GPU / compositor for `linux/`)
- what you did, what you expected, and what happened
- relevant lines from `%LOCALAPPDATA%\Universal-FrameFX\crash.log` or `updates\update.log` (check them for
  anything private first)

Security problems: please don't open a public issue. See [SECURITY.md](SECURITY.md).

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Keep changes focused; one fix or feature per pull request.
3. Make sure it builds: `dotnet build -c Release` (on Linux/macOS add `-p:EnableWindowsTargeting=true`).
   The GitHub Actions build must pass. For `linux/`, see `linux/README.md`.
4. If you change anything that touches the network, files, other processes or the desktop, update the
   "What FrameFX does and doesn't do on your PC" section of the README in the same pull request. That section
   must always match the code.
5. Don't add telemetry, analytics, ads or new network endpoints.

By contributing you agree that your contribution is licensed under the MIT licence in [LICENSE](LICENSE).
