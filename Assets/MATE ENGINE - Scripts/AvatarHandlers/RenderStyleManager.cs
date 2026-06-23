using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime, switchable render-style system.
///
/// Approach (Route A): a full-screen color grade (saturation / contrast /
/// brightness) applied via a camera image effect (<see cref="RenderStyleImageEffect"/>
/// + Hidden/RenderStyleGrade) that PRESERVES the source alpha. This gives a real
/// global vibrancy boost — the thing UTS2 materials can't do — without breaking
/// MateEngine's per-pixel-alpha transparent window.
///
/// Why not material params or Built-in post-processing:
///  - The baked UTS2 materials set _Use_BaseAs1st/_Use_1stAs2nd=1, so runtime
///    shade-color tweaks are ignored, and UTS2 has no saturation knob → material
///    overrides produced no visible change.
///  - Built-in PostProcessLayer (Bloom/ColorGrading) does not preserve alpha and
///    paints the transparent window gray.
/// See DECISIONS_RECORD ADR-0010.
///
/// Self-bootstraps via RuntimeInitializeOnLoadMethod so the scene needs no edit.
/// </summary>
[DisallowMultipleComponent]
public class RenderStyleManager : MonoBehaviour
{
    public static RenderStyleManager Instance { get; private set; }

    public const string DefaultCode = "default";
    private const string GradeShaderResource = "RenderStyleGrade"; // Resources/RenderStyleGrade.shader

    // The full-screen grade is PAUSED: it runs on the single main camera, which
    // also renders the Screen-Space-Camera menu canvases, so it tinted the menu
    // too (user feedback). Making it model-only needs a dedicated layer-10 grade
    // camera (camera surgery) or a per-material re-bake (Route B). Until that is
    // chosen, keep the effect off so the menu is never tinted. See ADR-0010.
    private const bool GradeEnabled = false;

    [System.Serializable]
    public class RenderStyle
    {
        public string code;
        public string displayName;

        // Full-screen grade. All 1 / "default" code = passthrough (no grade).
        public float saturation = 1f;
        public float contrast = 1f;
        public float brightness = 1f;
    }

    private readonly List<RenderStyle> styles = new();
    private string currentCode = DefaultCode;

    private Material gradeMaterial;
    private RenderStyleImageEffect effect;

    public IReadOnlyList<RenderStyle> Styles => styles;
    public string CurrentCode => currentCode;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap() => EnsureInstance();

    /// <summary>Guarantees a manager exists regardless of script init order.</summary>
    public static RenderStyleManager EnsureInstance()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("RenderStyleManager");
        return go.AddComponent<RenderStyleManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        BuildStyles();
    }

    private void Start()
    {
        string code = SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data.renderStyle : DefaultCode;
        SetStyle(code, save: false); // reflect persisted choice without re-saving
    }

    // ------------------------------------------------------------------ styles

    private void BuildStyles()
    {
        styles.Clear();

        styles.Add(new RenderStyle { code = DefaultCode, displayName = "默认" }); // passthrough

        // Contrast kept at 1.0 — a >1 contrast pivots around 0.5 and crushed darks
        // to black (user feedback). Vibrancy comes from saturation + a gentle
        // brightness lift instead.
        styles.Add(new RenderStyle
        {
            code = "vivid",
            displayName = "鲜艳",
            saturation = 1.50f,
            contrast = 1.00f,
            brightness = 1.06f,
        });

        styles.Add(new RenderStyle
        {
            code = "soft",
            displayName = "柔和",
            saturation = 1.20f,
            contrast = 1.00f,
            brightness = 1.04f,
        });
    }

    public RenderStyle GetStyle(string code)
    {
        if (string.IsNullOrEmpty(code)) code = DefaultCode;
        foreach (var s in styles)
            if (s.code == code) return s;
        return styles.Count > 0 ? styles[0] : null;
    }

    public int IndexOf(string code)
    {
        if (string.IsNullOrEmpty(code)) code = DefaultCode;
        for (int i = 0; i < styles.Count; i++)
            if (styles[i].code == code) return i;
        return 0;
    }

    // -------------------------------------------------------------- public API

    /// <summary>Switch the active render style: updates the camera grade and persists.</summary>
    public void SetStyle(string code, bool save = true)
    {
        if (string.IsNullOrEmpty(code)) code = DefaultCode;
        var style = GetStyle(code);
        currentCode = style != null ? style.code : DefaultCode;

        ApplyStyle(style);

        if (save && SaveLoadHandler.Instance != null)
        {
            SaveLoadHandler.Instance.data.renderStyle = currentCode;
            SaveLoadHandler.Instance.SaveToDisk();
        }
    }

    /// <summary>Called after a model loads (from VRMLoader). The grade is global
    /// (camera), so this just re-asserts the effect is set up and active.</summary>
    public void ApplyToModel(GameObject model)
    {
        ApplyStyle(GetStyle(currentCode));
    }

    // ----------------------------------------------------------- camera effect

    private void EnsureEffect()
    {
        var cam = Camera.main;
        if (cam == null) cam = FindFirstObjectByType<Camera>();
        if (cam == null) return;

        if (gradeMaterial == null)
        {
            var shader = Resources.Load<Shader>(GradeShaderResource);
            if (shader == null)
            {
                Debug.LogError($"[RenderStyleManager] Grade shader '{GradeShaderResource}' not found in Resources.");
                return;
            }
            gradeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        if (effect == null)
            effect = cam.GetComponent<RenderStyleImageEffect>() ?? cam.gameObject.AddComponent<RenderStyleImageEffect>();
        effect.material = gradeMaterial;
    }

    private void ApplyStyle(RenderStyle style)
    {
        EnsureEffect();
        if (effect == null || gradeMaterial == null) return;

        if (!GradeEnabled || style == null || style.code == DefaultCode)
        {
            effect.active = false; // passthrough = identical to no effect
            return;
        }

        gradeMaterial.SetFloat("_Saturation", style.saturation);
        gradeMaterial.SetFloat("_Contrast", style.contrast);
        gradeMaterial.SetFloat("_Brightness", style.brightness);
        effect.active = true;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            if (effect != null) effect.active = false;
            if (gradeMaterial != null) Destroy(gradeMaterial);
            Instance = null;
        }
    }
}
