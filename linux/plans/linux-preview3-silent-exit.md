# Linux preview.3 — fix silent instant-exit on CachyOS + GNOME (Mica)

**Symptom:** `Universal-FrameFX-1.4.1-linux-preview.2-linux-x86_64.AppImage` opens
and closes instantly on a CachyOS + GNOME/Wayland box ("Mica") with zero visible
error. No window, no dialog, nothing in a terminal if launched from a file manager.

**Root cause class:** every failure path in this codebase is `fprintf(stderr, ...); return 1;`
(or just `exit`). Launched from GNOME's app grid there is no terminal attached to see
stderr, so *any* early failure — bad GPU pick, missing shaders, missing FUSE, bad
composite alpha — looks identical: the app flashes and disappears. This plan fixes the
observability gap and the specific failure modes most likely to be hit on a modern
Mesa/Wayland desktop (iGPU+dGPU laptop or desktop, GNOME portal/pipewire stack).

This is a planning document only. No source has been modified yet.

---

## Confirmed findings (read directly from the tree at `/workspace/ufx-linux-desktop`)

1. **Fatal UX** — `src/ui/main.cpp` has 9 early-return failure sites between
   `SDL_Init` (line 210) and the main loop (line 338), lines 211, 224, 228, 234,
   238, 242, 248, 253, 258 — every one is `fprintf(stderr, ...); return 1;` with no
   UI, no file log. Desktop/AppImage launch shows nothing. **Confirmed.**

2. **GPU selection** — `src/gpu/vk_context.cpp:99`:
   ```cpp
   ctx->phys = physs[0];
   ```
   `vk_device_create()` unconditionally takes `physs[0]` from
   `vkEnumeratePhysicalDevices`, then *afterward* searches queue families on that
   one device for compute+present(+graphics) (lines 103-129), falling back to
   first compute-capable family *without present support* if none found (lines
   122-129, `found` can stay `false` and a non-presenting family gets used
   anyway). On a multi-GPU machine where the iGPU enumerates first (common on
   CachyOS with hybrid graphics) and the iGPU can't present to the surface SDL
   created (or has no Vulkan-capable driver reporting present support
   correctly), swapchain/device setup silently uses a bad queue family or the
   wrong device, and callers (`vk_window_init_swapchain`,
   `vk_device_create` itself) fail. Combined with finding #1, this is an instant,
   silent exit. **Confirmed — exact line matches hypothesis.**

3. **AppImage FUSE** — not in-tree (runtime environment issue), but
   `scripts/package-appimage.sh` already works around missing `/dev/fuse` for
   *linuxdeploy* (lines 108-121) and does **not** document
   `--appimage-extract-and-run` for end users running the *shipped* AppImage
   itself. On Arch/CachyOS, `fuse2` (not fuse3) is what AppImages historically
   need and it's frequently not installed by default. **Plausible root cause for
   some CachyOS installs; needs a documented workaround since we can't bundle
   FUSE.**

4. **Shader discovery** — `src/gpu/vk_context.cpp` `vk_find_shader_dir()`
   (lines 18-49). Candidates relative to the binary are only
   `"/shaders"`, `"/../shaders"`, `"/../src/shaders"` (line 32). The AppImage
   layout installs the shaders at `usr/share/ufx-linux/shaders` (see
   `scripts/package-appimage.sh` lines 9, 17) and relies on `AppRun`/the
   `ufx-linux-wrapper` setting `UFX_SHADER_DIR` *before* exec'ing the binary
   (lines 28-34, 48-52 of the packaging script) — so today this only works
   because the wrapper always sets the env var. If a user runs the extracted
   `usr/bin/ufx-linux` directly (e.g. after `--appimage-extract`, which is
   exactly the FUSE workaround in finding #3) *without* going through `AppRun`,
   `UFX_SHADER_DIR` is unset and `vk_find_shader_dir` has no candidate for
   `../share/ufx-linux/shaders` or `../../share/ufx-linux/shaders`. **Confirmed
   gap — directly undermines the FUSE workaround we're about to document.**

5. **Composite alpha** — `src/gpu/vk_window.cpp:99`:
   ```cpp
   sci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
   ```
   hardcoded, no capability check against `caps.supportedCompositeAlpha` (the
   `VkSurfaceCapabilitiesKHR caps` queried at line 44 is otherwise used but this
   field is ignored), and no fallback/retry on `vkCreateSwapchainKHR` failure
   (line 102 just returns `false`). Some Wayland/GNOME Mutter + Mesa
   combinations only advertise `PRE_MULTIPLIED`/`INHERIT`/`POST_MULTIPLIED` for
   certain surface configs. **Plausible — real gap, no fallback exists today.**

---

## Fixes to implement (in order, each independently testable)

### Fix 1 — Fatal UX: log file + message box on every early exit

**File:** `src/ui/main.cpp`

Add near the top (after existing includes, before `load_notice()`):

```cpp
#include <cstdlib>
#include <sys/stat.h>

static std::string ufx_log_path() {
    const char* home = std::getenv("HOME");
    std::string base = home ? std::string(home) : "/tmp";
    std::string dir = base + "/.local/share/universal-framefx";
    // mkdir -p (two levels under HOME is enough; HOME and .local/share already exist)
    mkdir((base + "/.local").c_str(), 0755);
    mkdir((base + "/.local/share").c_str(), 0755);
    mkdir(dir.c_str(), 0755);
    return dir + "/last-run.log";
}

static void ufx_fatal(const std::string& msg, bool sdl_initialized) {
    std::string path = ufx_log_path();
    std::ofstream f(path, std::ios::app);
    if (f) {
        std::time_t t = std::time(nullptr);
        char tbuf[64];
        std::strftime(tbuf, sizeof(tbuf), "%Y-%m-%d %H:%M:%S", std::localtime(&t));
        f << "[" << tbuf << "] " << UFX_PRODUCT_NAME << " " << UFX_VERSION_STRING
          << " FATAL: " << msg << "\n";
    }
    std::fprintf(stderr, "%s\n", msg.c_str());
    std::string box_msg = msg + "\n\nLog: " + path +
                          "\nPaste this file's contents when reporting the issue.";
    if (!sdl_initialized) {
        // Try a minimal SDL_Init just for the message box; ignore failure.
        if (SDL_Init(SDL_INIT_VIDEO) == 0) sdl_initialized = true;
    }
    if (sdl_initialized) {
        SDL_ShowSimpleMessageBox(SDL_MESSAGEBOX_ERROR,
                                 UFX_PRODUCT_NAME " failed to start", box_msg.c_str(), nullptr);
    }
}
```

Replace every early-return failure path in `main()` with a call to `ufx_fatal(...)`
followed by `return 1;`. Concretely (line numbers from current file):

| Line | Current | Replace with |
|------|---------|---------------|
| 211-212 | `fprintf(stderr, "SDL_Init failed: %s\n", ...); return 1;` | `ufx_fatal(std::string("SDL_Init failed: ") + SDL_GetError(), false); return 1;` |
| 224-225 | shaders not found | `ufx_fatal("Shaders not found. Set UFX_SHADER_DIR or reinstall.", true); return 1;` |
| 228-229 | `vk_instance_create` err | `ufx_fatal("Vulkan instance: " + err, true); return 1;` |
| 234-235 | control window failed | `ufx_fatal(std::string("Window creation failed: ") + SDL_GetError(), true); return 1;` |
| 238-239 | `vk_device_create` err | `ufx_fatal("Vulkan device: " + err, true); return 1;` |
| 242-243 | control swapchain failed | `ufx_fatal("Swapchain creation failed (see log for GPU/compositor details).", true); return 1;` |
| 248-249 | CSR pipeline err | `ufx_fatal("CSR 1.2 pipeline: " + err, true); return 1;` |
| 253-254 | CSR2 pipeline err | `ufx_fatal("CSR 2.0 pipeline: " + err, true); return 1;` |
| 258-259 | FG2 pipeline err | `ufx_fatal("Frame-gen pipeline: " + err, true); return 1;` |

Note `SDL_Init` has already happened by line 210 for all cases except the
`SDL_Init failed` case itself, so `sdl_initialized=true` is correct for every
later site; only the first call passes `false`.

Also log the version string unconditionally at process start (first line of
`main()`) so `last-run.log` always has at least one line even if nothing fails
visibly later but the process is killed (e.g. OOM, driver abort):

```cpp
{
    std::ofstream f(ufx_log_path(), std::ios::app);
    if (f) f << "---- launch " << UFX_PRODUCT_NAME << " " << UFX_VERSION_STRING << " ----\n";
}
```

Place this right after entering `main()`, before argv parsing.

**Needed includes:** `<ctime>`, `<sys/stat.h>` (new); `<fstream>` already included.

### Fix 2 — GPU selection: don't blindly take `physs[0]`

**File:** `src/gpu/vk_context.cpp`, function `vk_device_create` (lines 90-131).

Replace the single-device logic with a loop over *all* physical devices that
scores each one and requires a queue family with graphics+compute+present
(present only required when `present_surface != VK_NULL_HANDLE`, matching the
headless selftest path at `vk_context_init_headless`):

```cpp
bool vk_device_create(VkContext* ctx, VkSurfaceKHR present_surface, std::string* err) {
    uint32_t nphys = 0;
    vkEnumeratePhysicalDevices(ctx->instance, &nphys, nullptr);
    if (nphys == 0) {
        if (err) *err = "No Vulkan physical devices";
        return false;
    }
    std::vector<VkPhysicalDevice> physs(nphys);
    vkEnumeratePhysicalDevices(ctx->instance, &nphys, physs.data());

    struct Candidate {
        VkPhysicalDevice phys;
        uint32_t queue_family;
        VkPhysicalDeviceProperties props;
    };
    std::vector<Candidate> candidates;

    for (auto p : physs) {
        uint32_t nq = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(p, &nq, nullptr);
        std::vector<VkQueueFamilyProperties> qf(nq);
        vkGetPhysicalDeviceQueueFamilyProperties(p, &nq, qf.data());
        for (uint32_t i = 0; i < nq; ++i) {
            const bool compute = (qf[i].queueFlags & VK_QUEUE_COMPUTE_BIT) != 0;
            const bool graphics = (qf[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0;
            VkBool32 present = VK_TRUE;
            if (present_surface) {
                vkGetPhysicalDeviceSurfaceSupportKHR(p, i, present_surface, &present);
            }
            if (compute && graphics && present) {
                VkPhysicalDeviceProperties props{};
                vkGetPhysicalDeviceProperties(p, &props);
                candidates.push_back({p, i, props});
                break; // first qualifying family on this device is enough
            }
        }
    }

    if (candidates.empty()) {
        if (err) *err = present_surface
            ? "No Vulkan device exposes a graphics+compute queue that can present "
              "to this window (checked all physical devices)."
            : "No Vulkan device exposes a graphics+compute queue.";
        return false;
    }

    // Prefer DISCRETE_GPU, then INTEGRATED_GPU, then whatever's left; first
    // match in enumeration order within a tier wins (stable, no "random" pick).
    auto tier = [](VkPhysicalDeviceType t) {
        switch (t) {
            case VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU: return 0;
            case VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU: return 1;
            case VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU: return 2;
            case VK_PHYSICAL_DEVICE_TYPE_CPU: return 3;
            default: return 4;
        }
    };
    std::stable_sort(candidates.begin(), candidates.end(), [&](const Candidate& a, const Candidate& b) {
        return tier(a.props.deviceType) < tier(b.props.deviceType);
    });

    ctx->phys = candidates.front().phys;
    ctx->queue_family = candidates.front().queue_family;
    ctx->props = candidates.front().props;
    ctx->device_name = ctx->props.deviceName;

    // ... rest of function (extension enumeration, feature query, device
    // create, command pool create) stays as-is, just delete the old
    // "find queue family on ctx->phys" block (old lines 103-129) since
    // ctx->queue_family is already set above.
```

Add `#include <algorithm>` to `src/gpu/vk_context.cpp` for `std::stable_sort`
(not currently included — currently only pulls it in transitively, confirm at
implementation time).

This also fixes the silent-fallback bug in the old code where `found` could
stay `false` and a non-presenting queue family got used anyway (old lines
122-129) — that path no longer exists.

### Fix 3 — Shader path discovery for AppImage layout

**File:** `src/gpu/vk_context.cpp`, function `vk_find_shader_dir` (lines 18-49).

Extend the `cands` array at line 32 to also check the AppImage `usr/bin` →
`usr/share/ufx-linux/shaders` layout, so a direct run of the extracted
`usr/bin/ufx-linux` (bypassing `AppRun`/the wrapper, e.g. after
`--appimage-extract`) still finds shaders without `UFX_SHADER_DIR`:

```cpp
const char* cands[] = {
    "/shaders", "/../shaders", "/../src/shaders",
    "/../share/ufx-linux/shaders",       // AppDir usr/bin -> usr/share/ufx-linux/shaders
    "/../../share/ufx-linux/shaders",    // extra nesting safety
};
```

No other change needed in this function; `UFX_SHADER_DIR` env check (lines
19-24) still takes priority, and the wrapper/AppRun continue to set it as
belt-and-suspenders.

### Fix 4 — AppRun / run.sh logging + FUSE documentation

**File:** `scripts/package-appimage.sh`

1. `ufx-linux-wrapper` heredoc (lines 28-34) and `AppRun` heredoc (lines 48-51):
   tee stdout+stderr into the same `~/.local/share/universal-framefx/last-run.log`
   that `ufx_fatal()` writes to (Fix 1), so a crash *before* any C++ log line is
   written (segfault, missing `.so`, dynamic linker error) is still captured.
   New `AppRun`:

   ```sh
   #!/bin/sh
   HERE="$(dirname "$(readlink -f "$0")")"
   export UFX_SHADER_DIR="${UFX_SHADER_DIR:-$HERE/usr/share/ufx-linux/shaders}"
   cd "$HERE/usr/share/ufx-linux" 2>/dev/null || true
   LOGDIR="$HOME/.local/share/universal-framefx"
   mkdir -p "$LOGDIR"
   exec "$HERE/usr/bin/ufx-linux" "$@" 2>&1 | tee -a "$LOGDIR/last-run.log"
   ```

   Same pattern for `ufx-linux-wrapper` (lines 28-34) using
   `$HERE/../share/ufx-linux` as cwd.

   Note: piping through `tee` loses the exit code of `ufx-linux` (the pipeline's
   exit status becomes `tee`'s). If `set -o pipefail` is not already in effect in
   whatever shell runs this (it's `#!/bin/sh`, not guaranteed to support
   `pipefail`), use:
   ```sh
   "$HERE/usr/bin/ufx-linux" "$@" > >(tee -a "$LOGDIR/last-run.log") 2>&1
   ```
   only if bash is guaranteed; since `AppRun` is POSIX `sh`, prefer capturing the
   exit code explicitly:
   ```sh
   "$HERE/usr/bin/ufx-linux" "$@" 2>&1 | tee -a "$LOGDIR/last-run.log"
   exit "${PIPESTATUS[0]:-$?}"
   ```
   `PIPESTATUS` is bash-only; since the shebang is `/bin/sh`, the simplest
   POSIX-correct approach is to not pipe at all and instead rely on the app's own
   `ufx_fatal()` log writer (Fix 1) for the common case, and only redirect
   (not pipe) for the catch-all:
   ```sh
   exec "$HERE/usr/bin/ufx-linux" "$@" >> "$LOGDIR/last-run.log" 2>&1
   ```
   This is simpler, preserves the exit code via `exec`, and still captures
   dynamic-linker-level failures. **Use this form, not a tee pipeline.**

2. `run.sh` heredoc (lines 74-80): same append-redirect addition:

   ```sh
   #!/bin/sh
   HERE="$(dirname "$(readlink -f "$0")")"
   export LD_LIBRARY_PATH="$HERE/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
   export UFX_SHADER_DIR="$HERE/shaders"
   LOGDIR="$HOME/.local/share/universal-framefx"
   mkdir -p "$LOGDIR"
   exec "$HERE/ufx-linux" "$@" >> "$LOGDIR/last-run.log" 2>&1
   ```

3. Ship `docs/TROUBLESHOOT.md` (new, Fix 6 below) into both the AppDir and the
   stage dir, same pattern as the existing `docs/` copy:
   - AppDir: already copied wholesale via `cp -a "$ROOT/docs/." "$APPDIR/usr/share/ufx-linux/docs/"` (line 22) — no script change needed once the file exists in `docs/`.
   - Stage: already copied wholesale via `cp -a "$ROOT/docs/." "$STAGE/docs/"` (line 62) — same, no script change needed.
   - Confirm both globs pick up the new file (they use `cp -a dir/.` so yes).

4. Bump `VERSION` default at line 4 from `1.4.1-linux-preview.2` to
   `1.4.1-linux-preview.3` (Fix 6).

### Fix 5 — Swapchain composite-alpha fallback

**File:** `src/gpu/vk_window.cpp`, function `vk_window_init_swapchain` (lines 43-179).

Replace the hardcoded `sci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;`
(line 99) with a capability-checked pick, and retry swapchain creation with the
next candidate on failure instead of giving up after one `vkCreateSwapchainKHR`
call (line 102):

```cpp
VkCompositeAlphaFlagBitsKHR alpha_order[] = {
    VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
    VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR,
    VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR,
    VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR,
};
VkResult create_result = VK_ERROR_INITIALIZATION_FAILED;
for (auto alpha : alpha_order) {
    if (!(caps.supportedCompositeAlpha & alpha)) continue;
    sci.compositeAlpha = alpha;
    create_result = vkCreateSwapchainKHR(ctx->device, &sci, nullptr, &win->swapchain);
    if (create_result == VK_SUCCESS) break;
}
if (create_result != VK_SUCCESS) return false;
```

This both (a) picks a *supported* mode up front via `caps.supportedCompositeAlpha`
(already queried at line 44-45, just unused today) instead of assuming OPAQUE is
always supported, and (b) retries with the next supported mode if creation still
fails for an unrelated reason. Order matches the hypothesis (OPAQUE first, then
PRE_MULTIPLIED, then INHERIT) plus POST_MULTIPLIED for completeness.

No signature change needed; `win->swapchain` reset to `VK_NULL_HANDLE` is not
required since we only assign it on the successful branch (`vkCreateSwapchainKHR`
leaves the handle untouched on failure per spec, and `*out = {}`-equivalent
zero-init already happened via `SdlVkWindow win{}` at the call site in
`main.cpp`).

### Fix 6 — Version bump to 1.4.1-linux-preview.3

Bump in every location that currently says `1.4.1-linux-preview.2` (or, for
`NOTICE.md`, the stale `1.4.1-linux-preview.1`):

| File | Line(s) | Current | New |
|------|---------|---------|-----|
| `src/ufx_version.h` | 3 | `"1.4.1-linux-preview.2"` | `"1.4.1-linux-preview.3"` |
| `scripts/package-appimage.sh` | 4 | `VERSION="${UFX_VERSION:-1.4.1-linux-preview.2}"` | `...preview.3` |
| `README.md` | 9, 45 | `**1.4.1-linux-preview.2**`, `` `Universal-FrameFX 1.4.1-linux-preview.2` `` | `...preview.3` |
| `STATUS.md` | 1 | `# Status — 1.4.1-linux-preview.2 (...)` | `...preview.3`, update date/sha256s after rebuild+repackage (see Verification) |
| `NOTICE.md` | 3 | `` `1.4.1-linux-preview.1` `` | `` `1.4.1-linux-preview.3` `` (currently wrong even for preview.2 — fix the drift) |

STATUS.md will also need its "Artifacts under dist/" sha256 block regenerated
after rebuilding/repackaging under the new version string — do this as the last
step, after Fixes 1-5 are built and the new AppImage/zip/tar.gz are produced.

---

## New file: `docs/TROUBLESHOOT.md`

Create this file (shipped into both AppDir and the stage/tar via the existing
`cp -a docs/.` lines in `scripts/package-appimage.sh`, see Fix 4.3). Contents
should cover, concretely for CachyOS/Arch + GNOME:

- **Nothing happens when I double-click the AppImage**
  - Check `~/.local/share/universal-framefx/last-run.log` first — every run now
    appends a line, and any fatal error is both logged and (when possible) shown
    in a message box.
  - If the log is empty/missing entirely, the AppImage likely never started —
    this is almost always FUSE:
    ```bash
    sudo pacman -S fuse2        # CachyOS/Arch: AppImages need fuse2 (not fuse3-only)
    ```
    or run without FUSE at all:
    ```bash
    ./Universal-FrameFX-*.AppImage --appimage-extract-and-run
    ```
- **GPU / Vulkan driver packages** (install the one matching your hardware):
  ```bash
  sudo pacman -S vulkan-radeon      # AMD
  sudo pacman -S vulkan-intel       # Intel iGPU
  sudo pacman -S nvidia-utils       # NVIDIA (proprietary)
  vulkaninfo --summary              # sanity check a usable ICD exists
  ```
  On hybrid-GPU laptops, note which GPU is "DISCRETE" vs "INTEGRATED" in
  `vulkaninfo`'s output — preview.3 prefers the discrete GPU automatically when
  both expose a presentable graphics+compute queue (see Fix 2); if only the
  iGPU can present, that's expected and fine.
- **Portal / screen capture**
  ```bash
  sudo pacman -S xdg-desktop-portal-gnome xdg-desktop-portal
  ```
  GNOME's ScreenCast portal backend is required for capture; without it, the UI
  still opens (Fix 1 ensures *startup* no longer depends on capture) but
  clicking Start will fail with a status message, not a crash.
- **Audio/session plumbing**
  ```bash
  sudo pacman -S pipewire pipewire-pulse wireplumber
  systemctl --user status pipewire pipewire-pulse wireplumber
  ```
- **Still nothing / blank log**: run from a terminal to capture early dynamic
  linker errors that predate our own logging:
  ```bash
  ./Universal-FrameFX-*.AppImage --appimage-extract-and-run 2>&1 | tee /tmp/ufx-debug.log
  ```
  Attach `/tmp/ufx-debug.log` (or `~/.local/share/universal-framefx/last-run.log`)
  when reporting the issue.
- **Lavapipe-only sanity check** (no real GPU driver needed):
  ```bash
  export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
  ./Universal-FrameFX-*.AppImage --appimage-extract-and-run --selftest
  ```

Also add a short "Troubleshooting" pointer section to `README.md` linking to
`docs/TROUBLESHOOT.md`, and mention `--appimage-extract-and-run` there next to
the existing `## Build` / `## CLI` sections.

---

## Files touched (summary)

- `src/ui/main.cpp` — `ufx_fatal()` helper + log-path helper; replace 9 early
  returns; log line at process start.
- `src/gpu/vk_context.cpp` — rewrite `vk_device_create` physical-device
  selection loop; extend `vk_find_shader_dir` candidate list.
- `src/gpu/vk_window.cpp` — composite-alpha capability check + retry loop in
  `vk_window_init_swapchain`.
- `scripts/package-appimage.sh` — `AppRun` / `ufx-linux-wrapper` / `run.sh`
  log redirection; version bump.
- `docs/TROUBLESHOOT.md` — new file.
- `README.md` — version string (x2), new Troubleshooting section pointer,
  `--appimage-extract-and-run` mention.
- `STATUS.md` — version, date, regenerated artifact sha256s (after rebuild).
- `NOTICE.md` — version string fix (was already stale at preview.1).
- `src/ufx_version.h` — version string.

No changes to `src/gpu/vk_context.h` struct layout are required — `VkContext`
already has the fields needed (`phys`, `queue_family`, `props`, `device_name`).

---

## Verification plan

1. **Build:** `./scripts/build.sh` (Release) — must succeed with no new
   warnings treated as errors from the `vk_context.cpp` / `vk_window.cpp`
   changes (watch for `-Wswitch` on the new `tier()` lambda's `switch` if
   `-Werror` is on — the `default:` case covers it).
2. **Lavapipe selftest** (must still PASS, this exercises `vk_context_init_headless`
   → `vk_device_create(ctx, VK_NULL_HANDLE, ...)`, i.e. the no-surface path of
   the new device-selection loop):
   ```bash
   export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
   ./build/ufx-linux --selftest
   ```
3. **Device picker sanity:** add a temporary `--session-info`-style print (or
   check `last-run.log`) confirming `ctx->device_name` is not silently
   `physs[0]` on a multi-GPU dev box, if one is available; at minimum confirm
   via code review that the loop no longer references `physs[0]` directly.
4. **Forced-failure message box / log check:** temporarily rename/hide the
   shader dir (or set `UFX_SHADER_DIR=/nonexistent`) and confirm:
   - `~/.local/share/universal-framefx/last-run.log` gets a new FATAL line,
   - a message box appears (when run under a real X11/Wayland session, not
     headless CI).
5. **Composite alpha:** no direct unit test; confirmed by the app actually
   presenting a window on the target GNOME/Wayland session without falling
   back to `return false` at swapchain init. Code review confirms
   `caps.supportedCompositeAlpha` is consulted before picking (previously
   unused).
6. **Packaging:** rebuild AppImage + zip + tar.gz with
   `UFX_VERSION=1.4.1-linux-preview.3 ./scripts/package-appimage.sh`, confirm:
   - filenames carry `.preview.3`,
   - `AppRun` / `run.sh` write to `~/.local/share/universal-framefx/last-run.log`
     (smoke test: run the AppImage once, check the log file exists and has
     content),
   - `docs/TROUBLESHOOT.md` is present under both
     `dist/AppDir/usr/share/ufx-linux/docs/` and
     `dist/Universal-FrameFX-1.4.1-linux-preview.3-linux-x86_64-stage/docs/`.
7. **Direct extracted-binary run** (regression check for Fix 3): after
   `--appimage-extract`, run `squashfs-root/usr/bin/ufx-linux` directly (not via
   `AppRun`) with `UFX_SHADER_DIR` unset, confirm shaders are still found via
   the new `../share/ufx-linux/shaders` candidate.
8. Update `STATUS.md` with new sha256 sums and date once artifacts are rebuilt.

No fake backends, no stubbed GPU/driver shims are introduced by this plan — all
fixes operate on real Vulkan device/queue enumeration and real swapchain
capability queries.

---

## Checklist

- [ ] Fix 1: `ufx_fatal()` helper + log file in `src/ui/main.cpp`, all 9 early
      returns converted, startup log line added.
- [ ] Fix 2: `vk_device_create` in `src/gpu/vk_context.cpp` scores all physical
      devices for graphics+compute+present, prefers discrete on tie, clear
      error when none qualify.
- [ ] Fix 3: `vk_find_shader_dir` AppDir candidates added
      (`../share/ufx-linux/shaders`, `../../share/ufx-linux/shaders`).
- [ ] Fix 4: `AppRun`, `ufx-linux-wrapper`, `run.sh` in
      `scripts/package-appimage.sh` redirect to `last-run.log`;
      `docs/TROUBLESHOOT.md` confirmed shipped via existing `cp -a docs/.` lines.
- [ ] Fix 5: `vk_window_init_swapchain` in `src/gpu/vk_window.cpp` checks
      `caps.supportedCompositeAlpha` and retries OPAQUE → PRE_MULTIPLIED →
      INHERIT → POST_MULTIPLIED.
- [ ] Fix 6: version bumped to `1.4.1-linux-preview.3` in
      `src/ufx_version.h`, `scripts/package-appimage.sh`, `README.md`,
      `STATUS.md`, `NOTICE.md`.
- [ ] New `docs/TROUBLESHOOT.md` written (fuse2, vulkan-radeon/intel/nvidia,
      xdg-desktop-portal-gnome, pipewire, `--appimage-extract-and-run`).
- [ ] `README.md` links to `docs/TROUBLESHOOT.md`.
- [ ] Build passes; `--selftest` PASS on Lavapipe.
- [ ] Forced-failure test shows message box + populated `last-run.log`.
- [ ] Repackaged AppImage/zip/tar.gz under `.preview.3`; `STATUS.md` sha256s
      refreshed.
