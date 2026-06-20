// Serializable render-preset DTO used by the Blender dump script and Unity
// material mapper. It deliberately stores only portable values, never local paths.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    [Serializable]
    public class PmxRenderPreset
    {
        public int formatVersion = 1;
        public string sourceBlendName;
        public PmxPresetScene scene = new();
        public List<PmxPresetMaterial> materials = new();

        [NonSerialized] private Dictionary<string, PmxPresetMaterial> _byExact;
        [NonSerialized] private Dictionary<string, PmxPresetMaterial> _byTrimmed;

        public static PmxRenderPreset Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var preset = JsonUtility.FromJson<PmxRenderPreset>(File.ReadAllText(path));
            preset?.BuildLookup();
            return preset;
        }

        public PmxPresetMaterial FindMaterial(string materialName, PmxMaterialMatchMode mode)
        {
            BuildLookup();
            string exact = NormalizeName(materialName);
            string trimmed = TrimSuffix(exact);

            if (mode == PmxMaterialMatchMode.Exact)
                return _byExact.TryGetValue(exact, out var m) ? m : null;

            if (_byTrimmed.TryGetValue(trimmed, out var byTrim))
                return byTrim;

            if (_byExact.TryGetValue(exact, out var byExact))
                return byExact;

            if (mode != PmxMaterialMatchMode.Fuzzy || materials == null || materials.Count == 0)
                return null;

            float bestScore = 0f;
            PmxPresetMaterial best = null;
            foreach (var material in materials)
            {
                float score = Similarity(trimmed, TrimSuffix(NormalizeName(material.name)));
                if (score > bestScore) { bestScore = score; best = material; }
            }
            return bestScore >= 0.55f ? best : null;
        }

        private void BuildLookup()
        {
            if (_byExact != null) return;
            _byExact = new Dictionary<string, PmxPresetMaterial>();
            _byTrimmed = new Dictionary<string, PmxPresetMaterial>();
            if (materials == null) return;

            foreach (var material in materials)
            {
                if (material == null || string.IsNullOrWhiteSpace(material.name)) continue;
                string exact = NormalizeName(material.name);
                string trimmed = string.IsNullOrWhiteSpace(material.trimmedName)
                    ? TrimSuffix(exact) : NormalizeName(material.trimmedName);
                if (!_byExact.ContainsKey(exact)) _byExact.Add(exact, material);
                if (!_byTrimmed.ContainsKey(trimmed)) _byTrimmed.Add(trimmed, material);
            }
        }

        public static Color ColorOr(float[] values, Color fallback)
        {
            if (values == null || values.Length < 3) return fallback;
            float a = values.Length >= 4 ? values[3] : fallback.a;
            return new Color(values[0], values[1], values[2], a);
        }

        public static string NormalizeName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Replace('\\', '/');
        }

        public static string TrimSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            int dot = value.LastIndexOf('.');
            if (dot > 0 && dot < value.Length - 1)
            {
                string tail = value.Substring(dot + 1);
                if (int.TryParse(tail, out _)) return value.Substring(0, dot);
            }
            return value;
        }

        private static float Similarity(string a, string b)
        {
            if (a == b) return 1f;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;
            int distance = Levenshtein(a, b);
            return 1f - (float)distance / Mathf.Max(a.Length, b.Length);
        }

        private static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Mathf.Min(Mathf.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }
    }

    [Serializable]
    public class PmxPresetScene
    {
        public bool bloomEnabled;
        public float bloomIntensity;
        public float[] bloomColor;
        public string viewTransform;
        public string look;
        public bool hasFaceLocator;
        public string faceLocatorName;
        public float[] faceLocatorPosition;
        public float[] faceLocatorRotation;
        public float[] faceLocatorScale;

        public bool HasPostProcess =>
            bloomEnabled || !string.IsNullOrWhiteSpace(viewTransform) || !string.IsNullOrWhiteSpace(look);
    }

    [Serializable]
    public class PmxPresetMaterial
    {
        public string name;
        public string trimmedName;
        public string baseTexture;
        public string shadeTexture;
        public string matcapTexture;
        public string emissionTexture;
        public string highColorTexture;
        public string highColorMaskTexture;
        public string maskTexture;
        public string toonTexture;
        public List<string> images;
        public float[] baseColor;
        public float[] shadeColor;
        public float[] shadeColor2;
        public float[] highColor;
        public float[] rimColor;
        public float[] emissionColor;
        public float[] outlineColor;
        public float alpha = 1f;
        public bool alphaClip;
        public bool alphaBlend;
        public bool useMatcap;
        public bool useHighColor;
        public bool useProceduralSparkle;
        public bool useRim;
        public bool useEmission;
        public bool useOutline;
        public float matcapStrength;
        public float highColorPower;
        public float rimPower;
        public float emissionStrength;
        public float outlineWidth;
    }
}
