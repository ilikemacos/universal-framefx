"""Temporal accumulation reference for SSRT. Mirrors shaders/ssrt_temporal.hlsl.

All randomness comes from numpy Generator(PCG64(seed)). The same seed repeats the same numbers.
"""
import os, json, datetime
import numpy as np
from PIL import Image

import ssrt_ref as R

# Harness resolution divides the 1050 Ti light grid (640/128 = 5, 512/128 = 4) so a
# constant pan of a few source pixels lands on whole light texels.
W, H, FRAMES = 640, 512, 8
SEEDS = tuple(range(1, 17))


def _clip01(x):
    return np.clip(x, 0.0, 1.0)


def _shift(img, dx, dy):
    """img shifted so out[y, x] = img[y+dy, x+dx]. Valid where the source was on screen."""
    out = np.zeros_like(img)
    valid = np.zeros(img.shape[:2], bool)
    H_, W_ = img.shape[:2]
    dx, dy = int(dx), int(dy)
    x0, x1 = max(0, -dx), min(W_, W_ - dx)
    y0, y1 = max(0, -dy), min(H_, H_ - dy)
    if x1 > x0 and y1 > y0:
        out[y0:y1, x0:x1] = img[y0 + dy:y1 + dy, x0 + dx:x1 + dx]
        valid[y0:y1, x0:x1] = True
    return out, valid


def _dilate(mask, r):
    if r <= 0 or not mask.any():
        return mask
    p = np.pad(mask, r, constant_values=False)
    v = np.lib.stride_tricks.sliding_window_view(p, (2 * r + 1, 2 * r + 1))
    return v.any(axis=(-1, -2))


def _box_minmax(a):
    """3x3 min/max, edge clamped. a is (H, W) or (H, W, C)."""
    lh, lw = a.shape[:2]
    pads = [(1, 1), (1, 1)] + [(0, 0)] * (a.ndim - 2)
    p = np.pad(a, pads, mode='edge')
    v = np.lib.stride_tricks.sliding_window_view(p, (3, 3), axis=(0, 1))
    # v: (lh, lw, ..., 3, 3)
    axes = (-1, -2)
    return v.min(axis=axes), v.max(axis=axes)


def _bilinear(tex, hpx, hpy):
    """Sample tex at texel-index coords (integer = that texel). Returns sample, inside."""
    lh, lw = tex.shape[:2]
    flx = np.floor(hpx); fly = np.floor(hpy)
    frx = (hpx - flx)[..., None] if tex.ndim == 3 else (hpx - flx)
    fry = (hpy - fly)[..., None] if tex.ndim == 3 else (hpy - fly)
    h0x = flx.astype(np.int32); h0y = fly.astype(np.int32)
    inside = (h0x >= 0) & (h0y >= 0) & (h0x < lw) & (h0y < lh)
    inside &= np.where(hpx - flx > 1e-6, h0x + 1 < lw, True)
    inside &= np.where(hpy - fly > 1e-6, h0y + 1 < lh, True)
    x0 = np.clip(h0x, 0, lw - 1); y0 = np.clip(h0y, 0, lh - 1)
    x1 = np.clip(h0x + 1, 0, lw - 1); y1 = np.clip(h0y + 1, 0, lh - 1)
    s00 = tex[y0, x0]; s10 = tex[y0, x1]; s01 = tex[y1, x0]; s11 = tex[y1, x1]
    samp = (s00 * (1 - frx) + s10 * frx) * (1 - fry) + (s01 * (1 - frx) + s11 * frx) * fry
    return samp.astype(np.float32), inside


def _sample_mv(mv, uvx, uvy):
    """mv is (H, W, 2+) full-res. uv in 0..1 of the light pixel."""
    mh, mw = mv.shape[:2]
    ix = np.clip((uvx * mw).astype(np.int32), 0, mw - 1)
    iy = np.clip((uvy * mh).astype(np.int32), 0, mh - 1)
    return mv[iy, ix]


def accum(cur, hist, mv, pan_uv, mode, reset, src_w, src_h):
    """One CSSsrtAccum frame. cur/hist are dicts of gi (5), depth (4), refl (4), shadow (H,W)."""
    T = R.TEMPORAL
    gi, depth, refl, sh = cur['gi'], cur['depth'], cur['refl'], cur['shadow']
    lh, lw = gi.shape[:2]
    ys, xs = np.mgrid[0:lh, 0:lw].astype(np.float32)
    out_gi = gi.copy(); out_d = depth.copy(); out_r = refl.copy(); out_s = sh.copy()
    if hist is None or mode == 0 or reset:
        return dict(gi=out_gi, depth=out_d, refl=out_r, shadow=out_s)
    if mode == 1:
        uvx = (xs + 0.5) / lw; uvy = (ys + 0.5) / lh
        m = _sample_mv(mv, uvx, uvy)
        mv_px = m[..., :2]
        conf = m[..., 2] if m.shape[-1] > 2 else np.ones((lh, lw), np.float32)
    else:
        mv_px = np.zeros((lh, lw, 2), np.float32)
        mv_px[..., 0] = float(pan_uv[0]) * src_w
        mv_px[..., 1] = float(pan_uv[1]) * src_h
        conf = np.ones((lh, lw), np.float32)
    offset_x = mv_px[..., 0] * lw / max(src_w, 1)
    offset_y = mv_px[..., 1] * lh / max(src_h, 1)
    hpx = xs + offset_x; hpy = ys + offset_y
    hgi, ok_g = _bilinear(hist['gi'], hpx, hpy)
    hd, ok_d = _bilinear(hist['depth'], hpx, hpy)
    hr, ok_r = _bilinear(hist['refl'], hpx, hpy)
    hs, ok_s = _bilinear(hist['shadow'], hpx, hpy)
    inside = ok_g & ok_d & ok_r & ok_s & (conf > 0.02)
    # Neighbourhood clamp in the same space as the shader (GI in linear light).
    clin = gi[..., :3] ** 2
    hlin = hgi[..., :3] ** 2
    cmin, cmax = _box_minmax(clin)
    omin, omax = _box_minmax(gi[..., 4])
    dmin, dmax = _box_minmax(depth[..., 0])
    rmin, rmax = _box_minmax(refl[..., :3])
    smin, smax = _box_minmax(sh)
    b = float(T['clipBox'])
    lo = clin + (cmin - clin) * b; hi = clin + (cmax - clin) * b
    hlin = np.clip(hlin, np.minimum(lo, hi), np.maximum(lo, hi))
    olo = gi[..., 4] + (omin - gi[..., 4]) * b; ohi = gi[..., 4] + (omax - gi[..., 4]) * b
    hocc = np.clip(hgi[..., 4], np.minimum(olo, ohi), np.maximum(olo, ohi))
    dlo = depth[..., 0] + (dmin - depth[..., 0]) * b; dhi = depth[..., 0] + (dmax - depth[..., 0]) * b
    hdepth = np.clip(hd[..., 0], np.minimum(dlo, dhi), np.maximum(dlo, dhi))
    rlo = refl[..., :3] + (rmin - refl[..., :3]) * b; rhi = refl[..., :3] + (rmax - refl[..., :3]) * b
    hrgb = np.clip(hr[..., :3], np.minimum(rlo, rhi), np.maximum(rlo, rhi))
    slo = sh + (smin - sh) * b; shi = sh + (smax - sh) * b
    hsh = np.clip(hs, np.minimum(slo, shi), np.maximum(slo, shi))
    keep = (1.0 - float(T['alpha'])) * conf
    keep = np.where(np.abs(hdepth - depth[..., 0]) > float(T['depthReject']), 0.0, keep)
    if mode == 1:
        huvx = np.clip((hpx + 0.5) / lw, 0, 1); huvy = np.clip((hpy + 0.5) / lh, 0, 1)
        mv_h = _sample_mv(mv, huvx, huvy)[..., :2]
        disagree = np.sqrt(np.sum((mv_px - mv_h) ** 2, -1))
        keep = np.where(disagree > float(T['mvRejectPx']), 0.0, keep)
    keep = np.where(inside, keep, 0.0).astype(np.float32)
    k = keep[..., None]
    out_lin = clin * (1 - k) + hlin * k
    out_rgb = np.sqrt(np.clip(out_lin, 0, 1))
    out_occ = gi[..., 4] * (1 - keep) + hocc * keep
    out_gi = np.concatenate([out_rgb, gi[..., 3:4], out_occ[..., None]], -1)
    out_gi = R_quant(out_gi)
    out_d = depth.copy()
    out_d[..., 0] = np.clip(depth[..., 0] * (1 - keep) + hdepth * keep, 0, 1)
    out_r = refl * (1 - k) + np.concatenate([hrgb, hr[..., 3:4]], -1) * k
    out_r = R.q8(np.clip(out_r, 0, 1))
    out_s = np.clip(sh * (1 - keep) + hsh * keep, 0, 1).astype(np.float32)
    return dict(gi=out_gi.astype(np.float32), depth=out_d.astype(np.float32),
                refl=out_r.astype(np.float32), shadow=out_s)


def R_quant(x):
    # Local alias so a missing harness quant still matches the 11/11/10 pack.
    return ssrt_quant(x)


def ssrt_quant(x):
    qs = np.array([2047, 2047, 1023, 255, 255], np.float32)
    return (np.floor(np.clip(x, 0, 1) * qs + 0.5) / qs).astype(np.float32)


def _canvas(rng, W_, H_):
    yy, xx = np.mgrid[0:H_, 0:W_].astype(np.float32)
    img = np.zeros((H_, W_, 3), np.float32)
    img[..., 0] = 0.28 + 0.06 * np.sin(xx * 0.04 + rng.random())
    img[..., 1] = 0.30 + 0.05 * np.cos(yy * 0.03)
    img[..., 2] = 0.26 + 0.04 * np.sin((xx + yy) * 0.02)
    floor = yy > 0.62 * H_
    img[floor] = np.array([0.22, 0.18, 0.14], np.float32)
    win = (xx > 0.08 * W_) & (xx < 0.26 * W_) & (yy > 0.08 * H_) & (yy < 0.42 * H_)
    img[win] = np.array([0.92, 0.95, 1.0], np.float32)
    mull = win & ((np.abs((xx - 0.17 * W_)) < 3) | (np.abs(yy - 0.25 * H_) < 3))
    img[mull] = 0.05
    panel = (xx > 0.55 * W_) & (xx < 0.78 * W_) & (yy > 0.18 * H_) & (yy < 0.55 * H_)
    img[panel] = np.array([0.55, 0.16, 0.12], np.float32)
    nse = rng.random((H_, W_, 3), np.float32) * 0.03
    return _clip01(img + nse).astype(np.float32)


def _paint(img, x, y, w, h, color):
    out = img.copy()
    m = np.zeros(img.shape[:2], bool)
    x0 = int(np.clip(x, 0, img.shape[1] - 1)); y0 = int(np.clip(y, 0, img.shape[0] - 1))
    x1 = int(np.clip(x0 + w, 0, img.shape[1])); y1 = int(np.clip(y0 + h, 0, img.shape[0]))
    if x1 <= x0 or y1 <= y0:
        return out, m
    out[y0:y1, x0:x1] = color
    out[y0:y1, x0:min(x0 + 3, x1)] = 0.02
    m[y0:y1, x0:x1] = True
    return out, m


def build_scenes(rng):
    """Seven sequences. back is the backward pan in source pixels (content moved by -back)."""
    n, W_, H_ = FRAMES, W, H
    big = _canvas(rng, W_ + 220, H_ + 80)
    alt = _clip01(big[:, ::-1] * np.array([0.35, 0.40, 0.55], np.float32) + 0.02)
    x0, y0 = 160, 40

    def pans(px, py, image, mode, ox=None, oy=None):
        frames, backs, mvs, pans, objs = [], [], [], [], []
        sx = x0 if ox is None else ox
        sy = y0 if oy is None else oy
        for t in range(n):
            # Keep the window on the canvas. A 28 px whip over 8 frames needs a later origin.
            frames.append(np.ascontiguousarray(image[sy - py * t:sy - py * t + H_, sx - px * t:sx - px * t + W_]))
            if t == 0:
                bx = by = 0.0
            else:
                bx, by = float(-px), float(-py)
            backs.append((bx, by))
            mv = np.zeros((H_, W_, 3), np.float32)
            mv[..., 0] = bx; mv[..., 1] = by; mv[..., 2] = 1.0
            mvs.append(mv)
            pans.append(np.array([bx / W_, by / H_], np.float32))
            objs.append(None)
        return dict(frames=frames, back=backs, mv=mvs, pan=pans, obj=objs, mode=mode, cut=None)

    slow = pans(5, 0, big, 2)          # global pan only
    slow['name'] = 'slow_pan'
    fast = pans(20, 0, big, 1)
    fast['name'] = 'fast_pan'
    diag = pans(10, 4, big, 1)
    diag['name'] = 'diagonal_pan'
    cut_at = n // 2
    cut = pans(5, 0, big, 1)
    # Rebuild the cut sequence on two pictures.
    frames, backs = [], []
    for t in range(n):
        src = big if t < cut_at else alt
        tt = t if t < cut_at else t - cut_at
        frames.append(np.ascontiguousarray(src[y0:y0 + H_, x0 - 5 * tt:x0 - 5 * tt + W_]))
        if t == 0 or t == cut_at:
            backs.append((0.0, 0.0))
        else:
            backs.append((-5.0, 0.0))
    mvs, pans_cut, objs = [], [], []
    for t in range(n):
        bx, by = backs[t]
        mv = np.zeros((H_, W_, 3), np.float32)
        mv[..., 0] = bx; mv[..., 1] = by; mv[..., 2] = 1.0
        mvs.append(mv); pans_cut.append(np.array([bx / W_, by / H_], np.float32)); objs.append(None)
    cut = dict(name='scene_cut', frames=frames, back=backs, mv=mvs, pan=pans_cut, obj=objs, mode=1, cut=cut_at)

    base = _canvas(rng, W_, H_)
    # Moving object, static camera.
    ow, oh, oy, speed = 48, 64, H_ // 2, 8
    frames, mvs, objs, backs, pans_still = [], [], [], [], []
    for t in range(n):
        x = 140 + t * speed
        img, m = _paint(base, x, oy, ow, oh, np.array([0.95, 0.72, 0.20], np.float32))
        mv = np.zeros((H_, W_, 3), np.float32); mv[..., 2] = 1.0
        if t > 0:
            mv[m, 0] = float(-speed)
        frames.append(img); mvs.append(mv); objs.append(m)
        backs.append((0.0, 0.0)); pans_still.append(np.zeros(2, np.float32))
    moving = dict(name='moving_object', frames=frames, back=backs, mv=mvs, pan=pans_still, obj=objs, mode=1, cut=None)

    # Disocclusion: the same sprite, mask of pixels it uncovers is built while scoring.
    dis = dict(name='disocclusion', frames=frames, back=backs, mv=mvs, pan=pans_still,
               obj=[o.copy() for o in objs], mode=1, cut=None, reveal=True)

    # Rotating / flickering source over a static picture. Motion follows the blob.
    frames, mvs, objs = [], [], []
    for t in range(n):
        ang = t * 0.7 + float(rng.random()) * 0.0
        cx = int(W_ * 0.45 + np.cos(ang) * 36)
        cy = int(H_ * 0.30 + np.sin(ang) * 24)
        flick = 0.75 + 0.25 * (0.5 + 0.5 * np.sin(t * 2.3 + float(rng.random()) * 0))
        # High-frequency lamp noise from the seed, on top of the orbit.
        flick *= 0.85 + 0.3 * float(rng.random())
        img, m = _paint(base, cx, cy, 28, 28, np.array([1.0, 0.95, 0.7], np.float32) * flick)
        mv = np.zeros((H_, W_, 3), np.float32); mv[..., 2] = 1.0
        frames.append(_clip01(img)); mvs.append(mv); objs.append(m)
    light = dict(name='flicker_light', frames=frames, back=[(0.0, 0.0)] * n, mv=mvs,
                 pan=[np.zeros(2, np.float32)] * n, obj=objs, mode=1, cut=None)
    # Give the lamp a backward vector from its centre motion so disagreement can reject its edge.
    for t in range(1, n):
        # centre shift
        prev = np.argwhere(light['obj'][t - 1])
        cur = np.argwhere(light['obj'][t])
        if prev.size and cur.size:
            dy = float(cur[:, 0].mean() - prev[:, 0].mean())
            dx = float(cur[:, 1].mean() - prev[:, 1].mean())
            light['mv'][t][light['obj'][t], 0] = -dx
            light['mv'][t][light['obj'][t], 1] = -dy
    whip = pans(28, 2, big, 1, ox=200)
    whip['name'] = 'whip_pan'
    frames, mvs, objs, backs, pans_l = [], [], [], [], []
    cw, ch, cy, speed = 36, 92, H_ // 3, 14
    for t in range(n):
        x = 70 + t * speed
        img, m = _paint(base, x, cy, cw, ch, np.array([0.045, 0.040, 0.035], np.float32))
        img, mh = _paint(img, x + 6, cy, cw - 12, 16, np.array([0.09, 0.07, 0.06], np.float32))
        m = m | mh
        mv = np.zeros((H_, W_, 3), np.float32)
        mv[..., 2] = 1.0
        if t > 0:
            mv[m, 0] = float(-speed)
        frames.append(img)
        mvs.append(mv)
        objs.append(m)
        backs.append((0.0, 0.0))
        pans_l.append(np.zeros(2, np.float32))
    character = dict(name='character_move', frames=frames, back=backs, mv=mvs, pan=pans_l,
                     obj=objs, mode=1, cut=None)
    night_src = _canvas(rng, W_ + 220, H_ + 80)
    night_src = _clip01(night_src * 0.16 + 0.015)
    night_src[36:48, 48:62] = (1.0, 0.82, 0.40)
    night_src[70:80, 200:214] = (0.45, 0.70, 1.0)
    night_src[24:34, 120:132] = (1.0, 0.40, 0.18)
    night = pans(16, 0, night_src, 1)
    night['name'] = 'night_pan'
    return [slow, fast, diag, cut, moving, dis, light, whip, character, night]


def _shade(h, img, preset, frame, lw, lh, bits_cache=None):
    grid = h.phase1(img)
    bits, _, _ = h.phase2(grid, lx=5, ly=6)
    raw_d, raw_l, protect = R.cs_depth(h, img, lw, lh)
    depth, nx, ny, smooth = R.cs_depth_blur(h, raw_d, raw_l, protect)
    light, _ = R.march_depth(h, grid, bits, depth, img.shape[1], img.shape[0], lw, lh,
                             frame=frame, use_gi=True, use_ao=True, preset=preset, protect=protect)
    light = h.filter3(light, rangeK=R.CUE['rangeK'])
    refl = R._reflect_from_buffers(h, img, depth, nx, ny, smooth, preset)
    ldir = R.cs_light_dir(grid, h.G)
    shadow = R.cs_contact(h, grid, depth, ldir, img.shape[1], img.shape[0], lw, lh, preset, protect)
    depth4 = np.stack([depth, nx, ny, smooth], -1).astype(np.float32)
    mean = float(((grid >> 24).astype(np.float32) * (1.0 / 255.0)).mean())
    return dict(gi=light, depth=depth4, refl=refl.astype(np.float32), shadow=shadow.astype(np.float32),
                mean=mean, grid=grid, bits=bits)


def _converged_gi(h, img, preset, lw, lh, bits, depth):
    acc = None
    for fi in (0, 2, 4, 6):
        light, _ = R.march_depth(h, None if False else h.phase1(img), bits, depth, img.shape[1], img.shape[0],
                                 lw, lh, frame=fi, use_gi=True, use_ao=True, preset=preset)
        # phase1 twice is wasteful; caller passes grid via bits already computed. Recompute grid once outside.
        q = light[..., :3] ** 2
        acc = q if acc is None else acc + q
    acc = np.sqrt(np.clip(acc / 4.0, 0, 1))
    # guide / occ from frame 0 is close enough for the bilateral; use the mean occ of that pass by re-filtering frame 0.
    light0, _ = R.march_depth(h, h.phase1(img), bits, depth, img.shape[1], img.shape[0], lw, lh,
                              frame=0, use_gi=True, use_ao=True, preset=preset)
    packed = np.concatenate([acc, light0[..., 3:4], light0[..., 4:5]], -1)
    return h.filter3(ssrt_quant(packed), rangeK=R.CUE['rangeK'])


def _composite(h, img, sig):
    packed = R.cs_pack(sig['refl'], sig['shadow'], True, True)
    final, _ = R.composite_ssrt(h, img, sig['gi'], packed, R.CUE['reflStrength'], R.CUE['contactStrength'])
    return final


def _play(h, scene, preset, lw, lh):
    """Per-frame composites: no temporal, temporal, converged (4 march rotations)."""
    T = R.TEMPORAL
    hist = None
    prev_mean = 0.0
    cool = 0.0
    have = False
    late_mv = None
    late_pan = None
    raw_c, tmp_c, ref_c = [], [], []
    for t, img in enumerate(scene['frames']):
        cur = _shade(h, img, preset, t, lw, lh)
        mode_now = 0 if late_mv is None else scene['mode']
        jump = abs(cur['mean'] - prev_mean)
        if not have or mode_now == 0:
            reset, cool = True, 0.0
        elif jump > float(T['lumCut']):
            reset, cool = True, 1.0
        elif cool > 0.5:
            reset, cool = True, 0.0
        else:
            reset = False
        if reset or mode_now == 0:
            blended = cur
        elif mode_now == 1:
            blended = accum(cur, hist, late_mv, None, 1, False, img.shape[1], img.shape[0])
        else:
            blended = accum(cur, hist, None, late_pan, 2, False, img.shape[1], img.shape[0])
        hist = blended
        have = mode_now != 0
        prev_mean = cur['mean']
        late_mv = scene['mv'][t]
        late_pan = scene['pan'][t]
        raw_c.append(_composite(h, img, cur))
        tmp_c.append(_composite(h, img, blended))
        grid, bits = cur['grid'], cur['bits']
        depth = cur['depth'][..., 0]
        acc = None
        for fi in (0, 2, 4, 6):
            light, _ = R.march_depth(h, grid, bits, depth, img.shape[1], img.shape[0], lw, lh,
                                     frame=fi, use_gi=True, use_ao=True, preset=preset)
            q = light[..., :3] ** 2
            acc = q if acc is None else acc + q
        acc = np.sqrt(np.clip(acc / 4.0, 0, 1))
        packed = np.concatenate([acc, cur['gi'][..., 3:4], cur['gi'][..., 4:5]], -1)
        ref_gi = h.filter3(ssrt_quant(packed), rangeK=R.CUE['rangeK'])
        ref_c.append(_composite(h, img, dict(gi=ref_gi, depth=cur['depth'], refl=cur['refl'], shadow=cur['shadow'])))
        print(f'    frame {t + 1}/{len(scene["frames"])}', flush=True)
    return raw_c, tmp_c, ref_c


def _metrics(scene, raw_c, tmp_c, ref_c):
    n = len(raw_c)
    flick_r = flick_t = 0.0
    flick_nr = flick_nt = 0
    acc_t = acc_n = 0.0
    ghost_r, ghost_t = [], []
    cut = scene['cut']
    for t in range(n):
        H_, W_ = raw_c[t].shape[:2]
        border = np.zeros((H_, W_), bool)
        border[:8] = border[-8:] = border[:, :8] = border[:, -8:] = True
        if t == 0:
            valid = np.ones((H_, W_), bool)
        else:
            bx, by = scene['back'][t]
            _, valid = _shift(raw_c[t - 1], int(round(bx)), int(round(by)))
        obj = scene['obj'][t]
        edge = np.zeros((H_, W_), bool)
        reveal = np.zeros((H_, W_), bool)
        if obj is not None:
            edge = _dilate(obj, 6)
            if t > 0 and scene['obj'][t - 1] is not None:
                edge |= _dilate(scene['obj'][t - 1], 6)
                reveal = scene['obj'][t - 1] & ~obj
                reveal = _dilate(reveal, 3)
        if scene.get('reveal'):
            ghost_m = reveal | edge
        elif obj is not None:
            ghost_m = edge
        else:
            ghost_m = ~valid
        cut_m = np.zeros((H_, W_), bool)
        if cut is not None and t in (cut, cut + 1):
            cut_m[:] = True
        stable = valid & ~edge & ~reveal & ~cut_m & ~border
        if t > 0 and not cut_m.any():
            bx, by = scene['back'][t]
            wr, vr = _shift(raw_c[t - 1], int(round(bx)), int(round(by)))
            wt, vt = _shift(tmp_c[t - 1], int(round(bx)), int(round(by)))
            m = stable & vr & vt
            if m.any():
                flick_r += float(np.abs(wr[m] - raw_c[t][m]).mean())
                flick_t += float(np.abs(wt[m] - tmp_c[t][m]).mean())
                flick_nr += 1
                flick_nt += 1
        if stable.any() and not (cut is not None and t in (cut, cut + 1)):
            acc_t += float(np.abs(tmp_c[t][stable] - ref_c[t][stable]).mean())
            acc_n += 1
        if ghost_m.any() and not cut_m.any():
            ghost_r.append(np.abs(raw_c[t] - ref_c[t]).mean(-1)[ghost_m])
            ghost_t.append(np.abs(tmp_c[t] - ref_c[t]).mean(-1)[ghost_m])
    def _p99(chunks):
        if not chunks:
            return 0.0
        a = np.concatenate([c.astype(np.float32).ravel() for c in chunks])
        return float(np.percentile(a, 99))
    pr, pt = _p99(ghost_r), _p99(ghost_t)
    ratio = pt / max(pr, 1.0 / 255.0)
    trail = 0.0
    if cut is not None:
        acc = []
        for t in (cut, cut + 1):
            if 0 <= t < n:
                acc.append(float(np.abs(tmp_c[t] - raw_c[t]).mean()))
        trail = float(np.mean(acc)) if acc else 0.0
    flick_raw = flick_r / flick_nr if flick_nr else 0.0
    flick_tmp = flick_t / flick_nt if flick_nt else 0.0
    return dict(flicker_raw=flick_raw, flicker_temporal=flick_tmp,
                accuracy=acc_t / acc_n if acc_n else 0.0,
                ghost_p99_raw=pr, ghost_p99_temporal=pt, ghost_ratio=ratio, trail=trail)


def _save(path, img):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8)).save(path)


def run(h, preview_root='/workspace/ssrt_preview', build_dir=None, seeds=SEEDS):
    here = os.path.dirname(os.path.abspath(__file__))
    if build_dir is None:
        build_dir = os.path.normpath(os.path.join(here, '..', '..', 'build', 'ssrt'))
    os.makedirs(build_dir, exist_ok=True)
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M')
    out = os.path.join(preview_root, stamp + '-temporal')
    os.makedirs(out, exist_ok=True)
    preset = R.SSRT_PRESETS['GTX1050Ti_1080p']
    lw, lh = R.light_size(W, H, preset['div'], h.G)
    costs = R.cost_model()
    rows = []
    preview_done = False
    # Prove one seed is deterministic before the rest of the table.
    first = int(seeds[0])
    print(f'=== SSRT temporal, seed {first} twice (determinism) ===', flush=True)
    a = _one_seed(h, first, preset, lw, lh, preview=None)
    b = _one_seed(h, first, preset, lw, lh, preview=None)
    same = a['signature'] == b['signature']
    print(f'deterministic: {same}  signature {a["signature"]}', flush=True)
    if not same:
        raise SystemExit('ssrt-temporal is not deterministic for seed %d' % first)
    rows.append(a['row'])
    _write_preview(h, first, preset, lw, lh, out)
    preview_done = True
    for seed in list(seeds)[1:]:
        print(f'=== SSRT temporal seed {seed} ===', flush=True)
        rows.append(_one_seed(h, int(seed), preset, lw, lh, preview=None)['row'])
    all_pass = all(r['pass'] for r in rows)
    decision = 'on' if all_pass else 'off'
    doc = dict(
        decision=decision,
        temporal_default=all_pass,
        criteria=dict(flicker_reduction_min=0.30, ghost_p99_ratio_max=1.10, trail_max_over_255=1.5),
        constants=R.TEMPORAL,
        presets_ms={k: costs[k]['total_ms'] for k in costs},
        within_budget={k: costs[k]['within_budget'] for k in costs},
        seeds=rows,
        preview=out,
        note='TemporalDefault is true only when every seed passes.',
    )
    path = os.path.join(build_dir, 'temporal_seeds.json')
    with open(path, 'w') as f:
        json.dump(doc, f, indent=2)
    _print_table(rows, decision, costs, path, out)
    return doc


def _one_seed(h, seed, preset, lw, lh, preview):
    rng = np.random.Generator(np.random.PCG64(seed))
    scenes = build_scenes(rng)
    per = []
    sig_parts = []
    for sc in scenes:
        print(f'  scene {sc["name"]}', flush=True)
        raw_c, tmp_c, ref_c = _play(h, sc, preset, lw, lh)
        m = _metrics(sc, raw_c, tmp_c, ref_c)
        m['name'] = sc['name']
        per.append(m)
        sig_parts.append(round(m['flicker_temporal'], 6))
        sig_parts.append(round(m['ghost_ratio'], 6))
        sig_parts.append(round(m['trail'], 6))
    # Pixel-weighted flicker: scenes that reported a flicker (not only the cut).
    fr = np.array([p['flicker_raw'] for p in per if p['flicker_raw'] > 0], np.float64)
    ft = np.array([p['flicker_temporal'] for p in per if p['flicker_raw'] > 0], np.float64)
    flick_raw = float(fr.mean()) if fr.size else 0.0
    flick_tmp = float(ft.mean()) if ft.size else 0.0
    reduction = 1.0 - flick_tmp / flick_raw if flick_raw > 1e-8 else 0.0
    ghost_names = ('moving_object', 'disocclusion', 'fast_pan', 'diagonal_pan', 'slow_pan',
                   'whip_pan', 'character_move', 'night_pan')
    ghost_scenes = [p for p in per if p['name'] in ghost_names]
    worst = max((p['ghost_ratio'] for p in ghost_scenes), default=1.0)
    trail = max((p['trail'] for p in per), default=0.0)
    acc = float(np.mean([p['accuracy'] for p in per]))
    ok = reduction >= 0.30 and worst <= 1.10 and trail * 255.0 <= 1.5
    row = dict(seed=seed, flicker_before=flick_raw, flicker_after=flick_tmp, reduction=reduction,
               accuracy=acc, ghost_p99_ratio=worst, trail=trail, trail_over_255=trail * 255.0,
               pass_=ok, scenes=[{k: p[k] for k in p} for p in per])
    # JSON key pass not pass_
    row['pass'] = row.pop('pass_')
    return dict(row=row, signature=sig_parts)


def _write_preview(h, seed, preset, lw, lh, out):
    rng = np.random.Generator(np.random.PCG64(seed))
    scenes = {sc['name']: sc for sc in build_scenes(rng)}
    # Static flicker: slow pan, a middle frame, raw vs temporal.
    sc = scenes['slow_pan']
    raw_c, tmp_c, _ = _play(h, sc, preset, lw, lh)
    t = len(raw_c) // 2
    _save(os.path.join(out, 'flicker_static_before.png'), raw_c[t])
    _save(os.path.join(out, 'flicker_static_after.png'), tmp_c[t])
    sc = scenes['moving_object']
    raw_c, tmp_c, ref_c = _play(h, sc, preset, lw, lh)
    t = len(raw_c) // 2
    obj = sc['obj'][t]
    ys, xs = np.where(obj)
    if ys.size:
        y0, y1 = max(0, ys.min() - 16), min(H, ys.max() + 16)
        x0, x1 = max(0, xs.min() - 16), min(W, xs.max() + 16)
    else:
        y0, y1, x0, x1 = 0, H, 0, W
    _save(os.path.join(out, 'ghost_moving_raw.png'), raw_c[t][y0:y1, x0:x1])
    _save(os.path.join(out, 'ghost_moving_temporal.png'), tmp_c[t][y0:y1, x0:x1])
    _save(os.path.join(out, 'ghost_moving_reference.png'), ref_c[t][y0:y1, x0:x1])
    sc = scenes['scene_cut']
    raw_c, tmp_c, _ = _play(h, sc, preset, lw, lh)
    ct = sc['cut']
    _save(os.path.join(out, 'cut_before.png'), raw_c[ct])
    _save(os.path.join(out, 'cut_temporal.png'), tmp_c[ct])
    _save(os.path.join(out, 'cut_side_by_side.png'), np.concatenate([raw_c[ct], tmp_c[ct]], 1))


def _print_table(rows, decision, costs, path, preview):
    print('\n=== SSRT temporal seeds ===', flush=True)
    print(f'{"seed":>6} {"flick%":>8} {"ghost":>8} {"trail":>8} {"acc":>8} {"pass":>6}')
    for r in rows:
        print(f'{r["seed"]:6d} {r["reduction"] * 100:8.1f} {r["ghost_p99_ratio"]:8.3f} '
              f'{r["trail_over_255"]:8.3f} {r["accuracy"] * 255:8.3f} {"yes" if r["pass"] else "NO":>6}')
    print(f'DECISION: temporal {"ON" if decision == "on" else "OFF"} by default')
    for name, c in costs.items():
        print(f'{name}: {c["total_ms"]:.3f} ms  {"OK" if c["within_budget"] else "OVER"}')
    print('json:', path)
    print('previews:', preview, flush=True)


if __name__ == '__main__':
    import ssgi_harness as h
    run(h)
