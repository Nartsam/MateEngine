// 材质 ID 通道专用着色器：无光照、平涂、可选 alpha 裁剪。
//
// 由 MERefRenderer.RenderIdPass 使用，给每个子网格涂一个唯一颜色，供
// Tools/PmxPipeline/compare_renders.py 把画面按材质分区。
//
// 为什么不用内置着色器：Unlit/Color 不支持 alpha 裁剪（头发、裙摆的镂空处会被
// 涂实，把后面的脸和腿误标成前者）；Unlit/Transparent Cutout 支持裁剪但**没有
// _Color 属性**，涂上去的是贴图本身而不是 ID 色。两个都不满足，只能自己写。
Shader "Hidden/MateEngine/IdPass"
{
    Properties
    {
        _Color ("ID Color", Color) = (1,1,1,1)
        _MainTex ("Alpha Source", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [Toggle] _UseAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        // 双面：PMX 的头发片、裙摆多为单层面片，剔除背面会在 ID 图上留洞
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Cutoff;
            float _UseAlphaClip;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_UseAlphaClip > 0.5)
                    clip(tex2D(_MainTex, i.uv).a - _Cutoff);
                // alpha 恒为 1：ID 图的 alpha 只用来区分「有没有几何」
                return fixed4(_Color.rgb, 1);
            }
            ENDCG
        }
    }
}
