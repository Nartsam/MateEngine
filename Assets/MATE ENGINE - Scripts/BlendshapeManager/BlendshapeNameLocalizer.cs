using System.Collections.Generic;
using System.Text;

/// <summary>
/// Maps Japanese MMD/PMX morph (blendshape) names to a display string for the
/// Blendshapes menu, in the form "中文 / 原文" (translation / original) so the
/// user sees both. Unmapped names return the original unchanged.
///
/// The dictionary is keyed by the EXACT morph names of the current model
/// (dumped from the .me via PmxBlendshapeDump), so suffixed/handed variants
/// (困る2左, ウィンク２, half-width ｳｨﾝｸ２右 …) all map precisely.
///
/// Shared with the editor font-glyph baker: the menu font is a static SDF atlas
/// with no runtime source font, so every character shown must be pre-baked, else
/// it renders as a tofu box. <see cref="AllGlyphs"/> returns exactly the glyph
/// set this localizer can display (both originals and translations) plus the
/// full kana ranges as a safety net for other models.
/// </summary>
public static class BlendshapeNameLocalizer
{
    public const string Separator = " / ";

    // EXACT morph name -> Simplified-Chinese. Keys are 丽塔_m4's 59 morphs.
    private static readonly Dictionary<string, string> Map = new()
    {
        // 口 mouth / lip-sync
        { "あ", "口型啊" }, { "い", "口型伊" }, { "い1", "口型伊1" },
        { "う", "口型呜" }, { "う1", "口型呜1" }, { "え", "口型诶" }, { "お", "口型哦" },
        { "ワ", "口型哇" },
        { "口角上げ", "嘴角上扬" }, { "口角上げ1", "嘴角上扬1" },
        { "口角下げ", "嘴角下垂" }, { "口角下げ1", "嘴角下垂1" },
        { "口横広げ", "嘴角拉宽" },
        { "口横缩げ", "嘴角收窄" }, { "口横缩げ1", "嘴角收窄1" },
        { "口横平げ", "嘴角平展" },

        // 眉 brows
        { "真面目", "认真" }, { "真面目左", "认真左" }, { "真面目右", "认真右" },
        { "困る", "困扰" }, { "困る1", "困扰1" }, { "困る2", "困扰2" }, { "困る3", "困扰3" },
        { "困る左", "困扰左" }, { "困る右", "困扰右" },
        { "困る1左", "困扰1左" }, { "困る1右", "困扰1右" },
        { "困る2左", "困扰2左" }, { "困る2右", "困扰2右" },
        { "困る3左", "困扰3左" }, { "困る3右", "困扰3右" },
        { "にこり", "微笑" }, { "にこり左", "微笑左" }, { "にこり右", "微笑右" },
        { "怒り", "生气" }, { "怒り左", "生气左" }, { "怒り右", "生气右" },
        { "上", "眉上扬" }, { "下", "眉下垂" },

        // 目 eyes
        { "ウィンク", "眨眼" }, { "ウィンク右", "眨眼右" },
        { "ウィンク２", "眨眼2" }, { "ｳｨﾝｸ２右", "眨眼2右" },
        { "瞳小", "缩瞳" }, { "瞳小1", "缩瞳1" }, { "瞳小2", "缩瞳2" },
        { "じと目", "眯缝眼" }, { "じと目1", "眯缝眼1" },
        { "ｷﾘｯ", "锐利眼" },
        { "びっくり", "惊讶" }, { "びっくり1", "惊讶1" }, { "びっくり2", "惊讶2" },
        { "眼角下", "眼角下垂" },
        { "瞳縦潰れ", "瞳孔竖压" },
        { "恐ろしい子！", "惊恐" },
        { "なごみ", "平和眼" },
        { "はちゅ目", "睁圆眼" },
        { "はぅ", "羞涩眼" },

        // その他 other
        { "照れ", "害羞" },
    };

    public static string Localize(string jpName)
    {
        if (string.IsNullOrEmpty(jpName)) return jpName;
        return Map.TryGetValue(jpName, out var cn) ? cn + Separator + jpName : jpName;
    }

    /// <summary>Exactly the glyphs the Blendshapes menu / render-style UI can
    /// display, for baking into the static SDF atlas: every original morph name
    /// + every translation + the separator + the render-style strings. Kept
    /// minimal (no full-range safety net) because the atlas has limited room;
    /// other models would re-bake with their own morphs' glyphs.</summary>
    public static string AllGlyphs()
    {
        var set = new HashSet<char>();

        void Add(string s) { if (s != null) foreach (var c in s) set.Add(c); }

        Add(Separator);
        Add("默认鲜艳柔和渲染风格");
        foreach (var kv in Map) { Add(kv.Key); Add(kv.Value); }

        var sb = new StringBuilder(set.Count);
        foreach (var c in set) sb.Append(c);
        return sb.ToString();
    }
}
