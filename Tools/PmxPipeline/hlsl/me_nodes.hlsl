// ===== Blender 节点求值库（由 transpile.py 内联进每个生成的着色器）=====
//
// 每个函数都按 Blender 3.6 EEVEE 的 GLSL 实现逐行对应，不做「数学上等价」的化简
// ——EEVEE 在很多地方的行为并不是教科书写法（见下面各处注释），化简会引入偏差。
//
// 内联而不用 .cginc：生成物落在带中文名的目录下，相对 include 路径多一层不确定性，
// 而这份库只有两百行，内联的代价可以忽略。

#define ME_REC709 float3(0.2126, 0.7152, 0.0722)

// ---- 隐式类型转换 ----------------------------------------------------------
// Blender 把 Color 转 Float 用 Rec.709 亮度，Vector 转 Float 用算术平均。两者不同。
float ME_RgbaToValue(float4 c) { return dot(c.rgb, ME_REC709); }
float ME_VectorToValue(float3 v) { return (v.x + v.y + v.z) / 3.0; }

// ---- 标量运算（gpu_shader_common_math.glsl）--------------------------------
float ME_SafeDivide(float a, float b) { return (b != 0.0) ? a / b : 0.0; }

// Blender 的 pow 对负底数有专门处理：整数指数时按奇偶定符号，非整数指数返回 0。
// 本预设的高光用 pow(dot, 30) / pow(dot, 60)，而 dot 会是负数，走的正是这条分支，
// 用 HLSL 的 pow（负底数得 NaN）会直接出黑斑。
float ME_CompatiblePow(float x, float y)
{
    if (y == 0.0) return 1.0;
    if (x < 0.0)
    {
        if (fmod(-y, 2.0) == 0.0) return pow(-x, y);
        return -pow(-x, y);
    }
    if (x == 0.0) return 0.0;
    return pow(x, y);
}

float ME_Power(float a, float b)
{
    if (a >= 0.0) return pow(a, b);
    float fraction = fmod(abs(b), 1.0);
    if (fraction < 0.001) return ME_CompatiblePow(a, b);
    return 0.0;
}

float ME_Compare(float a, float b, float c) { return (abs(a - b) <= max(c, 1e-5)) ? 1.0 : 0.0; }

// ---- 向量运算 --------------------------------------------------------------
float3 ME_SafeNormalize(float3 v)
{
    float l = length(v);
    return (l != 0.0) ? v / l : float3(0.0, 0.0, 0.0);
}

// ---- Map Range -------------------------------------------------------------
float ME_MapRangeLinear(float v, float fmin, float fmax, float tmin, float tmax)
{
    return (fmax != fmin) ? tmin + ((v - fmin) / (fmax - fmin)) * (tmax - tmin) : 0.0;
}
float ME_ClampRange(float v, float mn, float mx)
{
    return (mx > mn) ? clamp(v, mn, mx) : clamp(v, mx, mn);
}
float ME_MapRangeStepped(float v, float fmin, float fmax, float tmin, float tmax, float steps)
{
    float f = (fmax != fmin) ? (v - fmin) / (fmax - fmin) : 0.0;
    f = (steps > 0.0) ? floor(f * (steps + 1.0)) / steps : 0.0;
    return tmin + f * (tmax - tmin);
}
float ME_MapRangeSmooth(float v, float fmin, float fmax, float tmin, float tmax)
{
    float f = (fmax != fmin) ? (v - fmin) / (fmax - fmin) : 0.0;
    f = clamp(f, 0.0, 1.0);
    f = f * f * (3.0 - 2.0 * f);
    return tmin + f * (tmax - tmin);
}
float ME_MapRangeSmoother(float v, float fmin, float fmax, float tmin, float tmax)
{
    float f = (fmax != fmin) ? (v - fmin) / (fmax - fmin) : 0.0;
    f = clamp(f, 0.0, 1.0);
    f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return tmin + f * (tmax - tmin);
}

// ---- Mapping（gpu_shader_material_mapping.glsl）----------------------------
// GLSL 的 mat3 是列优先、下标是 mat[列][行]，照抄时必须按列装配，写成行会得到
// 转置矩阵——绕 Z 轴的旋转会变成反向。
float3x3 ME_EulerToMat3(float3 e)
{
    float cx = cos(e.x), cy = cos(e.y), cz = cos(e.z);
    float sx = sin(e.x), sy = sin(e.y), sz = sin(e.z);
    float cc = cx * cz, cs = cx * sz, sc = sx * cz, ss = sx * sz;
    float3 c0 = float3(cy * cz, cy * sz, -sy);
    float3 c1 = float3(sy * sc - cs, sy * ss + cc, cy * sx);
    float3 c2 = float3(sy * cc + ss, sy * cs - sc, cy * cx);
    return float3x3(c0.x, c1.x, c2.x,
                    c0.y, c1.y, c2.y,
                    c0.z, c1.z, c2.z);
}

float3 ME_SafeDivide3(float3 a, float3 b)
{
    return float3(ME_SafeDivide(a.x, b.x), ME_SafeDivide(a.y, b.y), ME_SafeDivide(a.z, b.z));
}

float3 ME_MappingPoint(float3 v, float3 loc, float3 rot, float3 scale)
{
    return mul(ME_EulerToMat3(rot), v * scale) + loc;
}
float3 ME_MappingTexture(float3 v, float3 loc, float3 rot, float3 scale)
{
    return ME_SafeDivide3(mul(transpose(ME_EulerToMat3(rot)), v - loc), scale);
}
float3 ME_MappingVector(float3 v, float3 rot, float3 scale)
{
    return mul(ME_EulerToMat3(rot), v * scale);
}
float3 ME_MappingNormal(float3 v, float3 rot, float3 scale)
{
    return ME_SafeNormalize(mul(ME_EulerToMat3(rot), ME_SafeDivide3(v, scale)));
}

// ---- HSV（gpu_shader_material_hue_sat_val.glsl）----------------------------
float4 ME_RgbToHsv(float4 rgb)
{
    float cmax = max(rgb.r, max(rgb.g, rgb.b));
    float cmin = min(rgb.r, min(rgb.g, rgb.b));
    float cdelta = cmax - cmin;
    float v = cmax;
    float s = (cmax != 0.0) ? cdelta / cmax : 0.0;
    float h = 0.0;
    if (s != 0.0)
    {
        float3 c = (float3(cmax, cmax, cmax) - rgb.rgb) / cdelta;
        if (rgb.r == cmax)      h = c.b - c.g;
        else if (rgb.g == cmax) h = 2.0 + c.r - c.b;
        else                    h = 4.0 + c.g - c.r;
        h /= 6.0;
        if (h < 0.0) h += 1.0;
    }
    return float4(h, s, v, rgb.a);
}

float4 ME_HsvToRgb(float4 hsv)
{
    float h = hsv.x, s = hsv.y, v = hsv.z;
    float3 rgb;
    if (s == 0.0) { rgb = float3(v, v, v); }
    else
    {
        if (h == 1.0) h = 0.0;
        h *= 6.0;
        float i = floor(h);
        float f = h - i;
        float p = v * (1.0 - s);
        float q = v * (1.0 - (s * f));
        float t = v * (1.0 - (s * (1.0 - f)));
        if (i == 0.0)      rgb = float3(v, t, p);
        else if (i == 1.0) rgb = float3(q, v, p);
        else if (i == 2.0) rgb = float3(p, v, t);
        else if (i == 3.0) rgb = float3(p, q, v);
        else if (i == 4.0) rgb = float3(t, p, v);
        else               rgb = float3(v, p, q);
    }
    return float4(rgb, hsv.a);
}

float4 ME_HueSat(float hue, float sat, float value, float fac, float4 col)
{
    float4 hsv = ME_RgbToHsv(col);
    hsv.x = frac(hsv.x + hue + 0.5);
    hsv.y = clamp(hsv.y * sat, 0.0, 1.0);
    hsv.z = hsv.z * value;              // 有意不做 clamp，Blender 也不做
    float4 o = ME_HsvToRgb(hsv);
    o.rgb = max(o.rgb, 0.0);
    return lerp(col, o, fac);
}

// Invert（node_shader_invert.cc）。只反 RGB，alpha 原样透传；Fac 不钳制。
float4 ME_Invert(float fac, float4 col)
{
    return float4(lerp(col.rgb, 1.0 - col.rgb, fac), col.a);
}

// ---- 混合（material.c 的 ramp_blend，alpha 一律取 A 的）--------------------
// 注意：所有分支的最后一步都是 outcol.a = col1.a，不是混合后的 alpha。
float4 ME_MixBlend(float t, float4 a, float4 b)  { float4 o = lerp(a, b, t); o.a = a.a; return o; }
float4 ME_MixAdd(float t, float4 a, float4 b)    { float4 o = a + t * b; o.a = a.a; return o; }
float4 ME_MixMult(float t, float4 a, float4 b)   { float4 o = a * ((1.0 - t) + t * b); o.a = a.a; return o; }
float4 ME_MixSub(float t, float4 a, float4 b)    { float4 o = a - t * b; o.a = a.a; return o; }
float4 ME_MixScreen(float t, float4 a, float4 b)
{
    float4 o = 1.0 - ((1.0 - t) + t * (1.0 - b)) * (1.0 - a);
    o.a = a.a; return o;
}
float4 ME_MixDiv(float t, float4 a, float4 b)
{
    float facm = 1.0 - t;
    float4 o = a;
    if (b.r != 0.0) o.r = facm * a.r + t * a.r / b.r;
    if (b.g != 0.0) o.g = facm * a.g + t * a.g / b.g;
    if (b.b != 0.0) o.b = facm * a.b + t * a.b / b.b;
    o.a = a.a; return o;
}
float4 ME_MixDiff(float t, float4 a, float4 b)
{
    float4 o = lerp(a, abs(a - b), t); o.a = a.a; return o;
}
float4 ME_MixDark(float t, float4 a, float4 b)
{
    float4 o = min(a, b) * t + a * (1.0 - t); o.a = a.a; return o;
}
float4 ME_MixLight(float t, float4 a, float4 b)
{
    float4 o = max(a, t * b); o.a = a.a; return o;
}

// ---- 一维查表（EEVEE 的 ColorRamp / RGB Curves / 视图变换共用）-------------
// EEVEE 把表烤成 257 项（CM_TABLE + 1，写死的魔数），第 i 项对应输入 i/256；
// 采样时的偏移与缩放同样照抄 EEVEE：offset = 0.5/257，scale = 1 - 1/257。
// 这么取才能让输入 i/256 精确落在第 i 个纹素中心上。
#define ME_LUT_SIZE 257.0
float4 ME_SampleBand(sampler2D lut, float row, float rows, float fac)
{
    float u = saturate(fac) * (1.0 - 1.0 / ME_LUT_SIZE) + 0.5 / ME_LUT_SIZE;
    float v = (row + 0.5) / rows;
    return tex2D(lut, float2(u, v));
}

// ---- 视图变换（Filmic + High Contrast）-------------------------------------
// 参数化沿用 Blender config.ocio 里 Filmic Log 的 lg2 allocation。表里存的是
// **display sRGB 的线性值**：渲染目标是 sRGB 读写的，着色器返回线性、硬件负责
// 编码，这样落到 PNG 里的字节就等于 Blender 的显示值。
float3 ME_ViewTransform(sampler2D lut, float row, float rows, float3 x, float lo, float hi)
{
    float3 t;
    t.r = (x.r > 0.0) ? (log2(x.r) - lo) / (hi - lo) : 0.0;
    t.g = (x.g > 0.0) ? (log2(x.g) - lo) / (hi - lo) : 0.0;
    t.b = (x.b > 0.0) ? (log2(x.b) - lo) / (hi - lo) : 0.0;
    return float3(ME_SampleBand(lut, row, rows, t.r).r,
                  ME_SampleBand(lut, row, rows, t.g).r,
                  ME_SampleBand(lut, row, rows, t.b).r);
}

// ---- Generated 纹理坐标 ----------------------------------------------------
// Blender 的 Generated（ORCO）是「物体包围盒归一化坐标」。这里的包围盒由 Unity 侧
// 从网格 bounds 换算后写进 uniform。
//
// 注意坐标系：Unity 的局部坐标已经过 U = (-Bx, Bz, -By) 转换，要先转回 Blender 物体
// 空间再归一化，否则轴会错位——程序化纹理靠坐标吃饭，轴错了花纹方向就全变。
// 另一处近似：蒙皮网格在 Unity 的顶点着色器里拿到的是**已变形**的位置，而 Blender 的
// ORCO 取自静止姿势。差分渲染用的是 bindpose，两者相等；做动画时会有轻微漂移。
float3 _MEGenMin;
float3 _MEGenInvSize;

float3 ME_Generated(float3 opos)
{
    float3 b = float3(-opos.x, -opos.z, opos.y);
    return (b - _MEGenMin) * _MEGenInvSize;
}

// ---- Blender 的整数哈希（hash.glsl 的 Jenkins lookup3）---------------------
// 程序化噪声与 Voronoi 全靠它。位运算必须逐位照抄：改一个常量，花纹就完全是另一张图。
uint ME_rotl(uint x, uint k) { return (x << k) | (x >> (32u - k)); }

void ME_hash_final(inout uint a, inout uint b, inout uint c)
{
    c ^= b; c -= ME_rotl(b, 14u);
    a ^= c; a -= ME_rotl(c, 11u);
    b ^= a; b -= ME_rotl(a, 25u);
    c ^= b; c -= ME_rotl(b, 16u);
    a ^= c; a -= ME_rotl(c, 4u);
    b ^= a; b -= ME_rotl(a, 14u);
    c ^= b; c -= ME_rotl(b, 24u);
}

void ME_hash_mix(inout uint a, inout uint b, inout uint c)
{
    a -= c; a ^= ME_rotl(c, 4u);  c += b;
    b -= a; b ^= ME_rotl(a, 6u);  a += c;
    c -= b; c ^= ME_rotl(b, 8u);  b += a;
    a -= c; a ^= ME_rotl(c, 16u); c += b;
    b -= a; b ^= ME_rotl(a, 19u); a += c;
    c -= b; c ^= ME_rotl(b, 4u);  b += a;
}

uint ME_hash_uint2(uint kx, uint ky)
{
    uint a, b, c;
    a = b = c = 0xdeadbeefu + (2u << 2u) + 13u;
    b += ky; a += kx;
    ME_hash_final(a, b, c);
    return c;
}

uint ME_hash_uint3(uint kx, uint ky, uint kz)
{
    uint a, b, c;
    a = b = c = 0xdeadbeefu + (3u << 2u) + 13u;
    c += kz; b += ky; a += kx;
    ME_hash_final(a, b, c);
    return c;
}

uint ME_hash_uint4(uint kx, uint ky, uint kz, uint kw)
{
    uint a, b, c;
    a = b = c = 0xdeadbeefu + (4u << 2u) + 13u;
    a += kx; b += ky; c += kz;
    ME_hash_mix(a, b, c);
    a += kw;
    ME_hash_final(a, b, c);
    return c;
}

float ME_uint_to_float01(uint k) { return float(k) / 4294967295.0; }
// 浮点版哈希吃的是**浮点数的位模式**，不是取整后的格点，asuint 不能换成强制转换
float ME_hash_v2f(float2 k) { return ME_uint_to_float01(ME_hash_uint2(asuint(k.x), asuint(k.y))); }
float ME_hash_v3f(float3 k) { return ME_uint_to_float01(ME_hash_uint3(asuint(k.x), asuint(k.y), asuint(k.z))); }
float ME_hash_v4f(float4 k) { return ME_uint_to_float01(ME_hash_uint4(asuint(k.x), asuint(k.y), asuint(k.z), asuint(k.w))); }

float3 ME_hash_v3v3(float3 k)
{
    return float3(ME_hash_v3f(k),
                  ME_hash_v4f(float4(k, 1.0)),
                  ME_hash_v4f(float4(k, 2.0)));
}

float3 ME_random_vec3_offset(float seed)
{
    return float3(100.0 + ME_hash_v2f(float2(seed, 0.0)) * 100.0,
                  100.0 + ME_hash_v2f(float2(seed, 1.0)) * 100.0,
                  100.0 + ME_hash_v2f(float2(seed, 2.0)) * 100.0);
}

// ---- Perlin 噪声（gpu_shader_material_noise.glsl）--------------------------
float ME_noise_fade(float t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }

float ME_noise_grad(uint hash, float x, float y, float z)
{
    uint h = hash & 15u;
    float u = h < 8u ? x : y;
    float vt = ((h == 12u) || (h == 14u)) ? x : z;
    float v = h < 4u ? y : vt;
    float r = ((h & 1u) != 0u) ? -u : u;
    return r + (((h & 2u) != 0u) ? -v : v);
}

float ME_tri_mix(float v0, float v1, float v2, float v3,
                 float v4, float v5, float v6, float v7,
                 float x, float y, float z)
{
    float x1 = 1.0 - x, y1 = 1.0 - y, z1 = 1.0 - z;
    return z1 * (y1 * (v0 * x1 + v1 * x) + y * (v2 * x1 + v3 * x)) +
           z  * (y1 * (v4 * x1 + v5 * x) + y * (v6 * x1 + v7 * x));
}

float ME_noise_perlin(float3 v)
{
    float3 Xi = floor(v);
    float3 Xf = v - Xi;
    float3 u = float3(ME_noise_fade(Xf.x), ME_noise_fade(Xf.y), ME_noise_fade(Xf.z));
    int3 X = (int3)Xi;
    uint x0 = asuint(X.x), y0 = asuint(X.y), z0 = asuint(X.z);
    uint x1 = asuint(X.x + 1), y1 = asuint(X.y + 1), z1 = asuint(X.z + 1);

    float r = ME_tri_mix(
        ME_noise_grad(ME_hash_uint3(x0, y0, z0), Xf.x,       Xf.y,       Xf.z),
        ME_noise_grad(ME_hash_uint3(x1, y0, z0), Xf.x - 1.0, Xf.y,       Xf.z),
        ME_noise_grad(ME_hash_uint3(x0, y1, z0), Xf.x,       Xf.y - 1.0, Xf.z),
        ME_noise_grad(ME_hash_uint3(x1, y1, z0), Xf.x - 1.0, Xf.y - 1.0, Xf.z),
        ME_noise_grad(ME_hash_uint3(x0, y0, z1), Xf.x,       Xf.y,       Xf.z - 1.0),
        ME_noise_grad(ME_hash_uint3(x1, y0, z1), Xf.x - 1.0, Xf.y,       Xf.z - 1.0),
        ME_noise_grad(ME_hash_uint3(x0, y1, z1), Xf.x,       Xf.y - 1.0, Xf.z - 1.0),
        ME_noise_grad(ME_hash_uint3(x1, y1, z1), Xf.x - 1.0, Xf.y - 1.0, Xf.z - 1.0),
        u.x, u.y, u.z);
    return isinf(r) ? 0.0 : 0.9820 * r;      // noise_scale3
}

float ME_noise01(float3 p) { return 0.5 * ME_noise_perlin(p) + 0.5; }

float ME_fractal_noise(float3 p, float octaves, float roughness)
{
    float fscale = 1.0, amp = 1.0, maxamp = 0.0, sum = 0.0;
    octaves = clamp(octaves, 0.0, 15.0);
    int n = int(octaves);
    for (int i = 0; i <= n; i++)
    {
        sum += ME_noise01(fscale * p) * amp;
        maxamp += amp;
        amp *= clamp(roughness, 0.0, 1.0);
        fscale *= 2.0;
    }
    float rmd = octaves - floor(octaves);
    if (rmd != 0.0)
    {
        float sum2 = sum + ME_noise01(fscale * p) * amp;
        return (1.0 - rmd) * (sum / maxamp) + rmd * (sum2 / (maxamp + amp));
    }
    return sum / maxamp;
}

void ME_NoiseTexture3D(float3 co, float scale, float detail, float roughness,
                       float distortion, out float value, out float4 color)
{
    float3 p = co * scale;
    if (distortion != 0.0)
    {
        p += float3(ME_noise_perlin(p + ME_random_vec3_offset(0.0)) * distortion,
                    ME_noise_perlin(p + ME_random_vec3_offset(1.0)) * distortion,
                    ME_noise_perlin(p + ME_random_vec3_offset(2.0)) * distortion);
    }
    value = ME_fractal_noise(p, detail, roughness);
    color = float4(value,
                   ME_fractal_noise(p + ME_random_vec3_offset(3.0), detail, roughness),
                   ME_fractal_noise(p + ME_random_vec3_offset(4.0), detail, roughness),
                   1.0);
}

// ---- 随机 alpha 测试（Wyman & McGuire，EEVEE 的 hashed_alpha_threshold）------
// Blender 的 HASHED 混合模式：每个采样用一个在 [0,1) 上均匀分布的阈值裁剪，于是
// 存活概率恰好等于 alpha，多采样平均后收敛到正确的 alpha 合成——而且天然与绘制
// 顺序无关（深度写入负责遮挡）。
//
// EEVEE 靠 TAA 的 64 个采样收敛，每个采样抖一次 hashAlphaOffset；我们没有时间维
// 度，改由 4× 超采样提供 16 个子采样。阈值按屏幕导数量化，在 4× 分辨率下正好每个
// 子采样取一个独立值，平均后同样是无偏的。
float ME_HashedAlphaThreshold(float3 co)
{
    float max_deriv = max(length(ddx(co)), length(ddy(co)));
    float pix_scale = 1.0 / max(max_deriv, 1e-8);
    float pix_scale_log = log2(pix_scale);
    float2 pix_scales = float2(exp2(floor(pix_scale_log)), exp2(ceil(pix_scale_log)));

    float2 a = float2(ME_hash_v3f(floor(pix_scales.x * co)),
                      ME_hash_v3f(floor(pix_scales.y * co)));
    float fac = frac(pix_scale_log);
    float x = lerp(a.x, a.y, fac);

    // 两个尺度插值出来的 x 不再均匀分布，过一遍 CDF 拉回均匀，否则存活概率就不等
    // 于 alpha，覆盖率会系统性偏掉。
    float m = min(fac, 1.0 - fac);
    float one_m = 1.0 - m;
    float denom = 1.0 / max(2.0 * m * one_m, 1e-8);
    float one_x = 1.0 - x;
    float3 cases = float3(x * x * denom,
                          (x - 0.5 * m) / max(one_m, 1e-8),
                          1.0 - (one_x * one_x * denom));
    float threshold = (x < one_m) ? ((x < m) ? cases.x : cases.y) : cases.z;
    return clamp(threshold, 1e-6, 1.0);
}

// ---- Perlin 噪声 4D --------------------------------------------------------
// 和 3D 同源（gpu_shader_material_noise.glsl），只是梯度取 32 个方向、插值多一层，
// 归一化常数换成 noise_scale4。
float4 ME_random_vec4_offset(float seed)
{
    return float4(100.0 + ME_hash_v2f(float2(seed, 0.0)) * 100.0,
                  100.0 + ME_hash_v2f(float2(seed, 1.0)) * 100.0,
                  100.0 + ME_hash_v2f(float2(seed, 2.0)) * 100.0,
                  100.0 + ME_hash_v2f(float2(seed, 3.0)) * 100.0);
}

float ME_noise_grad4(uint hash, float x, float y, float z, float w)
{
    uint h = hash & 31u;
    float u = h < 24u ? x : y;
    float v = h < 16u ? y : z;
    float t = h < 8u ? z : w;
    return (((h & 1u) != 0u) ? -u : u) +
           (((h & 2u) != 0u) ? -v : v) +
           (((h & 4u) != 0u) ? -t : t);
}

float ME_quad_mix(float v0, float v1, float v2, float v3,
                  float v4, float v5, float v6, float v7,
                  float v8, float v9, float v10, float v11,
                  float v12, float v13, float v14, float v15,
                  float x, float y, float z, float w)
{
    return lerp(ME_tri_mix(v0, v1, v2, v3, v4, v5, v6, v7, x, y, z),
                ME_tri_mix(v8, v9, v10, v11, v12, v13, v14, v15, x, y, z), w);
}

float ME_noise_perlin4(float4 v)
{
    float4 Xi = floor(v);
    float4 Xf = v - Xi;
    float4 u = float4(ME_noise_fade(Xf.x), ME_noise_fade(Xf.y),
                      ME_noise_fade(Xf.z), ME_noise_fade(Xf.w));
    int4 X = (int4)Xi;
    uint x0 = asuint(X.x), y0 = asuint(X.y), z0 = asuint(X.z), w0 = asuint(X.w);
    uint x1 = asuint(X.x + 1), y1 = asuint(X.y + 1);
    uint z1 = asuint(X.z + 1), w1 = asuint(X.w + 1);
    float fx = Xf.x, fy = Xf.y, fz = Xf.z, fw = Xf.w;
    float gx = fx - 1.0, gy = fy - 1.0, gz = fz - 1.0, gw = fw - 1.0;

    float r = ME_quad_mix(
        ME_noise_grad4(ME_hash_uint4(x0, y0, z0, w0), fx, fy, fz, fw),
        ME_noise_grad4(ME_hash_uint4(x1, y0, z0, w0), gx, fy, fz, fw),
        ME_noise_grad4(ME_hash_uint4(x0, y1, z0, w0), fx, gy, fz, fw),
        ME_noise_grad4(ME_hash_uint4(x1, y1, z0, w0), gx, gy, fz, fw),
        ME_noise_grad4(ME_hash_uint4(x0, y0, z1, w0), fx, fy, gz, fw),
        ME_noise_grad4(ME_hash_uint4(x1, y0, z1, w0), gx, fy, gz, fw),
        ME_noise_grad4(ME_hash_uint4(x0, y1, z1, w0), fx, gy, gz, fw),
        ME_noise_grad4(ME_hash_uint4(x1, y1, z1, w0), gx, gy, gz, fw),
        ME_noise_grad4(ME_hash_uint4(x0, y0, z0, w1), fx, fy, fz, gw),
        ME_noise_grad4(ME_hash_uint4(x1, y0, z0, w1), gx, fy, fz, gw),
        ME_noise_grad4(ME_hash_uint4(x0, y1, z0, w1), fx, gy, fz, gw),
        ME_noise_grad4(ME_hash_uint4(x1, y1, z0, w1), gx, gy, fz, gw),
        ME_noise_grad4(ME_hash_uint4(x0, y0, z1, w1), fx, fy, gz, gw),
        ME_noise_grad4(ME_hash_uint4(x1, y0, z1, w1), gx, fy, gz, gw),
        ME_noise_grad4(ME_hash_uint4(x0, y1, z1, w1), fx, gy, gz, gw),
        ME_noise_grad4(ME_hash_uint4(x1, y1, z1, w1), gx, gy, gz, gw),
        u.x, u.y, u.z, u.w);
    return isinf(r) ? 0.0 : 0.8344 * r;      // noise_scale4
}

float ME_noise01_4(float4 p) { return 0.5 * ME_noise_perlin4(p) + 0.5; }

float ME_fractal_noise4(float4 p, float octaves, float roughness)
{
    float fscale = 1.0, amp = 1.0, maxamp = 0.0, sum = 0.0;
    octaves = clamp(octaves, 0.0, 15.0);
    int n = int(octaves);
    for (int i = 0; i <= n; i++)
    {
        sum += ME_noise01_4(fscale * p) * amp;
        maxamp += amp;
        amp *= clamp(roughness, 0.0, 1.0);
        fscale *= 2.0;
    }
    float rmd = octaves - floor(octaves);
    if (rmd != 0.0)
    {
        float sum2 = sum + ME_noise01_4(fscale * p) * amp;
        return (1.0 - rmd) * (sum / maxamp) + rmd * (sum2 / (maxamp + amp));
    }
    return sum / maxamp;
}

void ME_NoiseTexture4D(float3 co, float w, float scale, float detail,
                       float roughness, float distortion,
                       out float value, out float4 color)
{
    float4 p = float4(co, w) * scale;
    if (distortion != 0.0)
    {
        p += float4(ME_noise_perlin4(p + ME_random_vec4_offset(0.0)) * distortion,
                    ME_noise_perlin4(p + ME_random_vec4_offset(1.0)) * distortion,
                    ME_noise_perlin4(p + ME_random_vec4_offset(2.0)) * distortion,
                    ME_noise_perlin4(p + ME_random_vec4_offset(3.0)) * distortion);
    }
    value = ME_fractal_noise4(p, detail, roughness);
    color = float4(value,
                   ME_fractal_noise4(p + ME_random_vec4_offset(4.0), detail, roughness),
                   ME_fractal_noise4(p + ME_random_vec4_offset(5.0), detail, roughness),
                   1.0);
}

// ---- Voronoi F1 3D（欧氏距离）---------------------------------------------
// 27 个相邻格子 × 每格 3 次哈希，不便宜。本预设只有 `胸衣` 用到，面积很小。
void ME_VoronoiF1_3D(float3 coord, float scale, float randomness,
                     out float outDistance, out float4 outColor, out float3 outPosition)
{
    float3 c = coord * scale;
    float3 cellPosition = floor(c);
    float3 localPosition = c - cellPosition;

    float minDistance = 8.0;
    float3 targetOffset = float3(0, 0, 0);
    float3 targetPosition = float3(0, 0, 0);
    for (int k = -1; k <= 1; k++)
    for (int j = -1; j <= 1; j++)
    for (int i = -1; i <= 1; i++)
    {
        float3 cellOffset = float3(i, j, k);
        float3 pointPosition = cellOffset +
            ME_hash_v3v3(cellPosition + cellOffset) * randomness;
        float d = distance(pointPosition, localPosition);
        if (d < minDistance)
        {
            targetOffset = cellOffset;
            minDistance = d;
            targetPosition = pointPosition;
        }
    }
    outDistance = minDistance;
    outColor = float4(ME_hash_v3v3(cellPosition + targetOffset), 1.0);
    outPosition = (scale != 0.0) ? (targetPosition + cellPosition) / scale : float3(0, 0, 0);
}

// ---- Blender 相机空间 ------------------------------------------------------
// Blender 场景到 Unity 场景的映射是 U = (-B.x, B.z, -B.y)，行列式 -1（右手变左手）。
// 点积在正交变换下不变，所以 SUN / FRONT / RIGHT 那套只要一起映射就自动对上；但
// **相机空间的 X 轴会被镜像**：实测正面视角下 Blender 相机的右方向 +X 映射过来是
// Unity 视空间的 -X，而上方向与朝向一致。matcap 取的就是相机空间坐标，不翻会左右反。
float3 ME_WorldToBlenderCamera(float3 v)
{
    float3 c = mul((float3x3)UNITY_MATRIX_V, v);
    return float3(-c.x, c.y, c.z);
}

// ---- 法线贴图 --------------------------------------------------------------
float3 ME_NormalMapTangent(float4 tangent, float3 normal, float3 texnormal, float strength)
{
    float3 B = tangent.w * cross(normal, tangent.xyz);
    float3 n = texnormal.x * tangent.xyz + texnormal.y * B + texnormal.z * normal;
    n = normalize(n);
    return normalize(lerp(normal, n, max(strength, 0.0)));
}

// ---- 双三次采样（interpolation = Smart / Cubic）----------------------------
// EEVEE 对 Smart 走 texture_bicubic（4 抽头 B 样条）。人脸 SDF 图用的就是 Smart，
// 而它的输出直接进硬阈值，用双线性会让阴影分界线的形状与位置可见偏移。
float4 ME_TexBicubic(sampler2D tex, float4 texelSize, float2 uv)
{
    float2 texSize = texelSize.zw;
    float2 invSize = texelSize.xy;
    float2 xy = uv * texSize - 0.5;
    float2 f = frac(xy);
    float2 base = floor(xy);

    float2 w0 = (1.0 / 6.0) * (1.0 - f) * (1.0 - f) * (1.0 - f);
    float2 w1 = (1.0 / 6.0) * (4.0 - 6.0 * f * f + 3.0 * f * f * f);
    float2 w2 = (1.0 / 6.0) * (1.0 + 3.0 * f + 3.0 * f * f - 3.0 * f * f * f);
    float2 w3 = (1.0 / 6.0) * f * f * f;

    float2 s0 = w0 + w1;
    float2 s1 = w2 + w3;
    float2 o0 = (base + 0.5 + w1 / s0) * invSize;
    float2 o1 = (base + 2.5 + w3 / s1) * invSize;

    return s0.y * (s0.x * tex2D(tex, float2(o0.x, o0.y)) + s1.x * tex2D(tex, float2(o1.x, o0.y)))
         + s1.y * (s0.x * tex2D(tex, float2(o0.x, o1.y)) + s1.x * tex2D(tex, float2(o1.x, o1.y)));
}

// ---- 闭包 ------------------------------------------------------------------
// 预设里绝大多数材质是无光照纯函数（Color 直连 Surface 等价于 Emission），但
// `眼睛2` 是纯 Transparent BSDF、`表情` 是 mmd_tools 残留的真 BSDF 混合。用
// 「辐射 + 透射」两项就能把这四个节点类型都表达出来，且与 EEVEE 的合成方式一致：
// 最终 alpha = 1 - avg(transmittance)，RGB = radiance。
struct MEClosure { float3 radiance; float3 transmittance; };

MEClosure ME_Emission(float4 c)
{
    MEClosure o; o.radiance = c.rgb; o.transmittance = float3(0, 0, 0); return o;
}
MEClosure ME_Transparent(float4 c)
{
    MEClosure o; o.radiance = float3(0, 0, 0); o.transmittance = c.rgb; return o;
}
// 无灯环境下 Diffuse / Glossy 求值为纯黑且不透光（预设文件里唯一的灯 energy = 0）
MEClosure ME_BlackBsdf()
{
    MEClosure o; o.radiance = float3(0, 0, 0); o.transmittance = float3(0, 0, 0); return o;
}
MEClosure ME_MixShader(float t, MEClosure a, MEClosure b)
{
    MEClosure o;
    o.radiance = lerp(a.radiance, b.radiance, t);
    o.transmittance = lerp(a.transmittance, b.transmittance, t);
    return o;
}
float ME_ClosureAlpha(MEClosure c)
{
    return 1.0 - (c.transmittance.r + c.transmittance.g + c.transmittance.b) / 3.0;
}
