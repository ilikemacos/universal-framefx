"""NumPy reference for the SSRT passes (CSDepth, CSDepthBlur, CSMarchDepth, CSReflect,
CSLightDir, CSContactShadow, CSPack, PSCompositeSsrt). Mirrors the HLSL; the SSGI phases
come from the harness module passed into run().

Presets must match MakeSsrtPreset in src/ssgi_pipeline.h.
"""
import os, sys, json, datetime
import numpy as np
from PIL import Image, ImageFilter

EFF = 0.40
# SYNC with MakeSsrtPreset (ssgi_pipeline.h).
SSRT_PRESETS = {
    'GTX1050Ti_1080p': dict(
        id=1, w=1920, h=1080, div=5, rays=3, steps=10, stepCells=1.0, stepGrowth=1.12,
        reflSteps=8, contactRays=2, contactSteps=6,  # contactSteps overwritten below to match C++
        tflops=2.1, gbs=112.0, gtex=53.0, l2_mb=1.0,
    ),
    'GTX980Ti_1440p': dict(
        id=2, w=2560, h=1440, div=3, rays=4, steps=14, stepCells=0.9, stepGrowth=1.12,
        reflSteps=8, contactRays=2, contactSteps=6,
        tflops=6.1, gbs=336.0, gtex=176.0, l2_mb=3.0,
    ),
}
# The 1050 contact march is 5 steps (C++). Set it explicitly so a comment above cannot drift.
SSRT_PRESETS['GTX1050Ti_1080p']['contactSteps'] = 5

# Shared cue / material constants. SYNC with SsrtPreset defaults in ssgi_pipeline.h.
CUE = dict(
    reflStrength=0.45, contactStrength=0.55, thickness=0.08, parallaxWeight=0.40,
    horizonY=0.42, depthGrad=600.0, aoDepthScale=2.5, contactRadius=3.5,
    roughBlur=0.02, edgeFade=8.0, reflReach=0.80,
    wHorizon=0.62, wContrast=0.10, wDefocus=0.10, wLuma=0.18, wParallax=0.40,
    skyLuma=0.62, glossVarK=90.0, glossMin=0.45, giGain=1.55, panMinPx=1.5, glossBright=2.5,
    rangeK=40.0, heightScale=24.0, horizonBias=0.25, aoStrength=0.5, aoRadius=6.0,
    emitLo=0.35, emitHi=0.9, falloff=0.002, strength=1.5, aoGame=0.35,
)
# SYNC with kSsrtAlpha / kSsrtDepth / kSsrtMvPx / kSsrtLum / kSsrtClip in ssgi_pipeline.cpp.
TEMPORAL = dict(alpha=0.22, depthReject=0.045, mvRejectPx=6.0, lumCut=0.08, clipBox=1.0)

def sat(x):
    return np.clip(x, 0.0, 1.0)

def light_size(w, h, div, G):
    return max(G, w // div), max(G, h // div)

def q8(x):
    return np.floor(np.clip(x, 0, 1) * 255.0 + 0.5) / 255.0

def rankdata(v):
    v = np.asarray(v, np.float64).ravel()
    order = np.argsort(v, kind='mergesort')
    r = np.empty(len(v), np.float64)
    r[order] = np.arange(len(v))
    vs = v[order]
    i = 0
    n = len(v)
    while i < n:
        j = i + 1
        while j < n and vs[j] == vs[i]:
            j += 1
        if j > i + 1:
            r[order[i:j]] = 0.5 * (i + j - 1)
        i = j
    return r

def spearman(a, b):
    ra, rb = rankdata(a), rankdata(b)
    ra -= ra.mean(); rb -= rb.mean()
    den = np.sqrt((ra * ra).sum() * (rb * rb).sum())
    return float((ra * rb).sum() / den) if den > 0 else 0.0

def absrel(pred, gt):
    """Scale-and-shift fit. A negative scale is rejected (s = 0, t = mean gt): an inverted
    proxy does not get credit for flipping the scene."""
    p = np.asarray(pred, np.float64).ravel()
    g = np.asarray(gt, np.float64).ravel()
    n = p.size
    sp, sg = p.sum(), g.sum()
    den = n * np.dot(p, p) - sp * sp
    s = 0.0 if den <= 1e-12 else (n * np.dot(p, g) - sp * sg) / den
    if s < 0:
        s, t = 0.0, sg / n
    else:
        t = (sg - s * sp) / n
    a = s * p + t
    return float(np.mean(np.abs(a - g) / np.maximum(g, 0.05)))

def cs_depth(h, img, lw, lh, motion=None, pan_uv=None, use_parallax=True):
    """CSDepth. motion is (H, W, 2+) in source pixels. pan_uv is a length-2 backward UV.
    Returns depth, brightness, protect (1 on sky / flat UI)."""
    H, W = img.shape[:2]
    c = CUE
    step_x = max(W // lw, 1)
    step_y = max(H // lh, 1)
    xs = np.arange(lw)
    ys = np.arange(lh)
    cx = np.clip(((xs + 0.5) * W / lw).astype(np.int32), 0, W - 1)
    cy = np.clip(((ys + 0.5) * H / lh).astype(np.int32), 0, H - 1)
    stack = []
    rgbC = None
    k = 0
    for oy in (-1, 0, 1):
        for ox in (-1, 0, 1):
            px = np.clip(cx[None, :] + ox * step_x, 0, W - 1)
            py = np.clip(cy[:, None] + oy * step_y, 0, H - 1)
            rgb = img[py, px]
            stack.append(h.bright(rgb))
            if k == 4:
                rgbC = rgb
            k += 1
    stack = np.stack(stack, 0)
    Lc = stack[4]
    mean = stack.mean(0)
    contrast = np.abs(stack - Lc).mean(0)
    hf = np.abs(Lc - mean) / (mean + 0.08)
    ring = np.zeros_like(Lc)
    for ox, oy in ((-8, 0), (8, 0), (0, -8), (0, 8)):
        px = np.clip(cx[None, :] + ox * step_x, 0, W - 1)
        py = np.clip(cy[:, None] + oy * step_y, 0, H - 1)
        ring = np.maximum(ring, h.bright(img[py, px]))
    mx = rgbC.max(-1)
    mn = rgbC.min(-1)
    satC = (mx - mn) / np.maximum(mx, 1e-3)
    uvy = (ys + 0.5) / lh
    below = sat((uvy - c['horizonY']) / max(1.0 - c['horizonY'], 0.001))[:, None]
    above = sat((c['horizonY'] - uvy) / max(c['horizonY'], 0.001))[:, None]
    skyLike = sat((Lc - 0.48) * 3.5) * sat((0.28 - satC) * 4.0)
    blue = sat((rgbC[..., 2] - np.maximum(rgbC[..., 0], rgbC[..., 1])) * 4.0)
    skyLike = np.maximum(skyLike, blue * sat((Lc - 0.32) * 3.0))
    dH = np.maximum(1.0 * (1 - below) + 0.16 * below, above)
    dC = 0.82 + (0.20 - 0.82) * sat(contrast * 10.0)
    dD = 0.90 + (0.18 - 0.90) * sat(hf * 5.0)
    sky = sat((Lc - c['skyLuma']) * 5.0) * above
    dL = 0.55 + (1.0 - 0.55) * sky
    sil = sat((ring - Lc - 0.06) * 4.5)
    fol = sat((satC - 0.15) * 4.0) * sat(hf * 3.0)
    hazeAmt = sat((0.22 - satC) * 5.0) * sat(1.2 - hf * 3.0) * (1.0 - below)
    dP = np.full((lh, lw), 0.55, np.float32)
    wP = 0.0
    if use_parallax and motion is not None and pan_uv is not None and c['parallaxWeight'] > 0:
        mh, mw = motion.shape[:2]
        uvx = (xs + 0.5) / lw
        mpx = np.clip((uvx * mw).astype(np.int32), 0, mw - 1)
        mpy = np.clip((uvy * mh).astype(np.int32), 0, mh - 1)
        mv = motion[mpy[:, None], mpx[None, :]]
        pan_px = np.asarray(pan_uv, np.float32) * np.array([W, H], np.float32)
        pan_len = float(np.linalg.norm(pan_px))
        if pan_len >= c['panMinPx']:
            rel = np.linalg.norm(mv[..., :2], axis=-1) / pan_len
            dP = sat(1.15 - 0.55 * rel).astype(np.float32)
            wP = c['wParallax']
    # Column sky at 2% from the top. Interior and night keep the original prior.
    top_y = np.full(lh, int(0.02 * img.shape[0]), np.int32)
    top_y = np.clip(top_y, 0, img.shape[0] - 1)
    top_rgb = img[top_y[:, None], cx[None, :]]
    topL = h.bright(top_rgb)
    tmx = top_rgb.max(-1)
    tmn = top_rgb.min(-1)
    topSat = (tmx - tmn) / np.maximum(tmx, 1e-3)
    tr, tg, tb = top_rgb[..., 0], top_rgb[..., 1], top_rgb[..., 2]
    blueSky = (topL > 0.36) & (tb > tr + 0.05) & (tb > tg - 0.04)
    pale = (topSat < 0.30) & (topL > 0.50) & (tb > tr - 0.02)
    outdoor = (blueSky | pale).astype(np.float32)
    wSky = 0.70 * skyLike * above * outdoor
    wSil = 0.34 * sil * outdoor
    wTex = 0.22 * fol * outdoor
    wHz = 0.28 * hazeAmt * outdoor
    wsum = c['wHorizon'] + c['wContrast'] + c['wDefocus'] + c['wLuma'] + wP + wSky + wSil + wTex + wHz
    depth = (c['wHorizon'] * dH + c['wContrast'] * dC + c['wDefocus'] * dD
             + c['wLuma'] * dL + wP * dP + wSky * 1.0 + wSil * 0.14 + wTex * 0.22 + wHz * 0.95) / np.maximum(wsum, 1e-4)
    uv = uvy[:, None]
    skyP = (skyLike > 0.45) & (uv < 0.72) & (satC < 0.35)
    protect = skyP.astype(np.float32)
    return sat(depth).astype(np.float32), Lc.astype(np.float32), protect

def cs_depth_blur(h, raw_d, raw_l, protect=None):
    """CSDepthBlur, temporal off (no history read). Returns depth, nx, ny, smooth."""
    lh, lw = raw_d.shape
    c = CUE
    acc = np.zeros((lh, lw), np.float32)
    ws = np.zeros((lh, lw), np.float32)
    sumL = np.zeros((lh, lw), np.float32)
    sumL2 = np.zeros((lh, lw), np.float32)
    taps_d, taps_l = [], []
    yy, xx = np.mgrid[0:lh, 0:lw]
    for oy in (-1, 0, 1):
        for ox in (-1, 0, 1):
            s = raw_d[np.clip(yy + oy, 0, lh - 1), np.clip(xx + ox, 0, lw - 1)]
            l = raw_l[np.clip(yy + oy, 0, lh - 1), np.clip(xx + ox, 0, lw - 1)]
            taps_d.append(s); taps_l.append(l)
    lc = taps_l[4]
    for k, (oy, ox) in enumerate((oy, ox) for oy in (-1, 0, 1) for ox in (-1, 0, 1)):
        w = (2 - abs(ox)) * (2 - abs(oy)) * np.exp(-c['rangeK'] * (taps_l[k] - lc) ** 2)
        acc += w * taps_d[k]; ws += w
        sumL += taps_l[k]; sumL2 += taps_l[k] ** 2
    depth = sat(acc / np.maximum(ws, 1e-4))
    # Column / row means of the 3x3 (same order as the HLSL d[] array).
    dzdx = (taps_d[2] + taps_d[5] + taps_d[8]) / 3.0 - (taps_d[0] + taps_d[3] + taps_d[6]) / 3.0
    dzdy = (taps_d[6] + taps_d[7] + taps_d[8]) / 3.0 - (taps_d[0] + taps_d[1] + taps_d[2]) / 3.0
    nx = dzdx * c['depthGrad']
    ny = dzdy * c['depthGrad']
    nz = np.ones_like(nx)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    nx, ny, nz = nx / ln, ny / ln, nz / ln
    mean = sumL / 9.0
    var = np.maximum(sumL2 / 9.0 - mean * mean, 0.0)
    horizontal = sat((-ny - 0.40) * 5.0) * sat((0.72 - nz) * 4.0)
    brightG = sat((lc - 0.12) * c['glossBright'])
    smooth = sat(1.0 - var * c['glossVarK']) * horizontal * brightG
    if protect is not None:
        smooth = smooth * (protect <= 0.5)
    return depth.astype(np.float32), nx.astype(np.float32), ny.astype(np.float32), smooth.astype(np.float32)

def _sample_z(depth, px, py, lw, lh, G):
    ipx = np.clip((px / G * lw).astype(np.int32), 0, lw - 1)
    ipy = np.clip((py / G * lh).astype(np.int32), 0, lh - 1)
    return depth[ipy, ipx]

def march_depth(h, grid, bits, depth, W, H, lw, lh, frame=0, use_gi=True, use_ao=True, preset=None, protect=None):
    """CSMarchDepth. preset supplies rays/steps/stepCells/stepGrowth; other knobs come from CUE."""
    G = h.G
    p = preset
    c = CUE
    rays, steps = p['rays'], p['steps']
    stepCells, stepGrowth = p['stepCells'], p['stepGrowth']
    rgb = np.stack([(grid & 0xFF), (grid >> 8) & 0xFF, (grid >> 16) & 0xFF], -1).astype(np.float32) / 255
    lum = (grid >> 24).astype(np.float32) / 255
    ty, tx = np.mgrid[0:lh, 0:lw].astype(np.float32)
    gx, gy = (tx // 8).astype(int), (ty // 8).astype(int)
    ngx, ngy = (lw + 7) // 8, (lh + 7) // 8
    gate = np.zeros((ngy, ngx), bool)
    for j in range(ngy):
        for i in range(ngx):
            t0 = np.array([i * 8, j * 8]); t1 = np.minimum(t0 + 7, [lw - 1, lh - 1])
            c0 = (t0 * G) // [lw, lh]; c1 = (t1 * G) // [lw, lh]
            cx1 = min(c1[0], c0[0] + 7); cy1 = min(c1[1], c0[1] + 7)
            gate[j, i] = bits[c0[1]:cy1 + 1, c0[0]:cx1 + 1].any()
    on = gate[np.clip(gy, 0, ngy - 1), np.clip(gx, 0, ngx - 1)]
    uvx, uvy = (tx + 0.5) / lw, (ty + 0.5) / lh
    ccx, ccy = uvx * G, uvy * G
    ci = (np.clip(ccy.astype(int), 0, G - 1), np.clip(ccx.astype(int), 0, G - 1))
    lc = lum[ci]
    zc = depth[np.clip(ty.astype(int), 0, lh - 1), np.clip(tx.astype(int), 0, lw - 1)]
    aspect = W / H
    rot = np.mod((frame & 7) * 0.38196601, 1.0)
    n0 = np.mod(h.interleave3(tx, ty) + rot, 1.0)
    jit = np.mod(h.interleave3(ty + 1, tx + 1) * 0.875 + 0.0625 + rot * 0.5, 1.0)
    hc = c['heightScale'] * lc
    bounce = np.zeros((lh, lw, 3), np.float32)
    aoAcc = np.zeros((lh, lw), np.float32)
    emitLo, emitHi = c['emitLo'], c['emitHi']
    for r in range(rays):
        aj = (h.ign(tx + 17.0 * r, ty + 17.0 * r, frame) - 0.5) / 9.0
        jr = np.mod(jit + 0.61803399 * r, 1.0)
        ang = 2 * np.pi * (n0 + aj + r / rays)
        dx, dy = np.cos(ang), np.sin(ang) * aspect
        sinA = np.zeros((lh, lw), np.float32)
        sinH = np.full((lh, lw), -c['horizonBias'], np.float32)
        d = np.zeros((lh, lw), np.float32)
        ln = stepCells
        alive = on.copy()
        rayB = np.zeros((lh, lw, 3), np.float32)
        for s_ in range(steps):
            d = d + ln * ((0.5 + jr) if s_ == 0 else 1.0)
            ln = ln * stepGrowth
            px, py = ccx + dx * d, ccy + dy * d
            alive &= (px >= 0) & (py >= 0) & (px < G) & (py < G)
            ix = np.clip(px.astype(int), 0, G - 1)
            iy = np.clip(py.astype(int), 0, G - 1)
            l = lum[iy, ix]
            dc = d * stepCells
            zS = _sample_z(depth, px, py, lw, lh, G)
            if use_ao:
                dz = (zc - zS) * c['aoDepthScale']
                sinO = dz / np.sqrt(dz * dz + dc * dc + 1e-5)
                solid = sat((sinO - 0.18) * 3.5)
                sinA = np.where(alive, np.maximum(sinA, solid * sat(1 - dc / c['aoRadius'])), sinA)
            else:
                dz = c['heightScale'] * l - hc
                dzA = np.minimum(dz, 0.25 * c['heightScale'])
                sinO = dzA / np.sqrt(dzA * dzA + dc * dc) * (1 - h.smoothstep(emitLo, emitHi, l))
                sinA = np.where(alive, np.maximum(sinA, sinO * sat(1 - dc / c['aoRadius'])), sinA)
            if use_gi:
                travel = sat(d / 48.0)
                zRay = zc + (1.0 - zc) * travel
                gap = zRay - zS
                alive &= ~(gap > c['thickness'])
                hit = alive & (gap >= 0.0)
                band = 1.0 - gap / max(c['thickness'], 1e-4)
                add = np.where(hit, c['giGain'] * band * h.smoothstep(emitLo, emitHi, l) / (1 + c['falloff'] * 3.5 * dc * dc), 0)
                rayB += add[..., None] * (rgb[iy, ix] ** 2)
            else:
                dz = c['heightScale'] * l - hc
                sinS = dz / np.sqrt(dz * dz + dc * dc)
                up = alive & (sinS > sinH)
                add = np.where(up, (sinS - sinH) * h.smoothstep(emitLo, emitHi, l) / (1 + c['falloff'] * dc * dc), 0)
                rayB += add[..., None] * (rgb[iy, ix] ** 2)
                sinH = np.where(up, sinS, sinH)
        bounce += rayB
        aoAcc += sinA
    bounce /= rays * (1 + c['horizonBias'])
    bounce *= (1 - h.smoothstep(emitLo, emitHi, lc))[..., None]
    # Same 8x2 near gather as CSMarchDepth, only where the rays contributed almost nothing.
    weak = (bounce[..., 0] * 0.2126 + bounce[..., 1] * 0.7152 + bounce[..., 2] * 0.0722) < 0.02
    if protect is not None:
        weak = weak & (protect <= 0.5)
    extra = np.zeros_like(bounce)
    for i in range(8):
        ang = i * np.pi / 4.0
        dx, dy = np.cos(ang), np.sin(ang) * aspect
        for rad in (4.0, 12.0, 40.0):
            px, py = ccx + dx * rad, ccy + dy * rad
            inside = (px >= 0) & (py >= 0) & (px < G) & (py < G)
            ix = np.clip(px.astype(int), 0, G - 1)
            iy = np.clip(py.astype(int), 0, G - 1)
            l = lum[iy, ix]
            w = h.smoothstep(emitLo, emitHi, l) / (1.0 + 0.0035 * rad * rad)
            extra += (w * inside)[..., None] * c['giGain'] * 0.38 * (rgb[iy, ix] ** 2)
    bounce = bounce + np.where(weak[..., None], extra / 8.0, 0.0)
    occ = np.clip(aoAcc / rays, 0, 1)
    occ *= 1 - h.smoothstep(emitLo, emitHi, lc)
    if protect is not None:
        p = protect > 0.5
        bounce = np.where(p[..., None], 0, bounce)
        occ = np.where(p, 0, occ)
    ao = 1 - c['aoStrength'] * occ
    out = np.concatenate([np.sqrt(np.clip(bounce * ao[..., None], 0, 1)), lc[..., None], occ[..., None]], -1)
    neutral = np.concatenate([np.zeros((lh, lw, 3), np.float32), lc[..., None], np.zeros((lh, lw, 1), np.float32)], -1)
    out = np.where(on[..., None], out, neutral)
    return h.quant(out), gate

def cs_light_dir(grid, G):
    lum = (grid >> 24).astype(np.float32) / 255.0
    idx = int(np.argmax(lum))  # first maximum, row-major: matches the group reduction on ties
    return np.array([(idx % G) + 0.5, (idx // G) + 0.5, lum.ravel()[idx], 1.0], np.float32)

def _reflect_march(img, depth, smooth, uvx, uvy, uvdx, uvdy, Rz, active, W, H, steps=None):
    c = CUE
    steps = int(steps if steps is not None else 8)
    lh, lw = depth.shape
    nC = steps // 2
    nF = steps - nC
    sF = c['reflReach'] / max(4 * nC + nF, 1)
    sC = 4.0 * sF
    zc = depth
    traveled = np.zeros((lh, lw), np.float32)
    alive = active.copy()
    hit = np.zeros((lh, lw), bool)
    hit_u = np.stack([uvx, uvy], -1).astype(np.float32)
    gap_h = np.full((lh, lw), c['thickness'], np.float32)
    for s in range(steps):
        traveled = traveled + np.where(alive, sC if s < nC else sF, 0)
        uxx = uvx + uvdx * traveled
        uyy = uvy + uvdy * traveled
        inside = (uxx >= 0) & (uyy >= 0) & (uxx <= 1) & (uyy <= 1)
        alive &= inside
        ipx = np.clip((uxx * lw).astype(np.int32), 0, lw - 1)
        ipy = np.clip((uyy * lh).astype(np.int32), 0, lh - 1)
        zS = depth[ipy, ipx]
        zRay = zc + (1 - zc) * sat(traveled / max(c['reflReach'], 1e-3))
        gap = zRay - zS
        blocked = alive & (gap > c['thickness'])
        alive &= ~blocked
        got = alive & (gap >= 0) & ~hit
        hit |= got
        hit_u[..., 0] = np.where(got, uxx, hit_u[..., 0])
        hit_u[..., 1] = np.where(got, uyy, hit_u[..., 1])
        gap_h = np.where(got, gap, gap_h)
        alive &= ~got
    gp_x = np.clip((hit_u[..., 0] * W).astype(np.int32), 0, W - 1)
    gp_y = np.clip((hit_u[..., 1] * H).astype(np.int32), 0, H - 1)
    col = img[gp_y, gp_x]
    rough = sat(1 - smooth)
    perp_x, perp_y = -uvdy, uvdx
    u2x = sat(hit_u[..., 0] + perp_x * rough * c['roughBlur'])
    u2y = sat(hit_u[..., 1] + perp_y * rough * c['roughBlur'])
    g2x = np.clip((u2x * W).astype(np.int32), 0, W - 1)
    g2y = np.clip((u2y * H).astype(np.int32), 0, H - 1)
    col2 = img[g2y, g2x]
    col = np.where((hit & (rough > 0.02))[..., None], 0.5 * (col + col2), col)
    edge = np.minimum(np.minimum(hit_u[..., 0], hit_u[..., 1]), np.minimum(1 - hit_u[..., 0], 1 - hit_u[..., 1]))
    edge0 = np.minimum(np.minimum(uvx, uvy), np.minimum(1 - uvx, 1 - uvy))
    conf = sat(edge * c['edgeFade']) * sat(edge0 * c['edgeFade'])
    conf *= sat(1 - gap_h / max(c['thickness'], 1e-4))
    conf *= (1 - rough) * 0.65 + 0.35   # lerp(1, 0.35, rough) = 1 - 0.65*rough
    conf *= smooth
    conf = np.where(hit, sat(conf), 0)
    col = np.where(hit[..., None], col, 0)
    return np.concatenate([col, conf[..., None]], -1).astype(np.float32)

def cs_contact(h, grid, depth, light, W, H, lw, lh, preset, protect=None):
    G = h.G
    c = CUE
    lum = (grid >> 24).astype(np.float32) / 255.0
    ty, tx = np.mgrid[0:lh, 0:lw].astype(np.float32)
    uvx, uvy = (tx + 0.5) / lw, (ty + 0.5) / lh
    ccx, ccy = uvx * G, uvy * G
    to_x, to_y = light[0] - ccx, light[1] - ccy
    distL = np.sqrt(to_x * to_x + to_y * to_y)
    cell_x = np.clip(ccx.astype(np.int32), 0, G - 1)
    cell_y = np.clip(ccy.astype(np.int32), 0, G - 1)
    xm = np.clip(cell_x - 1, 0, G - 1); xp = np.clip(cell_x + 1, 0, G - 1)
    ym = np.clip(cell_y - 1, 0, G - 1); yp = np.clip(cell_y + 1, 0, G - 1)
    gx = lum[cell_y, xp] - lum[cell_y, xm]
    gy = lum[yp, cell_x] - lum[ym, cell_x]
    dir_x = to_x / np.maximum(distL, 1e-6) + gx * 0.5
    dir_y = to_y / np.maximum(distL, 1e-6) + gy * 0.5
    tiny = (dir_x * dir_x + dir_y * dir_y) < 1e-8
    dir_x = np.where(tiny, to_x / np.maximum(distL, 1e-6), dir_x)
    dir_y = np.where(tiny, to_y / np.maximum(distL, 1e-6), dir_y)
    dln = np.sqrt(dir_x * dir_x + dir_y * dir_y)
    dir_x, dir_y = dir_x / np.maximum(dln, 1e-8), dir_y / np.maximum(dln, 1e-8)
    aspect = W / H
    zc = depth
    rays, steps = int(preset['contactRays']), int(preset['contactSteps'])
    stepCells = preset['stepCells']
    base = np.arctan2(dir_y, dir_x)
    acc = np.zeros((lh, lw), np.float32)
    near = distL >= 0.75
    for r in range(rays):
        ang = base + (r - 0.5 * (rays - 1)) * 0.18
        rdx, rdy = np.cos(ang), np.sin(ang) * aspect
        best = np.zeros((lh, lw), np.float32)
        alive = near.copy()
        for s in range(1, steps + 1):
            dist = s * stepCells
            alive &= dist <= c['contactRadius']
            px, py = ccx + rdx * dist, ccy + rdy * dist
            alive &= (px >= 0) & (py >= 0) & (px < G) & (py < G)
            zS = _sample_z(depth, px, py, lw, lh, G)
            front = zc - zS
            ok = alive & (front > 0.012) & (front < c['thickness'] * 2.0)
            pen = sat(1.0 - dist / max(c['contactRadius'], 0.5)) * sat(front / max(c['thickness'], 1e-4))
            best = np.maximum(best, np.where(ok, pen, 0))
        acc += best
    shadow = np.where(near, acc / rays, 0).astype(np.float32)
    if protect is not None:
        shadow = np.where(protect > 0.5, 0, shadow)
    return shadow

def cs_pack(refl, shadow, refl_on, contact_on):
    rgb = refl[..., :3] * refl[..., 3:4] if refl_on else np.zeros_like(refl[..., :3])
    a = sat(shadow) if contact_on else np.zeros(shadow.shape, np.float32)
    packed = np.concatenate([rgb, a[..., None]], -1)
    return q8(packed)

def composite_ssrt(h, img, light, packed, refl_s, contact_s, strength=None, rangeK=None, aoGame=None):
    """PSCompositeSsrt. refl_s = contact_s = 0 matches phase4 (the extra taps are skipped)."""
    strength = CUE['strength'] if strength is None else strength
    rangeK = CUE['rangeK'] if rangeK is None else rangeK
    aoGame = CUE['aoGame'] if aoGame is None else aoGame
    extra = refl_s > 0 or contact_s > 0
    H, W = img.shape[:2]
    lh, lw = light.shape[:2]
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    bg = h.bright(img)
    lpx = (xx + 0.5) * lw / W - 0.5
    lpy = (yy + 0.5) * lh / H - 0.5
    bx, by = np.floor(lpx).astype(int), np.floor(lpy).astype(int)
    fx, fy = lpx - bx, lpy - by
    acc = np.zeros((H, W, 4), np.float32)
    ws = np.zeros((H, W), np.float32)
    for k in range(4):
        ox, oy = k & 1, k >> 1
        iy = np.clip(by + oy, 0, lh - 1)
        ix = np.clip(bx + ox, 0, lw - 1)
        sm = light[iy, ix]
        wb = (fx if ox else 1 - fx) * (fy if oy else 1 - fy)
        wr = np.exp(-rangeK * (bg - sm[..., 3]) ** 2)
        if extra:
            wr = np.maximum(wr, 0.22)
        w = wb * wr
        lin = np.concatenate([sm[..., :3] ** 2, sm[..., 4:5]], -1)
        acc += w[..., None] * lin
        ws += w
    lo = acc / np.maximum(ws, 0.25)[..., None]
    g = img ** 2
    albedo = 0.5 * np.clip(g / np.maximum(h.luma(g), 0.02)[..., None], 0, 2)
    if not extra:
        g = g * (1 - min(aoGame, 0.6) * lo[..., 3:4])
        lin = g + strength * lo[..., :3] * albedo
        return np.sqrt(np.clip(lin, 0, 1)), lo
    ixn = np.clip(np.floor(lpx + 0.5).astype(np.int32), 0, lw - 1)
    iyn = np.clip(np.floor(lpy + 0.5).astype(np.int32), 0, lh - 1)
    ex = packed[iyn, ixn]
    uv_y = (yy + 0.5) / H
    mx = img.max(-1)
    mn = img.min(-1)
    satC = (mx - mn) / np.maximum(mx, 1e-3)
    ui = ((img[..., 0] > 0.85) & (img[..., 1] < 0.12) & (img[..., 2] < 0.12)) | ((img[..., 2] > 0.80) & (img[..., 0] < 0.20) & (img[..., 1] < 0.35))
    glow = bg > 0.80
    upper = uv_y < 0.52
    blue_sky = (bg > 0.36) & (img[..., 2] > img[..., 0] + 0.05) & (img[..., 2] > img[..., 1] - 0.04)
    pale = (satC < 0.30) & (bg > 0.50) & (img[..., 2] > img[..., 0] - 0.02)
    sky = upper & (blue_sky | pale)
    protect = ui | glow | sky
    crevice = sat((lo[..., 3] - 0.45) / 0.25)
    shade = sat(crevice * min(aoGame, 0.6) * 0.32 + contact_s * 0.32 * np.clip(ex[..., 3], 0, 1) * (ex[..., 3] >= 0.45))
    shade = shade * (~protect)
    add = (strength * lo[..., :3] + refl_s * ex[..., :3]) * albedo * (~protect)[..., None]
    emitter = sat((satC - 0.40) * 8.0) * sat((bg - 0.26) * 5.0)
    add = add * (1.0 - emitter)[..., None]
    addY = h.luma(add)[..., None]
    lin = g * (1.0 - shade)[..., None] + (add - addY) * 0.95 + addY * 0.50
    y0 = h.luma(g)[..., None]
    y1 = h.luma(lin)[..., None]
    lin = lin + (y0 - y1) * 0.65
    lin = np.where(protect[..., None], g, lin)
    return np.sqrt(np.clip(lin, 0, 1)), lo

def old_luma_depth(h, img, lw, lh):
    """Previous proxy: brightness as near (the r3 height field h = heightScale * B), inverted to 0=near, 1=far."""
    H, W = img.shape[:2]
    xs = np.arange(lw); ys = np.arange(lh)
    cx = np.clip(((xs + 0.5) * W / lw).astype(np.int32), 0, W - 1)
    cy = np.clip(((ys + 0.5) * H / lh).astype(np.int32), 0, H - 1)
    L = h.bright(img[cy[:, None], cx[None, :]])
    return (1.0 - L).astype(np.float32)

def downsample_mask(mask, lw, lh):
    H, W = mask.shape
    ys = np.clip(((np.arange(lh) + 0.5) * H / lh).astype(int), 0, H - 1)
    xs = np.clip(((np.arange(lw) + 0.5) * W / lw).astype(int), 0, W - 1)
    return mask[ys[:, None], xs[None, :]]

def dilate(mask, r):
    if r <= 0:
        return mask
    im = Image.fromarray(mask.astype(np.uint8) * 255)
    im = im.filter(ImageFilter.MaxFilter(2 * r + 1))
    return np.asarray(im) > 0

def gt_at_light(gt, lw, lh):
    return downsample_mask(gt, lw, lh) if gt.dtype == bool else _resize_scalar(gt, lw, lh)

def _resize_scalar(a, lw, lh):
    H, W = a.shape
    ys = np.clip(((np.arange(lh) + 0.5) * H / lh).astype(int), 0, H - 1)
    xs = np.clip(((np.arange(lw) + 0.5) * W / lw).astype(int), 0, W - 1)
    return a[ys[:, None], xs[None, :]].astype(np.float32)

def _blur_far(img, gt):
    u8 = (np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8)
    blur = np.asarray(Image.fromarray(u8).filter(ImageFilter.GaussianBlur(radius=2.4)), np.float32) / 255.0
    # Only the far back wall / sky. Blurring the whole lower frame wiped the wall texture the gloss test needs.
    mix = sat((gt - 0.88) / 0.08)[..., None]
    return np.clip(img * (1 - mix) + blur * mix, 0, 1).astype(np.float32)

def make_room(W=1280, H=720, seed=1):
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xn = (xx + 0.5) / W
    yn = (yy + 0.5) / H
    horizon = 0.42
    depth = np.ones((H, W), np.float32)
    floor = yn >= horizon
    t = np.clip((yn - horizon) / (1 - horizon), 0, 1)
    depth = np.where(floor, 0.96 - 0.78 * np.power(t, 0.72), depth)
    back = (yn < horizon) & (xn > 0.16) & (xn < 0.84)
    depth = np.where(back, 0.90, depth)
    left = xn <= 0.16
    depth = np.where(left, 0.42 + 0.40 * (xn / 0.16), depth)
    right = xn >= 0.84
    depth = np.where(right, 0.42 + 0.40 * ((1 - xn) / 0.16), depth)
    window = (xn > 0.36) & (xn < 0.60) & (yn > 0.05) & (yn < 0.30)
    depth = np.where(window, 1.0, depth)
    crate = (xn > 0.60) & (xn < 0.80) & (yn > 0.55) & (yn < 0.84)
    depth = np.where(crate, 0.24, depth)
    img = np.zeros((H, W, 3), np.float32)
    floor_col = np.stack([0.42 + 0.20 * t, 0.40 + 0.16 * t, 0.36 + 0.10 * t], -1)
    img = np.where(floor[..., None], floor_col, img)
    # High-frequency checker on everything that should NOT mirror: walls, rug, crate.
    # Period ~6 px so neighbouring light-res taps (4 px apart) disagree.
    checker = (((xx.astype(np.int32) // 3) + (yy.astype(np.int32) // 3)) & 1).astype(np.float32)
    rug = floor & (xn > 0.22) & (xn < 0.40) & (yn > 0.60)
    img = np.where(rug[..., None], 0.20 + 0.45 * checker[..., None], img)
    img = np.where(back[..., None], 0.22 + 0.28 * checker[..., None], img)
    img = np.where(left[..., None], 0.16 + 0.30 * checker[..., None], img)
    img = np.where(right[..., None], 0.18 + 0.30 * checker[..., None], img)
    sky = np.stack([0.78 + 0.18 * (1 - yn), np.full_like(yn, 0.90), np.full_like(yn, 1.0)], -1)
    img = np.where(window[..., None], sky, img)
    img = np.where(crate[..., None], 0.04 + 0.16 * checker[..., None], img)
    # dark near corner (interior): close, but luma says "far"
    corner = floor & (xn < 0.22) & (yn > 0.78)
    depth = np.where(corner, 0.20, depth)
    img = np.where(corner[..., None], 0.03 + 0.10 * checker[..., None], img)
    glossy = floor & ~rug & ~crate & ~corner & (xn > 0.18) & (xn < 0.90)
    img = _blur_far(np.clip(img, 0, 1), depth)
    return img.astype(np.float32), depth.astype(np.float32), glossy, crate, floor, window

def make_sky(W=1280, H=720):
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    yn = (yy + 0.5) / H
    xn = (xx + 0.5) / W
    horizon = 0.62
    sky = yn < horizon
    t = np.clip((yn - horizon) / (1 - horizon), 0, 1)
    depth = np.where(sky, 0.98, 0.94 - 0.76 * np.power(t, 0.8)).astype(np.float32)
    img = np.stack([
        np.where(sky, 0.55 + 0.40 * (1 - yn), 0.35 + 0.25 * t),
        np.where(sky, 0.70 + 0.25 * (1 - yn), 0.33 + 0.18 * t),
        np.where(sky, 0.95, 0.30 + 0.10 * t),
    ], -1).astype(np.float32)
    # a dark bush, near, on the ground line — luma treats it as far
    bush = (xn > 0.45) & (xn < 0.62) & (yn > 0.70) & (yn < 0.92)
    depth = np.where(bush, 0.22, depth)
    img = np.where(bush[..., None], 0.05, img)
    img = _blur_far(img, depth)
    glossy = (~sky) & ~bush
    return img, depth, glossy, bush, ~sky, sky

def make_dark(W=1280, H=720, seed=2):
    """Dim interior: a near dark table, a far wall, one small bright lamp (far, on the back wall)."""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    yn = (yy + 0.5) / H
    xn = (xx + 0.5) / W
    horizon = 0.40
    floor = yn >= horizon
    t = np.clip((yn - horizon) / max(1 - horizon, 1e-3), 0, 1)
    depth = np.where(floor, 0.90 - 0.68 * np.power(t, 0.7), 0.86).astype(np.float32)
    rng = np.random.default_rng(seed)
    noise = rng.random((H, W, 3), np.float32)
    img = np.where(floor[..., None], 0.08 + 0.04 * t[..., None] + 0.02 * noise, 0.06 + 0.03 * noise)
    lamp = (xn > 0.42) & (xn < 0.52) & (yn > 0.10) & (yn < 0.22)
    depth = np.where(lamp, 0.95, depth)
    img = np.where(lamp[..., None], np.array([1.0, 0.85, 0.55], np.float32), img)
    table = (xn > 0.30) & (xn < 0.55) & (yn > 0.58) & (yn < 0.86)
    depth = np.where(table, 0.28, depth)
    img = np.where(table[..., None], 0.05 + 0.04 * noise, img)
    img = _blur_far(np.clip(img, 0, 1), depth)
    return img.astype(np.float32), depth, floor & ~table, table, floor, lamp

def make_outdoor(W=1280, H=720):
    """High-contrast outdoor: blue sky, a red barn (colour bleed), yellow field."""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xn, yn = (xx + 0.5) / W, (yy + 0.5) / H
    horizon = 0.48
    sky = yn < horizon
    t = np.clip((yn - horizon) / max(1.0 - horizon, 1e-3), 0, 1)
    depth = np.where(sky, 0.98, 0.90 - 0.70 * np.power(t, 0.75)).astype(np.float32)
    img = np.zeros((H, W, 3), np.float32)
    img[..., 0] = np.where(sky, 0.42 + 0.30 * (1 - yn), 0.32 + 0.18 * t)
    img[..., 1] = np.where(sky, 0.60 + 0.28 * (1 - yn), 0.28 + 0.10 * t)
    img[..., 2] = np.where(sky, 0.90, 0.14 + 0.05 * t)
    barn = (xn > 0.10) & (xn < 0.32) & (yn > 0.26) & (yn < 0.64)
    depth = np.where(barn, 0.52, depth)
    img = np.where(barn[..., None], np.array([0.64, 0.11, 0.07], np.float32), img)
    field = (~sky) & (xn > 0.48) & (xn < 0.86) & (yn > 0.68)
    img = np.where(field[..., None], np.array([0.74, 0.58, 0.14], np.float32), img)
    return np.clip(img, 0, 1).astype(np.float32), depth, ~sky & ~barn, barn, ~sky, sky & ~barn

def make_foliage(W=1280, H=720):
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xn, yn = (xx + 0.5) / W, (yy + 0.5) / H
    horizon = 0.62
    sky = yn < 0.30
    ground = yn >= horizon
    t = np.clip((yn - horizon) / max(1.0 - horizon, 1e-3), 0, 1)
    depth = np.where(sky, 0.99, np.where(ground, 0.82 - 0.55 * t, 0.46)).astype(np.float32)
    checker = (((xx.astype(np.int32) // 2) + (yy.astype(np.int32) // 3)) & 1).astype(np.float32)
    n = np.random.default_rng(7).random((H, W)).astype(np.float32)
    leaf = (0.40 + 0.38 * checker) * (0.72 + 0.28 * n)
    img = np.zeros((H, W, 3), np.float32)
    img[..., 0] = np.where(sky, 0.48 + 0.22 * (1 - yn), np.where(ground, 0.26 + 0.08 * t, 0.06 + 0.12 * leaf))
    img[..., 1] = np.where(sky, 0.66 + 0.22 * (1 - yn), np.where(ground, 0.24 + 0.06 * t, 0.22 + 0.45 * leaf))
    img[..., 2] = np.where(sky, 0.92, np.where(ground, 0.12, 0.05 + 0.04 * leaf))
    foliage = ~sky & ~ground
    return np.clip(img, 0, 1).astype(np.float32), depth, ground, foliage, ground, sky

def make_night(W=1280, H=720, seed=4):
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xn, yn = (xx + 0.5) / W, (yy + 0.5) / H
    horizon = 0.40
    floor = yn >= horizon
    t = np.clip((yn - horizon) / max(1.0 - horizon, 1e-3), 0, 1)
    depth = np.where(floor, 0.88 - 0.62 * np.power(t, 0.7), 0.90).astype(np.float32)
    rng = np.random.default_rng(seed)
    noise = rng.random((H, W, 3), np.float32)
    img = np.where(floor[..., None], 0.045 + 0.02 * t[..., None] + 0.01 * noise, 0.03 + 0.015 * noise)
    lights = [
        ((0.18, 0.28, 0.08, 0.16), np.array([1.0, 0.78, 0.35], np.float32), 0.86),
        ((0.62, 0.74, 0.10, 0.18), np.array([0.35, 0.55, 1.0], np.float32), 0.92),
        ((0.42, 0.50, 0.22, 0.30), np.array([1.0, 0.28, 0.12], np.float32), 0.80),
    ]
    lamps = np.zeros((H, W), bool)
    for (x0, x1, y0, y1), col, z in lights:
        m = (xn > x0) & (xn < x1) & (yn > y0) & (yn < y1)
        lamps |= m
        depth = np.where(m, z, depth)
        img = np.where(m[..., None], col, img)
    return np.clip(img, 0, 1).astype(np.float32), depth, floor, lamps, floor, np.zeros((H, W), bool)

def make_character(W=1280, H=720):
    img, depth, glossy, _obj, floor, sky = make_outdoor(W, H)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xn, yn = (xx + 0.5) / W, (yy + 0.5) / H
    body = (np.abs(xn - 0.58) < 0.045) & (yn > 0.38) & (yn < 0.86)
    head = ((xn - 0.58) ** 2 + ((yn - 0.34) / 0.7) ** 2) < 0.018
    fig = body | head
    depth = np.where(fig, 0.20 + 0.10 * (yn - 0.34), depth)
    img = np.where(fig[..., None], np.array([0.05, 0.045, 0.04], np.float32), img)
    return img.astype(np.float32), depth.astype(np.float32), glossy & ~fig, fig, floor, sky & ~fig

def paint_hud(img):
    """Thick white text, a saturated health bar, a saturated minimap. Every masked pixel trips the UI test."""
    out = np.array(img, copy=True)
    H, W = out.shape[:2]
    text = np.zeros((H, W), bool)
    y0, x0 = H - 72, 20
    text[y0:y0 + 46, x0:x0 + 6] = True
    text[y0:y0 + 46, x0 + 20:x0 + 26] = True
    text[y0 + 18:y0 + 26, x0:x0 + 26] = True
    bar = np.zeros((H, W), bool)
    bar[y0:y0 + 20, 64:240] = True
    mm = np.zeros((H, W), bool)
    mm[18:120, W - 150:W - 18] = True
    out[text] = (1.0, 1.0, 1.0)
    out[bar] = (0.95, 0.04, 0.03)
    out[mm] = (0.08, 0.22, 0.90)
    return np.clip(out, 0, 1).astype(np.float32), text | bar | mm

def make_hud(W=1280, H=720):
    img, depth, glossy, obj, floor, sky = make_outdoor(W, H)
    img, hud = paint_hud(img)
    return img, depth, glossy, obj, floor, sky, hud

def depth_metrics(h, img, gt, motion=None, pan_uv=None):
    H, W = img.shape[:2]
    lw, lh = light_size(W, H, 4, h.G)
    raw_d, raw_l, protect = cs_depth(h, img, lw, lh, motion, pan_uv, use_parallax=motion is not None)
    depth, nx, ny, smooth = cs_depth_blur(h, raw_d, raw_l, protect)
    old = old_luma_depth(h, img, lw, lh)
    gt_l = _resize_scalar(gt, lw, lh)
    return dict(
        old_spearman=spearman(old, gt_l), new_spearman=spearman(depth, gt_l),
        old_absrel=absrel(old, gt_l), new_absrel=absrel(depth, gt_l),
        depth=depth, nx=nx, ny=ny, smooth=smooth, old=old, gt=gt_l, lw=lw, lh=lh,
    )

def save_gray(path, a):
    Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8)).save(path)

def save_rgb(path, a):
    Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8)).save(path)

def upsample_nn(a, H, W):
    lh, lw = a.shape[:2]
    ys = np.clip((np.arange(H) * lh / H).astype(int), 0, lh - 1)
    xs = np.clip((np.arange(W) * lw / W).astype(int), 0, lw - 1)
    return a[ys[:, None], xs[None, :]]

def run_scene_gi(h, img, preset_ssrt, preset_old):
    H, W = img.shape[:2]
    G = h.G
    grid = h.phase1(img)
    bits, _, _ = h.phase2(grid, lx=5, ly=6)
    lw, lh = light_size(W, H, preset_ssrt['div'], G)
    raw_d, raw_l, protect = cs_depth(h, img, lw, lh)
    depth, nx, ny, smooth = cs_depth_blur(h, raw_d, raw_l, protect)
    old_kw = dict(rays=preset_old['rays'], steps=preset_old['steps'], stepCells=preset_old['stepCells'],
                  stepGrowth=preset_old['stepGrowth'], heightScale=CUE['heightScale'], falloff=CUE['falloff'])
    lw_o, lh_o = light_size(W, H, preset_old['div'], G)
    light_old, _ = h.phase3(grid, bits, W, H, lw_o, lh_o, **old_kw)
    light_old = h.filter3(light_old, rangeK=CUE['rangeK'])
    final_old, _ = h.phase4(img, light_old, CUE['strength'], CUE['rangeK'], CUE['aoGame'])
    light_new, _ = march_depth(h, grid, bits, depth, W, H, lw, lh, use_gi=True, use_ao=True, preset=preset_ssrt, protect=protect)
    light_new = h.filter3(light_new, rangeK=CUE['rangeK'])
    gi_after, _ = composite_ssrt(h, img, light_new, np.zeros((*light_new.shape[:2], 4), np.float32), 0, 0)
    refl = _reflect_from_buffers(h, img, depth, nx, ny, smooth, preset_ssrt)
    light = cs_light_dir(grid, G)
    shadow = cs_contact(h, grid, depth, light, W, H, lw, lh, preset_ssrt, protect)
    packed = cs_pack(refl, shadow, True, True)
    final, _ = composite_ssrt(h, img, light_new, packed, CUE['reflStrength'], CUE['contactStrength'])
    # features-off equals today's phase3/4, and the new composite with zero strengths matches phase4
    off_a, _ = h.phase4(img, light_old, CUE['strength'], CUE['rangeK'], CUE['aoGame'])
    off_b, _ = composite_ssrt(h, img, light_old, np.zeros((*light_old.shape[:2], 4), np.float32), 0.0, 0.0)
    return dict(
        grid=grid, depth=depth, nx=nx, ny=ny, smooth=smooth, old_depth=old_luma_depth(h, img, lw, lh),
        gi_before=final_old, gi_after=gi_after, refl=refl, shadow=shadow, packed=packed,
        final=final, light=light, lw=lw, lh=lh,
        regress_max=float(np.max(np.abs(off_a - off_b))),
        regress_off_is_today=float(np.max(np.abs(off_a - final_old))),
    )

def _reflect_from_buffers(h, img, depth, nx, ny, smooth, preset):
    H, W = img.shape[:2]
    lh, lw = depth.shape
    c = CUE
    ys, xs = np.mgrid[0:lh, 0:lw]
    uvx = (xs + 0.5) / lw
    uvy = (ys + 0.5) / lh
    aspect = W / float(H)
    nz = np.sqrt(sat(1.0 - nx * nx - ny * ny))
    ln = np.sqrt(nx * nx + ny * ny + nz * nz) + 1e-8
    Nx, Ny, Nz = nx / ln, ny / ln, nz / ln
    Vx = (uvx * 2 - 1) * aspect
    Vy = uvy * 2 - 1
    Vz = 1.0
    vln = np.sqrt(Vx * Vx + Vy * Vy + Vz * Vz)
    Vx, Vy, Vz = Vx / vln, Vy / vln, Vz / vln
    dot = Vx * Nx + Vy * Ny + Vz * Nz
    Rx, Ry, Rz = Vx - 2 * dot * Nx, Vy - 2 * dot * Ny, Vz - 2 * dot * Nz
    uvdx, uvdy = Rx / aspect, Ry
    dl = np.sqrt(uvdx * uvdx + uvdy * uvdy)
    uvdx = np.divide(uvdx, dl, out=np.zeros_like(uvdx), where=dl > 1e-4)
    uvdy = np.divide(uvdy, dl, out=np.zeros_like(uvdy), where=dl > 1e-4)
    active = (smooth >= c['glossMin']) & (Rz >= 0.05) & (dl >= 1e-4)
    return _reflect_march(img, depth, smooth, uvx, uvy, uvdx, uvdy, Rz, active, W, H, steps=preset['reflSteps'])

def reflection_fraction(refl, glossy_light):
    energy = refl[..., :3].sum(-1) * refl[..., 3]
    total = float(energy.sum()) + 1e-8
    on = float(energy[glossy_light].sum())
    return on / total, total

def contact_locality(shadow, contact_band):
    inside = shadow[contact_band]
    outside = shadow[~contact_band]
    mi = float(inside.mean()) if inside.size else 0.0
    mo = float(outside.mean()) if outside.size else 0.0
    return mi / (mo + 1e-8), mi, mo

def cost_model():
    """ALU / texel / unique-footprint bandwidth, efficiency 0.40, pass time = max of the three, total = sum.
    A bilinear SampleLevel counts as 4 texel fetches. Bandwidth is the unique bytes touched (a cached
    2x2 tap does not multiply DRAM). L2 residency is not assumed for the full-frame image; the 64 KB
    proxy is included in the march footprint. Gating is not credited."""
    out = {}
    for name, p in SSRT_PRESETS.items():
        W, H = p['w'], p['h']
        lw, lh = light_size(W, H, p['div'], 128)
        pix = lw * lh
        full = W * H
        tflops = p['tflops'] * 1e12 * EFF
        gbs = p['gbs'] * 1e9 * EFF
        gtex = p['gtex'] * 1e9 * EFF
        def tmax(alu, fetches, read_b, write_b):
            return max(alu / tflops, fetches / gtex, (read_b + write_b) / gbs) * 1e3
        game = full * 4
        raw_b = pix * 8          # RGBA16F: depth, brightness, protect
        depth_b = pix * 8        # RGBA16F
        light_b = pix * 8        # R32G32
        refl_b = pix * 4
        sh_b = pix * 4           # R32
        pack_b = pix * 4
        # per-pixel ALU, from the HLSL (the taken GI+AO / gloss path; uniform branches that are off are not counted)
        down_alu = 16384 * (16 * 12 + 20)
        depth_alu = pix * 70
        blur_alu = pix * 48
        march_alu = pix * (p['rays'] * p['steps'] * 60 + p['rays'] * 36)
        filter_alu = pix * 40
        light_alu = 16384 * 8 + 256 * 8
        refl_alu = pix * (p['reflSteps'] * 28 + 40)
        contact_alu = pix * (p['contactRays'] * p['contactSteps'] * 22 + 30)
        pack_alu = pix * 8
        comp_alu = full * 55
        # CSSsrtAccum: 3x3 of GI/depth/refl/contact (36) + bilinear history (16) + 2 motion + 1 pan.
        # Unique bytes: current + history + write of those four, plus one motion texel (8 B) per light pixel.
        acc_fetches = pix * 55 + 64
        acc_alu = pix * 80 + 16384 * 4
        acc_read = pix * (8 + 8 + 4 + 4 + 8 + 8 + 4 + 4 + 8) + 65536
        acc_write = pix * (8 + 8 + 4 + 4) + 16
        passes = {
            'downsample': tmax(down_alu, 16384 * 16 * 4, game, 65536),
            'depth': tmax(depth_alu, pix * 14, game, raw_b),   # 3x3, 4-tap ring, one column-sky sample
            'depth_blur': tmax(blur_alu, pix * 9, raw_b, depth_b),
            # ray loads plus an 8x2 near-field gather on pixels the rays missed (counted on every pixel).
            'march': tmax(march_alu, pix * (p['rays'] * p['steps'] * 2 + 24), 65536 + depth_b, light_b),
            'filter': tmax(filter_alu, pix * 9, light_b, light_b),
            'light_dir': tmax(light_alu, 16384, 65536, 16),
            'reflect': tmax(refl_alu, pix * (1 + p['reflSteps'] + 2), depth_b + game, refl_b),
            'contact': tmax(contact_alu, pix * (4 + p['contactRays'] * p['contactSteps']), depth_b + 65536, sh_b),
            'pack': tmax(pack_alu, pix * 2, refl_b + sh_b, pack_b),
            'temporal': tmax(acc_alu, acc_fetches, acc_read, acc_write),
            # 1 game + 4 lighting + 1 nearest packed (the 2x2 packed taps are gone).
            'composite': tmax(comp_alu, full * 6, game + light_b + pack_b, game),
        }
        # reflect's game read is sparse (one hit tap). Count a quarter of the frame, not a second full read,
        # on top of the texel term which already counts every tap.
        passes['reflect'] = tmax(refl_alu, pix * (1 + p['reflSteps'] + 2), depth_b + game // 4, refl_b)
        total = float(sum(passes.values()))
        out[name] = dict(
            resolution=[W, H], light=[lw, lh], divisor=p['div'],
            rays=p['rays'], steps=p['steps'], refl_steps=p['reflSteps'],
            contact_rays=p['contactRays'], contact_steps=p['contactSteps'],
            efficiency=EFF,
            card=dict(tflops=p['tflops'], gb_s=p['gbs'], gtexel_s=p['gtex']),
            passes_ms={k: round(v, 4) for k, v in passes.items()},
            total_ms=round(total, 4),
            within_budget=total <= (1.80 if '1050' in name else 2.00),
        )
    return out

def _motion_from_depth(gt, pan_px):
    """Nearer pixels move more than the background. pan_px is the far-field motion in source pixels."""
    H, W = gt.shape
    scale = (1.0 + 1.6 * (1.0 - gt)).astype(np.float32)
    mv = np.zeros((H, W, 2), np.float32)
    mv[..., 0] = pan_px[0] * scale
    mv[..., 1] = pan_px[1] * scale
    pan_uv = np.array([pan_px[0] / W, pan_px[1] / H], np.float32)
    return mv, pan_uv

def _srgb_to_lab(rgb):
    """Display RGB (the composited image) to CIE Lab. CIE76 ΔE is the Euclidean distance."""
    a = np.asarray(rgb, np.float64)
    x = np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)
    M = np.array([[0.4124564, 0.3575761, 0.1804375],
                  [0.2126729, 0.7151522, 0.0721750],
                  [0.0193339, 0.1191920, 0.9503041]], np.float64)
    xyz = x @ M.T
    white = np.array([0.95047, 1.0, 1.08883], np.float64)
    t = xyz / white
    d = 6.0 / 29.0
    f = np.where(t > d ** 3, np.cbrt(t), t / (3 * d * d) + 4.0 / 29.0)
    L = 116 * f[..., 1] - 16
    A = 500 * (f[..., 0] - f[..., 1])
    B = 200 * (f[..., 1] - f[..., 2])
    return np.stack([L, A, B], -1)

def delta_e(a, b):
    d = _srgb_to_lab(a) - _srgb_to_lab(b)
    return np.sqrt(np.sum(d * d, -1))

def mean_luma(img):
    return float((img[..., 0] * 0.2126 + img[..., 1] * 0.7152 + img[..., 2] * 0.0722).mean())

def brightness_shift(final, original):
    a, b = mean_luma(original), mean_luma(final)
    return (b - a) / max(a, 1e-6)

def look_metrics(original, final, hud=None, sky=None, jnd=2.0):
    de = delta_e(original, final)
    vis = de > jnd
    effect = vis if hud is None else (vis & ~hud)
    if sky is not None:
        effect = effect & ~sky
    return dict(
        brightness_shift=brightness_shift(final, original),
        visible_fraction=float(vis.mean()),
        mean_de_effect=float(de[effect].mean()) if effect.any() else 0.0,
        mean_de=float(de.mean()),
        hud_de=float(de[hud].mean()) if hud is not None and hud.any() else 0.0,
        sky_de=float(de[sky].mean()) if sky is not None and np.asarray(sky).any() else 0.0,
    )

def fullres_protect(img):
    """Same full-res test as PSCompositeSsrt. Used as the HUD/sky metric mask when a scene has no painted mask."""
    bg = np.maximum(img[..., 0] * 0.2126 + img[..., 1] * 0.7152 + img[..., 2] * 0.0722, 0.75 * img.max(-1))
    H = img.shape[0]
    yy = (np.arange(H) + 0.5) / H
    mx, mn = img.max(-1), img.min(-1)
    satC = (mx - mn) / np.maximum(mx, 1e-3)
    # No guide spread here: callers that need the flat-UI test use the composite. This mask is the
    # obvious saturated fill plus anything already brighter than the emissive gate.
    ui = ((img[..., 0] > 0.85) & (img[..., 1] < 0.12) & (img[..., 2] < 0.12)) | ((img[..., 2] > 0.80) & (img[..., 0] < 0.20) & (img[..., 1] < 0.35))
    glow = bg > 0.80
    upper = yy[:, None] < 0.52
    blue_sky = (bg > 0.36) & (img[..., 2] > img[..., 0] + 0.05) & (img[..., 2] > img[..., 1] - 0.04)
    pale = (satC < 0.30) & (bg > 0.50) & (img[..., 2] > img[..., 0] - 0.02)
    sky = upper & (blue_sky | pale)
    return ui | glow, sky

def shade_frame(mod, h, img, preset):
    """One features-on frame through either this module or the frozen r1 module."""
    H, W = img.shape[:2]
    grid = h.phase1(img)
    bits, _, _ = h.phase2(grid, lx=5, ly=6)
    lw, lh = light_size(W, H, preset['div'], h.G)
    got = mod.cs_depth(h, img, lw, lh)
    protect = None
    if len(got) == 3:
        raw_d, raw_l, protect = got
        depth, nx, ny, smooth = mod.cs_depth_blur(h, raw_d, raw_l, protect)
        light, _ = mod.march_depth(h, grid, bits, depth, W, H, lw, lh, use_gi=True, use_ao=True,
                                   preset=preset, protect=protect)
        shadow = mod.cs_contact(h, grid, depth, mod.cs_light_dir(grid, h.G), W, H, lw, lh, preset, protect)
    else:
        raw_d, raw_l = got
        depth, nx, ny, smooth = mod.cs_depth_blur(h, raw_d, raw_l)
        light, _ = mod.march_depth(h, grid, bits, depth, W, H, lw, lh, use_gi=True, use_ao=True, preset=preset)
        shadow = mod.cs_contact(h, grid, depth, mod.cs_light_dir(grid, h.G), W, H, lw, lh, preset)
    light = h.filter3(light, rangeK=mod.CUE['rangeK'])
    refl = mod._reflect_from_buffers(h, img, depth, nx, ny, smooth, preset)
    packed = mod.cs_pack(refl, shadow, True, True)
    final, _ = mod.composite_ssrt(h, img, light, packed, mod.CUE['reflStrength'], mod.CUE['contactStrength'])
    return dict(final=final.astype(np.float32), depth=depth, lw=lw, lh=lh, refl=refl, shadow=shadow, smooth=smooth)

def load_r1():
    import subprocess, types
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.normpath(os.path.join(here, '..', '..', '..', '..'))
    src = subprocess.check_output(['git', 'show', '03a4715:windows/ssgi/tests/harness/ssrt_ref.py'], cwd=repo)
    mod = types.ModuleType('ssrt_ref_r1')
    exec(compile(src, 'ssrt_ref_r1.py', 'exec'), mod.__dict__)
    return mod

def _fit_row(img, height):
    H, W = img.shape[:2]
    if H == height:
        return img
    ys = np.clip((np.arange(height) * H / height).astype(int), 0, H - 1)
    width = max(1, int(round(W * height / H)))
    xs = np.clip((np.arange(width) * W / width).astype(int), 0, W - 1)
    return img[ys[:, None], xs[None, :]]

def write_contact_sheet(path, rows):
    """rows: (name, original, old, new, depth) display RGB, depth is gray."""
    from PIL import ImageDraw, ImageFont
    cell_h = 150
    label_w, head_h, gap = 120, 28, 4
    fitted = []
    cell_w = 0
    for name, orig, old, new, depth in rows:
        cols = []
        for im in (orig, old, new):
            cols.append(_fit_row(np.clip(im, 0, 1), cell_h))
        d = _fit_row(np.clip(depth, 0, 1), cell_h)
        if d.ndim == 2:
            d = np.repeat(d[..., None], 3, -1)
        cols.append(d)
        cell_w = max(cell_w, max(c.shape[1] for c in cols))
        fitted.append((name, cols))
    sheet_w = label_w + 4 * (cell_w + gap) + gap
    sheet_h = head_h + len(fitted) * (cell_h + gap) + gap
    canvas = np.full((sheet_h, sheet_w, 3), 0.08, np.float32)
    im = Image.fromarray((canvas * 255).astype(np.uint8))
    draw = ImageDraw.Draw(im)
    try:
        font = ImageFont.truetype('DejaVuSans.ttf', 14)
    except Exception:
        font = ImageFont.load_default()
    headers = ('original', 'old (03a4715)', 'new', 'new depth')
    for i, title in enumerate(headers):
        draw.text((label_w + i * (cell_w + gap), 6), title, fill=(230, 230, 230), font=font)
    for r, (name, cols) in enumerate(fitted):
        y = head_h + r * (cell_h + gap)
        draw.text((6, y + cell_h // 2 - 8), name[:16], fill=(230, 230, 230), font=font)
        for c, col in enumerate(cols):
            x = label_w + c * (cell_w + gap)
            tile = np.zeros((cell_h, cell_w, 3), np.float32)
            tile[:, :col.shape[1]] = col
            im.paste(Image.fromarray((np.clip(tile, 0, 1) * 255 + 0.5).astype(np.uint8)), (x, y))
    im.save(path)

def run(h, preview_root='/workspace/ssrt_preview', build_dir=None):
    here = os.path.dirname(os.path.abspath(__file__))
    if build_dir is None:
        build_dir = os.path.normpath(os.path.join(here, '..', '..', 'build', 'ssrt'))
    os.makedirs(build_dir, exist_ok=True)
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M')
    out = os.path.join(preview_root, stamp + '-r2')
    os.makedirs(out, exist_ok=True)
    costs = cost_model()
    with open(os.path.join(build_dir, 'cost_estimate.json'), 'w') as f:
        json.dump(costs, f, indent=2)
    print('=== SSRT cost estimate (efficiency %.0f%%, not a GPU measurement) ===' % (EFF * 100))
    print(f'{"preset":<22} {"light":>12} {"total ms":>10} {"budget":>8}')
    for name, c in costs.items():
        print(f'{name:<22} {c["light"][0]:>5}x{c["light"][1]:<5} {c["total_ms"]:10.3f} {"OK" if c["within_budget"] else "OVER":>8}')
        for pk, pv in c['passes_ms'].items():
            print(f'    {pk:<16} {pv:8.3f}')

    p1050 = SSRT_PRESETS['GTX1050Ti_1080p']
    old1050 = dict(div=4, rays=4, steps=16, stepCells=1.0, stepGrowth=1.15)
    scenes = {
        'room': make_room(),
        'sky': make_sky(),
        'dark': make_dark(),
    }
    metrics = {'presets_ms': {k: costs[k]['total_ms'] for k in costs}, 'depth': {}, 'notes': {
        'old_depth': '1 - brightness (r3 height field, bright = near)',
        'new_depth': 'horizon + contrast + defocus + sky luma, parallax only on the pan sequence',
        'absrel': 'scale/shift least squares, negative scale rejected',
        'contact_N_px': 16,
        'temporal': 'off',
    }}
    # Main previews are the room (the glossy floor + contact case).
    room_img, room_gt, room_glossy, room_crate, room_floor, _ = scenes['room']
    bundle = run_scene_gi(h, room_img, p1050, old1050)
    H, W = room_img.shape[:2]
    gloss_l = downsample_mask(room_glossy, bundle['lw'], bundle['lh'])
    frac, energy = reflection_fraction(bundle['refl'], gloss_l)
    # contact band at light resolution, N = 16 source pixels
    n_light = max(1, 16 // p1050['div'])
    crate_l = downsample_mask(room_crate, bundle['lw'], bundle['lh'])
    floor_l = downsample_mask(room_floor, bundle['lw'], bundle['lh'])
    band = dilate(crate_l, n_light) & floor_l & ~crate_l
    ratio, mean_in, mean_out = contact_locality(bundle['shadow'], band)
    metrics['reflection_floor_fraction'] = frac
    metrics['reflection_energy'] = energy
    metrics['contact_locality_ratio'] = ratio
    metrics['contact_mean_in'] = mean_in
    metrics['contact_mean_out'] = mean_out
    metrics['no_regression_max_abs'] = bundle['regress_max']
    metrics['features_off_equals_today'] = bundle['regress_off_is_today']
    metrics['smooth_glossy_mean'] = float(bundle['smooth'][gloss_l].mean()) if gloss_l.any() else 0.0
    metrics['smooth_elsewhere_mean'] = float(bundle['smooth'][~gloss_l].mean())

    def normals_rgb(nx, ny):
        nz = np.sqrt(np.clip(1 - nx * nx - ny * ny, 0, 1))
        return np.stack([(nx + 1) * 0.5, (ny + 1) * 0.5, (nz + 1) * 0.5], -1)

    save_rgb(f'{out}/original.png', room_img)
    save_gray(f'{out}/depth_old.png', upsample_nn(bundle['old_depth'], H, W))
    save_gray(f'{out}/depth_new.png', upsample_nn(bundle['depth'], H, W))
    save_rgb(f'{out}/depth_old_vs_new.png', np.concatenate([
        np.repeat(upsample_nn(bundle['old_depth'], H, W)[..., None], 3, -1),
        np.repeat(upsample_nn(bundle['depth'], H, W)[..., None], 3, -1),
    ], 1))
    save_rgb(f'{out}/normals.png', upsample_nn(normals_rgb(bundle['nx'], bundle['ny']), H, W))
    save_rgb(f'{out}/gi_before.png', bundle['gi_before'])
    save_rgb(f'{out}/gi_after.png', bundle['gi_after'])
    save_rgb(f'{out}/reflections.png', upsample_nn(bundle['refl'][..., :3] * bundle['refl'][..., 3:4], H, W))
    save_gray(f'{out}/contact.png', upsample_nn(bundle['shadow'], H, W))
    save_rgb(f'{out}/final_before.png', bundle['gi_before'])
    save_rgb(f'{out}/final_after.png', bundle['final'])
    save_rgb(f'{out}/final_side_by_side.png', np.concatenate([room_img, bundle['gi_before'], bundle['final']], 1))

    for scene_name, (img, gt, glossy, obj, floor, _far) in scenes.items():
        dm = depth_metrics(h, img, gt)
        metrics['depth'][scene_name] = {k: dm[k] for k in ('old_spearman', 'new_spearman', 'old_absrel', 'new_absrel')}
        if scene_name != 'room':
            sub = os.path.join(out, scene_name)
            os.makedirs(sub, exist_ok=True)
            save_rgb(f'{sub}/original.png', img)
            save_gray(f'{sub}/depth_old.png', upsample_nn(dm['old'], img.shape[0], img.shape[1]))
            save_gray(f'{sub}/depth_new.png', upsample_nn(dm['depth'], img.shape[0], img.shape[1]))
            save_rgb(f'{sub}/normals.png', upsample_nn(normals_rgb(dm['nx'], dm['ny']), img.shape[0], img.shape[1]))

    # room depth metrics from the same buffers (avoid a second depth pass disagreeing)
    gt_l = _resize_scalar(room_gt, bundle['lw'], bundle['lh'])
    metrics['depth']['room'] = dict(
        old_spearman=spearman(bundle['old_depth'], gt_l), new_spearman=spearman(bundle['depth'], gt_l),
        old_absrel=absrel(bundle['old_depth'], gt_l), new_absrel=absrel(bundle['depth'], gt_l),
    )

    # Moving camera: parallax cue on vs off. Far-field pan of 10 px, nearer pixels move faster.
    pan_px = np.array([10.0, 0.0], np.float32)
    mv, pan_uv = _motion_from_depth(room_gt, pan_px)
    with_p = depth_metrics(h, room_img, room_gt, mv, pan_uv)
    no_p = depth_metrics(h, room_img, room_gt, None, None)
    metrics['depth']['pan_parallax'] = dict(
        off_spearman=no_p['old_spearman'] and no_p['new_spearman'],
        on_spearman=with_p['new_spearman'],
        off_absrel=no_p['new_absrel'], on_absrel=with_p['new_absrel'],
        old_spearman=no_p['old_spearman'],
        pan_px=10.0, note='weight is 0 when motion or pan is missing, or when the pan is under panMinPx',
    )
    # the "off_spearman" line above used a boolean trick by mistake if old is 0. Fix explicitly.
    metrics['depth']['pan_parallax']['off_spearman'] = no_p['new_spearman']
    save_gray(f'{out}/depth_parallax.png', upsample_nn(with_p['depth'], H, W))

    # Sample image the existing harness already builds (Poly Haven composite), no ground-truth depth.
    try:
        sample = h.make_scene()
    except Exception as ex:
        sample = None
        metrics['sample_error'] = str(ex)
    if sample is not None:
        sb = run_scene_gi(h, sample, p1050, old1050)
        sub = os.path.join(out, 'sample')
        os.makedirs(sub, exist_ok=True)
        Hs, Ws = sample.shape[:2]
        save_rgb(f'{sub}/original.png', sample)
        save_gray(f'{sub}/depth_old.png', upsample_nn(sb['old_depth'], Hs, Ws))
        save_gray(f'{sub}/depth_new.png', upsample_nn(sb['depth'], Hs, Ws))
        save_rgb(f'{sub}/normals.png', upsample_nn(normals_rgb(sb['nx'], sb['ny']), Hs, Ws))
        save_rgb(f'{sub}/gi_before.png', sb['gi_before'])
        save_rgb(f'{sub}/gi_after.png', sb['gi_after'])
        save_rgb(f'{sub}/reflections.png', upsample_nn(sb['refl'][..., :3] * sb['refl'][..., 3:4], Hs, Ws))
        save_gray(f'{sub}/contact.png', upsample_nn(sb['shadow'], Hs, Ws))
        save_rgb(f'{sub}/final_before.png', sb['gi_before'])
        save_rgb(f'{sub}/final_after.png', sb['final'])
        save_rgb(f'{sub}/final_side_by_side.png', np.concatenate([sample, sb['gi_before'], sb['final']], 1))
        metrics['sample_regress_max_abs'] = sb['regress_max']

    # trivial-pan check: a 0.2 px pan must not change depth versus the no-motion run
    mv_tiny, pan_tiny = _motion_from_depth(room_gt, np.array([0.2, 0.0], np.float32))
    tiny = depth_metrics(h, room_img, room_gt, mv_tiny, pan_tiny)
    metrics['parallax_trivial_max_abs'] = float(np.max(np.abs(tiny['depth'] - no_p['depth'])))

    r1 = load_r1()
    me = sys.modules[__name__]
    p_old = r1.SSRT_PRESETS['GTX1050Ti_1080p']
    r2_scenes = []
    for name in ('room', 'sky', 'dark'):
        img, gt, glossy, obj, floor, sky_m = scenes[name]
        r2_scenes.append((name, img, gt, None if name == 'dark' else sky_m, None))
    for name, built in (('outdoor', make_outdoor()), ('foliage', make_foliage()),
                        ('night', make_night()), ('character', make_character())):
        img, gt, glossy, obj, floor, sky_m = built
        r2_scenes.append((name, img, gt, None if name == 'night' else sky_m, None))
    hud_img, hud_gt, _, _, _, hud_sky, hud_mask = make_hud()
    r2_scenes.append(('hud', hud_img, hud_gt, hud_sky, hud_mask))
    for name, path in (
        ('haloisland', '/workspace/haloisland-screen.png'),
        ('atomic_dawn', '/workspace/atomic-dawn-still-odd.png'),
        ('halo_center', '/workspace/halo-center-check.png'),
    ):
        if not os.path.isfile(path):
            print('skip missing screenshot', path)
            continue
        im = np.asarray(Image.open(path).convert('RGB'), np.float32) / 255.0
        if im.shape[0] > 720:
            im = _fit_row(im, 720)
        det_ui, det_sky = fullres_protect(im)
        r2_scenes.append((name, im, None, det_sky, det_ui))
    r2 = {'scenes': {}, 'preview': out}
    sheet_rows = []
    print('\n=== old (03a4715) vs new ===')
    print(f'{"scene":<16} {"dY old":>8} {"dY new":>8} {"vis old":>8} {"vis new":>8} {"dE fx":>8} {"HUD":>8} {"sky":>8} {"rho old":>8} {"rho new":>8}')
    for name, img, gt, sky_m, hud_m in r2_scenes:
        print(f'  render {name}', flush=True)
        old_b = shade_frame(r1, h, img, p_old)
        new_b = shade_frame(me, h, img, p1050)
        sky_ok = sky_m is not None and np.asarray(sky_m).any()
        hud_ok = hud_m is not None and np.asarray(hud_m).any()
        mo = look_metrics(img, old_b['final'], hud_m if hud_ok else None, sky_m if sky_ok else None)
        mn = look_metrics(img, new_b['final'], hud_m if hud_ok else None, sky_m if sky_ok else None)
        row = {'old': mo, 'new': mn, 'depth': None}
        if gt is not None:
            row['depth'] = dict(
                old_spearman=spearman(old_b['depth'], _resize_scalar(gt, old_b['lw'], old_b['lh'])),
                new_spearman=spearman(new_b['depth'], _resize_scalar(gt, new_b['lw'], new_b['lh'])),
                old_absrel=absrel(old_b['depth'], _resize_scalar(gt, old_b['lw'], old_b['lh'])),
                new_absrel=absrel(new_b['depth'], _resize_scalar(gt, new_b['lw'], new_b['lh'])),
                luma_spearman=spearman(old_luma_depth(h, img, new_b['lw'], new_b['lh']),
                                       _resize_scalar(gt, new_b['lw'], new_b['lh'])),
            )
        r2['scenes'][name] = row
        sub = os.path.join(out, name)
        os.makedirs(sub, exist_ok=True)
        save_rgb(f'{sub}/original.png', img)
        save_rgb(f'{sub}/old_final.png', old_b['final'])
        save_rgb(f'{sub}/new_final.png', new_b['final'])
        dep = upsample_nn(new_b['depth'], img.shape[0], img.shape[1])
        save_gray(f'{sub}/new_depth.png', dep)
        sheet_rows.append((name, img, old_b['final'], new_b['final'], dep))
        rho_o = row['depth']['old_spearman'] if row['depth'] else float('nan')
        rho_n = row['depth']['new_spearman'] if row['depth'] else float('nan')
        print(f'{name:<16} {mo["brightness_shift"]:8.3f} {mn["brightness_shift"]:8.3f} '
              f'{mo["visible_fraction"]:8.3f} {mn["visible_fraction"]:8.3f} {mn["mean_de_effect"]:8.2f} '
              f'{mn["hud_de"]:8.3f} {mn["sky_de"]:8.3f} {rho_o:8.3f} {rho_n:8.3f}')
    write_contact_sheet(os.path.join(out, 'contact_sheet.png'), sheet_rows)
    r2['presets_ms'] = metrics['presets_ms']
    with open(os.path.join(build_dir, 'metrics_r2.json'), 'w') as f:
        json.dump(r2, f, indent=2)
    metrics['r2_preview'] = out
    with open(os.path.join(build_dir, 'metrics.json'), 'w') as f:
        json.dump(metrics, f, indent=2)
    print('\n=== SSRT metrics ===')
    print(f'{"scene":<16} {"old rho":>8} {"new rho":>8} {"old absrel":>10} {"new absrel":>10}')
    for name, d in metrics['depth'].items():
        if 'old_spearman' not in d:
            continue
        print(f'{name:<16} {d["old_spearman"]:8.3f} {d.get("new_spearman", d.get("on_spearman", 0)):8.3f} '
              f'{d.get("old_absrel", 0):10.3f} {d.get("new_absrel", d.get("on_absrel", 0)):10.3f}')
    pp = metrics['depth']['pan_parallax']
    print(f'{"pan off/on":<16} {pp["old_spearman"]:8.3f} {pp["off_spearman"]:8.3f} / {pp["on_spearman"]:.3f}')
    print(f'reflection energy on glossy floor: {frac * 100:.1f}%  (energy {energy:.4f})')
    print(f'contact locality (in/out): {ratio:.2f}   mean in {mean_in:.4f}  out {mean_out:.4f}')
    print(f'features-off vs phase4 max abs: {bundle["regress_max"]:.3e}')
    print(f'smooth glossy {metrics["smooth_glossy_mean"]:.3f}  elsewhere {metrics["smooth_elsewhere_mean"]:.3f}')
    print('previews:', out)
    print('metrics:', os.path.join(build_dir, 'metrics.json'))
    return metrics, costs, out
