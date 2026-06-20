// Shared option/config model for the PMX offline pipeline. External paths are
// injected through CLI args or ignored local settings; pipeline code must not
// bake machine-specific locations into source.
using System;
using System.IO;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public enum PmxMaterialMatchMode { Exact, TrimSuffix, Fuzzy }

    [Serializable]
    public class PmxPipelineLocalSettings
    {
        public string pmxPath;
        public string presetPath;
        public string blenderPath;
        public string outputRoot;
        public string tempRoot;
        public string modelName;
        public string prefabPath;
        public string exportPath;
        public string logsRoot;
        public string materialMatchMode;
        public string stylePath;
    }

    public sealed class PmxPipelineOptions
    {
        public const string DefaultOutputRoot = "Assets/PmxImported";
        public const string DefaultLogsRoot = "Logs";
        public const string DefaultToolsRoot = "Tools/PmxPipeline";
        public const string DefaultStylesRoot = "Tools/PmxPipeline/styles";
        public const string DefaultBuildRoot = "Build/PmxModels";
        public const string DefaultTempRoot = "Temp/PmxPipelineBundle";

        private const string ConfigPath = "Library/MateEngineUserData/PmxPipeline/settings.json";

        public string PmxPath;
        public string PresetPath;
        public string BlenderPath;
        public string OutputRoot = DefaultOutputRoot;
        public string TempRoot = DefaultTempRoot;
        public string ModelName;
        public string PrefabPath;
        public string OutPath;
        public string LogsRoot = DefaultLogsRoot;
        public PmxMaterialMatchMode MatchMode = PmxMaterialMatchMode.TrimSuffix;
        public float Scale = PmxMeshBuilder.DefaultScale;

        // Explicit style-config path (CLI/local settings); empty => resolve by model name.
        public string StylePath;
        // Loaded per-model style overrides (null when none found). Set by ResolveAndLoadStyle.
        public PmxStyleConfig Style;

        public bool HasPreset => !string.IsNullOrWhiteSpace(PresetPath);

        public static string ProjectRoot => Directory.GetCurrentDirectory();

        public static PmxPipelineOptions FromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            var local = LoadLocalSettings();
            var options = new PmxPipelineOptions
            {
                PmxPath = FirstNonEmpty(GetArg(args, "-pmx"), local.pmxPath),
                PresetPath = FirstNonEmpty(GetArg(args, "-preset"), local.presetPath),
                BlenderPath = FirstNonEmpty(GetArg(args, "-blender"), local.blenderPath),
                OutputRoot = FirstNonEmpty(GetArg(args, "-outputRoot"), local.outputRoot, DefaultOutputRoot),
                TempRoot = FirstNonEmpty(GetArg(args, "-tempRoot"), local.tempRoot, DefaultTempRoot),
                ModelName = FirstNonEmpty(GetArg(args, "-modelName"), local.modelName),
                PrefabPath = FirstNonEmpty(GetArg(args, "-prefab"), local.prefabPath),
                OutPath = FirstNonEmpty(GetArg(args, "-out"), local.exportPath),
                LogsRoot = FirstNonEmpty(GetArg(args, "-logsRoot"), local.logsRoot, DefaultLogsRoot),
                MatchMode = ParseMatchMode(FirstNonEmpty(GetArg(args, "-matchMode"), local.materialMatchMode)),
                StylePath = FirstNonEmpty(GetArg(args, "-style"), local.stylePath),
                Scale = float.TryParse(GetArg(args, "-scale"), out var s) ? s : PmxMeshBuilder.DefaultScale
            };

            options.Normalize();
            return options;
        }

        public string RequirePmxPath()
        {
            if (string.IsNullOrWhiteSpace(PmxPath))
                throw new InvalidOperationException("PMX path is required. Pass -pmx <path> or set Library/MateEngineUserData/PmxPipeline/settings.json.");
            if (!File.Exists(PmxPath))
                throw new FileNotFoundException("PMX file not found.", PmxPath);
            return PmxPath;
        }

        public void ValidatePresetInputs()
        {
            if (!HasPreset) return;
            if (!File.Exists(PresetPath))
                throw new FileNotFoundException("Render preset .blend file not found.", PresetPath);
            if (string.IsNullOrWhiteSpace(BlenderPath))
                throw new InvalidOperationException("Blender path is required when -preset is used. Pass -blender <path> or set local settings.");
            if (!File.Exists(BlenderPath))
                throw new FileNotFoundException("Blender executable not found.", BlenderPath);
        }

        public string ResolveModelName(PmxModel model, string pmxPath)
        {
            if (!string.IsNullOrWhiteSpace(ModelName))
                return SanitizeName(ModelName);

            string name = SanitizeName(string.IsNullOrEmpty(model.NameUniversal) ? model.NameLocal : model.NameUniversal);
            if (!string.IsNullOrEmpty(name))
                return name;

            return SanitizeName(Path.GetFileNameWithoutExtension(pmxPath));
        }

        // Locate and load the per-model style config. Explicit -style path wins; otherwise
        // look for Tools/PmxPipeline/styles/<modelName>.style.json. Returns the path tried
        // (whether or not it existed); Style is null when no config was found.
        public string ResolveAndLoadStyle(string modelName)
        {
            string path = !string.IsNullOrWhiteSpace(StylePath)
                ? NormalizeFilePath(StylePath)
                : Path.Combine(NormalizeFilePath(DefaultStylesRoot), SanitizeName(modelName) + ".style.json");
            Style = PmxStyleConfig.Load(path);
            return path;
        }

        public string ResolveDefaultOutPath(string prefabPath)
        {
            // Only honor -out (or the stored exportPath) for .me here; a stored .vrm path must not
            // hijack the .me output. Otherwise fall back to the project default Build root.
            if (!string.IsNullOrWhiteSpace(OutPath) && HasExtension(OutPath, ".me"))
                return NormalizeFilePath(OutPath);

            string name = Path.GetFileNameWithoutExtension(prefabPath);
            string dir = NormalizeFilePath(DefaultBuildRoot);
            return Path.Combine(dir, name + ".me");
        }

        private static bool HasExtension(string path, string ext)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return Path.GetExtension(path).ToLowerInvariant() == ext;
        }

        public string ResolveRenderPresetJsonPath()
        {
            string logs = NormalizeProjectDirectory(LogsRoot);
            return Path.Combine(logs, "pmx_render_preset.json");
        }

        public string ResolveBlenderDumpScriptPath()
        {
            return Path.Combine(NormalizeProjectDirectory(DefaultToolsRoot), "dump_render_preset.py");
        }

        public string ResolveTempRoot()
        {
            string temp = NormalizeFilePath(TempRoot);
            EnsureUnderProject(temp, "tempRoot");
            EnsureSafeTempPath(temp);
            Directory.CreateDirectory(temp);
            return temp;
        }

        public void SaveLastBuild(string prefabPath, string exportPath = null)
        {
            var settings = LoadLocalSettings();
            settings.pmxPath = PmxPath;
            settings.presetPath = PresetPath;
            settings.blenderPath = BlenderPath;
            settings.outputRoot = OutputRoot;
            settings.tempRoot = TempRoot;
            settings.modelName = ModelName;
            settings.prefabPath = prefabPath;
            settings.exportPath = string.IsNullOrWhiteSpace(exportPath) ? OutPath : exportPath;
            settings.logsRoot = LogsRoot;
            settings.materialMatchMode = MatchMode.ToString();
            settings.stylePath = StylePath;

            string path = NormalizeFilePath(ConfigPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(settings, true), new System.Text.UTF8Encoding(false));
        }

        private void Normalize()
        {
            PmxPath = NormalizeExistingCandidate(PmxPath);
            PresetPath = NormalizeExistingCandidate(PresetPath);
            BlenderPath = NormalizeExistingCandidate(BlenderPath);
            PrefabPath = NormalizeAssetPathCandidate(PrefabPath);
            OutputRoot = NormalizeAssetFolder(OutputRoot);
            TempRoot = NormalizeProjectRelativeOrAbsolute(TempRoot, DefaultTempRoot);
            LogsRoot = NormalizeProjectRelativeOrAbsolute(LogsRoot, DefaultLogsRoot);
            OutPath = NormalizeExistingCandidate(OutPath);
        }

        private static PmxPipelineLocalSettings LoadLocalSettings()
        {
            string path = NormalizeFilePath(ConfigPath);
            if (!File.Exists(path)) return new PmxPipelineLocalSettings();

            try
            {
                return JsonUtility.FromJson<PmxPipelineLocalSettings>(File.ReadAllText(path)) ?? new PmxPipelineLocalSettings();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MEPmxPipeline] Ignoring invalid local settings '{path}': {e.Message}");
                return new PmxPipelineLocalSettings();
            }
        }

        private static string GetArg(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim().Trim('"');
            return null;
        }

        private static PmxMaterialMatchMode ParseMatchMode(string value)
        {
            return Enum.TryParse(value, true, out PmxMaterialMatchMode mode)
                ? mode : PmxMaterialMatchMode.TrimSuffix;
        }

        private static string NormalizeExistingCandidate(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : NormalizeFilePath(path);
        }

        private static string NormalizeProjectRelativeOrAbsolute(string path, string fallback)
        {
            return string.IsNullOrWhiteSpace(path) ? fallback : path.Replace('\\', '/');
        }

        private static string NormalizeAssetPathCandidate(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : ToProjectRelativeAssetPath(path);
        }

        private static string NormalizeAssetFolder(string path)
        {
            string assetPath = ToProjectRelativeAssetPath(string.IsNullOrWhiteSpace(path) ? DefaultOutputRoot : path);
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) && assetPath != "Assets")
                throw new InvalidOperationException($"outputRoot must be inside Assets: {path}");
            return assetPath.TrimEnd('/');
        }

        public static string ToProjectRelativeAssetPath(string path)
        {
            string normalized = path.Replace('\\', '/').Trim();
            if (normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized == "Assets")
                return normalized;

            string full = NormalizeFilePath(path).Replace('\\', '/');
            string root = NormalizeFilePath(ProjectRoot).Replace('\\', '/').TrimEnd('/') + "/";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Asset path must be inside the project: {path}");
            return full.Substring(root.Length);
        }

        public static string NormalizeFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string trimmed = path.Trim().Trim('"');
            return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(ProjectRoot, trimmed));
        }

        public static string NormalizeProjectDirectory(string path)
        {
            string full = NormalizeFilePath(path);
            Directory.CreateDirectory(full);
            return full;
        }

        private static void EnsureUnderProject(string fullPath, string label)
        {
            string root = NormalizeFilePath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{label} must stay inside the project workspace: {fullPath}");
        }

        private static void EnsureSafeTempPath(string fullPath)
        {
            string root = NormalizeFilePath(ProjectRoot).Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(fullPath).Replace('\\', '/').TrimEnd('/');
            string rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
            if (rel == "Temp" || rel == "Library" || rel == "Assets" || rel == ".")
                throw new InvalidOperationException($"tempRoot is too broad for recursive cleanup: {fullPath}");
            if (!rel.StartsWith("Temp/", StringComparison.Ordinal)
                && !rel.StartsWith("Library/MateEngineUserData/PmxPipeline/Temp/", StringComparison.Ordinal))
                throw new InvalidOperationException($"tempRoot must be under Temp/ or Library/MateEngineUserData/PmxPipeline/Temp/: {fullPath}");
        }

        private static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}
