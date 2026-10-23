"""Offline CPU harness for the SSGI pipeline (phases 1-4), mirroring the HLSL in ../../shaders as closely as numpy allows.
Builds a synthetic game-like frame from CC0 Poly Haven textures (or takes --image), runs:
  phase 1  128x128 proxy grid (16 corner-snapped bilinear taps per cell, RGB8 + brightness8 packed in one uint32; round 2: B = max(luma, 0.75 max(r,g,b)))
  phase 2  importance map (reference implementation; the C++ SIMD kernel is checked against grid.bin / mask_py.bin)
  phase 3  importance-gated march (whole 8x8 groups skipped like the GPU) -> RGBA8 lighting texture
  phase 3b low-res 3x3 bilateral (CSFilter), linear light
  phase 4  2x2 joint-bilateral upsample + composite  Final = sqrt(saturate(G^2 + strength * Bounce * AO * albedo(G)))
and writes before/after PNGs + debug views to --out (default /workspace/ssgi_preview).
This is a sanity check of the algorithm, not a GPU measurement; floating-point details differ slightly from the GPU."""
import argparse, json, os, numpy as np
from PIL import Image, ImageFilter

G = 128
TEX = '/workspace/ph'

def load(path, size):
    return np.asarray(Image.open(path).convert('RGB').resize(size, Image.BILINEAR), np.float32) / 255.0

def make_scene(W=1280, H=720):
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    wall = load(f'{TEX}/tex/concrete_wall_008_D.jpg', (W, H))
    floor = load(f'{TEX}/tex/cobblestone_floor_08_D.jpg', (W, H))
    red = load(f'{TEX}/tex/red_plaster_weathered_D.jpg', (W, H))
    crate = load(f'{TEX}/mod/wooden_crate_02/textures/wooden_crate_02_diff_2k.jpg', (260, 260))
    img = wall.copy()
    fl = yy > 0.66 * H
    img[fl] = floor[fl]
    # baked direct light: falls off from the window on the left, darker floor far right
    light = 0.18 + 0.75 * np.exp(-((xx - 0.17 * W) ** 2 / (0.35 * W) ** 2 + (yy - 0.3 * H) ** 2 / (0.6 * H) ** 2))
    img *= light[..., None]
    # bright window with dark mullions
    win = (xx > 0.07 * W) & (xx < 0.27 * W) & (yy > 0.12 * H) & (yy < 0.52 * H)
    sky = np.stack([0.95 - 0.1 * (yy / H), 0.97 - 0.05 * (yy / H), np.full_like(yy, 1.0)], -1)
    img[win] = sky[win]
    mull = win & ((np.abs(xx - 0.17 * W) < 4) | (np.abs(yy - 0.32 * H) < 4))
    img[mull] = 0.08
    # red plaster panel on the right wall, dimly lit
    pan = (xx > 0.32 * W) & (xx < 0.48 * W) & (yy > 0.14 * H) & (yy < 0.50 * H)   # red panel, lit by the window
    img[pan] = np.clip(red[pan] * np.array([1.35, 0.5, 0.4]) * 1.25, 0, 1)
    # warm lamp glow top-right
    d2 = (xx - 0.58 * W) ** 2 + (yy - 0.16 * H) ** 2
    img += (np.array([1.0, 0.62, 0.25]) * np.exp(-d2 / (0.03 * W) ** 2)[..., None]) * 1.2
    # crate with contact shadow
    cx, cy = int(0.42 * W), int(0.56 * H)
    sh = np.exp(-(((xx - (cx + 130)) / 190) ** 2 + ((yy - (cy + 262)) / 22) ** 2))
    img *= (1 - 0.65 * sh)[..., None]
    img[cy:cy + 260, cx:cx + 260] = crate * 0.55
    return np.clip(img, 0, 1)

def bilinear(img, x, y):   # pixel-centre convention like D3D (texel i centre at i + 0.5)
    H, W, _ = img.shape
    x = x - 0.5; y = y - 0.5
    x0 = np.clip(np.floor(x).astype(int), 0, W - 1); y0 = np.clip(np.floor(y).astype(int), 0, H - 1)
    x1 = np.clip(x0 + 1, 0, W - 1); y1 = np.clip(y0 + 1, 0, H - 1)
    fx = (x - np.floor(x))[..., None]; fy = (y - np.floor(y))[..., None]
    return (img[y0, x0] * (1 - fx) + img[y0, x1] * fx) * (1 - fy) + (img[y1, x0] * (1 - fx) + img[y1, x1] * fx) * fy

def luma(c): return c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722
def bright(c): return np.maximum(luma(c), 0.75 * c.max(-1))
def interleave3(x, y): return (np.mod(x, 3) + 3 * np.mod(y, 3)) / 9.0

def phase1(img):
    H, W, _ = img.shape
    cell = np.array([W / G, H / G], np.float32)
    cy, cx = np.mgrid[0:G, 0:G].astype(np.float32)
    acc = np.zeros((G, G, 3), np.float32)
    for i in range(16):
        sub = (np.array([i & 3, i >> 2], np.float32) + 0.5) * 0.25
        px = np.clip(np.maximum(np.round(cx * cell[0] + sub[0] * cell[0]), 1), None, W - 1)
        py = np.clip(np.maximum(np.round(cy * cell[1] + sub[1] * cell[1]), 1), None, H - 1)
        acc += bilinear(img, px, py)          # corner position: mean of a 2x2 texel block
    avg = acc / 16
    q = np.floor(np.clip(np.concatenate([avg, bright(avg)[..., None]], -1), 0, 1) * 255 + 0.5).astype(np.uint32)
    return q[..., 0] | (q[..., 1] << 8) | (q[..., 2] << 16) | (q[..., 3] << 24)

def dilate_log2(b, lx, ly):
    """Same doubling dilation as ssgi_importance.cpp: radius 2^lx - 1 along x, 2^ly - 1 along y."""
    o = b.copy()
    for k in range(min(lx, 5)):
        s = 1 << k; t = o.copy(); o[:, s:] |= t[:, :-s]; o[:, :-s] |= t[:, s:]
    for k in range(min(ly, 6)):
        s = 1 << k; t = o.copy(); o[s:] |= t[:-s]; o[:-s] |= t[s:]
    return o

def phase2(grid, emit_floor=96, emit_above=40, edge=20, lx=5, ly=6, edge_mode=1):
    L = (grid >> 24).astype(np.int32)
    mean = int(L.sum()) // (G * G)
    T = min(255, max(emit_floor, mean + emit_above))
    Lr = np.concatenate([L[:, 1:], L[:, -1:]], 1); Ld = np.concatenate([L[1:], L[-1:]], 0)
    g = np.maximum(np.abs(L - Lr), np.abs(L - Ld))
    emit, edg = L >= T, g >= edge
    bits = dilate_log2(emit, lx, ly)
    if edge_mode:
        e1 = dilate_log2(edg, 1, 1)
        if edge_mode == 1: e1 &= dilate_log2(emit, lx + 1, ly + 1)
        bits |= e1
    words = np.zeros(512, np.uint32)
    for y in range(G):
        for w in range(4):
            v = 0
            for b_ in range(32):
                if bits[y, w * 32 + b_]: v |= 1 << b_
            words[y * 4 + w] = v
    return bits, words, dict(mean=mean, T=T, emit=int(emit.sum()), edge=int(edg.sum()), set=int(bits.sum()))

def ign(px, py, frame):
    px = px + 5.588238 * (frame & 63)
    return np.mod(52.9829189 * np.mod(px * 0.06711056 + py * 0.00583715, 1.0), 1.0)

def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t)

def phase3(grid, bits, W, H, lw, lh, frame=0, aoRadius=6.0, steps=12, rays=2, stepCells=1.5, aoStrength=0.5,
           emitLo=0.35, emitHi=0.9, edgeLuma=0.12, falloff=0.02, forceAll=False,
           heightScale=6.0, stepGrowth=1.12, horizonBias=0.25):
    rgb = np.stack([(grid & 0xFF), (grid >> 8) & 0xFF, (grid >> 16) & 0xFF], -1).astype(np.float32) / 255
    lum = (grid >> 24).astype(np.float32) / 255
    ty, tx = np.mgrid[0:lh, 0:lw].astype(np.float32)
    # whole-group gate, like CSMarch
    gx, gy = (tx // 8).astype(int), (ty // 8).astype(int)
    active = np.zeros((lh + 7) // 8 * 8 // 8 + 1, bool)
    ngx, ngy = (lw + 7) // 8, (lh + 7) // 8
    gate = np.zeros((ngy, ngx), bool)
    for j in range(ngy):
        for i in range(ngx):
            t0 = np.array([i * 8, j * 8]); t1 = np.minimum(t0 + 7, [lw - 1, lh - 1])
            c0 = (t0 * G) // [lw, lh]; c1 = (t1 * G) // [lw, lh]
            cx1 = min(c1[0], c0[0] + 7); cy1 = min(c1[1], c0[1] + 7)
            gate[j, i] = forceAll or bits[c0[1]:cy1 + 1, c0[0]:cx1 + 1].any()
    on = gate[gy, gx]
    uvx, uvy = (tx + 0.5) / lw, (ty + 0.5) / lh
    ccx, ccy = uvx * G, uvy * G
    ci = (np.clip(ccy.astype(int), 0, G - 1), np.clip(ccx.astype(int), 0, G - 1))
    lc = lum[ci]
    aspect = W / H
    rot = np.mod((frame & 7) * 0.38196601, 1.0)
    n0 = np.mod(interleave3(tx, ty) + rot, 1.0)
    jit = np.mod(interleave3(ty + 1, tx + 1) * 0.875 + 0.0625 + rot * 0.5, 1.0)
    # r3: horizon march on the pseudo height field h = heightScale * B (mirrors CSMarch)
    hc = heightScale * lc
    bounce = np.zeros((lh, lw, 3), np.float32); aoAcc = np.zeros((lh, lw), np.float32)
    for r in range(rays):
        aj = (ign(tx + 17.0 * r, ty + 17.0 * r, frame) - 0.5) / 9.0      # r4: per-ray sub-bin angle jitter
        jr = np.mod(jit + 0.61803399 * r, 1.0)                           # r4: per-ray step offset
        ang = 2 * np.pi * (n0 + aj + r / rays)
        dx, dy = np.cos(ang), np.sin(ang) * aspect
        sinA = np.zeros((lh, lw), np.float32)
        sinH = np.full((lh, lw), -horizonBias, np.float32); d = np.zeros((lh, lw), np.float32); ln = stepCells
        alive = on.copy(); rayB = np.zeros((lh, lw, 3), np.float32)
        for s_ in range(steps):
            d = d + ln * ((0.5 + jr) if s_ == 0 else 1.0); ln *= stepGrowth
            px, py = ccx + dx * d, ccy + dy * d
            alive &= (px >= 0) & (py >= 0) & (px < G) & (py < G)
            ix, iy = np.clip(px.astype(int), 0, G - 1), np.clip(py.astype(int), 0, G - 1)
            l = lum[iy, ix]; dz = heightScale * l - hc; dc = d * stepCells
            sinS = dz / np.sqrt(dz * dz + dc * dc)
            dzA = np.minimum(dz, 0.25 * heightScale)                       # r5: contrast clamp + emitters do not occlude
            sinO = dzA / np.sqrt(dzA * dzA + dc * dc) * (1 - smoothstep(emitLo, emitHi, l))
            sinA = np.where(alive, np.maximum(sinA, sinO * np.clip(1 - dc / aoRadius, 0, 1)), sinA)
            up = alive & (sinS > sinH)
            add = np.where(up, (sinS - sinH) * smoothstep(emitLo, emitHi, l) / (1 + falloff * dc * dc), 0)
            rayB += add[..., None] * rgb[iy, ix] ** 2
            sinH = np.where(up, sinS, sinH)
        bounce += rayB; aoAcc += sinA
    bounce /= rays * (1 + horizonBias)
    bounce *= (1 - smoothstep(emitLo, emitHi, lc))[..., None]
    occ = np.clip(aoAcc / rays, 0, 1)
    occ *= 1 - smoothstep(emitLo, emitHi, lc)                          # r5: bright receivers not darkened
    ao = 1 - aoStrength * occ
    out = np.concatenate([np.sqrt(np.clip(bounce * ao[..., None], 0, 1)), lc[..., None], occ[..., None]], -1)
    out = np.where(on[..., None], out, np.concatenate([np.zeros((lh, lw, 3)), lc[..., None], np.zeros((lh, lw, 1))], -1))
    return quant(out), gate

QS = np.array([2047, 2047, 1023, 255, 255], np.float32)
def quant(x):   # r4 R32G32_UINT storage: sqrt bounce 11/11/10, guide 8, occlusion 8
    return (np.floor(np.clip(x, 0, 1) * QS + 0.5) / QS).astype(np.float32)

def filter3(light, rangeK=40.0, hist=None, alpha=0.0, shift=None):
    lh, lw, _ = light.shape; c = light[..., 3]
    acc = np.zeros((lh, lw, 4), np.float32); ws = np.zeros((lh, lw), np.float32)
    yy, xx = np.mgrid[0:lh, 0:lw]
    for k in range(9):
        ox, oy = k % 3 - 1, k // 3 - 1
        sm = light[np.clip(yy + oy, 0, lh - 1), np.clip(xx + ox, 0, lw - 1)]
        w = (2 - abs(ox)) * (2 - abs(oy)) * np.exp(-rangeK * (sm[..., 3] - c) ** 2)
        acc += w[..., None] * np.concatenate([sm[..., :3] ** 2, sm[..., 4:5]], -1); ws += w
    cur = acc / ws[..., None]
    if hist is not None and alpha > 0 and shift is not None and shift[2] > 0.5:   # r4 reprojected temporal (CSFilter)
        hx = np.floor(xx + 0.5 - shift[0] * lw / G).astype(int); hy = np.floor(yy + 0.5 - shift[1] * lh / G).astype(int)
        ok = (hx >= 0) & (hy >= 0) & (hx < lw) & (hy < lh)
        h = hist[np.clip(hy, 0, lh - 1), np.clip(hx, 0, lw - 1)]
        keep = np.where(ok, np.clip(1 - np.abs(h[..., 3] - c) * 10, 0, 1) * (1 - alpha), 0)
        hv = np.concatenate([h[..., :3] ** 2, h[..., 4:5]], -1)
        cur = cur + (hv - cur) * keep[..., None]
    return quant(np.concatenate([np.sqrt(cur[..., :3]), c[..., None], cur[..., 3:4]], -1))

def shift_search(grid, prev, maxSad=0.06):   # mirrors CSShift
    L = (grid >> 24).astype(np.float32) / 255; P = (prev >> 24).astype(np.float32) / 255
    cc = 8 + 4 * np.arange(28); sad = np.zeros((9, 9))
    for sy in range(-4, 5):
        for sx in range(-4, 5):
            sad[sy + 4, sx + 4] = np.abs(L[np.ix_(cc, cc)] - P[np.ix_(cc - sy, cc - sx)]).mean()
    flat = sad.ravel(); best = 40
    for t in range(81):
        if flat[t] < flat[best] - 1e-4: best = t
    by, bx = divmod(best, 9); f = [bx - 4.0, by - 4.0]; m = flat[best]
    if 0 < bx < 8:
        l, r = sad[by, bx - 1], sad[by, bx + 1]; den = l - 2 * m + r
        if den > 1e-5: f[0] += np.clip(0.5 * (l - r) / den, -0.5, 0.5)
    if 0 < by < 8:
        u, d = sad[by - 1, bx], sad[by + 1, bx]; den = u - 2 * m + d
        if den > 1e-5: f[1] += np.clip(0.5 * (u - d) / den, -0.5, 0.5)
    return np.array([f[0], f[1], 1.0 if m <= maxSad else 0.0, m])

def phase4(img, light, strength=1.0, rangeK=40.0, aoGame=0.35):
    H, W, _ = img.shape; lh, lw, _ = light.shape
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    bg = bright(img)
    lpx = (xx + 0.5) * lw / W - 0.5; lpy = (yy + 0.5) * lh / H - 0.5
    bx, by = np.floor(lpx).astype(int), np.floor(lpy).astype(int); fx, fy = lpx - bx, lpy - by
    acc = np.zeros((H, W, 4), np.float32); ws = np.zeros((H, W), np.float32)
    wbest = np.full((H, W), -1.0, np.float32); best = np.zeros((H, W, 4), np.float32)
    for k in range(4):
        ox, oy = k & 1, k >> 1
        sm = light[np.clip(by + oy, 0, lh - 1), np.clip(bx + ox, 0, lw - 1)]
        wb = (fx if ox else 1 - fx) * (fy if oy else 1 - fy)
        w = wb * np.exp(-rangeK * (bg - sm[..., 3]) ** 2)
        lin = np.concatenate([sm[..., :3] ** 2, sm[..., 4:5]], -1)
        acc += w[..., None] * lin; ws += w
        upd = wb > wbest; best = np.where(upd[..., None], lin, best); wbest = np.maximum(wb, wbest)
    lightUp = acc / np.maximum(ws, 0.25)[..., None]                  # r4: fade out instead of nearest-tap fallback
    g = img ** 2
    albedo = 0.5 * np.clip(g / np.maximum(luma(g), 0.02)[..., None], 0, 2)
    gA = g * (1 - min(aoGame, 0.6) * lightUp[..., 3:4])                  # r4: visible AO on the game image
    return np.sqrt(np.clip(gA + strength * lightUp[..., :3] * albedo, 0, 1)), lightUp

def to_png(a, path): Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8)).save(path)

# --- pan-test: temporal accumulation reprojected by FrameFX's FX_GlobalMotion estimate -----------------
# FX_GlobalMotion (Shaders/FrameFX.hlsl) writes a BACKWARD vector in UV:
#   prev luma at (x+dx, y+dy) is matched to curr luma at (x, y); output = (dx, dy) / (64, 36).
#   So curr(uv) came from prev(uv + pan). Callers sample the previous frame at uv + pan
#   (FX_FastSearch, FX_MvBorder: source = uv + gGlob * size).
# SSGI runs before this frame's EncodeMotionFast, so the 1x1 SRV still holds the PREVIOUS
# Process() estimate: motion from frame t-2 to t-1 while SSGI is shading frame t (one frame late).
# The estimate itself is the shader's search, not an extra noise source:
#   * luma box-averaged onto the same 64x36 grid (at 1280x720 that is exactly 20x20 px blocks;
#     the GPU reaches that grid via the 1/8 luma pyramid + 4 bilinear taps — same quantisation step)
#   * integer search ±12 x ±6 texels, cost = mean |dLuma| + 0.0005*(|dx|+|dy|)
#   * parabolic sub-texel fit, clamped to ±0.5 texel
# There is no confidence channel. A cut still returns some vector; SSGI rejects it when the
# vector is long or the pan-aligned proxy brightness still disagrees (see proxy_sad).

GM_W, GM_H, GM_RX, GM_RY = 64, 36, 12, 6

def gm_grid(img):
    lum = luma(img).astype(np.float32)
    H, W = lum.shape
    bh, bw = H // GM_H, W // GM_W
    if bh >= 1 and bw >= 1:
        y0 = (H - bh * GM_H) // 2; x0 = (W - bw * GM_W) // 2
        sl = lum[y0:y0 + bh * GM_H, x0:x0 + bw * GM_W]
        return sl.reshape(GM_H, bh, GM_W, bw).mean(axis=(1, 3))
    im = Image.fromarray(np.clip(lum * 255.0, 0, 255).astype(np.uint8))
    return np.asarray(im.resize((GM_W, GM_H), Image.BOX), np.float32) * (1.0 / 255.0)

def fx_global_motion(prev, curr):
    """Numpy port of FX_GlobalMotion. Returns backward UV (float2) and the winning cost."""
    A, B = gm_grid(prev), gm_grid(curr)
    cost = np.full((2 * GM_RY + 1, 2 * GM_RX + 1), 1e9, np.float32)
    for dy in range(-GM_RY, GM_RY + 1):
        for dx in range(-GM_RX, GM_RX + 1):
            y0, y1 = max(0, -dy), min(GM_H, GM_H - dy)
            x0, x1 = max(0, -dx), min(GM_W, GM_W - dx)
            n = (y1 - y0) * (x1 - x0)
            if n < 400: continue
            sad = np.abs(A[y0 + dy:y1 + dy, x0 + dx:x1 + dx] - B[y0:y1, x0:x1]).mean()
            cost[dy + GM_RY, dx + GM_RX] = sad + 0.0005 * (abs(dx) + abs(dy))
    iy, ix = np.unravel_index(int(np.argmin(cost)), cost.shape)
    bc = float(cost[iy, ix]); gx, gy = float(ix - GM_RX), float(iy - GM_RY)
    if 0 < ix < 2 * GM_RX:
        cl, cr = float(cost[iy, ix - 1]), float(cost[iy, ix + 1]); den = cl - 2.0 * bc + cr
        if den > 1e-9: gx += float(np.clip(0.5 * (cl - cr) / den, -0.5, 0.5))
    if 0 < iy < 2 * GM_RY:
        cu, cd = float(cost[iy - 1, ix]), float(cost[iy + 1, ix]); den = cu - 2.0 * bc + cd
        if den > 1e-9: gy += float(np.clip(0.5 * (cu - cd) / den, -0.5, 0.5))
    return np.array([gx / GM_W, gy / GM_H], np.float32), bc

def proxy_sad(grid, prev, pan_uv):
    """Mean |dB| of the 28x28 stride-4 proxy samples after the backward pan (CSPanGate). 1 if unknown."""
    if prev is None or pan_uv is None: return 1.0
    L = (grid >> 24).astype(np.float32) * (1.0 / 255.0)
    P = (prev >> 24).astype(np.float32) * (1.0 / 255.0)
    pcx, pcy = float(pan_uv[0]) * G, float(pan_uv[1]) * G
    cc = 8 + 4 * np.arange(28, dtype=np.float32)
    xs = cc[None, :] + pcx; ys = cc[:, None] + pcy
    x0 = np.floor(xs).astype(np.int32); y0 = np.floor(ys).astype(np.int32)
    ok = (x0 >= 0) & (y0 >= 0) & (x0 + 1 < G) & (y0 + 1 < G)
    if int(ok.sum()) < 200: return 1.0
    fx = (xs - np.floor(xs))[..., None] if False else (xs - x0)
    fy = ys - y0
    x0c = np.clip(x0, 0, G - 1); y0c = np.clip(y0, 0, G - 1)
    x1c = np.clip(x0 + 1, 0, G - 1); y1c = np.clip(y0 + 1, 0, G - 1)
    s00 = P[y0c, x0c]; s10 = P[y0c, x1c]; s01 = P[y1c, x0c]; s11 = P[y1c, x1c]
    samp = (s00 * (1 - fx) + s10 * fx) * (1 - fy) + (s01 * (1 - fx) + s11 * fx) * fy
    Lc = L[cc.astype(np.int32)[:, None], cc.astype(np.int32)[None, :]]
    return float(np.abs(Lc - samp)[ok].mean())

def neigh_minmax(light):
    """Min/max of the current 3x3 in linear rgb and occlusion (edge-clamped), matching CSFilter."""
    lin = light[..., :3] ** 2
    occ = light[..., 4]
    lh, lw = lin.shape[:2]
    yy, xx = np.mgrid[0:lh, 0:lw]
    rmin = np.full_like(lin, 1e9); rmax = np.full_like(lin, -1e9)
    omin = np.full((lh, lw), 1e9, np.float32); omax = np.full((lh, lw), -1e9, np.float32)
    for oy in (-1, 0, 1):
        for ox in (-1, 0, 1):
            s = lin[np.clip(yy + oy, 0, lh - 1), np.clip(xx + ox, 0, lw - 1)]
            o = occ[np.clip(yy + oy, 0, lh - 1), np.clip(xx + ox, 0, lw - 1)]
            rmin = np.minimum(rmin, s); rmax = np.maximum(rmax, s)
            omin = np.minimum(omin, o); omax = np.maximum(omax, o)
    return rmin.astype(np.float32), rmax.astype(np.float32), omin, omax

def sample_light(light, x, y):
    """Bilinear sample. x, y are continuous texel coords (0 at texel 0's corner-origin, GPU: id + pan*size).
    Returns (sample, ok) where ok is False if the 2x2 would leave the texture (disocclusion / off-screen)."""
    lh, lw, _ = light.shape
    x0 = np.floor(x).astype(np.int32); y0 = np.floor(y).astype(np.int32)
    ok = (x0 >= 0) & (y0 >= 0) & (x0 + 1 < lw) & (y0 + 1 < lh)
    fx = (x - np.floor(x))[..., None]; fy = (y - np.floor(y))[..., None]
    xs0 = np.clip(x0, 0, lw - 1); ys0 = np.clip(y0, 0, lh - 1)
    xs1 = np.clip(x0 + 1, 0, lw - 1); ys1 = np.clip(y0 + 1, 0, lh - 1)
    s00 = light[ys0, xs0]; s10 = light[ys0, xs1]; s01 = light[ys1, xs0]; s11 = light[ys1, xs1]
    samp = (s00 * (1 - fx) + s10 * fx) * (1 - fy) + (s01 * (1 - fx) + s11 * fx) * fy
    return samp, ok

def reproject_blend(cur, hist, pan_uv, alpha, guide_k, bounds, box=1.0):
    """CSFilter external-pan path. pan_uv is the FX_GlobalMotion backward vector (sample history at uv+pan).
    Neighbourhood clamp is min/max of the current 3x3 in linear light, pulled toward the current texel by
    `box` (1 = the full 3x3 box, smaller = tighter). Guide reject drops history where brightness changed.
    alpha is the new-frame weight. The stored guide stays the current texel's guide."""
    lh, lw, _ = cur.shape
    yy, xx = np.mgrid[0:lh, 0:lw].astype(np.float32)
    samp, ok = sample_light(hist, xx + float(pan_uv[0]) * lw, yy + float(pan_uv[1]) * lh)
    rmin, rmax, omin, omax = bounds
    clin = cur[..., :3] ** 2
    b = float(box)
    lo = clin + (rmin - clin) * b; hi = clin + (rmax - clin) * b
    hlin = np.clip(samp[..., :3] ** 2, np.minimum(lo, hi), np.maximum(lo, hi))
    cocc = cur[..., 4]
    olo = cocc + (omin - cocc) * b; ohi = cocc + (omax - cocc) * b
    hocc = np.clip(samp[..., 4], np.minimum(olo, ohi), np.maximum(olo, ohi))
    keep = np.clip(1.0 - np.abs(samp[..., 3] - cur[..., 3]) * guide_k, 0.0, 1.0) * (1.0 - alpha)
    keep = np.where(ok, keep, 0.0)
    blended = clin + (hlin - clin) * keep[..., None]
    occ = cur[..., 4] + (hocc - cur[..., 4]) * keep
    out = np.concatenate([np.sqrt(np.clip(blended, 0, 1)), cur[..., 3:4], occ[..., None]], -1)
    return quant(out)

def shift_valid(img, bx, by):
    """out[y, x] = img[y+by, x+bx] (integer). valid where the source was on screen."""
    H, W = img.shape[:2]
    out = np.zeros_like(img); valid = np.zeros((H, W), bool)
    bx, by = int(bx), int(by)
    x0, x1 = max(0, -bx), min(W, W - bx)
    y0, y1 = max(0, -by), min(H, H - by)
    if x1 > x0 and y1 > y0:
        out[y0:y1, x0:x1] = img[y0 + by:y1 + by, x0 + bx:x1 + bx]
        valid[y0:y1, x0:x1] = True
    return out, valid

def dilate_mask(mask, r):
    if r <= 0: return mask
    p = np.pad(mask, r, constant_values=False)
    v = np.lib.stride_tricks.sliding_window_view(p, (2 * r + 1, 2 * r + 1))
    return v.any(axis=(-1, -2))

def crop_at(big, x0, y0, W, H):
    return np.ascontiguousarray(big[y0:y0 + H, x0:x0 + W])

def converged_from_grid(grid, bits, W, H, lw, lh, preset, height, falloff, rotations=(0, 2, 4, 6)):
    kw = dict(rays=preset['rays'], steps=preset['steps'], stepCells=preset['stepCells'],
              stepGrowth=preset['stepGrowth'], heightScale=height, falloff=falloff)
    acc_l = None; acc_o = None; guide = None
    for fi in rotations:
        raw, _ = phase3(grid, bits, W, H, lw, lh, frame=int(fi), **kw)
        acc_l = raw[..., :3] ** 2 if acc_l is None else acc_l + raw[..., :3] ** 2
        acc_o = raw[..., 4] if acc_o is None else acc_o + raw[..., 4]
        guide = raw[..., 3]
    n = float(len(rotations))
    packed = np.concatenate([np.sqrt(np.clip(acc_l / n, 0, 1)), guide[..., None], (acc_o / n)[..., None]], -1)
    return filter3(quant(packed))

def simulate_sequence(frames, preset, height, falloff, true_back, cut_at, obj_masks, rotations=(0, 2, 4, 6)):
    """frames: list of HxWx3. true_back[t] is the backward pan in pixels of frame t vs t-1 (0,0 at t=0).
    Returns dict of per-frame arrays the temporal filter and the metrics need."""
    W = frames[0].shape[1]; H = frames[0].shape[0]
    lw, lh = max(G, W // preset['div']), max(G, H // preset['div'])
    lights, refs, grids, pans, bounds = [], [], [], [], []
    prev_img = None; delayed = None
    n = len(frames)
    for t, img in enumerate(frames):
        grid = phase1(img)
        bits, _, _ = phase2(grid, lx=preset.get('lx', 5), ly=preset.get('ly', 6))
        kw = dict(rays=preset['rays'], steps=preset['steps'], stepCells=preset['stepCells'],
                  stepGrowth=preset['stepGrowth'], heightScale=height, falloff=falloff)
        raw, _ = phase3(grid, bits, W, H, lw, lh, frame=t, **kw)
        filt = filter3(raw)
        ref = converged_from_grid(grid, bits, W, H, lw, lh, preset, height, falloff, rotations)
        est = None
        if prev_img is not None:
            est, _ = fx_global_motion(prev_img, img)
        pans.append(delayed)          # what SSGI sees: previous Process() result, or None
        delayed = est
        prev_img = img
        lights.append(filt); refs.append(ref); grids.append(grid); bounds.append(neigh_minmax(filt))
        print(f'  frame {t + 1}/{n}', flush=True)
    return dict(lights=lights, refs=refs, grids=grids, pans=pans, bounds=bounds, W=W, H=H, lw=lw, lh=lh,
                true_back=true_back, cut_at=cut_at, obj_masks=obj_masks, frames=frames)

def run_temporal(seq, mode, alpha, guide_k, lum_reset, pan_max_px, box=1.0):
    """mode 'a' current SSGI, 'b' temporal + pan reprojection, 'c' temporal with a zero pan (no reprojection)."""
    hist = None; prev_grid = None; outs = []
    W, H = seq['W'], seq['H']
    for t, cur in enumerate(seq['lights']):
        pan = seq['pans'][t]
        reset = hist is None
        if mode == 'b':
            if pan is None: reset = True
            else:
                mag = float(np.hypot(pan[0] * W, pan[1] * H))
                sad = proxy_sad(seq['grids'][t], prev_grid, pan)
                if mag > pan_max_px or sad > lum_reset: reset = True
            use = pan if (pan is not None and not reset) else None
        elif mode == 'c':
            # Same blend and clamp, but history is sampled in place. No pan-magnitude reset: this is the
            # ghosting the reprojection exists to avoid. A full-frame brightness break still drops history
            # per texel via the guide test; there is no global reset, so a cut leaves a trail.
            use = np.zeros(2, np.float32) if not reset else None
        else:
            use = None; reset = True
        if reset or use is None:
            out = cur
        else:
            out = reproject_blend(cur, hist, use, alpha, guide_k, seq['bounds'][t], box)
        outs.append(out); hist = out; prev_grid = seq['grids'][t]
    return outs

def _f(x):
    return float(x) if x is not None else None

def composite_metrics(seq, lights, strength, border=8, edge_r=8):
    """Metrics on the composited image (phase 4).
    Flicker: mean |Δ| of motion-compensated composited frames on pixels that should be stable
    (on-screen source, away from the moving-object edge, not a cut frame, not the image border).
    Accuracy: mean |composite - converged reference| on those stable pixels.
    Ghosting: |composite - reference| on disoccluded strips, moving-object edges and the cut frame
    plus the one after it. max and 99th percentile."""
    frames = seq['frames']; true_back = seq['true_back']; cut_at = seq['cut_at']; objs = seq['obj_masks']
    n = len(frames)
    comps, rcomps = [], []
    for t in range(n):
        c, _ = phase4(frames[t], lights[t], strength)
        r, _ = phase4(frames[t], seq['refs'][t], strength)
        comps.append(c); rcomps.append(r)
        print(f'    composite {t + 1}/{n}', flush=True)
    flick_n = flick_a = 0.0
    acc_n = acc_a = 0.0
    ghost = []
    cut_err_b = []   # |this - reference| on cut frames, for the trail check vs method (a) done by the caller
    cut_diff = []    # filled by caller when comparing two methods; here we record per-pixel ref error and the image
    for t in range(n):
        H, W, _ = comps[t].shape
        if t == 0:
            valid = np.ones((H, W), bool)
        else:
            bx, by = true_back[t]
            _, valid = shift_valid(comps[t - 1], int(round(bx)), int(round(by)))
        disocc = np.logical_not(valid)
        edge = np.zeros((H, W), bool)
        if objs is not None:
            edge = dilate_mask(objs[t], edge_r)
            if t > 0: edge |= dilate_mask(objs[t - 1], edge_r)
        cut_m = np.zeros((H, W), bool)
        if cut_at is not None and (t == cut_at or t == cut_at + 1): cut_m[:] = True
        border_m = np.zeros((H, W), bool)
        border_m[:border] = border_m[-border:] = border_m[:, :border] = border_m[:, -border:] = True
        ghost_m = disocc | edge | cut_m
        stable = valid & np.logical_not(edge) & np.logical_not(cut_m) & np.logical_not(border_m)
        err = np.abs(comps[t] - rcomps[t]).mean(-1)
        acc_a += float(err[stable].sum()); acc_n += int(stable.sum())
        if ghost_m.any(): ghost.append(err[ghost_m].astype(np.float32).ravel())
        if cut:
            cut_err_b.append(err.astype(np.float32))
        if t > 0:
            warped, v = shift_valid(comps[t - 1], int(round(true_back[t][0])), int(round(true_back[t][1])))
            d = np.abs(warped - comps[t]).mean(-1)
            m = stable & v
            flick_a += float(d[m].sum()); flick_n += int(m.sum())
    ghost_all = np.concatenate(ghost) if ghost else np.zeros(1, np.float32)
    return dict(
        flicker=_f(flick_a / flick_n) if flick_n else None,
        accuracy=_f(acc_a / acc_n) if acc_n else None,
        ghost_max=_f(ghost_all.max()),
        ghost_p99=_f(np.percentile(ghost_all, 99)),
        ghost_n=int(ghost_all.size),
        cut_mean=_f(np.mean(np.stack([e.mean() for e in cut_err_b]))) if cut_err_b else None,
        comps=comps,
    )

def trail_mean(comps_b, comps_a, cut_at):
    """Mean |b-a| on the cut frame and the one after (0 when there is no cut). A reset makes this ~0 on the cut frame."""
    if cut_at is None: return 0.0
    acc = []
    for t in (cut_at, cut_at + 1):
        if 0 <= t < len(comps_b):
            acc.append(np.abs(comps_b[t] - comps_a[t]).mean())
    return float(np.mean(acc)) if acc else 0.0

def build_scenes(n, W=1280, H=720):
    """Five sequences, each n frames, at 1280x720. Returns list of scenario dicts."""
    # Room for the fastest pan (16 px/frame) and the diagonal (6 px/frame) to stay inside the picture.
    need_x = 16 * (n - 1) + 8; need_y = 6 * (n - 1) + 8
    span_x, span_y = need_x * 2, need_y * 2
    big = make_scene(W + span_x, H + span_y)
    # A second picture for the cut: mirrored and regraded so the brightness really changes.
    alt = np.clip(big[:, ::-1] * np.array([0.42, 0.50, 0.80], np.float32) + 0.04, 0, 1)
    x_m = span_x // 2; y_m = span_y // 2

    def pan_seq(px, py, image):
        frames, backs = [], []
        for t in range(n):
            frames.append(crop_at(image, x_m - px * t, y_m - py * t, W, H))
            backs.append((0.0, 0.0) if t == 0 else (float(-px), float(-py)))  # backward: content moved +(px,py)
        return frames, backs

    slow_f, slow_b = pan_seq(2, 0, big)
    fast_f, fast_b = pan_seq(16, 0, big)
    diag_f, diag_b = pan_seq(10, 6, big)
    # Cut: 2 px/frame on `big`, then a jump onto `alt` (still 2 px/frame) at the midpoint.
    cut = n // 2
    cut_f, cut_b = [], []
    for t in range(n):
        src = big if t < cut else alt
        # restart the crop on alt so the jump is a content cut, not a continuation
        tt = t if t < cut else t - cut
        cut_f.append(crop_at(src, x_m - 2 * tt, y_m - 2 * tt, W, H))
        if t == 0 or t == cut: cut_b.append((0.0, 0.0))
        else: cut_b.append((-2.0, 0.0))
    # Small bright object, 8 px/frame, over a static frame. Camera pan is zero.
    base = make_scene(W, H)
    obj_f, obj_b, obj_m = [], [], []
    ow, oh, oy = 56, 56, H // 2 - 28
    for t in range(n):
        img = base.copy(); m = np.zeros((H, W), bool)
        x = 180 + t * 8
        img[oy:oy + oh, x:x + ow] = np.array([1.0, 0.78, 0.28], np.float32)
        img[oy:oy + oh, x:x + 3] = 0.04
        m[oy:oy + oh, x:x + ow] = True
        obj_f.append(img); obj_b.append((0.0, 0.0)); obj_m.append(m)
    return [
        dict(name='horiz_slow_2px', frames=slow_f, back=slow_b, cut=None, obj=None),
        dict(name='horiz_fast_16px', frames=fast_f, back=fast_b, cut=None, obj=None),
        dict(name='diagonal_10x6', frames=diag_f, back=diag_b, cut=None, obj=None),
        dict(name='cut', frames=cut_f, back=cut_b, cut=cut, obj=None),
        dict(name='moving_object', frames=obj_f, back=obj_b, cut=None, obj=obj_m),
    ]

def lighting_flicker(seq, outs):
    """Cheap stand-in used only to rank blend settings: mean |Δ| of reprojected lighting on stable texels."""
    lh, lw = seq['lh'], seq['lw']
    scale = np.array([seq['W'] / lw, seq['H'] / lh], np.float32)
    acc = n = 0.0
    for t in range(1, len(outs)):
        bx, by = seq['true_back'][t]
        # lighting-texel backward (source px / lighting px)
        tbx, tby = bx / float(scale[0]), by / float(scale[1])
        warped, valid = shift_valid(outs[t - 1], int(round(tbx)), int(round(tby)))
        if seq['cut_at'] is not None and (t == seq['cut_at'] or t == seq['cut_at'] + 1):
            continue
        d = np.abs(warped[..., :3] - outs[t][..., :3]).mean(-1)
        m = valid.copy()
        m[:2] = m[-2:] = m[:, :2] = m[:, -2:] = False
        acc += float(d[m].mean()); n += 1
    return acc / n if n else 0.0

def pan_selfcheck(big_probe=None):
    """Known shift: content moves +16 x px. Backward UV * width should come back near -16."""
    W, H = 1280, 720
    img = make_scene(W + 64, H)
    a = np.ascontiguousarray(img[:, 32:32 + W])
    b = np.ascontiguousarray(img[:, 32 - 16:32 - 16 + W])   # crop origin -16: content moved +16
    uv, cost = fx_global_motion(a, b)
    px = float(uv[0] * W); py = float(uv[1] * H)
    print(f'pan model self-check: content +16 px -> estimated backward ({px:+.2f}, {py:+.2f}) px, cost {cost:.4f} (expect ~-16, 0)', flush=True)
    return px, py

def frame_masks(seq, t, border=8, edge_r=8):
    H, W = seq['frames'][t].shape[:2]
    if t == 0:
        valid = np.ones((H, W), bool)
    else:
        bx, by = seq['true_back'][t]
        _, valid = shift_valid(seq['frames'][t - 1], int(round(bx)), int(round(by)))
    edge = np.zeros((H, W), bool)
    objs = seq['obj_masks']
    if objs is not None:
        edge = dilate_mask(objs[t], edge_r)
        if t > 0: edge |= dilate_mask(objs[t - 1], edge_r)
    cut_m = np.zeros((H, W), bool)
    cut_at = seq['cut_at']
    if cut_at is not None and (t == cut_at or t == cut_at + 1): cut_m[:] = True
    border_m = np.zeros((H, W), bool)
    border_m[:border] = border_m[-border:] = border_m[:, :border] = border_m[:, -border:] = True
    ghost_m = np.logical_not(valid) | edge | cut_m
    stable = valid & np.logical_not(edge) & np.logical_not(cut_m) & np.logical_not(border_m)
    return stable, ghost_m, cut_m

def score_bundles(seq, bundles, strength):
    """Composite each lighting bundle once per frame (reference included) and accumulate metrics.
    bundles is a list of (tag, lights). Tag 'a' is the no-temporal output; tags starting with 'b' or 'c' are compared to it."""
    n = len(seq['frames'])
    tags = [t for t, _ in bundles]
    prev = {t: None for t in tags}
    flick = {t: [0.0, 0] for t in tags}
    acc = {t: [0.0, 0] for t in tags}
    ghost = {t: [] for t in tags}
    trail = {t: [0.0, 0] for t in tags}
    for i in range(n):
        ref, _ = phase4(seq['frames'][i], seq['refs'][i], strength)
        comps = {}
        for tag, lights in bundles:
            c, _ = phase4(seq['frames'][i], lights[i], strength)
            comps[tag] = c
        stable, ghost_m, cut_m = frame_masks(seq, i)
        for tag, c in comps.items():
            err = np.abs(c - ref).mean(-1)
            if stable.any():
                acc[tag][0] += float(err[stable].sum()); acc[tag][1] += int(stable.sum())
            if ghost_m.any():
                ghost[tag].append(err[ghost_m].astype(np.float32).ravel())
            if tag != 'a' and cut_m.any():
                trail[tag][0] += float(np.abs(c - comps['a']).mean()); trail[tag][1] += 1
            if i > 0 and prev[tag] is not None:
                bx, by = seq['true_back'][i]
                warped, v = shift_valid(prev[tag], int(round(bx)), int(round(by)))
                d = np.abs(warped - c).mean(-1)
                m = stable & v
                if m.any():
                    flick[tag][0] += float(d[m].sum()); flick[tag][1] += int(m.sum())
        prev = comps
        print(f'    frame {i + 1}/{n}', flush=True)
    out = {}
    for tag in tags:
        g = np.concatenate(ghost[tag]) if ghost[tag] else np.zeros(1, np.float32)
        out[tag] = dict(
            flicker=(flick[tag][0] / flick[tag][1]) if flick[tag][1] else None,
            accuracy=(acc[tag][0] / acc[tag][1]) if acc[tag][1] else None,
            ghost_max=float(g.max()), ghost_p99=float(np.percentile(g, 99)),
            trail=(trail[tag][0] / trail[tag][1]) if trail[tag][1] else 0.0,
        )
    return out

def run_pan_test(out_dir, preset, height, falloff, strength, frames=24, rotations=(0, 2, 4, 6), seed=1):
    # Explicit seed. The scene builder does not draw from numpy's global RNG; seeding it
    # keeps any incidental use stable without changing the default sequence (seed 1).
    np.random.seed(int(seed))
    os.makedirs(out_dir, exist_ok=True)
    px, py = pan_selfcheck()
    print('building scenes…', flush=True)
    scenes = build_scenes(frames)
    # One scenario at a time so the full-res composites are not all resident together.
    # Candidates: new-frame weight x guide-reject. lum_reset is measured per the whole set, so the
    # proxy SADs are collected on a first pass over the (already built) scenes' grids only... they
    # fall out of the same simulate() call, so each scenario is simulated, scored, then dropped.
    # lum_reset must be known before the blend. Measure it with a grid-only prepass.
    print('measuring pan-aligned brightness breaks…', flush=True)
    pan_sads, cut_sads = [], []
    cached = []
    for sc in scenes:
        print(f'scenario {sc["name"]} ({frames} frames, {len(rotations)} reference rotations)', flush=True)
        seq = simulate_sequence(sc['frames'], preset, height, falloff, sc['back'], sc['cut'], sc['obj'], rotations)
        prev = None
        for t in range(len(seq['grids'])):
            pan = seq['pans'][t]
            sad = proxy_sad(seq['grids'][t], prev, pan if pan is not None else np.zeros(2, np.float32))
            if sc['name'] == 'cut' and seq['cut_at'] is not None and t == seq['cut_at']:
                cut_sads.append(sad)
            elif sc['name'] not in ('cut', 'moving_object') and pan is not None and t > 2:
                pan_sads.append(sad)
            prev = seq['grids'][t]
        cached.append((sc['name'], seq))
    pan_hi = float(np.percentile(pan_sads, 95)) if pan_sads else 0.02
    cut_lo = float(min(cut_sads)) if cut_sads else 0.2
    if cut_lo > pan_hi * 1.5:
        lum_reset = float(np.sqrt(max(pan_hi, 1e-4) * cut_lo))
    else:
        lum_reset = float(pan_hi * 1.8 + 0.01)
    lum_reset = float(np.clip(lum_reset, 0.02, 0.25))
    pan_max_px = 48.0
    print(f'SAD pan p95 {pan_hi:.4f}  cut {cut_lo:.4f}  -> lum_reset {lum_reset:.4f}  pan_max_px {pan_max_px:.0f}', flush=True)
    # guide 8 won the lighting-space rank; sweep the new-frame weight. 'c' is the same blend with a zero pan.
    alphas = (0.22, 0.35, 0.50)
    guide = 8.0
    per_sc = []
    for name, seq in cached:
        print(f'compositing {name}', flush=True)
        bundles = [('a', seq['lights'])]
        for alpha in alphas:
            bundles.append((f'b{alpha:.2f}', run_temporal(seq, 'b', alpha, guide, lum_reset, pan_max_px)))
            bundles.append((f'c{alpha:.2f}', run_temporal(seq, 'c', alpha, guide, lum_reset, pan_max_px)))
        per_sc.append((name, score_bundles(seq, bundles, strength)))
        seq.clear()
    # Aggregate. Decision uses the best alpha that clears the ghost rule, else the best reduction with default OFF.
    results = []
    for alpha in alphas:
        bt, ct = f'b{alpha:.2f}', f'c{alpha:.2f}'
        def col(tag, key):
            xs = [m[tag][key] for _, m in per_sc if m[tag][key] is not None]
            return float(np.mean(xs)) if xs else None
        fa, fb, fc = col('a', 'flicker'), col(bt, 'flicker'), col(ct, 'flicker')
        reduction = (1.0 - fb / fa) if fa else 0.0
        ratios, trails, rows = [], [], []
        for name, m in per_sc:
            ga, gb = m['a']['ghost_p99'], m[bt]['ghost_p99']
            if ga and ga > 1e-8: ratios.append(gb / ga)
            trails.append(m[bt]['trail'])
            rows.append(dict(name=name,
                             flicker_a=m['a']['flicker'], flicker_b=m[bt]['flicker'], flicker_c=m[ct]['flicker'],
                             acc_a=m['a']['accuracy'], acc_b=m[bt]['accuracy'], acc_c=m[ct]['accuracy'],
                             ghost_max_a=m['a']['ghost_max'], ghost_max_b=m[bt]['ghost_max'], ghost_max_c=m[ct]['ghost_max'],
                             ghost_p99_a=ga, ghost_p99_b=gb, ghost_p99_c=m[ct]['ghost_p99'],
                             trail_mean_abs=m[bt]['trail']))
        worst_ratio = float(max(ratios)) if ratios else 1.0
        trail = float(max(trails)) if trails else 0.0
        ghost_ok = worst_ratio <= 1.10 and trail * 255.0 <= 1.5
        flicker_ok = reduction >= 0.30
        results.append(dict(alpha=alpha, guide=guide, lum_reset=lum_reset, pan_max_px=pan_max_px,
                            flicker_a=fa, flicker_b=fb, flicker_c=fc, reduction=reduction,
                            acc_a=col('a', 'accuracy'), acc_b=col(bt, 'accuracy'), acc_c=col(ct, 'accuracy'),
                            worst_ghost_p99_ratio=worst_ratio, trail_max=trail,
                            ghost_ok=bool(ghost_ok), flicker_ok=bool(flicker_ok), per=rows))
    passing = [r for r in results if r['ghost_ok'] and r['flicker_ok']]
    if passing:
        chosen = max(passing, key=lambda r: r['reduction']); enable = True
    else:
        chosen = max(results, key=lambda r: r['reduction']); enable = False
    reason = ('temporal on by default: flicker reduction {:.1f}% and ghost p99 ratio {:.3f}, trail {:.2f}/255'.format(
              chosen['reduction'] * 100, chosen['worst_ghost_p99_ratio'], chosen['trail_max'] * 255)
              if enable else
              'temporal OFF by default: flicker reduction {:.1f}% (need >= 30), worst ghost p99 ratio {:.3f} (need <= 1.10), trail {:.2f}/255 (need <= 1.5)'.format(
              chosen['reduction'] * 100, chosen['worst_ghost_p99_ratio'], chosen['trail_max'] * 255))
    summary = dict(
        decision='on' if enable else 'off', enable_by_default=enable, reason=reason, seed=int(seed),
        pan_model='FX_GlobalMotion 64x36 integer search + 0.0005 regulariser + parabolic sub-texel, one frame late (SSGI runs before Process writes _gmv). Sample history at uv+pan (backward UV).',
        selfcheck_backward_px=[px, py], chosen=chosen, candidates=results,
        sad=dict(pan_p95=pan_hi, cut=cut_lo, lum_reset=lum_reset, pan_max_px=pan_max_px),
        clamp_box=1.0,
    )
    path = os.path.join(out_dir, 'pan_temporal.json')
    with open(path, 'w') as f:
        json.dump(summary, f, indent=2)
    print('\n=== pan test (composited, /255) ===', flush=True)
    hdr = f'{"scenario":<18} {"flick a":>8} {"flick b":>8} {"flick c":>8} {"acc a":>8} {"acc b":>8} {"gP99 a":>8} {"gP99 b":>8} {"gP99 c":>8} {"trail":>8}'
    print(hdr)
    for p in chosen['per']:
        def q(v): return f'{(v or 0) * 255:8.3f}'
        print(f'{p["name"]:<18} {q(p["flicker_a"])} {q(p["flicker_b"])} {q(p["flicker_c"])} {q(p["acc_a"])} {q(p["acc_b"])} {q(p["ghost_p99_a"])} {q(p["ghost_p99_b"])} {q(p["ghost_p99_c"])} {q(p["trail_mean_abs"])}')
    print(f'chosen alpha {chosen["alpha"]:.2f}  guide {chosen["guide"]:.0f}  lum_reset {lum_reset:.4f}  pan_max_px {pan_max_px:.0f}')
    print(f'flicker a {chosen["flicker_a"] * 255:.3f}  b {chosen["flicker_b"] * 255:.3f}  c {chosen["flicker_c"] * 255:.3f}   reduction {chosen["reduction"] * 100:.1f}%')
    print(f'worst ghost p99 ratio b/a {chosen["worst_ghost_p99_ratio"]:.3f}   trail {chosen["trail_max"] * 255:.3f}/255')
    print('DECISION:', 'ON by default' if enable else 'OFF by default')
    print(reason)
    print('json:', path, flush=True)
    return summary

if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--image'); ap.add_argument('--out', default='/workspace/ssgi_preview')
    ap.add_argument('--strength', type=float, default=1.5)
    ap.add_argument('--height', type=float, default=24.0); ap.add_argument('--falloff', type=float, default=0.002)
    ap.add_argument('--pan', action='store_true', help='r4 synthetic pan test for reprojected temporal accumulation')
    ap.add_argument('--pan-test', action='store_true', help='temporal reprojection from the FrameFX camera-pan estimate')
    ap.add_argument('--frames', type=int, default=24)
    ap.add_argument('--rotations', type=int, default=4, help='noise rotations averaged into the converged reference (1..8)')
    ap.add_argument('--preset', default='default', choices=['default', 'gtx1050ti', 'gtx980ti'])
    ap.add_argument('--ssrt', action='store_true', help='SSRT reference: depth, reflections, contact shadows, cost, metrics')
    ap.add_argument('--ssrt-temporal', action='store_true', help='SSRT temporal accumulation, several seeds')
    ap.add_argument('--seed', type=int, default=1, help='explicit RNG seed (pan-test and ssrt-temporal)')
    a = ap.parse_args()
    if a.ssrt_temporal:
        import sys
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import ssrt_temporal
        ssrt_temporal.run(sys.modules['__main__'], seeds=tuple(range(int(a.seed), int(a.seed) + 16)))
        raise SystemExit(0)
    if a.ssrt:
        import sys
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import ssrt_ref
        ssrt_ref.run(sys.modules['__main__'])
        raise SystemExit(0)
    P = dict(default=dict(div=4, rays=2, steps=12, stepCells=1.5, stepGrowth=1.12),
             gtx1050ti=dict(div=4, rays=4, steps=16, stepCells=1.0, stepGrowth=1.15, lx=5, ly=6),
             gtx980ti=dict(div=3, rays=6, steps=20, stepCells=0.8, stepGrowth=1.13, lx=5, ly=6))[a.preset]
    if a.pan_test:
        cycle = (0, 2, 4, 6, 1, 3, 5, 7)
        rots = cycle[:max(1, min(8, a.rotations))]
        run_pan_test(a.out, P, a.height, a.falloff, a.strength, max(8, a.frames), rots, seed=a.seed)
        raise SystemExit(0)
    os.makedirs(a.out, exist_ok=True)
    if a.pan:
        big = make_scene(1400, 720); NF, PX = 8, 7                          # pan 7 px/frame to the right (~0.7 cell)
        kwp = dict(rays=P['rays'], steps=P['steps'], stepCells=P['stepCells'], stepGrowth=P['stepGrowth'], heightScale=a.height, falloff=a.falloff)
        hist = {'off': None, 'nopan': None, 'pan': None}; prevGrid = None; shifts = []
        for k in range(NF):
            img = np.ascontiguousarray(big[:, 112 - PX * k: 112 - PX * k + 1280]) # camera pans right: content moves +x
            H, W, _ = img.shape; lw, lh = max(G, W // P['div']), max(G, H // P['div'])
            grid = phase1(img); bits, _, _ = phase2(grid, lx=P.get('lx', 5), ly=P.get('ly', 6))
            raw, _ = phase3(grid, bits, W, H, lw, lh, frame=k, **kwp)
            sh = shift_search(grid, prevGrid) if prevGrid is not None else np.array([0, 0, 0, 1.0])
            shifts.append(sh); prevGrid = grid
            hist['off'] = filter3(raw)
            hist['nopan'] = filter3(raw, hist=hist['nopan'], alpha=0.25, shift=np.array([0, 0, 1.0 if k else 0, 0]))
            hist['pan'] = filter3(raw, hist=hist['pan'], alpha=0.25, shift=sh)
        ref, _ = phase3(grid, bits, W, H, lw, lh, frame=0, forceAll=True, **dict(kwp, rays=8))
        refF, _ = phase4(img, filter3(ref), a.strength)
        res = {}
        for kk in hist:
            fin, _ = phase4(img, hist[kk], a.strength); res[kk] = fin
            bl = np.asarray(Image.fromarray((np.clip((fin - img) * 4, 0, 1) * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(3)), np.float32) / 255 / 4
            print(f'pan {kk:5s}: mean |final - ref(8 rays, ungated)| {np.abs(fin - refF).mean() * 255:.3f}/255, '
                  f'p99 {np.percentile(np.abs(fin - refF), 99) * 255:.1f}/255, grain {np.abs(np.clip(fin - img, 0, 1) - bl).mean() * 255:.3f}/255')
        print('shift estimates (cells, true +%.2f x):' % (PX * G / 1280), ' '.join(f'({x[0]:+.2f},{x[1]:+.2f},{int(x[2])})' for x in shifts[1:]))
        strip = np.concatenate([np.clip((res[kk] - img) * 4 + 0.0, 0, 1) for kk in ['off', 'nopan', 'pan']] + [np.clip((refF - img) * 4, 0, 1)], 1)
        to_png(np.asarray(Image.fromarray((strip * 255).astype(np.uint8)).resize((strip.shape[1] // 2, strip.shape[0] // 2), Image.BILINEAR)) / 255.0,
               f'{a.out}/pan_test_strip.png')
        raise SystemExit(0)
    img = load(a.image, Image.open(a.image).size) if a.image else make_scene()
    H, W, _ = img.shape; lw, lh = max(G, W // P['div']), max(G, H // P['div'])
    kw = dict(rays=P['rays'], steps=P['steps'], stepCells=P['stepCells'], stepGrowth=P['stepGrowth'], heightScale=a.height, falloff=a.falloff)
    grid = phase1(img)
    bits, words, st = phase2(grid, lx=P.get('lx', 5), ly=P.get('ly', 6))
    light, gate = phase3(grid, bits, W, H, lw, lh, **kw)
    lightAll, _ = phase3(grid, bits, W, H, lw, lh, forceAll=True, **kw)
    light, lightAll = filter3(light), filter3(lightAll)
    final, lightUp = phase4(img, light, a.strength)
    finalAll, _ = phase4(img, lightAll, a.strength)
    grid.astype('<u4').tofile(f'{a.out}/grid.bin'); words.astype('<u4').tofile(f'{a.out}/mask_py.bin')
    to_png(img, f'{a.out}/before.png'); to_png(final, f'{a.out}/after.png')
    to_png(np.clip(final - img, 0, 1) * 4, f'{a.out}/added_light_x4.png')
    prox = np.stack([(grid & 0xFF), (grid >> 8) & 0xFF, (grid >> 16) & 0xFF], -1).astype(np.float32) / 255
    imp = np.repeat(bits[..., None], 3, -1).astype(np.float32)
    to_png(np.asarray(Image.fromarray((prox * 255).astype(np.uint8)).resize((W // 2, H // 2), Image.NEAREST)) / 255.0, f'{a.out}/proxy_grid.png')
    to_png(np.asarray(Image.fromarray((imp * 255).astype(np.uint8)).resize((W // 2, H // 2), Image.NEAREST)) / 255.0, f'{a.out}/importance.png')
    # side by side: before | after (top), proxy | importance | added light (bottom)
    half = lambda x: np.asarray(Image.fromarray((np.clip(x, 0, 1) * 255).astype(np.uint8)).resize((W // 2, H // 2), Image.BILINEAR)) / 255.0
    top = np.concatenate([img, final], 1)
    bot = np.concatenate([half(img) * 0 + np.asarray(Image.open(f'{a.out}/proxy_grid.png')) / 255.0,
                          np.asarray(Image.open(f'{a.out}/importance.png')) / 255.0,
                          half(np.clip(final - img, 0, 1) * 4), half(np.repeat(1 - lightUp[..., 3:4], 3, -1))], 1)
    to_png(np.concatenate([top, bot], 0), f'{a.out}/before_after.png')
    diff = np.abs(final - finalAll).max()
    print(f'image {W}x{H}, lighting {lw}x{lh}; grid mean luma {st["mean"]}, emit threshold {st["T"]}, '
          f'emit cells {st["emit"]}, edge cells {st["edge"]}, final map {st["set"]}/16384 ({st["set"] / 163.84:.1f}%)')
    print(f'march groups active {gate.sum()}/{gate.size} ({100 * gate.mean():.1f}%); max |gated - ungated| in final image '
          f'{diff * 255:.1f}/255, mean {np.abs(final - finalAll).mean() * 255:.3f}/255')
    print(f'added light: mean {np.mean(final - img) * 255:.2f}/255, p99 {np.percentile(final - img, 99) * 255:.1f}/255')
