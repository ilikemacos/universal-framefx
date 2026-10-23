# Universal-FrameFX — Linux Game Mode / gamescope

**Status:** experimental path in `1.4.1-linux-preview.2`.  
**Verified this run:** Lavapipe + unit detection only. **Not** verified on Steam Deck Game Mode hardware.

## What “Game Mode support” means here

SteamOS Game Mode runs under **gamescope**, a dedicated Wayland compositor. The Desktop Mode
`xdg-desktop-portal` ScreenCast picker is often missing or different there.

FrameFX therefore:

1. **Detects** Game Mode via `GAMESCOPE_WAYLAND_DISPLAY`, `XDG_CURRENT_DESKTOP=gamescope`,
   `XDG_SESSION_DESKTOP=gamescope`, `STEAMOS_SESSION=gamescope`, or `UFX_FORCE_GAMEMODE=1`.
2. **Prefers** attaching to gamescope’s PipeWire video source (node name matching `gamescope`,
   commonly `gamescope` / `gamescope:out_0`) on the default PipeWire remote.
3. **Falls back** to portal ScreenCast (SteamOS may ship a gamescope portal backend).
4. On total failure: clear status text + this doc. With `--capture-fail-soft`, opens a null
   (black) capture so the UI still runs.

This is **capture-only** (no process injection, no anti-cheat interaction), same as Desktop Mode.

## Nested gamescope / Steam non-Steam entry

From Desktop Mode, or as a Steam “non-Steam game” launch option:

```bash
# Next to the binary / inside the AppImage extract:
./scripts/ufx-gamescope-launch.sh
# or:
UFX_BIN=/path/to/ufx-linux ./scripts/ufx-gamescope-launch.sh --capture-fail-soft
```

If already inside gamescope, the script execs `ufx-linux` directly (no double-nest).

Steam launch example (adjust path):

```
/home/deck/Applications/Universal-FrameFX*.AppImage
```

or wrap with the launch script after extract.

## CLI

* `--session-info` — print detected session and exit 0
* `--capture-fail-soft` — if capture cannot start, continue with null capture
* `UFX_FORCE_GAMEMODE=1` — force Game Mode capture preference (for testing)

## Honest limits

* Hardware Game Mode QA is **unverified** on this preview box (software Vulkan / Lavapipe only).
* gamescope may not export a PipeWire node on every build; portal may still be required.
* Presenting FrameFX as a full-screen gamescope client is supported as a launch path; a
  transparent “overlay on top of an already-running game” without capture is **out of scope**.
* Do not treat a Lavapipe selftest pass as Deck Game Mode quality proof.
