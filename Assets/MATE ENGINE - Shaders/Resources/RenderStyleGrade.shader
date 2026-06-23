// Full-screen color grade (saturation / contrast / brightness) for the runtime
// render-style system. CRITICAL: this is alpha-preserving — it only adjusts RGB
// and writes back the ORIGINAL source alpha, and it leaves fully-transparent
// pixels untouched. That is what lets it run on MateEngine's per-pixel-alpha
// layered window without painting the transparent background gray (unlike the
// Built-in PostProcessLayer Bloom/ColorGrading, which is why those are unusable
// here — see DECISIONS_RECORD ADR-0010).
Shader "Hidden/RenderStyleGrade"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Saturation ("Saturation", Float) = 1
        _Contrast ("Contrast", Float) = 1
        _Brightness ("Brightness", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Saturation;
            float _Contrast;
            float _Brightness;

            fixed4 frag (v2f_img i) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, i.uv);

                float3 c = src.rgb * _Brightness;
                float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
                c = lerp(float3(luma, luma, luma), c, _Saturation); // saturation
                c = (c - 0.5) * _Contrast + 0.5;                    // contrast
                c = saturate(c);

                // Preserve transparency: don't lift fully-transparent pixels (would
                // glow gray under premultiplied alpha); keep the source alpha exactly.
                float mask = step(0.0001, src.a);
                float3 outRGB = lerp(src.rgb, c, mask);
                return fixed4(outRGB, src.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
