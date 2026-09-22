using System;
using System.IO;
using UnityEngine;

public static class PortablePaths
{
    static string userDataRoot;
    static string legacyDataPath;

    public static string UserDataRoot { get { EnsureInitialized(); return userDataRoot; } }
    public static string LegacyDataPath { get { EnsureInitialized(); return legacyDataPath; } }

    public static string ThumbnailsDir { get { EnsureInitialized(); return Path.Combine(userDataRoot, "Thumbnails"); } }
    public static string ModsDir       { get { EnsureInitialized(); return Path.Combine(userDataRoot, "Mods"); } }
    public static string VRMDir        { get { EnsureInitialized(); return Path.Combine(userDataRoot, "VRM"); } }
    public static string BlendshapesDir{ get { EnsureInitialized(); return Path.Combine(userDataRoot, "Blendshapes"); } }
    public static string SyncDir       { get { EnsureInitialized(); return Path.Combine(userDataRoot, "Sync"); } }
    public static string CacheDir      { get { EnsureInitialized(); return Path.Combine(userDataRoot, "Cache"); } }
    public static string MECacheDir    { get { EnsureInitialized(); return Path.Combine(userDataRoot, "Cache", "ME_Cache"); } }
    public static string AIDir         { get { EnsureInitialized(); return Path.Combine(userDataRoot, "AI"); } }
    public static string LLMDir        { get { EnsureInitialized(); return Path.Combine(userDataRoot, "LLM"); } }
    public static string LLMModelsDir  { get { EnsureInitialized(); return Path.Combine(userDataRoot, "LLM", "models"); } }
    public static string ValueChangerDir { get { EnsureInitialized(); return Path.Combine(userDataRoot, "MEValueChanger"); } }
    public static string ScreenshotsDir{ get { EnsureInitialized(); return Path.Combine(userDataRoot, "Screenshots"); } }

    static bool initialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
    static void Initialize()
    {
        EnsureInitialized();
    }

    public static void EnsureInitialized()
    {
        if (initialized) return;
        initialized = true;

#if UNITY_EDITOR
        userDataRoot = GetEditorUserDataRoot();
        legacyDataPath = userDataRoot;
#else
        string exeDir = Path.GetDirectoryName(Application.dataPath);
        userDataRoot = Path.Combine(exeDir, "UserData");
        legacyDataPath = GetLegacyDataPath();

        string customDataDir = null;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--datadir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                customDataDir = SanitizeDataDirName(args[i + 1]);
        }

        if (!string.IsNullOrEmpty(customDataDir))
            userDataRoot = Path.Combine(userDataRoot, customDataDir);

        if (!TestWritePermission(userDataRoot))
        {
            Debug.LogError($"[PortablePaths] No write permission to '{userDataRoot}'. Runtime data will not be redirected to external user folders.");
        }
#endif

        EnsureDirectories();

#if !UNITY_EDITOR
        // Local bundles do not need Unity's cache. Native persistence paths are
        // redirected before UnityMain by Tools/PortableRuntime/PortableMain.cpp.
        PortableMigration.MigrateIfNeeded();
#endif

        Debug.Log($"[PortablePaths] UserDataRoot = {userDataRoot}");
    }

#if UNITY_EDITOR
    static string GetEditorUserDataRoot()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        if (string.IsNullOrEmpty(projectRoot))
            projectRoot = Directory.GetCurrentDirectory();

        return Path.Combine(projectRoot, "Library", "MateEngineUserData");
    }
#else
    static string GetLegacyDataPath()
    {
#if UNITY_STANDALONE_WIN
        return GetWindowsLegacyDataPath(Application.companyName, Application.productName);
#else
        return Application.persistentDataPath;
#endif
    }
#endif

    public static string GetWindowsLegacyDataPath(string companyName, string productName)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return string.Empty;
        if (string.IsNullOrEmpty(companyName) || string.IsNullOrEmpty(productName)) return string.Empty;

        return Path.Combine(
            userProfile,
            "AppData",
            "LocalLow",
            companyName,
            productName);
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

    static bool TestWritePermission(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".writetest");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(userDataRoot);
            Directory.CreateDirectory(ThumbnailsDir);
            Directory.CreateDirectory(ModsDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(AIDir);
            Directory.CreateDirectory(LLMDir);
            Directory.CreateDirectory(ScreenshotsDir);
        }
        catch (Exception e)
        {
            Debug.LogError($"[PortablePaths] Failed to create directories: {e.Message}");
        }
    }

}
