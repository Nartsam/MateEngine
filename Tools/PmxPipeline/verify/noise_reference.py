# -*- coding: utf-8 -*-
"""把 me_nodes.hlsl 里的 Perlin 噪声照抄成 numpy，用来和 Blender 的渲染逐点比。"""
import numpy as np

U32 = np.uint32


def rot(x, k):
    return (x << U32(k)) | (x >> U32(32 - k))


def _mix(a, b, c):
    a = a - c; a ^= rot(c, 4);  c = c + b
    b = b - a; b ^= rot(a, 6);  a = a + c
    c = c - b; c ^= rot(b, 8);  b = b + a
    a = a - c; a ^= rot(c, 16); c = c + b
    b = b - a; b ^= rot(a, 19); a = a + c
    c = c - b; c ^= rot(b, 4);  b = b + a
    return a, b, c


def _final(a, b, c):
    c ^= b; c = c - rot(b, 14)
    a ^= c; a = a - rot(c, 11)
    b ^= a; b = b - rot(a, 25)
    c ^= b; c = c - rot(b, 16)
    a ^= c; a = a - rot(c, 4)
    b ^= a; b = b - rot(a, 14)
    c ^= b; c = c - rot(b, 24)
    return c


def hash_uint4(kx, ky, kz, kw):
    init = U32(0xdeadbeef) + (U32(4) << U32(2)) + U32(13)
    a = b = c = np.full(np.broadcast(kx, ky, kz, kw).shape, init, dtype=U32)
    a = a + kx.astype(U32); b = b + ky.astype(U32); c = c + kz.astype(U32)
    a, b, c = _mix(a, b, c)
    a = a + kw.astype(U32)
    return _final(a, b, c)


def fade(t):
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


def grad4(h, x, y, z, w):
    h = (h & U32(31)).astype(np.int64)
    u = np.where(h < 24, x, y)
    v = np.where(h < 16, y, z)
    s = np.where(h < 8, z, w)
    return (np.where((h & 1) != 0, -u, u) + np.where((h & 2) != 0, -v, v)
            + np.where((h & 4) != 0, -s, s))


def tri_mix(v, x, y, z):
    x1, y1, z1 = 1 - x, 1 - y, 1 - z
    return (z1 * (y1 * (v[0] * x1 + v[1] * x) + y * (v[2] * x1 + v[3] * x)) +
            z * (y1 * (v[4] * x1 + v[5] * x) + y * (v[6] * x1 + v[7] * x)))


def perlin4(p):
    Xi = np.floor(p)
    Xf = p - Xi
    u = fade(Xf)
    X = Xi.astype(np.int32)
    x0, y0, z0, w0 = (X[..., i].view(U32) for i in range(4))
    x1, y1, z1, w1 = ((X[..., i] + 1).view(U32) for i in range(4))
    fx, fy, fz, fw = (Xf[..., i] for i in range(4))
    gx, gy, gz, gw = (Xf[..., i] - 1.0 for i in range(4))
    C = []
    for wv, ww in ((w0, fw), (w1, gw)):
        for zv, zz in ((z0, fz), (z1, gz)):
            for yv, yy in ((y0, fy), (y1, gy)):
                for xv, xx in ((x0, fx), (x1, gx)):
                    C.append(grad4(hash_uint4(xv, yv, zv, wv), xx, yy, zz, ww))
    # tri_mix 的角点顺序是 (x,y,z) 递增，上面的循环是 w,z,y,x，正好每 8 个一组
    lo = tri_mix(C[0:8], u[..., 0], u[..., 1], u[..., 2])
    hi = tri_mix(C[8:16], u[..., 0], u[..., 1], u[..., 2])
    r = lo + (hi - lo) * u[..., 3]
    return np.where(np.isinf(r), 0.0, 0.8344 * r)


def noise01_4(p):
    return 0.5 * perlin4(p) + 0.5


def fractal4(p, octaves, roughness):
    fscale, amp, maxamp, s = 1.0, 1.0, 0.0, 0.0
    n = int(np.clip(octaves, 0, 15))
    for _ in range(n + 1):
        s = s + noise01_4(fscale * p) * amp
        maxamp += amp
        amp *= np.clip(roughness, 0, 1)
        fscale *= 2.0
    rmd = octaves - np.floor(octaves)
    if rmd != 0.0:
        s2 = s + noise01_4(fscale * p) * amp
        return (1 - rmd) * (s / maxamp) + rmd * (s2 / (maxamp + amp))
    return s / maxamp


def hash_uint3(kx, ky, kz):
    init = U32(0xdeadbeef) + (U32(3) << U32(2)) + U32(13)
    a = b = c = np.full(np.broadcast(kx, ky, kz).shape, init, dtype=U32)
    a = a + kx.astype(U32); b = b + ky.astype(U32); c = c + kz.astype(U32)
    return _final(a, b, c)


def grad3(h, x, y, z):
    h = (h & U32(15)).astype(np.int64)
    u = np.where(h < 8, x, y)
    vt = np.where((h == 12) | (h == 14), x, z)
    v = np.where(h < 4, y, vt)
    return np.where((h & 1) != 0, -u, u) + np.where((h & 2) != 0, -v, v)


def perlin3(p):
    Xi = np.floor(p); Xf = p - Xi; u = fade(Xf)
    X = Xi.astype(np.int32)
    x0, y0, z0 = (X[..., i].view(U32) for i in range(3))
    x1, y1, z1 = ((X[..., i] + 1).view(U32) for i in range(3))
    fx, fy, fz = (Xf[..., i] for i in range(3))
    gx, gy, gz = (Xf[..., i] - 1.0 for i in range(3))
    C = []
    for zv, zz in ((z0, fz), (z1, gz)):
        for yv, yy in ((y0, fy), (y1, gy)):
            for xv, xx in ((x0, fx), (x1, gx)):
                C.append(grad3(hash_uint3(xv, yv, zv), xx, yy, zz))
    r = tri_mix(C, u[..., 0], u[..., 1], u[..., 2])
    return np.where(np.isinf(r), 0.0, 0.9820 * r)


def fractal3(p, octaves, roughness):
    fscale, amp, maxamp, s = 1.0, 1.0, 0.0, 0.0
    n = int(np.clip(octaves, 0, 15))
    for _ in range(n + 1):
        s = s + (0.5 * perlin3(fscale * p) + 0.5) * amp
        maxamp += amp
        amp *= np.clip(roughness, 0, 1)
        fscale *= 2.0
    rmd = octaves - np.floor(octaves)
    if rmd != 0.0:
        s2 = s + (0.5 * perlin3(fscale * p) + 0.5) * amp
        return (1 - rmd) * (s / maxamp) + rmd * (s2 / (maxamp + amp))
    return s / maxamp
