using System;
using System.IO;

namespace UnityEngine.AddressableAssets
{
    internal static class AddressablesPortablePaths
    {
        static string cacheDataFolder;

        public static string CacheDataFolder
        {
            get
            {
                if (cacheDataFolder == null)
                    cacheDataFolder = GetCacheDataFolder();
                return cacheDataFolder;
            }
        }

        public static bool HasCacheDataFolder
        {
            get { return !string.IsNullOrEmpty(CacheDataFolder); }
        }

        static string GetCacheDataFolder()
        {
            string root = GetUserDataRoot();
            if (string.IsNullOrEmpty(root))
                return string.Empty;

            return NormalizePath(Path.Combine(root, "Cache"));
        }

        static string GetUserDataRoot()
        {
#if UNITY_EDITOR
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot))
                projectRoot = Directory.GetCurrentDirectory();

            return Path.Combine(projectRoot, "Library", "MateEngineUserData");
#else
            string exeDir = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(exeDir))
                return string.Empty;

            string root = Path.Combine(exeDir, "UserData");
            string customDataDir = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--datadir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    customDataDir = SanitizeDataDirName(args[i + 1]);
            }

            if (!string.IsNullOrEmpty(customDataDir))
                root = Path.Combine(root, customDataDir);

            return root;
#endif
        }

        static string SanitizeDataDirName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string name = value.Trim().Trim('"');
            name = name.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            while (name.Contains(".."))
                name = name.Replace("..", "_");

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }
    }
}
