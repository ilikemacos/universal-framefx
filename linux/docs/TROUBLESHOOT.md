# Troubleshooting (Linux)

Focus: CachyOS / Arch + GNOME (Wayland). Most commands translate directly to other distros.

## Nothing happens when I double-click the AppImage

1. Check `~/.local/share/universal-framefx/last-run.log` first. Every run appends a
   `---- launch ... ----` line, and any fatal startup error is both logged there and
   (when a display is available) shown in an error message box. The AppImage launcher
   also appends the app's stdout/stderr to the same file.
2. If the log is empty or missing, the AppImage most likely never started. This is
   almost always FUSE:

   ```bash
   sudo pacman -S fuse2        # CachyOS/Arch: AppImages need fuse2 (not fuse3-only)
   ```

   or run without FUSE at all:

   ```bash
   ./Universal-FrameFX-*.AppImage --appimage-extract-and-run
   ```

   Running `squashfs-root/usr/bin/ufx-linux` directly after `--appimage-extract` also
   works: shaders are found at `../share/ufx-linux/shaders` without `UFX_SHADER_DIR`.

## GPU / Vulkan driver packages

Install the one matching your hardware:

```bash
sudo pacman -S vulkan-radeon      # AMD
sudo pacman -S vulkan-intel       # Intel iGPU
sudo pacman -S nvidia-utils       # NVIDIA (proprietary)
vulkaninfo --summary              # sanity check a usable ICD exists
```

On hybrid-GPU machines, note which GPU is `DISCRETE` vs `INTEGRATED` in `vulkaninfo`.
Since preview.3 the app checks every Vulkan device and prefers a discrete GPU when more
than one exposes a graphics+compute queue that can present to the window. If only the
iGPU can present, it is used; that is expected. If no device qualifies, the error says
so explicitly instead of exiting silently.

## Portal / screen capture

```bash
sudo pacman -S xdg-desktop-portal-gnome xdg-desktop-portal
```

GNOME's ScreenCast portal backend is required for capture. Without it the UI still
opens; clicking Start reports a status message rather than crashing.

## Audio / session plumbing

```bash
sudo pacman -S pipewire pipewire-pulse wireplumber
systemctl --user status pipewire pipewire-pulse wireplumber
```

## Still nothing / blank log

Run from a terminal to capture early dynamic-linker errors that happen before the app's
own logging:

```bash
./Universal-FrameFX-*.AppImage --appimage-extract-and-run 2>&1 | tee /tmp/ufx-debug.log
```

Note the AppImage launcher redirects the app's output into `last-run.log`, so check that
file too. Attach `/tmp/ufx-debug.log` and/or `~/.local/share/universal-framefx/last-run.log`
when reporting the issue.

## Lavapipe-only sanity check (no real GPU driver needed)

```bash
export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
./Universal-FrameFX-*.AppImage --appimage-extract-and-run --selftest
```

The `--selftest` output goes to `last-run.log` when launched through the AppImage; run
`squashfs-root/usr/bin/ufx-linux --selftest` after `--appimage-extract` to see it on the
terminal.
