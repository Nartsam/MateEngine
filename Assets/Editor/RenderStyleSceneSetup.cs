using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TMPro;

/// <summary>
/// Editor commands for the runtime render-style switcher's settings UI:
///   * AddDropdown        — clones the Graphics (quality) dropdown into the
///                          General group to create the render-style dropdown.
///   * FixDropdown        — renames its title to "渲染风格" and adjusts spacing.
///   * BakeFontGlyphs     — bakes the Chinese glyphs the new UI/blendshape menu
///                          needs into the static CJK SDF atlas (the menu font
///                          has no runtime source font, so any shown glyph must
///                          be pre-baked, else it renders as a tofu box).
///   * ApplyFeedbackFixes — headless: BakeFontGlyphs + FixDropdown + save.
///
/// All idempotent and revertable via git. Run from the MateEngine menu (so you
/// can see/nudge the result) or headless via -executeMethod.
/// </summary>
public static class RenderStyleSceneSetup
{
    private const string ScenePath = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";
    private const string CloneName = "RenderStyle";
    private const string TitleText = "渲染风格";

    // Static SC atlas the menus fall back to. We bake glyphs in here at edit time
    // by briefly flipping it to Dynamic with its source TTF linked, then restoring
    // Static + clearing the source ref so the build never bundles the ~10MB TTF.
    private const string ChineseFontPath = "Assets/MATE ENGINE - Fonts/Asia Fonts/Chinese (Simple).asset";
    private const string ChineseSourceTtfPath = "Assets/MATE ENGINE - Fonts/Asia Fonts/Asia TTF/NotoSansSC-Medium.ttf";

    // Extra UI strings (beyond the blendshape localizer) whose glyphs must exist.
    private const string ExtraGlyphs = "默认鲜艳柔和渲染风格";

    [MenuItem("MateEngine/Render Style/Add Settings Dropdown")]
    public static void AddDropdownMenu()
    {
        bool changed = AddDropdown(out string msg);
        if (changed)
        {
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
        }
        EditorUtility.DisplayDialog("Render Style Dropdown", msg, "OK");
    }

    [MenuItem("MateEngine/Render Style/Fix Dropdown Label + Spacing")]
    public static void FixDropdownMenu()
    {
        bool changed = FixDropdownLabelAndSpacing(out string msg);
        if (changed)
        {
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
        }
        EditorUtility.DisplayDialog("Render Style Dropdown", msg, "OK");
    }

    [MenuItem("MateEngine/Render Style/Bake Font Glyphs")]
    public static void BakeFontGlyphsMenu()
    {
        bool changed = BakeFontGlyphs(out string msg);
        if (changed) AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("Bake Font Glyphs", msg, "OK");
    }

    // Headless entry: open scene, bake glyphs, fix dropdown, save everything.
    public static void AddDropdownBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        bool changed = AddDropdown(out string msg);
        Debug.Log("[RenderStyleSceneSetup] " + msg);
        if (changed)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[RenderStyleSceneSetup] Scene saved.");
        }
    }

    public static void ApplyFeedbackFixes()
    {
        bool bakeChanged = BakeFontGlyphs(out string bakeMsg);
        Debug.Log("[RenderStyleSceneSetup] Bake: " + bakeMsg);

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        bool fixChanged = FixDropdownLabelAndSpacing(out string fixMsg);
        Debug.Log("[RenderStyleSceneSetup] Fix: " + fixMsg);

        if (fixChanged)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[RenderStyleSceneSetup] Scene saved.");
        }
        if (bakeChanged)
        {
            AssetDatabase.SaveAssets();
            Debug.Log("[RenderStyleSceneSetup] Font asset saved.");
        }
    }

    // -------------------------------------------------------------- font bake

    public static bool BakeFontGlyphs(out string message)
    {
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(ChineseFontPath);
        if (font == null)
        {
            message = $"Font asset not found at {ChineseFontPath}.";
            return false;
        }

        var ttf = AssetDatabase.LoadAssetAtPath<Font>(ChineseSourceTtfPath);
        if (ttf == null)
        {
            message = $"Source TTF not found at {ChineseSourceTtfPath}.";
            return false;
        }

        string chars = ExtraGlyphs + BlendshapeNameLocalizer.AllGlyphs();

        // Idempotent: if every needed glyph is already baked, do nothing (avoids a
        // pointless Dynamic flip that re-reports the chars as "missing").
        bool allPresent = true;
        foreach (var ch in chars)
            if (ch != '\n' && ch != '\r' && !font.HasCharacter(ch)) { allPresent = false; break; }
        if (allPresent)
        {
            message = $"All needed glyphs already present in '{font.name}'. No change.";
            return false;
        }

        int before = font.characterTable != null ? font.characterTable.Count : 0;

        // --- flip to Dynamic + link source TTF (TryAddCharacters is a no-op on
        //     Static fonts) -------------------------------------------------------
        var so = new SerializedObject(font);
        var modeProp = so.FindProperty("m_AtlasPopulationMode");
        var srcProp = so.FindProperty("m_SourceFontFile");
        var multiProp = so.FindProperty("m_IsMultiAtlasTexturesEnabled");
        Object prevSrc = srcProp != null ? srcProp.objectReferenceValue : null;
        int prevMode = modeProp != null ? modeProp.enumValueIndex : 0;
        if (modeProp != null) modeProp.enumValueIndex = 1; // Dynamic
        if (srcProp != null) srcProp.objectReferenceValue = ttf;
        // Allow spilling into additional atlas textures when the first fills up
        // (the existing 2048x1024 atlas is nearly full). Multi-atlas renders fine
        // in Static mode too.
        if (multiProp != null) multiProp.boolValue = true;
        so.ApplyModifiedProperties();
        font.ReadFontAssetDefinition();

        bool ok = font.TryAddCharacters(chars, out string missing);
        int after = font.characterTable != null ? font.characterTable.Count : 0;
        Debug.Log($"[RenderStyleSceneSetup] TryAddCharacters ok={ok} {before}->{after} missing='{missing}'");

        // --- restore Static + unlink source (keep baked glyphs; avoid TTF bloat) -
        var so2 = new SerializedObject(font);
        var modeProp2 = so2.FindProperty("m_AtlasPopulationMode");
        var srcProp2 = so2.FindProperty("m_SourceFontFile");
        if (modeProp2 != null) modeProp2.enumValueIndex = prevMode; // Static
        if (srcProp2 != null) srcProp2.objectReferenceValue = prevSrc; // null
        so2.ApplyModifiedProperties();
        font.ReadFontAssetDefinition();

        EditorUtility.SetDirty(font);
        if (font.atlasTextures != null)
            foreach (var tex in font.atlasTextures)
                if (tex != null) EditorUtility.SetDirty(tex);

        message = $"Baked glyphs into '{font.name}': {before}->{after} chars (added ok={ok})." +
                  (string.IsNullOrEmpty(missing) ? "" : $" Still missing: '{missing}'");
        return after != before;
    }

    // ------------------------------------------------------ dropdown clone add

    public static bool AddDropdown(out string message)
    {
        var handler = Object.FindFirstObjectByType<SettingsHandlerDropdowns>(FindObjectsInactive.Include);
        if (handler == null || handler.graphicsDropdown == null)
        {
            message = "Could not find SettingsHandlerDropdowns.graphicsDropdown in the open scene.";
            return false;
        }

        var src = handler.graphicsDropdown.gameObject;
        var parent = src.transform.parent;
        if (parent == null)
        {
            message = "Graphics dropdown has no parent group; aborting.";
            return false;
        }

        for (int i = 0; i < parent.childCount; i++)
            if (parent.GetChild(i).name == CloneName)
            {
                message = $"'{CloneName}' already present under '{parent.name}'. No change.";
                return false;
            }

        var clone = Object.Instantiate(src, parent);
        clone.name = CloneName;

        var srcRect = (RectTransform)src.transform;
        var rect = (RectTransform)clone.transform;
        rect.localScale = srcRect.localScale;
        rect.anchorMin = srcRect.anchorMin;
        rect.anchorMax = srcRect.anchorMax;
        rect.pivot = srcRect.pivot;
        rect.sizeDelta = srcRect.sizeDelta;

        float lowestY = float.MaxValue;
        for (int i = 0; i < parent.childCount; i++)
            if (parent.GetChild(i) is RectTransform r && r != rect)
                lowestY = Mathf.Min(lowestY, r.anchoredPosition.y);
        if (lowestY == float.MaxValue) lowestY = srcRect.anchoredPosition.y;
        rect.anchoredPosition = new Vector2(srcRect.anchoredPosition.x, lowestY - 100f);

        clone.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1);

        var stale = clone.GetComponent<RenderStyleDropdownHandler>();
        if (stale != null) Object.DestroyImmediate(stale);

        var dropdown = clone.GetComponent<TMP_Dropdown>();
        var rsHandler = clone.AddComponent<RenderStyleDropdownHandler>();
        var so = new SerializedObject(rsHandler);
        var list = so.FindProperty("dropdowns");
        list.arraySize = 1;
        list.GetArrayElementAtIndex(0).objectReferenceValue = dropdown;
        so.ApplyModifiedPropertiesWithoutUndo();

        message = $"Added '{CloneName}' under '{parent.name}' at {rect.anchoredPosition}.";
        return true;
    }

    // -------------------------------------------------- label rename + spacing

    public static bool FixDropdownLabelAndSpacing(out string message)
    {
        var handler = Object.FindFirstObjectByType<SettingsHandlerDropdowns>(FindObjectsInactive.Include);
        Transform parent = handler != null && handler.graphicsDropdown != null
            ? handler.graphicsDropdown.transform.parent
            : null;

        GameObject clone = null;
        if (parent != null)
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == CloneName) { clone = parent.GetChild(i).gameObject; break; }

        if (clone == null)
        {
            message = $"'{CloneName}' clone not found; run Add Settings Dropdown first.";
            return false;
        }

        bool changed = false;

        // 1) Title: find the direct-child TMP_Text that is NOT the dropdown caption,
        //    disable any localization component on it, set static "渲染风格".
        var dd = clone.GetComponent<TMP_Dropdown>();
        TMP_Text title = null;
        foreach (Transform child in clone.transform)
        {
            var t = child.GetComponent<TMP_Text>();
            if (t != null && (dd == null || t != dd.captionText)) { title = t; break; }
        }
        if (title != null)
        {
            foreach (var comp in title.GetComponents<MonoBehaviour>())
                if (comp != null && comp.GetType().Name.Contains("Localize") && comp is Behaviour b && b.enabled)
                { b.enabled = false; changed = true; }

            if (title.text != TitleText) { title.text = TitleText; changed = true; }
        }
        else
        {
            Debug.LogWarning("[RenderStyleSceneSetup] No title TMP_Text found on clone; label not changed.");
        }

        // 2) Placement: put the dropdown at the END of the General group as a new
        //    bottom row (left column, one row-pitch below the bottom item), AND
        //    push every section BELOW General down to make room — the sections are
        //    absolute-positioned siblings that don't reflow, so without this the
        //    new row collides with = AUDIO. The scroll area lengthens automatically
        //    because SettingsMenuScrollBoundsLimiter recomputes Content height from
        //    the (now lower) bottom element (ADR-0007 情况A).
        var rect = (RectTransform)clone.transform;
        var target = new Vector2(-104.9f, -360f);  // left column, row below Discord (-260)
        const float sectionShift = 120f;
        if ((rect.anchoredPosition - target).sqrMagnitude > 0.01f)
        {
            rect.anchoredPosition = target;

            // Shift sections below General down (parent of General == sections root).
            // Use sibling order: everything AFTER General in the list sits below it.
            var general = (RectTransform)parent;
            var sectionsRoot = general.parent;
            if (sectionsRoot != null)
            {
                int genIndex = general.GetSiblingIndex();
                for (int i = genIndex + 1; i < sectionsRoot.childCount; i++)
                    if (sectionsRoot.GetChild(i) is RectTransform sec)
                        sec.anchoredPosition += new Vector2(0f, -sectionShift);
            }
            changed = true;
        }

        message = changed
            ? $"Fixed '{CloneName}': title='{(title != null ? title.text : "?")}', pos={rect.anchoredPosition}."
            : $"'{CloneName}' already fixed; no change.";
        return changed;
    }
}
