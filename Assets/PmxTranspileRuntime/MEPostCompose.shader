// 转译渲染的末端合成：bloom + 视图变换，逐像素做，保留 alpha。
//
// 为什么必须走离屏缓冲而不是逐材质做完：
//  - bloom 要在**线性域**上取亮部并扩散，而视图变换（Filmic + High Contrast）是
//    非线性的，顺序反了结果完全不同；
//  - `眼睛2` / `裙内侧` 这两个 BLEND 材质在 EEVEE 里是在线性域合成之后才过视图变换，
//    逐材质做的话 Unity 只能在显示域混合。
//
// 和 ADR-0010「全屏后处理会破坏分层窗口 alpha」的关系：当年破坏 alpha 是因为用了
// Unity 自带后处理栈，它直接丢 alpha。这里自己写，alpha 全程原样透传——实测 Blender
// 的 bloom 也**不改 alpha**（辉光只写进 RGB，轮廓外 314456 个像素 alpha 仍为 0）。
Shader "Hidden/MateEngine/PostCompose"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        [HideInInspector] _MEDepthCull ("Depth cull", Float) = 0
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;

    // EEVEE 的二次阈值曲线参数，与 effect_bloom.c 的装配方式一致：
    //   x = threshold - knee，y = knee * 2，z = 0.25 / knee，w = threshold
    float4 _CurveThreshold;
    float  _SampleScale;

    struct v2f_blit { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

    v2f_blit vert_blit(appdata_img v)
    {
        v2f_blit o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    float3 ME_BrightnessThreshold(float3 c)
    {
        float br = max(c.r, max(c.g, c.b));
        float rq = clamp(br - _CurveThreshold.x, 0.0, _CurveThreshold.y);
        rq = _CurveThreshold.z * rq * rq;
        return c * max(rq, br - _CurveThreshold.w) / max(br, 1e-4);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0: 取亮部
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_blit
            #pragma fragment frag
            float4 frag(v2f_blit i) : SV_Target
            {
                float3 c = max(tex2D(_MainTex, i.uv).rgb, 0.0);
                return float4(ME_BrightnessThreshold(c), 1.0);
            }
            ENDCG
        }

        // 1: 降采样（13 抽头，来自 CoD:AW 那套渐进式 bloom，EEVEE 用的也是它）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_blit
            #pragma fragment frag
            float4 frag(v2f_blit i) : SV_Target
            {
                float2 t = _MainTex_TexelSize.xy;
                float3 a = tex2D(_MainTex, i.uv + t * float2(-1, -1)).rgb;
                float3 b = tex2D(_MainTex, i.uv + t * float2( 1, -1)).rgb;
                float3 c = tex2D(_MainTex, i.uv + t * float2(-1,  1)).rgb;
                float3 d = tex2D(_MainTex, i.uv + t * float2( 1,  1)).rgb;
                float3 e = tex2D(_MainTex, i.uv).rgb;
                float3 f = tex2D(_MainTex, i.uv + t * float2(-2, -2)).rgb;
                float3 g = tex2D(_MainTex, i.uv + t * float2( 0, -2)).rgb;
                float3 h = tex2D(_MainTex, i.uv + t * float2( 2, -2)).rgb;
                float3 j = tex2D(_MainTex, i.uv + t * float2(-2,  0)).rgb;
                float3 k = tex2D(_MainTex, i.uv + t * float2( 2,  0)).rgb;
                float3 l = tex2D(_MainTex, i.uv + t * float2(-2,  2)).rgb;
                float3 m = tex2D(_MainTex, i.uv + t * float2( 0,  2)).rgb;
                float3 n = tex2D(_MainTex, i.uv + t * float2( 2,  2)).rgb;

                float3 o = (a + b + c + d) * 0.125;
                o += (f + g + e + j) * 0.03125;
                o += (g + h + k + e) * 0.03125;
                o += (j + e + l + m) * 0.03125;
                o += (e + k + m + n) * 0.03125;
                return float4(o, 1.0);
            }
            ENDCG
        }

        // 2: 上采样（9 抽头 tent），叠加到目标层上
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert_blit
            #pragma fragment frag
            float4 frag(v2f_blit i) : SV_Target
            {
                float2 t = _MainTex_TexelSize.xy * _SampleScale;
                float3 s = tex2D(_MainTex, i.uv + t * float2(-1, -1)).rgb;
                s += tex2D(_MainTex, i.uv + t * float2( 0, -1)).rgb * 2.0;
                s += tex2D(_MainTex, i.uv + t * float2( 1, -1)).rgb;
                s += tex2D(_MainTex, i.uv + t * float2(-1,  0)).rgb * 2.0;
                s += tex2D(_MainTex, i.uv).rgb * 4.0;
                s += tex2D(_MainTex, i.uv + t * float2( 1,  0)).rgb * 2.0;
                s += tex2D(_MainTex, i.uv + t * float2(-1,  1)).rgb;
                s += tex2D(_MainTex, i.uv + t * float2( 0,  1)).rgb * 2.0;
                s += tex2D(_MainTex, i.uv + t * float2( 1,  1)).rgb;
                return float4(s * (1.0 / 16.0), 1.0);
            }
            ENDCG
        }

        // 3: 合成 —— 加 bloom，过视图变换，alpha 原样透传
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_blit
            #pragma fragment frag

            sampler2D _BloomTex;
            float4 _BloomColor;      // rgb 已乘过 intensity 与标定增益
            float  _BloomClamp;      // 0 = 不钳制（Blender 默认）

            sampler2D _MELut;
            float4 _ViewTransform;   // x=行号 y=总行数 z=lo w=hi
            float  _MEPremultipliedOutput;   // 1 = 输出给按预乘 alpha 合成的桌面窗口

            float ME_VT(float x)
            {
                float t = (x > 0.0) ? (log2(x) - _ViewTransform.z)
                                    / (_ViewTransform.w - _ViewTransform.z) : 0.0;
                // 与着色器里的 ME_SampleBand 同一套偏移/缩放（EEVEE 的 257 项表）
                float u = saturate(t) * (1.0 - 1.0 / 257.0) + 0.5 / 257.0;
                float v = (_ViewTransform.x + 0.5) / _ViewTransform.y;
                return tex2D(_MELut, float2(u, v)).r;
            }

            float4 frag(v2f_blit i) : SV_Target
            {
                float4 src = tex2D(_MainTex, i.uv);
                float3 bloom = max(tex2D(_BloomTex, i.uv).rgb, 0.0) * _BloomColor.rgb;
                if (_BloomClamp > 0.0) bloom = min(bloom, _BloomClamp);
                float3 lin = max(src.rgb + bloom, 0.0);
                if (_MEPremultipliedOutput > 0.5)
                {
                    // HDR 缓冲在 MSAA 边缘与 BLEND 材质处是预乘值。视图变换是非线性的，直接作用
                    // 在预乘值上会让高亮边缘偏亮，边缘辉光也不随覆盖率衰减。先还原再乘回，
                    // 边缘即与材质内做视图变换的旧路径一致；alpha=0 处的辉光按原 alpha 策略丢弃。
                    if (src.a <= 0.0) return 0.0;
                    lin /= src.a;
                    return float4(float3(ME_VT(lin.r), ME_VT(lin.g), ME_VT(lin.b)) * src.a, src.a);
                }
                return float4(ME_VT(lin.r), ME_VT(lin.g), ME_VT(lin.b), src.a);
            }
            ENDCG
        }
        // 4: 写回主相机。alpha=0 的辉光不能留在目标 RGB 中，否则随后绘制的半透明 UI
        // 会把原本不可见的颜色混进菜单；离线差分仍使用上面的完整 RGB 合成 pass。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_blit
            #pragma fragment frag
            float4 frag(v2f_blit i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                return c.a > 0.0 ? c : 0.0;
            }
            ENDCG
        }
        // 5: 给主相机补模型深度，只写深度。按合成结果的覆盖率裁剪：覆盖不到一半的像素
        // 不写，alpha 裁掉的发梢、镂空处和窗口遮挡掉的部分不会误挡后面的辅助物体。
        Pass
        {
            Cull [_MEDepthCull]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert_depth
            #pragma fragment frag_depth

            sampler2D _MEModelMask;

            struct v2f_depth { float4 pos : SV_POSITION; float4 screen : TEXCOORD0; };

            v2f_depth vert_depth(float4 vertex : POSITION)
            {
                v2f_depth o;
                o.pos = UnityObjectToClipPos(vertex);
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            float4 frag_depth(v2f_depth i) : SV_Target
            {
                clip(tex2D(_MEModelMask, i.screen.xy / i.screen.w).a - 0.5);
                return 0.0;
            }
            ENDCG
        }
    }
    Fallback Off
}
