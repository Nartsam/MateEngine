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
