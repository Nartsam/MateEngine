// M4: PMX material + Blender preset values -> Built-in UTS2 materials.
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public sealed class PmxMaterialBuildStats
    {
        public int Total;
        public int Uts2;
        public int TransClipping;
        public int Matcap;
        public int HighColor;
        public int Rim;
        public int Outline;
        public int PresetMatches;
        public int StandardFallbacks;
    }

    public static class PmxMaterialMapper
    {
        private const string OpaqueShader = "UnityChanToonShader/Toon_DoubleShadeWithFeather";
        private const string TransClippingShader = "UnityChanToonShader/Toon_DoubleShadeWithFeather_TransClipping";
        private static readonly Dictionary<string, bool> TextureAlphaCache = new();

        private enum MaterialKind
        {
            Other,
            Skin,
            Hair,
            Eye,
            Mouth,
            Cloth,
            Metal
        }

        public static PmxMaterialBuildStats LastStats { get; private set; } = new();

        public static Material[] BuildMaterials(PmxModel model, Dictionary<int, Texture2D> tex,
            string outFolder, PmxRenderPreset preset, PmxMaterialMatchMode matchMode, PmxStyleConfig style = null)
        {
            // Data-driven style overrides (null config => neutral defaults => prior behavior).
            var globals = style?.global ?? new PmxStyleGlobals();
            var opaque = Shader.Find(OpaqueShader);
            var trans = Shader.Find(TransClippingShader);
            var standard = Shader.Find("Standard");
            var mats = new Material[model.Materials.Count];
            var used = new HashSet<string>();
            var stats = new PmxMaterialBuildStats { Total = model.Materials.Count };

            for (int i = 0; i < model.Materials.Count; i++)
            {
                var pm = model.Materials[i];
                var presetMaterial = preset?.FindMaterial(pm.NameLocal, matchMode);
                if (presetMaterial != null) stats.PresetMatches++;

                Texture2D baseTex = TryGetTexture(tex, pm.TextureIndex);
                Texture2D matcapTex = TryGetTexture(tex, pm.EnvironmentIndex);
                if (matcapTex == null)
                    matcapTex = FindTextureByName(tex, presetMaterial?.matcapTexture);
                Texture2D matcapMaskTex = IsMaskLikeTextureName(presetMaterial?.matcapTexture)
                    ? FindTextureByName(tex, presetMaterial?.matcapTexture) : null;
                Texture2D highColorTex = FindTextureByName(tex, presetMaterial?.highColorTexture);
                Texture2D highColorMaskTex = presetMaterial != null && presetMaterial.useProceduralSparkle
                    ? FindTextureByName(tex, presetMaterial.maskTexture) : null;
                if (highColorMaskTex == null)
                    highColorMaskTex = FindTextureByName(tex, presetMaterial?.highColorMaskTexture);
                if (highColorMaskTex == null)
                    highColorMaskTex = FindTextureByName(tex, presetMaterial?.maskTexture);
                Texture2D shadeMaskTex = FindTextureByName(tex, presetMaterial?.shadeTexture);
                if (shadeMaskTex == null && IsLightMapTextureName(presetMaterial?.highColorMaskTexture))
                    shadeMaskTex = FindTextureByName(tex, presetMaterial.highColorMaskTexture);
                Texture2D emissionTex = FindTextureByName(tex, presetMaterial?.emissionTexture);

                bool alphaMaterial = IsAlphaMaterial(model, i, pm, baseTex, presetMaterial);
                Shader shader = alphaMaterial && trans != null ? trans : opaque;
                if (shader == null)
                {
                    shader = standard;
                    stats.StandardFallbacks++;
                }
                else
                {
                    stats.Uts2++;
                    if (alphaMaterial) stats.TransClipping++;
                }

                string matName = UniqueMaterialName(used, pm.NameLocal, i);
                var mat = new Material(shader) { name = matName };
                var styleMat = style?.FindMaterial(pm.NameLocal);
                ApplyCommonToonSettings(mat, pm, presetMaterial, baseTex, matcapTex, matcapMaskTex, highColorTex,
                    highColorMaskTex, shadeMaskTex, emissionTex, alphaMaterial, globals, styleMat);
                if (GetFloat(mat, "_HighColor_Power") > 0.001f) stats.HighColor++;
                if (GetFloat(mat, "_RimLight") > 0.5f) stats.Rim++;
                if (GetFloat(mat, "_Outline_Width") > 0.0001f) stats.Outline++;

                // Preserve PMX material draw order. Face features (brows, lashes, eyes,
                // mouth) are separate strips coplanar with the face skin sharing one atlas;
                // at an identical render queue they z-fight into shattered patches. A small
                // per-index queue bump makes later materials draw on top deterministically.
                mat.renderQueue = (alphaMaterial
                    ? (int)UnityEngine.Rendering.RenderQueue.AlphaTest
                    : (int)UnityEngine.Rendering.RenderQueue.Geometry) + i;

                AssetDatabase.CreateAsset(mat, $"{outFolder}/{matName}.mat");
                mats[i] = mat;
                if (GetFloat(mat, "_MatCap") > 0.5f) stats.Matcap++;
            }

            LastStats = stats;
            return mats;
        }

        private static void ApplyCommonToonSettings(Material mat, PmxMaterial pm, PmxPresetMaterial preset,
            Texture2D baseTex, Texture2D matcapTex, Texture2D matcapMaskTex, Texture2D highColorTex,
            Texture2D highColorMaskTex, Texture2D shadeMaskTex, Texture2D emissionTex, bool alphaMaterial,
            PmxStyleGlobals style, PmxStyleMaterial styleMat)
        {
            var kind = Classify(pm.NameLocal);
            Color baseColor = ResolveBaseColor(pm, preset, baseTex, kind);
            baseColor = ApplyStyleToBase(baseColor, kind, style, styleMat);
            SetColor(mat, "_BaseColor", baseColor);
            SetColor(mat, "_Color", baseColor);
            if (baseTex != null)
            {
                SetTexture(mat, "_MainTex", baseTex);
                SetTexture(mat, "_BaseMap", baseTex);
            }

            // Clean baseline: force single-sided (cull back). This is a single-sided HoYo
            // model (it even ships a dedicated 裙内侧 / skirt-inner material), but the PMX
            // double-sided draw flag is over-applied. Honoring it renders back faces of the
            // curved face/skin meshes at near-identical depth -> self z-fighting plus
            // flipped toon normals = the shattered-face artifact. Winding is preserved by
            // the 180°-about-Y conversion, so cull-back faces outward correctly.
            SetFloat(mat, "_CullMode", 2f);

            Color shade1 = ApplyStyleToShade(ResolveShadeColor(preset?.shadeColor, baseColor, kind, false), kind, style, styleMat);
            Color shade2 = ApplyStyleToShade(ResolveShadeColor(preset?.shadeColor2, baseColor, kind, true), kind, style, styleMat);
            SetTexture(mat, "_1st_ShadeMap", baseTex);
            SetTexture(mat, "_2nd_ShadeMap", baseTex);
            SetFloat(mat, "_Use_BaseAs1st", 1f);
            SetFloat(mat, "_Use_1stAs2nd", 1f);
            SetColor(mat, "_1st_ShadeColor", shade1);
            SetColor(mat, "_2nd_ShadeColor", shade2);
            SetFloat(mat, "_Is_LightColor_Base", 1f);
            SetFloat(mat, "_Is_LightColor_1st_Shade", 1f);
            SetFloat(mat, "_Is_LightColor_2nd_Shade", 1f);
            // Do NOT feed the HoYo LightMap/Facemap into UTS2's shade-position slot. Those
            // maps pack AO/shadow-ramp/specular in channels with game-shader semantics UTS2
            // does not share, and the body LightMap was even assigned to face materials whose
            // UVs don't match it -> sampled with face UVs it carved fractured garbage shadows
            // onto the face. UTS2 derives shade position from lighting alone here.
            ApplyShadeProfile(mat, kind, preset != null);

            ApplyMatcap(mat, pm, preset, matcapTex, matcapMaskTex, kind, preset != null);
            ApplyHighColor(mat, preset, highColorTex, highColorMaskTex, kind, matcapTex != null);
            ApplyRim(mat, preset, kind, style);
            ApplyEmission(mat, preset, emissionTex);
            ApplyOutline(mat, pm, preset, kind, style, styleMat);
            ApplyAlpha(mat, alphaMaterial);
        }

        private static void ApplyShadeProfile(Material mat, MaterialKind kind, bool hasPreset)
        {
            float baseStep = hasPreset ? 0.42f : 0.305f;
            float baseFeather = hasPreset ? 0.22f : 0.207f;
            float shadeStep = hasPreset ? 0.18f : 0.245f;
            float shadeFeather = hasPreset ? 0.19f : 0.147f;

            switch (kind)
            {
                case MaterialKind.Skin:
                    baseStep = hasPreset ? 0.34f : baseStep;
                    baseFeather = hasPreset ? 0.285f : baseFeather;
                    shadeStep = hasPreset ? 0.14f : shadeStep;
                    shadeFeather = hasPreset ? 0.25f : shadeFeather;
                    break;
                case MaterialKind.Cloth:
                case MaterialKind.Metal:
                    baseStep = hasPreset ? 0.47f : baseStep;
                    baseFeather = hasPreset ? 0.18f : baseFeather;
                    shadeStep = hasPreset ? 0.21f : shadeStep;
                    shadeFeather = hasPreset ? 0.17f : shadeFeather;
                    break;
                case MaterialKind.Hair:
                    baseStep = hasPreset ? 0.39f : baseStep;
                    baseFeather = hasPreset ? 0.22f : baseFeather;
                    break;
                case MaterialKind.Eye:
                case MaterialKind.Mouth:
                    baseStep = hasPreset ? 0.27f : baseStep;
                    baseFeather = hasPreset ? 0.30f : baseFeather;
                    shadeStep = hasPreset ? 0.10f : shadeStep;
                    shadeFeather = hasPreset ? 0.30f : shadeFeather;
                    break;
            }

            SetFloat(mat, "_BaseColor_Step", baseStep);
            SetFloat(mat, "_BaseShade_Feather", baseFeather);
            SetFloat(mat, "_ShadeColor_Step", shadeStep);
            SetFloat(mat, "_1st2nd_Shades_Feather", shadeFeather);
            SetFloat(mat, "_Set_SystemShadowsToBase", 0f);
            SetFloat(mat, "_Tweak_SystemShadowsLevel", 0f);
            // Brightness floor so the model doesn't read dim under the app's scene light
            // (these are independent of scene light intensity in UTS2).
            SetFloat(mat, "_GI_Intensity", hasPreset ? 0.14f : 0.05f);
            SetFloat(mat, "_Unlit_Intensity", hasPreset ? 1.95f : 2.2f);

            // Clean baseline: follow the scene light instead of a baked fixed direction,
            // so toon shading is consistent with the app's lighting rather than fighting it.
            SetFloat(mat, "_Is_BLD", 0f);
        }

        // Clean-baseline policy: MatCap is disabled. The Blender preset feeds a game
        // MetalMap (a metalness/specular mask, NOT a sphere/env texture) into the matcap
        // slot, and the PMX carries additive mc1/mc3 sphere maps; sampling either in
        // view-space as a UTS2 MatCap smears a purple sheen over the whole body (incl.
        // skin). We drop it entirely to recover the true albedo, then add subtle stylized
        // sheen back from a working baseline if needed. See ADR-0008 M4 clean baseline.
        private static void ApplyMatcap(Material mat, PmxMaterial pm, PmxPresetMaterial preset, Texture2D matcapTex,
            Texture2D matcapMaskTex, MaterialKind kind, bool hasPreset)
        {
            SetFloat(mat, "_MatCap", 0f);
        }

        // Clean baseline: a single, very subtle neutral-warm rim on skin and hair only
        // (the soft backlight seen in the target). No rim on cloth/metal and no antipodean
        // rim, both of which added colored edge fringing. Strength is scaled by the style
        // config (rimStrength 0 disables it).
        private static void ApplyRim(Material mat, PmxPresetMaterial preset, MaterialKind kind, PmxStyleGlobals style)
        {
            float strength = style?.rimStrength ?? 1f;
            bool useRim = (kind == MaterialKind.Skin || kind == MaterialKind.Hair) && strength > 0.001f;
            if (!useRim) return;

            SetFloat(mat, "_RimLight", 1f);
            SetColor(mat, "_RimLightColor", new Color(0.99f, 0.99f, 1f, 1f));
            SetFloat(mat, "_Is_LightColor_RimLight", 0f);
            SetFloat(mat, "_RimLight_Power", (kind == MaterialKind.Skin ? 0.10f : 0.14f) * strength);
            SetFloat(mat, "_RimLight_InsideMask", kind == MaterialKind.Skin ? 0.55f : 0.42f);
            SetFloat(mat, "_Add_Antipodean_RimLight", 0f);
        }

        // ----- style overrides (data-driven, shader-agnostic) -----

        // Apply brightness, skin warmth and an optional per-material base tint to the albedo.
        private static Color ApplyStyleToBase(Color baseColor, MaterialKind kind, PmxStyleGlobals g,
            PmxStyleMaterial sm)
        {
            if (sm != null && PmxStyleConfig.TryParseHexColor(sm.baseColor, out var tint))
            {
                tint.a = baseColor.a;
                baseColor = tint;
            }

            float b = g?.brightness ?? 1f;
            if (Mathf.Abs(b - 1f) > 0.0001f)
                baseColor = new Color(baseColor.r * b, baseColor.g * b, baseColor.b * b, baseColor.a);

            if (kind == MaterialKind.Skin)
                baseColor = ApplyWarmth(baseColor, g?.skinWarmth ?? 0f);
            return baseColor;
        }

        // Apply skin warmth and an optional per-material shade tint to a shade color.
        private static Color ApplyStyleToShade(Color shade, MaterialKind kind, PmxStyleGlobals g,
            PmxStyleMaterial sm)
        {
            if (sm != null && PmxStyleConfig.TryParseHexColor(sm.shadeColor, out var tint))
            {
                tint.a = shade.a;
                shade = tint;
            }
            if (kind == MaterialKind.Skin)
                shade = ApplyWarmth(shade, g?.skinWarmth ?? 0f);
            return shade;
        }

        // warmth -1..+1: positive boosts red / trims blue (warmer), negative the reverse.
        private static Color ApplyWarmth(Color c, float warmth)
        {
            float w = Mathf.Clamp(warmth, -1f, 1f) * 0.08f;
            return new Color(Mathf.Clamp01(c.r * (1f + w)), c.g, Mathf.Clamp01(c.b * (1f - w)), c.a);
        }

        private static void ApplyHighColor(Material mat, PmxPresetMaterial preset, Texture2D highColorTex,
            Texture2D highColorMaskTex, MaterialKind kind, bool hasMatcap)
        {
            // Clean baseline: only apply a high-color (specular) sheen when the preset
            // gives an EXPLICIT, valid (non-black) high-color. The preset sets
            // useHighColor=True on nearly every body material but leaves highColor null/black;
            // injecting a per-kind default there painted a purple sheen over everything.
            bool useHighColor = preset != null && ValidColor(preset.highColor);
            if (!useHighColor) return;

            SetTexture(mat, "_HighColor_Tex", highColorTex);
            SetTexture(mat, "_Set_HighColorMask", highColorMaskTex);
            SetColor(mat, "_HighColor", ValidColor(preset?.highColor)
                ? PmxRenderPreset.ColorOr(preset.highColor, HighColor(kind))
                : HighColor(kind));
            float power = preset != null && preset.highColorPower > 0f
                ? Mathf.Clamp01(preset.highColorPower)
                : HighColorPower(kind, preset != null && preset.useProceduralSparkle);
            SetFloat(mat, "_HighColor_Power", power);
            SetFloat(mat, "_Is_LightColor_HighColor", 0f);
            SetFloat(mat, "_Is_SpecularToHighColor", 1f);
            SetFloat(mat, "_Is_BlendAddToHiColor", 1f);
            SetFloat(mat, "_Is_UseTweakHighColorOnShadow", 1f);
            SetFloat(mat, "_TweakHighColorOnShadow", 0.28f);
        }

        private static void ApplyEmission(Material mat, PmxPresetMaterial preset, Texture2D emissionTex)
        {
            if (preset == null || !preset.useEmission) return;
            SetTexture(mat, "_Emissive_Tex", emissionTex);
            SetColor(mat, "_Emissive_Color", PmxRenderPreset.ColorOr(preset.emissionColor, Color.black) *
                                             Mathf.Max(1f, preset.emissionStrength));
        }

        private static void ApplyOutline(Material mat, PmxMaterial pm, PmxPresetMaterial preset, MaterialKind kind,
            PmxStyleGlobals style, PmxStyleMaterial sm)
        {
            // Per-material outline override: 0 = force off, 1 = force on, -1 = profile default.
            int forced = sm?.outline ?? -1;
            float scale = style?.outlineScale ?? 1f;
            if (forced == 0 || scale <= 0.0001f) { SetFloat(mat, "_Outline_Width", 0f); return; }

            bool pmxOutline = (pm.DrawFlags & 0x10) != 0 && pm.EdgeScale > 0f;
            bool presetOutline = preset != null && preset.useOutline;
            bool stylizedPresetOutline = preset != null && kind != MaterialKind.Eye && kind != MaterialKind.Mouth;
            if (forced != 1 && !pmxOutline && !presetOutline && !stylizedPresetOutline) return;

            float width = scale * (presetOutline && preset.outlineWidth > 0f
                ? Mathf.Clamp(preset.outlineWidth, 0.001f, 0.012f)
                : Mathf.Clamp(Mathf.Max(pm.EdgeScale, 0.65f) * OutlineScale(kind), OutlineMin(kind), OutlineMax(kind)));
            SetFloat(mat, "_Outline_Width", width);
            SetColor(mat, "_Outline_Color", presetOutline && ValidColor(preset?.outlineColor)
                ? PmxRenderPreset.ColorOr(preset.outlineColor, pm.EdgeColor)
                : ResolveOutlineColor(pm, kind));
            SetFloat(mat, "_Is_BlendBaseColor", kind == MaterialKind.Skin ? 1f : 0f);
            SetFloat(mat, "_Is_LightColor_Outline", 0f);
        }

        private static void ApplyAlpha(Material mat, bool alphaMaterial)
        {
            if (!alphaMaterial) return;
            SetFloat(mat, "_IsBaseMapAlphaAsClippingMask", 1f);
            SetFloat(mat, "_Clipping_Level", 0.5f);
            SetFloat(mat, "_Cutoff", 0.5f);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        private static bool IsAlphaMaterial(PmxModel model, int materialIndex, PmxMaterial pm, Texture2D baseTex,
            PmxPresetMaterial preset)
        {
            if (pm.Diffuse.a < 0.995f) return true;
            if (preset != null && preset.alpha > 0f && preset.alpha < 0.995f) return true;
            return TextureHasMeaningfulAlpha(model, materialIndex, baseTex);
        }

        private static MaterialKind Classify(string name)
        {
            name ??= "";
            if (name.Contains("皮肤") || name.Contains("脸")) return MaterialKind.Skin;
            if (name.Contains("头发")) return MaterialKind.Hair;
            if (name.Contains("眼")) return MaterialKind.Eye;
            if (name.Contains("口") || name.Contains("舌") || name.Contains("牙")) return MaterialKind.Mouth;
            if (name.Contains("衣") || name.Contains("裙") || name.Contains("手套") || name.Contains("袜")
                || name.Contains("腿带") || name.Contains("鞋") || name.Contains("头饰") || name.Contains("指甲"))
                return MaterialKind.Cloth;
            if (name.Contains("饰")) return MaterialKind.Metal;
            return MaterialKind.Other;
        }

        private static Color ResolveBaseColor(PmxMaterial pm, PmxPresetMaterial preset, Texture2D baseTex,
            MaterialKind kind)
        {
            float alpha = Mathf.Clamp01(preset != null && preset.alpha > 0f ? preset.alpha : pm.Diffuse.a);

            // Most Blender materials in this preset are texture-driven node groups whose
            // diffuse/base socket defaults are black. For textured PMX materials, treating
            // that socket as a tint destroys the real albedo, so preserve texture color.
            if (baseTex != null)
            {
                var tint = TexturedBaseTint(kind, preset != null);
                tint.a = alpha;
                return tint;
            }

            if (ValidColor(preset?.baseColor))
            {
                var c = PmxRenderPreset.ColorOr(preset.baseColor, FallbackBaseColor(kind));
                c.a = alpha;
                return c;
            }

            if (VisibleColor(pm.Diffuse))
                return new Color(pm.Diffuse.r, pm.Diffuse.g, pm.Diffuse.b, alpha);

            var fallback = FallbackBaseColor(kind);
            fallback.a = alpha;
            return fallback;
        }

        // Clean baseline: the shade color multiplies the base texture in shadow, so a
        // NEUTRAL darkening preserves the real albedo (a colored tint here was the source
        // of the purple cast on cloth). Skin keeps a faint warm bias; everything else is a
        // neutral, slightly cool grey. An explicit valid preset shade color still wins.
        private static Color ResolveShadeColor(float[] values, Color baseColor, MaterialKind kind, bool second)
        {
            if (ValidColor(values))
                return PmxRenderPreset.ColorOr(values, Darken(baseColor, second ? 0.48f : 0.68f));

            // Neutral skin shade (faint cool, NOT warm) so skin stays fair/white instead of
            // reading reddish. Warm skin tints here were the source of the ruddy skin.
            if (kind == MaterialKind.Skin)
                return second ? new Color(0.91f, 0.91f, 0.92f, 1f) : new Color(0.96f, 0.96f, 0.97f, 1f);

            return second ? new Color(0.80f, 0.79f, 0.82f, 1f) : new Color(0.91f, 0.90f, 0.93f, 1f);
        }

        private static Color ResolveOutlineColor(PmxMaterial pm, MaterialKind kind)
        {
            if (kind == MaterialKind.Other && VisibleColor(pm.EdgeColor))
                return pm.EdgeColor;

            return kind switch
            {
                MaterialKind.Skin => new Color(0.42f, 0.22f, 0.23f, 0.82f),
                MaterialKind.Hair => new Color(0.13f, 0.12f, 0.13f, 1f),
                MaterialKind.Eye => new Color(0.18f, 0.15f, 0.28f, 0.85f),
                MaterialKind.Mouth => new Color(0.20f, 0.04f, 0.07f, 0.85f),
                MaterialKind.Metal => new Color(0.12f, 0.08f, 0.04f, 1f),
                _ => new Color(0.035f, 0.025f, 0.055f, 1f)
            };
        }

        // Clean baseline: keep the textured albedo essentially untinted (no >1 boosts or
        // color casts). Only a faint warm lift on skin; everything else stays white so the
        // PMX texture colors come through as authored.
        private static Color TexturedBaseTint(MaterialKind kind, bool hasPreset)
        {
            if (!hasPreset) return Color.white;
            return kind switch
            {
                // Faint, hue-neutral lift to keep skin fair without a red cast (was warm).
                MaterialKind.Skin => new Color(1.03f, 1.03f, 1.04f, 1f),
                MaterialKind.Hair => new Color(1.01f, 1.01f, 1.01f, 1f),
                _ => Color.white
            };
        }

        private static Color MatcapTint(MaterialKind kind, bool hasPreset)
        {
            if (!hasPreset) return Color.white;
            return kind switch
            {
                MaterialKind.Skin => new Color(1.0f, 0.84f, 0.78f, 1f),
                MaterialKind.Hair => new Color(0.96f, 0.90f, 0.78f, 1f),
                MaterialKind.Cloth => new Color(0.86f, 0.66f, 1.0f, 1f),
                MaterialKind.Metal => new Color(1.0f, 0.90f, 0.62f, 1f),
                _ => Color.white
            };
        }

        private static Color HighColor(MaterialKind kind)
        {
            return kind switch
            {
                MaterialKind.Hair => new Color(1.0f, 0.90f, 0.78f, 1f),
                MaterialKind.Cloth => new Color(0.86f, 0.56f, 1.0f, 1f),
                MaterialKind.Metal => new Color(1.0f, 0.84f, 0.48f, 1f),
                MaterialKind.Skin => new Color(1.0f, 0.72f, 0.66f, 1f),
                _ => new Color(1f, 0.85f, 1f, 1f)
            };
        }

        private static float HighColorPower(MaterialKind kind, bool sparkle)
        {
            if (sparkle) return 0.34f;
            return kind switch
            {
                MaterialKind.Hair => 0.10f,
                MaterialKind.Cloth => 0.18f,
                MaterialKind.Metal => 0.36f,
                MaterialKind.Skin => 0.045f,
                _ => 0.08f
            };
        }

        private static float ResolveRimPower(PmxPresetMaterial preset, MaterialKind kind)
        {
            if (preset != null && preset.rimPower > 0f)
                return Mathf.Clamp01(preset.rimPower);

            return kind switch
            {
                MaterialKind.Skin => 0.075f,
                MaterialKind.Hair => 0.12f,
                MaterialKind.Cloth => 0.18f,
                MaterialKind.Metal => 0.16f,
                _ => 0.10f
            };
        }

        private static Color AntipodeanRimColor(MaterialKind kind)
        {
            return kind switch
            {
                MaterialKind.Hair => new Color(0.78f, 0.62f, 1.0f, 1f),
                MaterialKind.Metal => new Color(0.76f, 0.82f, 1.0f, 1f),
                _ => new Color(0.78f, 0.52f, 1.0f, 1f)
            };
        }

        private static Color FallbackBaseColor(MaterialKind kind)
        {
            return kind switch
            {
                MaterialKind.Skin => new Color(1f, 0.82f, 0.76f, 1f),
                MaterialKind.Hair => new Color(0.78f, 0.74f, 0.68f, 1f),
                MaterialKind.Eye => new Color(0.82f, 0.88f, 1f, 1f),
                MaterialKind.Mouth => new Color(0.65f, 0.22f, 0.26f, 1f),
                MaterialKind.Cloth => new Color(0.62f, 0.54f, 0.86f, 1f),
                MaterialKind.Metal => new Color(0.78f, 0.74f, 0.95f, 1f),
                _ => Color.white
            };
        }

        private static Color RimColor(MaterialKind kind)
        {
            return kind switch
            {
                MaterialKind.Skin => new Color(1f, 0.82f, 0.76f, 1f),
                MaterialKind.Hair => new Color(0.95f, 0.9f, 0.82f, 1f),
                MaterialKind.Cloth => new Color(0.78f, 0.72f, 1f, 1f),
                MaterialKind.Metal => new Color(0.82f, 0.86f, 1f, 1f),
                _ => new Color(1f, 0.9f, 0.82f, 1f)
            };
        }

        private static float OutlineScale(MaterialKind kind)
        {
            return kind == MaterialKind.Skin || kind == MaterialKind.Eye || kind == MaterialKind.Mouth
                ? 0.0030f : kind == MaterialKind.Metal ? 0.0066f : 0.0072f;
        }

        private static float OutlineMin(MaterialKind kind)
        {
            return kind == MaterialKind.Skin || kind == MaterialKind.Eye || kind == MaterialKind.Mouth
                ? 0.0015f : 0.0034f;
        }

        private static float OutlineMax(MaterialKind kind)
        {
            return kind == MaterialKind.Skin || kind == MaterialKind.Eye || kind == MaterialKind.Mouth
                ? 0.0048f : 0.008f;
        }

        private static bool ValidColor(float[] values)
        {
            if (values == null || values.Length < 3) return false;
            float a = values.Length >= 4 ? values[3] : 1f;
            float max = Mathf.Max(Mathf.Abs(values[0]), Mathf.Abs(values[1]), Mathf.Abs(values[2]));
            return a > 0.01f && max > 0.03f;
        }

        private static bool VisibleColor(Color color)
        {
            return color.a > 0.01f && Mathf.Max(Mathf.Abs(color.r), Mathf.Abs(color.g), Mathf.Abs(color.b)) > 0.03f;
        }

        private static bool TextureHasMeaningfulAlpha(PmxModel model, int materialIndex, Texture2D tex)
        {
            if (tex == null) return false;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            string cacheKey = $"{path}|{materialIndex}";
            if (TextureAlphaCache.TryGetValue(cacheKey, out bool cached)) return cached;

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null || !importer.DoesSourceTextureHaveAlpha())
            {
                TextureAlphaCache[cacheKey] = false;
                return false;
            }

            bool oldReadable = importer.isReadable;
            try
            {
                if (!oldReadable)
                {
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                    tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                }

                if (tex == null)
                {
                    TextureAlphaCache[cacheKey] = false;
                    return false;
                }

                var pixels = tex.GetPixels32();
                bool hasTransparentPixel = MaterialUvSamplesTransparentAlpha(model, materialIndex, tex.width,
                    tex.height, pixels);
                TextureAlphaCache[cacheKey] = hasTransparentPixel;
                return hasTransparentPixel;
            }
            catch
            {
                TextureAlphaCache[cacheKey] = false;
                return false;
            }
            finally
            {
                if (importer != null && importer.isReadable != oldReadable)
                {
                    importer.isReadable = oldReadable;
                    importer.SaveAndReimport();
                }
            }
        }

        private static bool MaterialUvSamplesTransparentAlpha(PmxModel model, int materialIndex, int width, int height,
            Color32[] pixels)
        {
            if (model == null || materialIndex < 0 || materialIndex >= model.Materials.Count || pixels == null)
                return false;

            int start = 0;
            for (int i = 0; i < materialIndex; i++) start += model.Materials[i].SurfaceCount;

            int end = Mathf.Min(start + model.Materials[materialIndex].SurfaceCount, model.Indices.Count);
            for (int i = start; i + 2 < end; i += 3)
            {
                if (!TryGetUv(model, i, out var uv0) || !TryGetUv(model, i + 1, out var uv1)
                    || !TryGetUv(model, i + 2, out var uv2))
                    continue;

                if (TriangleSamplesTransparentAlpha(uv0, uv1, uv2, width, height, pixels))
                    return true;
            }
            return false;
        }

        private static bool TryGetUv(PmxModel model, int indexCursor, out Vector2 uv)
        {
            uv = default;
            if (indexCursor < 0 || indexCursor >= model.Indices.Count) return false;
            int vertexIndex = model.Indices[indexCursor];
            if (vertexIndex < 0 || vertexIndex >= model.Vertices.Count) return false;
            uv = model.Vertices[vertexIndex].Uv;
            return true;
        }

        private static bool TriangleSamplesTransparentAlpha(Vector2 uv0, Vector2 uv1, Vector2 uv2, int width,
            int height, Color32[] pixels)
        {
            const int steps = 5;
            for (int a = 0; a <= steps; a++)
            for (int b = 0; b <= steps - a; b++)
            {
                int c = steps - a - b;
                Vector2 uv = (uv0 * a + uv1 * b + uv2 * c) / steps;
                if (SampleTransparentAlpha(uv, width, height, pixels)) return true;
            }
            return false;
        }

        private static bool SampleTransparentAlpha(Vector2 uv, int width, int height, Color32[] pixels)
        {
            if (width <= 0 || height <= 0) return false;
            float u = Mathf.Clamp01(uv.x);
            float v = Mathf.Clamp01(uv.y);
            int x = Mathf.Clamp(Mathf.RoundToInt(u * (width - 1)), 0, width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(v * (height - 1)), 0, height - 1);
            int idx = y * width + x;
            return idx >= 0 && idx < pixels.Length && pixels[idx].a < 250;
        }

        private static Texture2D TryGetTexture(Dictionary<int, Texture2D> tex, int index)
        {
            return index >= 0 && tex.TryGetValue(index, out var t) ? t : null;
        }

        private static Texture2D FindTextureByName(Dictionary<int, Texture2D> tex, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            string wanted = Path.GetFileName(fileName).ToLowerInvariant();
            foreach (var texture in tex.Values)
            {
                string assetName = Path.GetFileName(AssetDatabase.GetAssetPath(texture)).ToLowerInvariant();
                if (assetName == wanted) return texture;
            }
            return null;
        }

        private static bool IsLightMapTextureName(string fileName)
        {
            return !string.IsNullOrWhiteSpace(fileName)
                   && Path.GetFileName(fileName).ToLowerInvariant().Contains("lightmap");
        }

        private static bool IsMaskLikeTextureName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            string name = Path.GetFileName(fileName).ToLowerInvariant();
            return name.Contains("mask") || name.Contains("metalmap");
        }

        private static string UniqueMaterialName(HashSet<string> used, string name, int index)
        {
            string matName = SanitizeName(name);
            if (string.IsNullOrEmpty(matName)) matName = $"mat_{index}";
            string baseName = matName;
            int suffix = 1;
            while (!used.Add(matName)) matName = $"{baseName}_{suffix++}";
            return matName;
        }

        private static Color Darken(Color color, float factor)
        {
            return new Color(color.r * factor, color.g * factor, color.b * factor, color.a);
        }

        private static void SetTexture(Material mat, string property, Texture texture)
        {
            if (texture != null && mat.HasProperty(property)) mat.SetTexture(property, texture);
        }

        private static void SetColor(Material mat, string property, Color color)
        {
            if (mat.HasProperty(property)) mat.SetColor(property, color);
        }

        private static void SetFloat(Material mat, string property, float value)
        {
            if (mat.HasProperty(property)) mat.SetFloat(property, value);
        }

        private static float GetFloat(Material mat, string property)
        {
            return mat.HasProperty(property) ? mat.GetFloat(property) : 0f;
        }

        private static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}
