// Per-model styling config for the PMX pipeline. The point of this file is to move
// model-specific look tuning OUT of C# and into data, so adjusting a model's colors
// no longer requires editing/recompiling the pipeline. Knobs here are deliberately
// shader-AGNOSTIC (base tint, warmth, brightness, outline, rim) so they survive a
// future switch of the material stage (e.g. a HoYo toon shader) instead of locking us
// to UTS2 internals. See Docs/DECISIONS_RECORD.md ADR-0008.
//
// Lookup order (first hit wins), resolved in PmxPipelineOptions:
//   1. -style <path> CLI arg / local settings
//   2. Tools/PmxPipeline/styles/<modelName>.style.json  (in-repo, versioned)
//   3. none -> code defaults
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    [Serializable]
    public sealed class PmxStyleConfig
    {
        public int formatVersion = 1;

        // Material stage selector. "uts2" is the current built-in mapping; reserved for a
        // future "hoyo" profile that consumes LightMap/MetalMap/face-SDF directly.
        public string materialProfile = "uts2";

        public PmxStyleGlobals global = new();
        public List<PmxStyleMaterial> materials = new();

        [NonSerialized] private Dictionary<string, PmxStyleMaterial> _byName;

        public static PmxStyleConfig Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var cfg = JsonUtility.FromJson<PmxStyleConfig>(File.ReadAllText(path));
                cfg?.BuildLookup();
                return cfg;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PmxStyle] Ignoring invalid style config '{path}': {e.Message}");
                return null;
            }
        }

        public PmxStyleMaterial FindMaterial(string materialName)
        {
            BuildLookup();
            if (string.IsNullOrEmpty(materialName)) return null;
            return _byName.TryGetValue(materialName.Trim(), out var m) ? m : null;
        }

        private void BuildLookup()
        {
            if (_byName != null) return;
            _byName = new Dictionary<string, PmxStyleMaterial>();
            if (materials == null) return;
            foreach (var m in materials)
                if (m != null && !string.IsNullOrWhiteSpace(m.name) && !_byName.ContainsKey(m.name.Trim()))
                    _byName.Add(m.name.Trim(), m);
        }

        // "#RRGGBB"/"#RRGGBBAA" -> Color; returns false for empty/invalid.
        public static bool TryParseHexColor(string hex, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.Trim().TrimStart('#');
            if (s.Length != 6 && s.Length != 8) return false;
            if (!int.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r)) return false;
            if (!int.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g)) return false;
            if (!int.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b)) return false;
            int a = 255;
            if (s.Length == 8 && !int.TryParse(s.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
                return false;
            color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            return true;
        }
    }

    [Serializable]
    public sealed class PmxStyleGlobals
    {
        // Skin hue bias: -1 cool .. 0 neutral .. +1 warm. Fixes "reddish" vs "washed-out".
        // Neutral by default so "no config" and "empty config" behave identically.
        public float skinWarmth = 0f;
        // Uniform brightness multiplier on the textured base tint (hue-neutral).
        public float brightness = 1f;
        // Outline width multiplier (0 disables outlines).
        public float outlineScale = 1f;
        // Rim strength multiplier (0 disables rim).
        public float rimStrength = 1f;
    }

    [Serializable]
    public sealed class PmxStyleMaterial
    {
        public string name;
        // Optional base tint applied to this material's albedo ("#RRGGBB", "" = unchanged).
        public string baseColor = "";
        // Optional shade color override ("#RRGGBB", "" = unchanged).
        public string shadeColor = "";
        // Outline override: -1 = leave to profile default, 0 = force off, 1 = force on.
        public int outline = -1;
    }
}
