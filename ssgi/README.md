# SSGI (experimental, off by default)

A lightweight screen-space "global illumination" pass for Universal-FrameFX, aimed at legacy NVIDIA GTX / GT cards
(Kepler, Maxwell, Pascal; Shader Model 5.0). The C# overlay calls it (round 5) when the Advanced toggle is on.
`SsgiSettings::enabled` still defaults to `false`. Round 6 adds optional steadier lighting; that also defaults to off.

**What it is, honestly:** an approximation built from the captured window image alone. There is no depth buffer, no
normals and no scene geometry, so this is not ray tracing and not physically based. Bright regions of the 2D picture are
treated as light sources. Their colour is spread to nearby pixels with a short 2D march over a 128×128 proxy of the
frame, attenuated across strong dark edges, and scaled by a luminance-contrast "pseudo-AO". It cannot know what is behind
or in front of anything, and it brightens regions near bright areas whether or not they would really receive that light.
On some content it looks like a soft local glow (bloom-like) more than true indirect light.

## Files

| File | What |
|---|---|
| `shaders/ssgi_common.hlsli` | cbuffer, packing (RGB8 + luma8 per uint), interleaved-gradient noise |
| `shaders/ssgi_downsample.hlsl` | Phase 1 `CSDownsample` (cs_5_0) |
| `shaders/ssgi_march.hlsl` | Phase 3 `CSMarch` (cs_5_0) |
| `shaders/ssgi_composite.hlsl` | Phase 4 `VSFullscreen` (vs_5_0) + `PSComposite` (ps_5_0) |
| `src/ssgi_importance.h/.cpp` | Phase 2 kernel: scalar / SSE2 / AVX2, bit-identical |
| `src/ssgi_worker.h/.cpp` | Phase 2 `std::jthread` worker + wait-free SPSC triple buffers |
| `src/ssgi_pipeline.h/.cpp` | D3D11: resources, staging ring, dispatches, upload, composite |
| `src/ssgi_capi.cpp` | flat C API (`ufx_ssgi_create/resize/execute/destroy`) for a future P/Invoke from C# |
| `tests/fxc-check-ssgi.sh` | compiles every entry point with the real Windows SDK fxc.exe (Wine) → `build/cso/*.cso` |
| `tests/importance_test.cpp` | SIMD equivalence, Python cross-check, timing, worker end-to-end (TSAN clean) |
| `tests/harness/ssgi_harness.py` | CPU reference of phases 1-4 on a sample image → before/after PNGs (`--ssrt` runs the SSRT reference) |
| `tests/harness/ssrt_ref.py` | NumPy mirror of the SSRT passes, cost model, metrics |
| `shaders/ssrt_*.hlsl` | depth, depth-aware march, reflections, contact shadows, packed composite |
| `build.sh` | runs all of the above (native g++ + mingw-w64 cross-compile of `ssgi.dll`) |

## Frame flow (all GPU except a 64 KB proxy; the render thread never waits)

1. **Phase 1 (`CSDownsample`, 16×16 groups):** each of the 128×128 cells averages 16 bilinear taps placed on texel
   corners (= 64 texels). The result is packed as RGB8 + luma8 in one uint into a 64 KB `RWStructuredBuffer`. The cost is
   fixed, whatever the capture size. The buffer is `CopyResource`d into a 3-deep ring of `STAGING / CPU_ACCESS_READ`
   buffers. The slot written 2 frames earlier is mapped with `D3D11_MAP_FLAG_DO_NOT_WAIT`. On
   `DXGI_ERROR_WAS_STILL_DRAWING` the read is skipped, never waited for. Full-resolution textures never leave the GPU.
2. **Phase 2 (worker thread):** the render thread memcpys the mapped 64 KB into the worker's triple-buffer slot (wait-free
   exchange) and bumps an atomic sequence (`atomic::wait` / `notify`). The worker takes the newest grid and builds the
   1-bit 128×128 ImportanceMap (512 uint32, bitwise ops; fixed work):
   - *Light-emitting zones:* luma ≥ max(floor, mean + k). The mean is a SIMD SAD.
   - *Shadow traps:* max(|dL/dx|, |dL/dy|) ≥ edge. Saturating subtracts, then compare via max/cmpeq, then movemask
     straight to bits.
   - *The map:* dilate(emit, march reach: 15 cells in x, 31 in y) | (dilate(edge, 1) ∩ dilate(emit, 2× reach)). Dilation
     is done by doubling shifts on 128-bit rows.
   - *Why the big dilation:* bounce light lands on the dull areas around bright zones. With a 1-cell dilation the harness
     lost up to 45/255 of the added light. With the reach dilation the gated result stays within 0.3/255 of the
     ungated one.

   The newest map is uploaded on the render thread with `Map(WRITE_DISCARD)` into a 2 KB `DYNAMIC` structured buffer
   (only when a newer one exists). Until the first map arrives, the buffer is all ones.
3. **Phase 3 (`CSMarch`, lighting texture = source/4, RGBA8):** each 8×8 group tests the importance bits of the cells it
   covers (groupshared `InterlockedOr`). If none is set, the **whole group** writes a neutral texel and returns. D3D11 cannot
   discard individual threads, and a partly idle wave costs a full wave, so the saving is the loop, per group. Active
   texels march 2 rays × 12 steps (8-16 allowed) over the proxy buffer:
   - *Ray directions:* interleaved-gradient noise (blue-noise-like, rotated per frame), with per-texel jitter.
   - *Bounce:* sample colour × smoothstep(emitLo, emitHi, luma) × 1/(1 + k·d²) × visibility. Visibility halves at each
     sharp luma drop (shadow trap). A texel's own emission weight is removed, so bright zones don't light themselves.
   - *Pseudo-AO:* 1 − aoStrength · saturate(4 · mean(max(0, L_near − L_centre))) over the first 4 steps.
   - *Output:* rgb = Bounce·AO, a = cell luma (the guide for phase 4).
4. **Phase 4 (`PSComposite`, full-screen triangle, no vertex buffer):** a 3×3 joint-bilateral filter on the lighting
   texture. The spatial weight is a 1.5-texel tent centred on the exact sub-texel position, which also does the
   bilinear-style upscale. The range weight is exp(−k·(L_game − L_guide)²).
   `Final = saturate(Game + strength · Bounce · AO)`, in display space (another approximation). The output must be a
   different texture from the game SRV.

**Thread safety:** only the render thread touches D3D11. The worker owns no D3D objects. The two hand-offs are SPSC
triple buffers: one atomic exchange per publish/acquire, and neither side can see a slot the other is writing (see
`ssgi_worker.h`). Parameters are one packed atomic word.

**Allocations:** everything is created in `Init`/`Resize` (resources, ~230 KB worker object, `jthread`). The per-frame
`Execute` and the worker loop allocate nothing and take no locks the render thread can contend on (note on `notify` in
`ssgi_worker.h`).

**State changes per frame:** 1 CB update, 2 compute dispatches sharing CB/sampler bindings, 1 copy, 1 draw. Hazard slots
(UAV→SRV) are unbound right after use; nothing else is saved or restored.

## Results (box, offline)

- `tests/fxc-check-ssgi.sh`: 4/4 entry points compile with Windows SDK fxc 10.0.28000 (cs_5_0 / vs_5_0 / ps_5_0, /O3),
  0 errors, 0 warnings.
- **C++20:**
  - native g++ 14 (`-std=c++20 -O2 -msse2 -Wall -Wextra -Wpedantic`): 0 warnings.
  - mingw-w64 g++ 14 (posix threads) cross-compile of `ssgi.dll` against the real `d3d11.h`: 0 warnings. Exports the 4
    C API functions.
  - `importance_test.exe` passes under Wine.
- **`importance_test`:**
  - scalar = SSE2 = AVX2, bit-identical on 240 grids.
  - C++ mask equals the Python harness mask.
  - BuildImportance takes 54 / 24 / 19 µs per frame (scalar / SSE2 / AVX2) on the box's Xeon.
  - Worker end-to-end: 3000 frames, 0 torn masks.
  - ThreadSanitizer and ASan/UBSan runs are clean.
- **Harness** (1280×720 synthetic frame from CC0 Poly Haven textures; `/workspace/ssgi_preview/`): 57% of cells important,
  61.5% of march groups active. Gated vs ungated final image differs by at most 0.3/255. Mean added light 3.2/255
  (p99 32/255), concentrated around the window and lamp.

## Cost estimate (ESTIMATE, not measured: no GPU on the build box)

1080p capture, lighting texture 480×270, ~50-60% of march groups active, from published unit counts and bandwidths:

| Pass | GTX 1050 (58 GTex/s, 112 GB/s) | GT 1030 GDDR5 (35 GTex/s, 48 GB/s) | GT 1030 DDR4 (17 GB/s) |
|---|---|---|---|
| Phase 1 downsample (262k taps, ~4 MB touched) | ~0.04 ms | ~0.09 ms | ~0.25 ms |
| Copy 64 KB + staging read-back | <0.01 ms | <0.01 ms | <0.01 ms |
| Phase 3 march (~1.9M proxy loads, ~50M ALU ops) | ~0.05-0.1 ms | ~0.1-0.2 ms | ~0.1-0.2 ms |
| Phase 4 composite (2.07M px × 10 loads, 16.6 MB r/w) | ~0.35-0.45 ms | ~0.6-0.8 ms | ~1.0-1.2 ms |
| **Total** | **~0.45-0.6 ms** | **~0.8-1.1 ms** | **~1.3-1.6 ms** |

- **CPU:** worker ~20-55 µs per processed frame. Render thread: one 64 KB memcpy + 2 KB upload (a few µs).
- **Biggest lever:** the composite is mostly a full-frame read + write. Folding phase 4 into the app's existing present
  pass would remove about a third of the GPU cost. Kepler-era GT cards (GT 710/730) would be several times slower than a
  GT 1030.

## Round 2 tuning (colour bleed, grain, composite cost, less bloom)

* **Colour bleed:** the grid's top byte is now brightness `B = max(luma709, 0.75 * max(r,g,b))`, not luma. A saturated
  mid-luma red (luma ~0.4) now counts as an emitter, which it did not before. The march gathers in approximate linear
  light (`c^2`) and stores `sqrt`. The composite multiplies the bounce by the **receiver's** albedo estimate
  (`0.5 * chroma of G^2`) in linear light: `Final = sqrt(saturate(G^2 + strength * Bounce * AO * albedo))`.
  That is what makes it read as bounce, not an additive glow/bloom. Default `strength` goes 0.6 -> 1.0 to compensate.
* **Grain:** ray angles and jitter come from a 3 x 3 interleaved pattern (9 rotations) plus a per-frame golden-ratio
  rotation (`frameIndex & 7`), replacing per-texel IGN. A new low-res pass **CSFilter** (3 x 3 binomial x range weight
  on B, linear light) runs on lighting A -> lighting B (one extra 320 x 180-class RGBA8 texture).
  No temporal accumulation yet: the per-frame rotation is the hook for it (this would need a history texture and a flag).
* **Composite cost:** the full-res pass drops from 3 x 3 taps (10 loads/pixel) to a 2 x 2 joint-bilateral upsample
  (5 loads/pixel). The filtering moved to the 1/16-area CSFilter. Folding `PSComposite` into the app's present/final
  pass would remove the separate full-frame read + write. That is the next big win on DDR4 parts and is documented in
  the shader.
* **ABI:** `SsgiShaders` gained a `filter` field (between `march` and `vsFullscreen`), so callers must pass CSFilter's
  bytecode. fxc-check-ssgi.sh compiles 5 entry points.

Harness (synthetic 1280 x 720 scene, offline, r1 -> r2): added-light chroma 0.21 -> 0.61 (the red panel now tints its
surroundings red; r1 added a near-white halo). High-frequency noise in the added light (mean |add - blur3(add)|)
2.48 -> 1.04 /255 (-58%). p99 added 32.2 -> 28.6 /255, mean 3.2 -> 3.4 /255 (spread wider, less hot core). Gated vs
ungated is still <= 0.3/255. Importance map 58.9% of cells, 62.9% of march groups active.

Cost estimate r2 (ESTIMATE, 1080p, not measured): composite ~0.2-0.25 ms on a GTX 1050 (was ~0.35-0.45). CSFilter
~0.02 ms. Totals roughly GTX 1050 ~0.3-0.4 ms, GT 1030 GDDR5 ~0.5-0.7 ms, GT 1030 DDR4 ~1.0-1.2 ms (bandwidth-bound,
was ~1.3-1.6).

## Round 3: horizon-based bounce + AO, GPU presets (GTX 1050 Ti / GTX 980 Ti)

* **Horizon march (CSMarch):** a pseudo height field `h = heightScale * B` (cells; default 24) is built from luminance.
  Per ray, the horizon sine only rises. A sample that raises it is visible and adds `emit(B) * colour * (sinS - sinH)`
  (the solid-angle slice it uncovers, GTAO-style). Samples behind a nearer raised cell add nothing, which replaces the
  r1/r2 "shadow trap". **AO = 1 - aoStrength * mean final horizon sine.** `horizonBias` (0.25) starts the horizon slightly
  below flat. Steps grow geometrically (`stepGrowth`), giving dense near-field contact plus far reach without extra steps.
  This stands in for marching a mip chain, which is not implemented. The clamps are now 1..8 rays and 8..32 steps.
* **Temporal accumulation (flag, OFF by default):** `temporal` / `temporalAlpha` in SsgiSettings. CSFilter blends with
  the previous filtered frame (ping-pong `lightB_[2]`, t3). History is rejected where the guide brightness changed.
  There are no motion vectors, so it assumes a static camera: expect smearing on camera pans.
* **Importance reach:** `reachLog2X/Y` are now in SsgiSettings and forwarded to the worker. The presets use 5 / 6
  (31 x 63 cells), because the longer march would otherwise be clipped by the map. In the harness the clipping is
  2.4 (1050 Ti) and 4.5 (980 Ti) /255 max and < 0.05/255 mean. The default preset keeps 4 / 5 (6.1/255 max clip).
* **Presets:** `MakePreset(SsgiPreset::GTX1050Ti | GTX980Ti)` and the C export `ufx_ssgi_preset(id, SsgiSettings*)`.
  The `SsgiSettings` and constant-buffer layouts changed (96 B).

| preset | target | lighting res | rays x steps | est. march | est. filter | est. composite | **est. total** |
|---|---|---|---|---|---|---|---|
| GTX1050Ti | 1080p | 480 x 270 (/4) | 4 x 16 | ~0.45-0.7 ms | ~0.03 ms | ~0.2-0.25 ms | **~0.8-1.1 ms** |
| GTX980Ti | 1440p | 853 x 480 (/3) | 6 x 20 | ~0.5-0.8 ms | ~0.03 ms | ~0.15-0.2 ms | **~0.8-1.1 ms** |

ESTIMATES ONLY (no GPU here). Basis: march = texels x rays x steps proxy loads (64 KB buffer, L2-resident) plus
~25 ALU ops per step. Composite = 5 loads/px, bandwidth-bound (1050 Ti 112 GB/s, 980 Ti 336 GB/s). Importance gating
(~80% of groups active with the wider reach) is not counted as savings. Both are inside the 1.5 ms budget with headroom;
temporal on adds ~0.02 ms.

Harness (1280 x 720 synthetic, r1 -> r3 GTX1050Ti preset): added-light chroma 0.21 -> 0.65. Grain (mean |add - blur|)
2.48 -> 0.95 /255. Lit pixels 221k -> 427k (wider, softer bounce with contact darkening at the emitters' edges).
Mean added light 3.2 -> 4.5 /255. Faint radial streaks are visible around small bright sources at 6 rays (x4 diff
view only). Previews: /workspace/ssgi_preview/before_after_{gtx1050ti,gtx980ti,default}.png.

## Round 4: visible AO, no streaks, pan-tolerant temporal, wider default reach

* **AO on the game image:** the lighting texture is now R32G32_UINT: sqrt(bounce) at 11/11/10 bits, plus guide B (8)
  and near-field occlusion (8). Occlusion is the max horizon sine within `aoRadius` (6 cells), so contact/crevice only;
  the far horizon still feeds the bounce. Composite: `Final = sqrt(G^2 * (1 - aoGame * occ) + strength * Bounce * albedo)`.
  `aoGame` defaults to 0.35 and is clamped to <= 0.6 on the CPU, so it never crushes to black.
* **Streaks:** each ray gets an IGN sub-bin angle jitter (+-1/18 turn) and a golden-ratio step offset, so angular coverage
  is continuous instead of 9 x rays fixed directions; CSFilter averages the jitter.
* **Composite fallback fix:** when the range test rejected every tap, r3 copied the nearest tap's full bounce. That lifted
  thin dark details inside bright zones (window mullions 20 -> ~66-70/255). r4 normalises by max(wsum, 0.25), so those
  pixels fade out instead (20 -> ~35/255).
* **Pan-tolerant temporal (still behind `temporal`, default OFF):** a new CSShift pass (1 group, 81 threads, ~22k
  L2-resident loads) searches +-4 cells of global shift between this frame's proxy grid and last frame's, with a sub-cell
  parabola fit. It is all on the GPU with no read-back. CSFilter reprojects history by that shift, still resets where
  the guide brightness changed, and drops history when the match error is > `panMaxSad`. Synthetic pan test (8 frames,
  0.7 cell/frame), error vs an 8-ray ungated reference: no temporal 0.77/255 mean (p99 6.1); temporal without reprojection
  0.94 (p99 9.4, ghosting); temporal with reprojection 0.77 (p99 7.3), with -15% grain. The estimated shift averaged
  ~+0.8 x, -0.2 y for a true +0.7 x. Not enabled by default: the gain is small and there is a y bias, and it still needs
  testing on real game footage (parallax, HUD).
* **Default reach:** ImportanceParams now default to 5 / 6 (31 x 63 cells); the C++ vs Python reference is regenerated.
* **API:** `SsgiShaders` gained `shift` (between `filter` and `vsFullscreen`), and `SsgiSettings` gained `aoRadius`,
  `aoGame`, `temporalPan` and `panMaxSad`. Constants are now 112 B. fxc checks 6 entry points.

Cost estimate r4 (ESTIMATE): the lighting texture doubles to 8 B/texel (~1 MB at 480 x 270, ~3.3 MB at 853 x 480).
The composite reads 4 x 8 B taps (cache-resident, ~+0.02 ms), and CSShift is ~0.005 ms. The march is unchanged apart from
a little ALU. GTX1050Ti @1080p ~0.85-1.15 ms; GTX980Ti @1440p ~0.85-1.15 ms. Both stay under 1.5 ms.

Harness r3 -> r4 (GTX1050Ti / GTX980Ti presets): AO-darkened pixels 0 -> 51k / 88k (max 11 / 14 /255). Mullion lift
50 -> 16 / 46 -> 14 /255. Grain 0.95 -> 0.87 / 1.00 -> 0.95. Chroma 0.65 -> 0.67 / 0.67 -> 0.69. Mean added light
4.5 -> 4.1 / 4.5 -> 3.9 /255. Previews: before_after{,_gtx1050ti,_gtx980ti,_default}.png, pan_test_strip.png (added
light x4: no temporal | temporal no reprojection | temporal + reprojection | 8-ray reference); r3 copies are *_r3*.png.

## Round 5: AO cleanup + wired into the C# app (toggle, OFF by default)

* **AO:** emitter-bright samples no longer occlude (their light is already in the bounce). The AO height step is clamped
  to 0.25 x heightScale (local-contrast clamp), and emitter-bright receivers are not darkened. Harness, r4 -> r5
  (GTX1050Ti / GTX980Ti): dark-side ring around the window, mean darkening 0.10 -> 0.00 / 0.23 -> 0.00 (max 7 -> 0 /
  11 -> 0). Red-panel interior darkening 0.31 -> 0.00 / 0.48 -> 0.00. Stone-floor crevice AO kept: 0.50 -> 0.48 /
  0.89 -> 0.86. Pixels darkened > 2/255: 41k -> 35k / 65k -> 56k.
* **App integration (windows/src/UniversalFrameFX):**
  * `Ssgi.cs` P/Invokes `ssgi.dll`, using the new export `ufx_ssgi_execute_preset`, so there is no SsgiSettings marshalling.
  * It loads `ssgi\*.cso` next to the exe and runs on the app's own D3D11 device + immediate context, on the present
    thread with Gpu.Lock held.
  * It is applied to the captured frame before upscaling / frame generation, so generated frames include it.
  * It adds no read-back on the render thread: the native side's 64 KB grid copy is read DO_NOT_WAIT on its worker ring.
  * Afterwards it unbinds its RTV and PS b0, so the pipeline's SRV of the output is valid.
  * UI: Advanced -> "SSGI (experimental)" toggle plus a preset combo (Auto / GTX 1050 Ti / GTX 980 Ti). Both are saved
    in ui.json (`Ssgi`, `SsgiPreset`) and live-applied. The HUD shows a status line when the toggle is on.
  * When OFF, nothing is created or called: the capture SRV goes to `FramePipeline.Process` exactly as before.
  * Build: run `windows/ssgi/build.sh` first. The csproj copies `ssgi.dll` and `ssgi/*.cso` from `windows/ssgi/build`
    when present.

Cost estimate r5 (ESTIMATE): unchanged vs r4 (the AO change is a few ALU ops per step), plus one full-res copy-sized
write of the SSGI output at source resolution. GTX1050Ti @1080p ~0.9-1.2 ms, GTX980Ti @1440p ~0.9-1.2 ms.

## Integration plan (superseded by round 5 for the app hook)

Build `ssgi.dll` (or port the HLSL + worker to C#). From the C# render thread after the upscale, call
`ufx_ssgi_execute(handle, ctx, outputSrv, compositeRtv, &settings)` behind a new off-by-default setting / env flag. Pass
the native `ID3D11Device*` / context / views from Vortice (`NativePointer`). The shaders come from `build/cso` (or embed
and compile them with D3DCompile at init).

## Round 6: temporal accumulation from FrameFX's camera pan (implemented, OFF by default)

SSGI runs in `OutputForm` before `FramePipeline.Process`, so the 1×1 pan SRV is the **previous** `Process` estimate:
while shading frame t the vector is motion from t−2 to t−1 (one frame late). Constant velocity makes the delay irrelevant.
The first usable pan is frame 2. `UFX_FG_GLOBAL=0` leaves the SRV invalid and this frame is not accumulated.

**Sign.** `FX_GlobalMotion` writes a **backward** UV vector: it matches previous luma at `(x+dx, y+dy)` to current luma
at `(x, y)`, and stores `(dx, dy) / (64, 36)`. Current content at `uv` came from `uv + pan`. History is sampled at
`uv + pan` (the same convention as `FX_FastSearch` / `FX_MvBorder`). A forward-displacement sample (`uv - pan`) would
ghost on every pan. Self-check of the numpy port: content moving +16 px comes back as backward **(−17.67, −0.02) px**,
cost 0.0053 (the ±0.5 texel parabola overshoots the 20 px grid).

**Pan model in the harness** (what the app would really have, not an extra noise source):
* Luma box-averaged onto the same 64×36 grid. At 1280×720 that is exact 20×20 blocks; the GPU reaches the same grid
  through the 1/8 pyramid plus 4 bilinear taps.
* Integer search ±12 × ±6 texels, cost = mean |dLuma| + 0.0005·(|dx|+|dy|), then the same parabolic sub-texel fit.
* The estimate is delayed one frame. There is no confidence channel and no extra synthetic noise; the quantisation is
  the search grid. `_globValid` only means the pass ran.

**GPU path** (`ufx_ssgi_execute_preset2`; the old `ufx_ssgi_execute_preset` is unchanged and does not accumulate).
History is the filtered lighting ping-pong that already existed (`lightB_`). `CSPanGate` is one group of 64 threads:
it reads the 1×1 `R32G32_FLOAT` pan on the GPU (no CPU readback), samples the same 28×28 stride-4 proxy lattice as
`CSShift`, and writes `(pan.xy, accept, mean |dB|)`. `CSFilter` then, only when the gate accepts:
* bilinear-samples history **in stored sqrt-rgb, then squares** (the harness tunes this, not a bilinear of linear light),
* clips linear rgb and occlusion to the current 3×3 min/max, pulled toward the current texel by `clampBox`
  (`1` = the full box),
* drops history where the guide brightness changed (`guideReject`) or the 2×2 leaves the texture.

Reset (no blend that frame, and the new lighting becomes history): first frame, resize, preset/march fingerprint
change (`rays | steps<<8 | divisor<<16`), null pan, `length(pan * srcSize) > panMaxPx`, or pan-aligned mean |dB|
above `lumReset`. Fewer than 200 in-bounds proxy samples counts as a mismatch (mean = 1).

**Thresholds**, from the same run. Proxy SAD on real pans, p95 = **0.0094**; on the cut frame = **0.2679**.
`lumReset` is the geometric mean, **0.0502** (inside the gap). `panMaxPx = 48` (above the 16 px and ~12 px diagonal
pans, far below the ±240 px search edge). Tuned blend: **new-frame weight 0.22**, **guideReject 8** (full reject at
|dB| = 0.125), **clampBox 1**.

**Harness** (`python3 tests/harness/ssgi_harness.py --pan-test --preset gtx1050ti --frames 24 --rotations 4`).
Five 24-frame 1280×720 sequences: horizontal 2 px/frame, horizontal 16 px/frame, diagonal 10×6, a cut at frame 12
(scene replaced by a mirrored regrade; the gate must drop history), and a 56×56 object moving 8 px/frame over a
static camera. Per-frame march noise is the shader's rotated interleaved-gradient pattern (`frameIndex = t`).
The reference is the mean of 4 golden-ratio rotations, filtered the same way. Metrics are on the **composited**
image, in display units /255. Flicker is motion-compensated mean |Δ| on pixels that should be stable. Ghosting is
|error vs reference| on disocclusions, a dilated moving-object edge, and the cut frame plus the one after it.

(a) no temporal, (b) temporal + pan reprojection + clamp/reset, (c) the same blend with pan forced to 0 and no
global reset (the ghosting reprojection is there to avoid).

| scenario | flick a | flick b | flick c | acc a | acc b | gP99 a | gP99 b | gP99 c | trail |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| horiz 2 px | 0.958 | 0.462 | 0.472 | 0.460 | 0.413 | 5.659 | 5.659 | 4.130 | 0 |
| horiz 16 px | 1.594 | 0.833 | 1.084 | 0.519 | 0.510 | 5.747 | 5.747 | 6.199 | 0 |
| diagonal 10×6 | 1.162 | 0.481 | 0.756 | 0.584 | 0.520 | 6.841 | 6.732 | 7.000 | 0 |
| cut | 2.537 | 2.337 | 2.330 | 0.313 | 0.339 | 1.571 | 1.571 | 1.704 | 0 |
| moving object | 0.836 | 0.233 | 0.205 | 0.610 | 0.321 | 6.744 | 10.636 | 10.761 | 0 |

Mean flicker a **1.417**, b **0.869**, c **0.969** (/255): **(b) is 38.7% steadier than (a)**. Worst ghost p99
ratio b/a is **1.577**, entirely the moving object (pans and the cut stay at 1.00). Trail after the cut is **0**
(the gate resets; (b) matches (a) on those frames). (c) is the one that smears a pan: fast-pan gP99 6.199 vs 5.747.

The other new-frame weights at the full 3×3 box, same guide: alpha 0.35 reduces flicker 33.1% with ghost ratio
1.253; alpha 0.50 reduces flicker 26.1% with ghost ratio 1.067. Raising `guideReject` from 8 to 64 only moves the
moving-object ratio from 1.577 to 1.504 (the leaked colour is inside the 3×3 and the guide barely differs).
Pulling the 3×3 box toward the current texel (`clampBox`) does kill that ghost, and it also spends the flicker win:

| clampBox | slow-pan flicker cut | fast-pan flicker cut | moving-object gP99 ratio |
|---|---:|---:|---:|
| 1.00 | 51.7% | 47.7% | 1.577 |
| 0.60 | 39.9% | 36.2% | 1.500 |
| 0.40 | 29.8% | 26.8% | 1.319 |
| 0.25 | 20.2% | 18.1% | 1.078 |

Ghost p99 ≤ +10% shows up only once pan flicker has already fallen under 30%. No setting clears both bars.

**Decision: OFF by default.** (b) does reduce flicker by ≥ 30% (38.7% at alpha 0.22), and the cut leaves no trail,
but the moving-object edge/disocclusion p99 is +58%, past the +10% limit. The toggle still runs this path
(`Ssgi.TemporalDefault = false`; `UFX_SSGI_TEMPORAL=0/1` overrides the saved choice without rewriting ui.json).
Shipped constants are the best flicker point: alpha 0.22, guide 8, clampBox 1, lumReset 0.0502, panMaxPx 48.

**App wiring.** `FramePipeline.WantGlobalPan` runs `FX_GlobalMotion` when steadier lighting is on, including when
frame generation is off, but only on paths that already build the luma pyramid (Performance temporal upscale, or
Quality). The fast search does not take that vector as a candidate unless frame generation is on, so upscaling is
unchanged. Spatial / FSR 1 / 1:1 vendor passes do not grow a pyramid for this; the pan SRV stays null and that
frame looks like SSGI without the toggle. JSON: `pan_temporal.json` under the harness `--out` directory.

**Cost (ESTIMATE, no GPU):** `CSPanGate` is one group, same order as `CSShift` (~0.005 ms). The history texture
already existed. One extra `FX_GlobalMotion` group when the pyramid is already being built. GTX 1050 Ti @1080p
stays ~0.9–1.2 ms. fxc: 7 entry points (`CSPanGate` added), cbuffer 144 bytes.

## SSRT (screen-space depth, reflections, contact shadows)

This is still not ray tracing. There is no depth buffer and no geometry. Every new signal is guessed from the
captured image, plus FrameFX motion when the caller passes it. `ufx_ssgi_execute_preset` and
`ufx_ssgi_execute_preset2` are unchanged. `ufx_ssrt_execute` with feature flags `0` calls that same preset path,
so the features-off image matches today's SSGI. The new work lives in a second constant buffer (`b1`) and in new
entry points; the SSGI cbuffer layout is the one the existing shaders were compiled against.

**Why there is no monocular depth network.** The smallest permissively licensed models do not fit the budget on
these cards, in hand-written D3D11 compute, Shader Model 5.0, no tensor cores:

* FastDepth (MIT) is about 0.75 GFLOP at 224² and was trained on indoor NYU depth. 0.75 GFLOP on a GTX 1050 Ti
  (768 cores, 1392 MHz boost, about 2.1 TFLOPS FP32) is already ~0.4 ms at full efficiency, and a real SM5.0
  implementation of a depthwise network is far below peak. It also does not transfer to a game frame.
* MiDaS v2.1 small and Depth-Anything-V2-Small are about 20–25 M parameters and several GFLOPs per frame. Several
  GFLOPs is multiple milliseconds even before memory traffic, which is already over the 0.5 ms that would leave
  room for the rest of this pass.
* Depth-Anything-V2 Base and Large are not under a commercial-use license.

Nothing in this directory loads or runs a depth model.

### Passes

| Entry | What |
|---|---|
| `CSDepth` | Light-resolution pseudo-depth. 3×3 point taps of the game image. Cues: vertical horizon prior (bottom nearer, sky far), local contrast, defocus (high-frequency energy over local mean), sky-luma (bright above the horizon is far). Temporal parallax is added only when a per-pixel motion SRV and a global-pan SRV are both bound and the pan is longer than `panMinPx` (1.5 source pixels): a larger motion magnitude than the pan reads as nearer. Otherwise that weight is 0. |
| `CSDepthBlur` | 3×3 edge-aware bilateral (range on brightness). Screen-space normal from the smoothed 3×3 depth gradient. Smoothness for the reflection gate: low luma variance, bright enough, and a near-horizontal normal (points up the image, not a front-facing wall). `ssrtTemporal` stays 0 here. History is a later pass, `CSSsrtAccum`. |
| `CSMarchDepth` | Same importance-map group early-out as `CSMarch`. With the GI flag, a sample is a bounce hit only when its depth is in front of the ray and inside a thickness band; a much nearer sample blocks the ray. With the AO flag, occlusion is the horizon angle of the depth instead of the luminance pseudo-height. A flag that is off keeps the r5 luma formula for that channel. |
| `CSReflect` | Reflects the view ray (+y down the image, +z into the screen) about the estimated normal. Marches only where smoothness ≥ `glossMin`. Coarse-to-fine strides (first half of the steps are 4× the fine stride, total length `reflReach` in units of screen height) stand in for a depth mip chain. Misses and screen edges drop the confidence. Roughness averages a second colour tap. |
| `CSLightDir` | One group. Brightest proxy cell, ties keep the smaller index. |
| `CSContactShadow` | Short rays toward that cell, blended with the local brightness gradient. A nearer depth sample inside the thickness band darkens the pixel, falling off with distance so only the contact neighbourhood is affected. A few angular jitters soften the penumbra. |
| `CSPack` | Reflection premultiplied by confidence, contact in alpha. |
| `CSSsrtCut` | One group. Mean luma of the light grid. A jump above `lumCut` (0.08) writes a reset and arms a one-frame cooldown so the following frame also resets. |
| `CSSsrtAccum` | Reprojects GI/AO, reflections, contact and pseudo-depth. Mode 1 uses the per-pixel motion SRV (`_mvLo`, one Process late). Mode 2 uses the global pan SRV. Mode 0, or neither SRV, resets and drops history. Rejection: 3×3 neighbourhood clamp, hard depth difference (`depthReject` 0.045), motion disagreement (`mvRejectPx` 6 source pixels, mode 1 only), scene-cut reset, confidence-weighted blend (`accumAlpha` 0.22 is the new-frame weight). |
| `PSCompositeSsrt` | Same 2×2 joint-bilateral GI/AO upsample as `PSComposite`. Reflection and contact reuse those weights. Strength 0 skips the extra taps, and the arithmetic then matches `PSComposite`. When accumulation ran, the composite reads the accumulated GI. |

`CSMarch` is still the features-off march. `PSComposite` is still used when reflections and contact shadows are both off.

### API

`ufx_ssrt_init(handle, SsrtShaders*)` after `ufx_ssgi_create`. `SsgiShaders` is not extended, so the C# P/Invoke
layout of `ufx_ssgi_create` stays as it is. The app toggle is "Ray-traced lighting (experimental)" (off by default).
When it is on, SSGI is not also run. Compare mode does not call this. A missing export or a failed init leaves the
feature off.

`ufx_ssrt_execute(handle, ctx, game, out, preset, flags, motion, pan, temporal)`

* preset `1` = `GTX1050Ti_1080p`, `2` = `GTX980Ti_1440p`
* flags: bit 0 depth-aware GI, bit 1 depth AO, bit 2 reflections, bit 3 contact shadows. Zero runs `MakePreset` + `Execute` (temporal off), i.e. `ufx_ssgi_execute_preset`
* `motion` may be null. When present it is FrameFX `_mvLo` from the previous Process: xy in **source pixels**
* `pan` may be null. When present it is `FX_GlobalMotion`: 1×1 **backward UV**
* `temporal` 0 skips accumulation. With temporal on, a motion SRV selects per-pixel reprojection, else a pan SRV selects the global pan, else the frame resets
* either null forces the depth-pass parallax weight to 0

### Cost (ESTIMATE, no GPU on the build box)

40% of published throughput. Pass time is the max of ALU, texel fetches, and unique-byte bandwidth (a 2×2 tap is
extra fetches, not 4× the DRAM). A bilinear `SampleLevel` counts as 4 fetches. Importance gating is not credited.
Full-frame textures count as a full read or write. Numbers from `tests/harness/ssrt_ref.py`, written to
`build/ssrt/cost_estimate.json`.

| preset | light | rays × steps | refl / contact | temporal | **total** |
|---|---|---|---|---|---|
| GTX1050Ti_1080p (1920×1080, /5) | 384×216 | 3×10 | 8 steps / 2×5 | included | **1.735 ms** |
| GTX980Ti_1440p (2560×1440, /3) | 853×480 | 4×14 | 8 steps / 2×6 | included | **1.968 ms** |

The 1050 Ti light divisor is 5. The composite reads one nearest packed texel instead of a second 2×2, which
was the 0.88 / 0.47 ms term. Depth also takes a 4-tap silhouette ring and one sample at the top of the
column; that pass is still limited by the full-frame read. Totals are inside 1.80 ms (1050 Ti) and 2.00 ms
(980 Ti). `--ssrt` rewrites the JSON.

Features-off still uses `PSComposite`. With the features on, bounce and reflections are added in the
receiver's colour, most of the added luma is removed so the frame does not get darker, and darkening is
only a strong nearby occlusion or a contact shadow. Bright cores, blue sky, pure-red UI fills and blue
panels are left unchanged. `giGain` is 1.55. Sky colour, haze, foliage and the silhouette cue run only
when the top of that column is outdoor sky, so an interior or a night scene keeps the previous depth prior.

### Harness

`python3 tests/harness/ssgi_harness.py --ssrt`

`python3 tests/harness/ssgi_harness.py --ssrt-temporal` runs sixteen seeds (default `--seed 1` is seeds 1–16), including a whip pan, a moving character and a night pan. The same seed is run twice first and must match. Pass per seed: flicker reduction ≥ 30%, worst ghost p99 ratio ≤ 1.10 on the moving-object, disocclusion, character and pan masks, trail after a cut ≤ 1.5/255. The table and the on/off decision are `build/ssrt/temporal_seeds.json`. Previews go to `/workspace/ssrt_preview/<YYYYMMDD-HHMM>-temporal/`. SSRT temporal is off by default: seed 114 ghost p99 ratio 1.107 > 1.10 in an independent 101-116 run.

`--ssrt` also renders the previous implementation (`git show 03a4715:…/ssrt_ref.py`) and writes
`/workspace/ssrt_preview/<YYYYMMDD-HHMM>-r2/` (original, old final, new final, new depth, `contact_sheet.png`)
plus `build/ssrt/metrics_r2.json`. Scenes: the original room / sky / dark interior, plus outdoor, foliage,
night lights, a character, a HUD overlay, the Poly Haven frame, and any of the three real screenshots under
`/workspace` that are present.

Synthetic room (glossy floor, bright far window, dark near crate), a sky/horizon, a dark interior, and the same
room with a 10 px far-field pan (nearer pixels move faster). The Poly Haven frame from `make_scene` is included
as `sample/` (no ground-truth depth). Previews go to `/workspace/ssrt_preview/<YYYYMMDD-HHMM>-r2/` and older folders
are left in place. Metrics: `build/ssrt/metrics.json`.

On the 1280×720 room (old proxy = `1 - brightness`, the r3 height field):

| scene | old Spearman | new Spearman | old abs-rel | new abs-rel |
|---|---:|---:|---:|---:|
| room | −0.19 | 0.85 | 0.55 | 0.25 |
| sky | −0.78 | 0.86 | 0.53 | 0.17 |
| dark interior | 0.61 | 0.93 | 0.49 | 0.11 |
| room + parallax | −0.21 | 0.91 | 0.00 | 0.18 |

The same room with the pan cue off is Spearman 0.85; with it on, 0.91. Abs-rel is after a scale/shift fit; a
negative scale is rejected, so an inverted proxy is not flipped into a good score. Reflection energy on the
glossy-floor mask: **95.7%**. Contact-shadow mean inside 16 px of the crate/floor contact vs everywhere else:
**6.8×**. Features-off composite vs today's `phase4`: max abs **0**. These are the `/5` 1050 Ti light grid.

### Limitations

* Off-screen light does not exist. A window that is not in the image cannot light the room, and a reflection that
  would land off screen is a miss.
* The horizon prior is a guess about gravity and camera pitch. A camera aimed at the ceiling, or a game with no
  ground, will get a wrong depth.
* "Glossy" means smooth, fairly bright, and sloped like a floor in the estimated depth. A shiny vertical wall is
  not detected. A flat wall whose estimated depth happens to tilt can still pick up a reflection.
* The dominant light is the brightest proxy cell. A white UI panel or a specular highlight can steal the contact
  shadow direction.
* Thickness and the normal are in the same made-up 0–1 depth. They are not view-space metres.
* Temporal accumulation is `CSSsrtAccum`. SSRT temporal is off by default: seed 114 ghost p99 ratio 1.107 > 1.10 in an independent 101-116 run. The table is `build/ssrt/temporal_seeds.json`. `UFX_SSRT_TEMPORAL=0/1` overrides; a saved true does not turn it on.
* The millisecond figures are a model, not a capture.
