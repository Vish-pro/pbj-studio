"""
PDF Signer splash v6 generator (story: fill -> comment -> pages -> two sets -> join -> jurat seal -> multi-sign -> logo).

Recreates the DBC splash motion language (E:\\DBC\\packaging\\brand\\DBC Brand Story.html):
thin outline icons whose contours morph shape-to-shape with ease-in-out / ease-out /
back easings, a gentle float that settles before the logo forms, then the contours
land exactly on shapes traced from the real logo, the glossy mark fades in over them
(ease-out, 9.35-9.75 s), the wordmark wipes in left to right (ease-in-out, 9.3-10 s)
and the outline layer fades away (9.7-9.95 s). White background.

Story (10 s):
  0.0-1.5  Need      a blank page with an empty signature line
  1.5-3.2  Paper     the page fans out into a set of pages
  3.2-5.2  Jurat     a DSC token rises, gold lines run to every page, each page is signed
  5.2-7.3  Binder    the pages bind into one, the token becomes the seal ring,
                     the gold line becomes the signature
  7.3-8.3  Seal      the ring turns to wax, the quill arrives, two drops fall
  8.3-10   Logo      every contour lands on the logo, the mark and wordmark appear

Usage (no install needed; uses Pillow, numpy, OpenCV and imageio_ffmpeg already on this PC):
  python -I splash_v5.py                 -> brand\\splash-v5.mp4 + brand\\splash-v5.zip
  python -I splash_v5.py --frames DIR    -> also dump verification PNGs (t = 0,2,4,6,8,9,9.5,10)
Outputs:
  splash-v5.mp4  1536x1024, 50 fps, 10 s animation + 2 s hold on the finished logo
  splash-v5.zip  251 JPEG frames (frame0000.jpg..), 1080x720, 25 fps, for the app splash
                 window (same format as splash-v4.zip / DBC splash.zip; app adds hold + fade)
"""
import argparse, io, math, os, subprocess, sys, zipfile
import numpy as np
import cv2
from PIL import Image
import imageio_ffmpeg

BRAND = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
LOCKUP = os.path.join(BRAND, 'logo-source', 'logo-lockup-v2.png')

DW, DH = 1080.0, 720.0          # design space (3:2, same as the lockup)
N = 200                         # points per contour
DUR = 15.0
HOLD = 2.0

RED = (163, 32, 42)
REDFILL = (128, 20, 24)
GOLD = (196, 150, 70)
INK = (43, 43, 43)
CREAM = (246, 228, 196)
WHITE = (255, 255, 255)

# ----------------------------------------------------------------- easing
clamp = lambda x, a=0.0, b=1.0: min(b, max(a, x))
seg = lambda t, a, b: clamp((t - a) / (b - a))
eio = lambda p: 2 * p * p if p < .5 else 1 - (-2 * p + 2) ** 2 / 2
eo = lambda p: 1 - (1 - p) ** 3
def eb(p):
    c1 = 1.70158; c3 = c1 + 1
    return 1 + c3 * (p - 1) ** 3 + c1 * (p - 1) ** 2
EASE = {'io': eio, 'o': eo, 'b': eb, 'lin': lambda p: p}

# ----------------------------------------------------------------- geometry
def resample(pts, n=N):
    pts = np.asarray(pts, float)
    closed = np.vstack([pts, pts[:1]])
    d = np.sqrt((np.diff(closed, axis=0) ** 2).sum(1))
    L = np.concatenate([[0], np.cumsum(d)])
    if L[-1] < 1e-9:
        return np.repeat(pts[:1], n, 0)
    s = np.linspace(0, L[-1], n, endpoint=False)
    x = np.interp(s, L, closed[:, 0]); y = np.interp(s, L, closed[:, 1])
    out = np.stack([x, y], 1)
    # consistent orientation (positive shoelace in image coords) and start at the top-left-most point
    area = 0.5 * np.sum(out[:, 0] * np.roll(out[:, 1], -1) - np.roll(out[:, 0], -1) * out[:, 1])
    if area < 0:
        out = out[::-1]
    k = np.argmin(out[:, 1] * 1000 + out[:, 0])
    return np.roll(out, -k, 0)

def align(prev, cur):
    """Cyclic shift of cur that best matches prev (shape-normalised), so morphs don't twist."""
    def norm(p):
        q = p - p.mean(0); s = np.sqrt((q ** 2).sum(1).mean()) or 1.0
        return q / s
    a, b = norm(prev), norm(cur)
    best, bk = None, 0
    for k in range(0, len(cur), 2):
        d = ((a - np.roll(b, -k, 0)) ** 2).sum()
        if best is None or d < best:
            best, bk = d, k
    return np.roll(cur, -bk, 0)

def rounded(poly, r):
    poly = np.asarray(poly, float); out = []
    m = len(poly)
    for i in range(m):
        p0, p, p1 = poly[i - 1], poly[i], poly[(i + 1) % m]
        v0 = p0 - p; v1 = p1 - p
        l0 = np.linalg.norm(v0); l1 = np.linalg.norm(v1)
        rr = min(r, l0 / 2, l1 / 2)
        a = p + v0 / l0 * rr; b = p + v1 / l1 * rr
        for u in np.linspace(0, 1, 8):
            out.append((1 - u) ** 2 * a + 2 * (1 - u) * u * p + u * u * b)
    return np.array(out)

def page(cx, cy, w=120, h=156, fold=26):
    x0, y0, x1, y1 = cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2
    return resample(rounded([(x0, y0), (x1 - fold, y0), (x1, y0 + fold), (x1, y1), (x0, y1)], 9))

def bar(x0, y0, x1, y1):
    """An open stroke as an out-and-back closed contour."""
    s = np.linspace(0, 1, N // 2)
    fwd = np.stack([x0 + (x1 - x0) * s, y0 + (y1 - y0) * s], 1)
    return resample(np.vstack([fwd, fwd[::-1][1:-1]]))

def path(pts):
    pts = np.asarray(pts, float)
    return resample(np.vstack([pts, pts[::-1][1:-1]]))

def circle(cx, cy, r, wav=0.0, k=9, ph=0.0):
    th = np.linspace(-math.pi / 2, 1.5 * math.pi, 400, endpoint=False)
    rr = r * (1 + wav * np.sin(k * th + ph) + wav * 0.5 * np.sin(2 * k * th + 1.3))
    return resample(np.stack([cx + rr * np.cos(th), cy + rr * np.sin(th)], 1))

def token(cx, cy, w=58, h=92, cw=32, ch=24):
    x0, x1, y0, y1 = cx - w / 2, cx + w / 2, cy - h / 2 + ch, cy + h / 2
    poly = [(cx - cw / 2, cy - h / 2), (cx + cw / 2, cy - h / 2), (cx + cw / 2, y0), (x1, y0),
            (x1, y1), (x0, y1), (x0, y0), (cx - cw / 2, y0)]
    return resample(rounded(poly, 7))

def tick(cx, cy, s=1.0):
    return path([(cx - 11 * s, cy), (cx - 3 * s, cy + 8 * s), (cx + 13 * s, cy - 10 * s)])

def squiggle(x0, y0, w):
    s = np.linspace(0, 1, 60)
    x = x0 + w * s
    y = y0 - 9 * np.sin(s * 2.6 * math.pi) * (1 - s * 0.4) - 4 * s
    return path(np.stack([x, y], 1))

def quill(tipx, tipy, ln=120, ang=-55):
    a = math.radians(ang)
    u = np.array([math.cos(a), math.sin(a)]); v = np.array([-u[1], u[0]])
    s = np.linspace(0, 1, 80)
    wdt = 22 * np.sin(np.clip((s - 0.15) / 0.85, 0, 1) * math.pi) ** 0.8
    base = np.array([tipx, tipy])
    left = base + np.outer(s * ln, u) + np.outer(wdt, v)
    right = base + np.outer(s * ln, u) - np.outer(wdt * 0.55, v)
    return resample(np.vstack([left, right[::-1]]))

def xf(p, dx=0.0, dy=0.0, s=1.0, about=None):
    c = p.mean(0) if about is None else np.array(about, float)
    return (p - c) * s + c + np.array([dx, dy])

def point(x, y):
    return np.tile([x, y], (N, 1)).astype(float)

# ----------------------------------------------------------------- tracing the real logo
def trace_logo():
    im = np.array(Image.open(LOCKUP).convert('RGBA'))
    r, g, b, a = [im[:, :, i].astype(int) for i in range(4)]
    k = DW / im.shape[1]

    def contours(mask, min_area):
        cs, _ = cv2.findContours(mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        return [c[:, 0, :].astype(float) for c in cs if cv2.contourArea(c) >= min_area]

    def smooth(c, w=5):
        if w <= 1: return c
        ker = np.ones(w) / w
        ext = np.vstack([c[-w:], c, c[:w]])
        return np.stack([np.convolve(ext[:, i], ker, 'same')[w:-w] for i in (0, 1)], 1)

    out = {}
    hsv = cv2.cvtColor(im[:, :, :3], cv2.COLOR_RGB2HSV)
    sealm = ((a > 100) & (hsv[:, :, 1] > 90) & ((hsv[:, :, 0] < 12) | (hsv[:, :, 0] > 165))).astype(np.uint8)
    sealm[600:] = 0
    sealm = cv2.morphologyEx(sealm, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)))
    cs = sorted(contours(sealm, 300), key=lambda c: -cv2.contourArea(c.astype(np.float32)))
    out['seal'] = resample(smooth(cs[0], 9) * k)
    drops = [c for c in cs[1:] if cv2.contourArea(c.astype(np.float32)) < 3000]
    drops = sorted(drops, key=lambda c: c[:, 1].mean())        # top-right drop first, then lower-left
    out['drops'] = [resample(smooth(c, 3) * k) for c in drops[:2]]

    cream = ((a > 230) & (r > 200) & (g > 160) & (b > 110) & (im[:, :, :3].min(2) < 250)).astype(np.uint8)
    box = np.zeros_like(cream); box[100:500, 500:990] = 1; cream *= box
    n, lab, st, cen = cv2.connectedComponentsWithStats(cream)
    comps = [i for i in range(1, n) if st[i, 4] > 300]
    ring_i = max(comps, key=lambda i: st[i, 2])
    rest = [i for i in comps if i != ring_i]
    doc_i = min([i for i in rest if st[i, 3] > 200], key=lambda i: st[i, 0])
    quill_i = max([i for i in rest if st[i, 3] > 150 and i != doc_i], key=lambda i: st[i, 0])
    small = [i for i in rest if i not in (doc_i, quill_i)]
    lines = sorted([i for i in small if st[i, 2] > 3 * st[i, 3]], key=lambda i: cen[i][1])
    squig = [i for i in small if i not in lines]

    def centreline(idx):
        # the outer edge of a thick outline, inset by half the stroke -> its centre line
        m = (lab == idx).astype(np.uint8)
        dist = cv2.distanceTransform(m, cv2.DIST_L2, 5)
        thick = 4 * dist[m > 0].mean()
        outer = resample(smooth(max(contours(m, 50), key=lambda c: cv2.contourArea(c.astype(np.float32))), 7), 400)
        tg = np.roll(outer, -1, 0) - np.roll(outer, 1, 0)
        nrm = np.stack([-tg[:, 1], tg[:, 0]], 1); nrm /= np.linalg.norm(nrm, axis=1, keepdims=True) + 1e-9
        cands = [outer + nrm * thick / 2, outer - nrm * thick / 2]
        area = lambda p: abs(np.sum(p[:, 0] * np.roll(p[:, 1], -1) - np.roll(p[:, 0], -1) * p[:, 1]))
        return resample(smooth(min(cands, key=area), 5) * k), thick * k

    out['doc'], out['doc_sw'] = centreline(doc_i)
    out['ring'], out['ring_sw'] = centreline(ring_i)
    out['quill'] = resample(smooth(contours(lab == quill_i, 50)[0], 3) * k)
    out['lines'] = [resample(contours(lab == i, 20)[0] * k) for i in lines]
    out['squig'] = resample(contours(lab == squig[0], 20)[0] * k) if squig else None
    # mark / wordmark split: a soft horizontal band between the seal and the letters, so the seal's
    # shadow fades out gently with the mark and the rest arrives with the wordmark wipe
    dark = (a > 150) & (r < 100) & (g < 100) & (b < 100)
    rows = np.where(dark.sum(1) > 20)[0]
    text_top = rows[rows > 590].min() if (rows > 590).any() else 610
    y = np.arange(im.shape[0], dtype=np.float32)
    u = np.clip((y - (text_top - 26)) / 22.0, 0, 1)
    out['text_mask'] = np.repeat((u * u * (3 - 2 * u))[:, None], im.shape[1], 1).astype(np.float32)
    out['seal_c'] = out['seal'].mean(0)
    return out

# ----------------------------------------------------------------- the timeline
class Track:
    def __init__(self, name, fl=None):
        self.name, self.fl, self.keys = name, fl, []
    def k(self, t, shape, e='io', **kw):
        prev = self.keys[-1] if self.keys else dict(sw=5.0, stroke=RED, so=1.0, fill=RED, fo=0.0, op=1.0)
        key = {kk: prev[kk] for kk in ('sw', 'stroke', 'so', 'fill', 'fo', 'op')}
        key.update(kw); key['t'] = t; key['e'] = e
        p = shape if shape is not None else prev['p']
        key['p'] = align(prev['p'], p) if self.keys else p
        self.keys.append(key)
        return self
    def hold(self, t, e='io', **kw):
        return self.k(t, None, e, **kw)

def rect(x, y, w, h, r=4):
    return resample(rounded([(x, y), (x + w, y), (x + w, y + h), (x, y + h)], r))

def bubble(cx, cy, w=128, h=64):
    x0, y0, x1, y1 = cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2
    poly = [(x0, y0), (x1, y0), (x1, y1), (x0 + 44, y1), (x0 + 16, y1 + 20), (x0 + 22, y1), (x0, y1)]
    return resample(rounded(poly, 9))

HAND = np.array([(-7, 0), (7, 0), (7, 32), (20, 32), (20, 40), (33, 40), (33, 48), (46, 48), (46, 76),
                 (38, 98), (-8, 98), (-22, 80), (-38, 56), (-30, 52), (-18, 62), (-7, 70)], float)

def hand(tx, ty, s=0.8):
    return resample(rounded(HAND * s + np.array([tx, ty]), 4))

def build():
    """Story: paper -> hand fills it -> comment -> more pages -> two sets -> join -> jurat seal
    -> multiple signs -> everything lands on the logo."""
    L = trace_logo()
    cx, cy = 540.0, 330.0
    HC = np.array([cx, cy])
    SC = L['seal_c']
    K1, K4, KL = 120 / 190, 150 / 190, 104 / 190
    ONE = (cx, cy)
    LGC = (SC[0] - 2, SC[1] + 6)
    T = []

    TX = lambda base, c, k: (base - HC) * k + np.asarray(c, float)
    pg = lambda c, k: page(c[0], c[1], 190 * k, 250 * k, 36 * k)
    stack = lambda j: (cx + (j - 2.5) * 9, cy + (j - 2.5) * 9)
    COL = [(cx, 158), (cx, 330), (cx, 502)]
    A = [(cx - 184, cy - 14), (cx - 170, cy), (cx - 156, cy + 14)]
    B = [(cx + 156, cy - 14), (cx + 170, cy), (cx + 184, cy + 14)]

    # ---- ring behind everything (jurat seal), then the two wax drops
    S = Track('seal', fl=2.2)
    S.k(0.0, circle(SC[0], SC[1], 150), op=0.0, stroke=RED, sw=5.0, fill=REDFILL, fo=0.0)
    S.hold(12.2, op=0.0)
    S.k(12.8, circle(SC[0], SC[1], 150), 'io', op=1.0)
    S.k(13.4, circle(SC[0], SC[1], 162, 0.045, 9, 0.4), 'io', fo=0.10)
    S.hold(13.7)
    S.k(14.2, L['seal'], 'io', sw=3.0, fo=1.0)
    T.append(S)
    for i, dshape in enumerate(L['drops']):
        D = Track(f'drop{i}')
        D.k(0.0, xf(dshape, 0, -40, 0.2), stroke=RED, fill=REDFILL, sw=2.0, fo=1.0, op=0.0)
        D.hold(13.1 + i * 0.15, op=0.0)
        D.k(13.6 + i * 0.15, dshape, 'b', op=1.0)
        T.append(D)

    RG = Track('ring')
    RG.k(0.0, circle(SC[0], SC[1], 150), sw=3.0, stroke=CREAM, fill=CREAM, fo=0.0, so=1.0, op=0.0)
    RG.hold(13.7, op=0.0)
    RG.k(14.2, L['ring'], 'io', sw=L['ring_sw'], op=1.0)
    T.append(RG)

    # ---- page timelines: (t, centre, scale, ease, opacity)
    def timeline(i):
        if i == 0:
            tl = [(0.0, (cx - 260, cy + 20), 0.9, 'io', 0.0), (0.8, ONE, 1.0, 'b', 1.0), (1.2, ONE, 1.0, 'io', 1.0),
                  (5.4, ONE, 1.0, 'io', 1.0), (6.0, COL[0], K1, 'io', 1.0), (7.2, COL[0], K1, 'io', 1.0)]
        elif i in (1, 2):
            t_in = 6.0 + (i - 1) * 0.4
            sp = (COL[i][0], COL[i][1] + 170)
            tl = [(0.0, sp, K1, 'io', 0.0), (t_in, sp, K1, 'io', 0.0), (t_in + 0.5, COL[i], K1, 'b', 1.0),
                  (7.2, COL[i], K1, 'io', 1.0)]
        else:
            j = i - 3
            sp = (cx + 470, B[j][1])
            tl = [(0.0, sp, K1, 'io', 0.0), (7.3 + 0.1 * j, sp, K1, 'io', 0.0), (7.9 + 0.1 * j, B[j], K1, 'b', 1.0)]
        if i < 3:
            tl += [(7.9, A[i], K1, 'io', 1.0), (8.9, A[i], K1, 'io', 1.0)]
        else:
            tl += [(8.9, B[i - 3], K1, 'io', 1.0)]
        tl += [(9.6, stack(i), K1, 'io', 1.0), (10.2, ONE, K4, 'io', 1.0), (12.2, ONE, K4, 'io', 1.0),
               (12.8, LGC, KL, 'io', 1.0 if i == 5 else 0.0)]
        return tl

    def follow(tr, base, tl, sw, vis=0.0, t_from=0.0, t_to=99.0, **kw0):
        for (t, c, k, e, op) in tl:
            if t < t_from or t > t_to:
                continue
            kw = dict(sw=max(1.4, sw * k), op=0.0 if t < vis else op)
            if not tr.keys:
                kw.update(kw0)
            tr.k(t, TX(base, c, k), e, **kw)

    INKS = dict(stroke=INK, fill=INK, fo=0.0, so=1.0)
    x0, y0 = cx - 95, cy - 125
    fx = x0 + 20
    fys = [y0 + 60, y0 + 100, y0 + 140]
    tys = [f + 12 for f in fys]
    cby = y0 + 182
    LB = [bar(cx - 70, cy - 62 + j * 24, cx - 70 + w, cy - 62 + j * 24) for j, w in enumerate((112, 88, 100, 76))]

    # ---- the hero page: paper comes in, the hand fills it, a comment lands
    tls = [timeline(i) for i in range(6)]
    pages = []
    for i in range(6):
        tr = Track(f'page{i}', fl=0.8 * i)
        for n, (t, c, k, e, op) in enumerate(tls[i]):
            kw = dict(op=op)
            if n == 0:
                kw.update(sw=4.0, stroke=RED, so=1.0, fill=WHITE, fo=1.0)
            tr.k(t, pg(c, k), e, **kw)
        pages.append(tr)
    R3 = pages[5]
    R3.hold(13.7)
    R3.k(14.2, L['doc'], 'io', sw=L['doc_sw'], stroke=CREAM, fo=0.0)

    # page 0 + its contents
    T.append(pages[0])
    def static(name, base, sw, fl, tl, vis, **kw0):
        tr = Track(name, fl=fl)
        follow(tr, base, tl, sw, vis=vis, **kw0)
        T.append(tr)
        return tr
    static('title', bar(x0 + 20, y0 + 26, x0 + 100, y0 + 26), 7.0, 0.0, tls[0], 1.2, **INKS)
    for j in range(3):
        static(f'field{j}', rect(fx, fys[j], 150, 24, 5), 2.0, 0.0, tls[0], 1.2, **INKS)
    static('check', rect(fx, cby, 20, 20, 3), 2.0, 0.0, tls[0], 1.2, **INKS)
    static('chklabel', bar(fx + 30, cby + 10, fx + 110, cby + 10), 3.5, 0.0, tls[0], 1.2, **INKS)

    # highlight (comment) over the first filled field
    hy = tys[0]
    HL = Track('hl', fl=0.0)
    HL.k(0.0, rect(fx + 8, hy - 9, 3, 18, 2), sw=1.0, so=0.0, stroke=GOLD, fill=GOLD, fo=0.4, op=0.0)
    HL.hold(3.7, op=0.0)
    HL.k(3.71, rect(fx + 8, hy - 9, 3, 18, 2), 'lin', op=1.0)
    HL.k(4.1, rect(fx + 8, hy - 9, 118, 18, 2), 'io')
    follow(HL, rect(fx + 8, hy - 9, 118, 18, 2), tls[0], 1.0, t_from=5.4)
    T.append(HL)

    # typed text bars
    for j, ln in enumerate((110, 80, 95)):
        a, b = 1.6 + j * 0.55, 2.0 + j * 0.55 - (0.05 if j == 2 else 0.0)
        a = [1.6, 2.16, 2.66][j]; b = [2.0, 2.5, 2.95][j]
        tb = Track(f'text{j}', fl=0.0)
        sx, sy = fx + 10, tys[j]
        tb.k(0.0, point(sx, sy), sw=4.0, stroke=INK, fill=INK, fo=0.0, so=1.0, op=0.0)
        tb.hold(a - 0.01, op=0.0)
        tb.k(a, point(sx, sy), 'lin', op=1.0)
        tb.k(b, bar(sx, sy, sx + ln, sy), 'lin')
        follow(tb, bar(sx, sy, sx + ln, sy), tls[0], 4.0, t_from=5.4)
        T.append(tb)

    # gold tick in the checkbox
    tkx, tky = fx + 10, cby + 10
    TK = Track('tick', fl=0.0)
    TK.k(0.0, xf(tick(tkx, tky), s=0.3), sw=4.0, stroke=GOLD, fill=GOLD, fo=0.0, so=1.0, op=0.0)
    TK.hold(3.1, op=0.0)
    TK.k(3.4, tick(tkx, tky), 'b', op=1.0)
    follow(TK, tick(tkx, tky), tls[0], 4.0, t_from=5.4)
    T.append(TK)

    # comment bubble
    bx, by = cx + 158, cy - 96
    BB = Track('bubble', fl=0.0)
    BB.k(0.0, point(bx, by), sw=4.0, stroke=GOLD, fill=WHITE, fo=1.0, so=1.0, op=0.0)
    BB.hold(4.1, op=0.0)
    BB.k(4.5, bubble(bx, by), 'b', op=1.0)
    BB.hold(5.0)
    BB.k(5.5, point(cx + 80, cy - 110), 'io', op=0.0)
    T.append(BB)
    for j, (xe, yy) in enumerate(((bx + 40, by - 8), (bx + 4, by + 10))):
        bl = Track(f'bline{j}', fl=0.0)
        bl.k(0.0, point(bx - 48, yy), sw=3.5, stroke=INK, fill=INK, fo=0.0, so=1.0, op=0.0)
        bl.hold(4.5 + j * 0.15, op=0.0)
        bl.k(4.8 + j * 0.15, bar(bx - 48, yy, xe, yy), 'o', op=1.0)
        bl.hold(5.0)
        bl.k(5.4, point(cx + 80, cy - 110), 'io', op=0.0)
        T.append(bl)

    # the hand
    def tip(j, end):
        return (fx + 10 + (110, 80, 95)[j] * (1 if end else 0) + 3, tys[j] + 3)
    HD = Track('hand')
    HD.k(0.0, hand(cx + 260, cy + 190), sw=3.0, stroke=INK, fill=WHITE, fo=1.0, so=1.0, op=0.0)
    HD.hold(1.2, op=0.0)
    HD.k(1.6, hand(*tip(0, 0)), 'o', op=1.0)
    HD.k(2.0, hand(*tip(0, 1)), 'lin')
    HD.k(2.15, hand(*tip(1, 0)), 'io')
    HD.k(2.5, hand(*tip(1, 1)), 'lin')
    HD.k(2.66, hand(*tip(2, 0)), 'io')
    HD.k(2.95, hand(*tip(2, 1)), 'lin')
    HD.k(3.2, hand(tkx + 3, tky + 3), 'io')
    HD.hold(3.45)
    HD.k(3.95, hand(cx + 250, cy + 210), 'io', op=0.0)
    T.append(HD)

    # pages 1..5 + their lines
    for i in range(1, 6):
        T.append(pages[i])
        for j in range(4):
            tr = Track(f'p{i}line{j}', fl=0.8 * i)
            follow(tr, LB[j], tls[i], 4.0, t_to=12.2 if i == 5 else 99.0, **INKS)
            if i == 5:
                sy = LGC[1] - 40 + j * 17
                tr.k(12.8, bar(SC[0] - 36, sy, SC[0] - 36 + [52, 44, 48, 38][j], sy), 'io', op=1.0)
                tr.hold(13.7)
                tgt = L['lines'][min(j, len(L['lines']) - 1)]
                tr.k(14.2, tgt, 'io', sw=1.5, stroke=CREAM, fill=CREAM, fo=1.0)
            T.append(tr)

    # plus sign between the two sets
    for nm, (a0, b0) in (('plusH', ((cx - 20, cy), (cx + 20, cy))), ('plusV', ((cx, cy - 20), (cx, cy + 20)))):
        pl = Track(nm)
        pl.k(0.0, point(cx, cy), sw=6.0, stroke=GOLD, fill=GOLD, fo=0.0, so=1.0, op=0.0)
        pl.hold(8.3, op=0.0)
        pl.k(8.7, bar(a0[0], a0[1], b0[0], b0[1]), 'b', op=1.0)
        pl.hold(8.9)
        pl.k(9.2, point(cx, cy), 'io', op=0.0)
        T.append(pl)

    # jurat stamp on the merged page, then three signatures
    stc = (cx + 44, cy + 58)
    ST = Track('stamp', fl=0.8 * 5)
    ST.k(0.0, point(*stc), sw=4.0, stroke=RED, fill=REDFILL, fo=0.0, so=1.0, op=0.0)
    ST.hold(10.3, op=0.0)
    ST.k(10.8, circle(stc[0], stc[1], 30, 0.05, 9, 0.4), 'b', op=1.0, fo=0.25)
    ST.hold(12.2)
    ST.k(12.8, circle(SC[0], SC[1], 150), 'io', op=0.0, fo=0.0)
    T.append(ST)
    for j, (sy, t0) in enumerate(((cy + 28, 10.9), (cy + 54, 11.2), (cy + 80, 11.5))):
        sx = cx - 62
        sg = Track(f'sign{j}', fl=0.8 * 5)
        sg.k(0.0, point(sx, sy), sw=3.0, stroke=GOLD, fill=GOLD, fo=0.0, so=1.0, op=0.0)
        sg.hold(t0, op=0.0)
        sg.k(t0 + 0.25, bar(sx, sy, sx + 58, sy), 'o', op=1.0)
        sg.k(t0 + 0.55, squiggle(sx + 2, sy, 56), 'io', sw=3.4)
        sg.hold(12.2)
        final = squiggle(SC[0] - 38, SC[1] + 52, 50)
        sg.k(12.8, final, 'io', op=1.0 if j == 2 else 0.0)
        if j == 2:
            sg.hold(13.7)
            sg.k(14.2, L['squig'] if L['squig'] is not None else final, 'io', sw=1.5, stroke=CREAM, fill=CREAM, fo=1.0)
        T.append(sg)

    # the quill
    Qu = Track('quill', fl=0.6)
    qx, qy = SC[0] + 30, SC[1] + 52
    Qu.k(0.0, xf(quill(qx, qy), s=0.2, about=(qx, qy)), sw=4.0, stroke=GOLD, op=0.0)
    Qu.hold(12.9, op=0.0)
    Qu.k(13.5, quill(qx, qy), 'b', op=1.0)
    Qu.hold(13.7)
    Qu.k(14.2, L['quill'], 'io', sw=1.5, stroke=CREAM, fill=CREAM, fo=1.0)
    T.append(Qu)
    return T, L

def state(tr, t):
    ks = tr.keys
    if t <= ks[0]['t']: a = b = ks[0]; p = 0.0
    elif t >= ks[-1]['t']: a = b = ks[-1]; p = 0.0
    else:
        i = 1
        while t >= ks[i]['t']: i += 1
        a, b = ks[i - 1], ks[i]
        p = EASE[b['e']]((t - a['t']) / (b['t'] - a['t']))
    mix = lambda u, v: u + (v - u) * p
    col = lambda u, v: tuple(u[j] + (v[j] - u[j]) * p for j in range(3))
    return dict(p=a['p'] + (b['p'] - a['p']) * p, sw=mix(a['sw'], b['sw']), so=mix(a['so'], b['so']),
                fo=mix(a['fo'], b['fo']), op=mix(a['op'], b['op']),
                stroke=col(a['stroke'], b['stroke']), fill=col(a['fill'], b['fill']))

# ----------------------------------------------------------------- rendering
class Renderer:
    def __init__(self, w, h, tracks, logo, ss=2):
        self.w, self.h, self.ss, self.T, self.L = w, h, ss, tracks, logo
        self.s = w / DW
        lk = Image.open(LOCKUP).convert('RGBA').resize((w, h), Image.LANCZOS)
        arr = np.asarray(lk).astype(np.float32) / 255.0
        self.rgb, self.alpha = arr[:, :, :3] * 255.0, arr[:, :, 3:4]
        tm = cv2.resize(logo['text_mask'], (w, h), interpolation=cv2.INTER_AREA)[:, :, None]
        self.text_a = self.alpha * tm
        self.mark_rgb, self.mark_a = self.rgb, self.alpha * (1 - tm)
        a = self.alpha   # the finished frame is the untouched lockup on white
        self.final = np.clip(255.0 * (1 - a) + self.rgb * a + 0.5, 0, 255).astype(np.uint8)

    def vector(self, t):
        W, H, f = self.w * self.ss, self.h * self.ss, self.s * self.ss
        cv = np.full((H, W, 3), 255.0, np.float32)
        fl = 2.5 * seg(t, 4.2, 5.2) * (1 - seg(t, 12.0, 12.8))
        for tr in self.T:
            st = state(tr, t)
            if st['op'] < 0.01: continue
            p = st['p'].copy()
            if tr.fl is not None and fl:
                p[:, 1] += fl * math.sin(t * 2.4 + tr.fl)
            p *= f
            sw = max(1, int(round(st['sw'] * f)))
            x0, y0 = np.floor(p.min(0) - sw - 2).astype(int); x1, y1 = np.ceil(p.max(0) + sw + 2).astype(int)
            x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, W), min(y1, H)
            if x1 <= x0 or y1 <= y0: continue
            q = np.round((p - [x0, y0]) * 16).astype(np.int32).reshape(-1, 1, 2)
            roi = cv[y0:y1, x0:x1]
            for kind in ('fill', 'stroke'):
                a = st['op'] * (st['fo'] if kind == 'fill' else st['so'])
                if a < 0.004: continue
                a = min(a, 1.0)
                m = np.zeros((y1 - y0, x1 - x0), np.uint8)
                if kind == 'fill':
                    cv2.fillPoly(m, [q], 255, cv2.LINE_AA, 4)
                else:
                    cv2.polylines(m, [q], True, 255, sw, cv2.LINE_AA, 4)
                m = (m.astype(np.float32) / 255.0 * a)[:, :, None]
                roi[:] = roi * (1 - m) + np.array(st[kind], np.float32) * m
        return cv2.resize(cv, (self.w, self.h), interpolation=cv2.INTER_AREA)

    def frame(self, t):
        if t >= DUR:
            return self.final
        lo = 1 - seg(t, DUR - 0.3, DUR - 0.05)
        img = np.full((self.h, self.w, 3), 255.0, np.float32)
        if lo > 0:
            img = 255.0 + (self.vector(t) - 255.0) * lo
        mo = eo(seg(t, DUR - 0.65, DUR - 0.25))
        if mo > 0:
            a = self.mark_a * mo
            img = img * (1 - a) + self.mark_rgb * a
        wp = eio(seg(t, DUR - 0.7, DUR))
        if wp > 0:
            feather = 40 * self.s
            xs = np.arange(self.w, dtype=np.float32)
            edge = wp * (self.w + feather)
            ramp = np.clip((edge - xs) / feather, 0, 1)[None, :, None]
            a = self.text_a * ramp
            img = img * (1 - a) + self.rgb * a
        return np.clip(img + 0.5, 0, 255).astype(np.uint8)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--frames', help='dump verification PNGs here')
    ap.add_argument('--out', default=BRAND)
    ap.add_argument('--name', default='splash-v6')
    ap.add_argument('--preview-only', action='store_true', help='with --frames: skip MP4/zip')
    args = ap.parse_args()
    tracks, logo = build()

    if args.frames:
        os.makedirs(args.frames, exist_ok=True)
        r = Renderer(1080, 720, tracks, logo)
        for t in (0.5, 1.0, 1.8, 2.4, 3.3, 4.3, 4.9, 5.7, 6.6, 7.5, 8.3, 8.7, 9.3, 9.9, 10.6, 11.3, 11.9, 12.5, 13.1, 13.9, 14.5, 14.8, 15.0, 15.0):
            Image.fromarray(r.frame(t)).save(os.path.join(args.frames, f't{t:05.2f}.png'))
        print('frames ->', args.frames)
        if args.preview_only:
            return

    # MP4: 1536x1024 (the lockup's native size), 50 fps, + hold
    fps = 50
    W, H = 1536, 1024
    mp4 = os.path.join(args.out, args.name + '.mp4')
    r = Renderer(W, H, tracks, logo)
    cmd = [imageio_ffmpeg.get_ffmpeg_exe(), '-y', '-loglevel', 'error', '-f', 'rawvideo', '-pix_fmt', 'rgb24',
           '-s', f'{W}x{H}', '-r', str(fps), '-i', '-',
           '-vf', 'scale=out_color_matrix=bt709:out_range=tv,format=yuv420p',
           '-c:v', 'libx264', '-preset', 'slow', '-crf', '14', '-tune', 'animation',
           '-color_primaries', 'bt709', '-color_trc', 'bt709', '-colorspace', 'bt709', '-color_range', 'tv',
           '-movflags', '+faststart', mp4]
    pr = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    n_anim = int(DUR * fps) + 1
    last = None
    for i in range(n_anim):
        last = r.frame(i / fps).tobytes()
        pr.stdin.write(last)
    for _ in range(int(HOLD * fps) - 1):
        pr.stdin.write(last)
    pr.stdin.close(); pr.wait()
    print('mp4 ->', mp4, f'{W}x{H} {fps}fps {(n_anim + int(HOLD * fps) - 1) / fps:.2f}s')

    # App splash frames: 1080x720 JPEG, 25 fps (same format as splash-v4.zip)
    r = Renderer(1080, 720, tracks, logo)
    zp = os.path.join(args.out, args.name + '.zip')
    with zipfile.ZipFile(zp, 'w', zipfile.ZIP_DEFLATED) as z:
        for i in range(int(DUR * 25) + 1):
            buf = io.BytesIO()
            Image.fromarray(r.frame(i / 25)).save(buf, 'JPEG', quality=92, subsampling=0)
            z.writestr(f'frame{i:04d}.jpg', buf.getvalue())
    print('zip ->', zp, f'{int(DUR * 25) + 1} frames 1080x720 25fps, {os.path.getsize(zp):,} bytes')

if __name__ == '__main__':
    main()
