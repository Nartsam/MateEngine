using System;
using System.IO;
using System.Text;
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

#if !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void PostSceneLoadCleanup()
    {
        CleanupEmptyLegacyDataPath();
    }
#endif

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
        ConfigureUnityCache();

        Application.quitting += OnApplicationQuitting;
        PortableMigration.MigrateIfNeeded();
        CleanupEmptyLegacyDataPath();
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

#if !UNITY_EDITOR
    static void OnApplicationQuitting()
    {
        // Run inline cleanup first — catches directories that are already empty.
        CleanupEmptyLegacyDataPath();

        // Schedule a detached background process that waits for THIS process
        // to exit, then removes any empty directory Unity's native shutdown
        // may recreate after all C# hooks have returned.
        SchedulePostExitCleanup();
    }

    /// <summary>
    /// Spawns a detached PowerShell process that monitors our PID.
    /// After we exit and Unity's C++ shutdown recreates the empty
    /// <c>LocalLow\Shinymoon\MateEngineX</c> directory, this process
    /// deletes it (and its empty parent directory) for a clean portable exit.
    /// </summary>
    static void SchedulePostExitCleanup()
    {
#if UNITY_STANDALONE_WIN
        try
        {
            string legacy = GetWindowsLegacyDataPath(Application.companyName, Application.productName);
            if (string.IsNullOrEmpty(legacy)) return;

            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;

            // Build a one-liner PowerShell script and base64-encode it
            // to avoid shell-escaping issues with quotes, backslashes, etc.
            string psScript =
                $"$d='{legacy.Replace("'", "''")}';" +
                $"try{{$p=Get-Process -Id {pid} -ErrorAction SilentlyContinue;if($p){{$exited=$p.WaitForExit(30000)}}else{{$exited=$true}}}}catch{{$exited=$true}};" +
                "if($exited){" +
                  "Start-Sleep -Milliseconds 500;" +
                  "if(Test-Path $d){" +
                    "$empty=$true;" +
                    "try{$c=@(Get-ChildItem $d -Force -ErrorAction SilentlyContinue).Count;if($c -gt 0){$empty=$false}}catch{};" +
                    "if($empty){" +
                      "Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue;" +
                      "$parent=Split-Path $d -Parent;" +
                      "if($parent -and (Test-Path $parent)){" +
                        "$pEmpty=$true;" +
                        "try{$pc=@(Get-ChildItem $parent -Force -ErrorAction SilentlyContinue).Count;if($pc -gt 0){$pEmpty=$false}}catch{};" +
                        "if($pEmpty){Remove-Item $parent -Force -ErrorAction SilentlyContinue}" +
                      "}" +
                    "}" +
                  "}" +
                "}";

            string base64Script = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psScript));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -EncodedCommand {base64Script}",
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psi);

            Debug.Log($"[PortablePaths] Scheduled post-exit cleanup for: {legacy}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PortablePaths] Failed to schedule post-exit cleanup: {e.Message}");
        }
#endif
    }
#endif

    public static void CleanupEmptyLegacyDataPath()
    {
        CleanupEmptyLegacyDataPath(Application.companyName, Application.productName);
    }

    public static void CleanupEmptyLegacyDataPath(string companyName, string productName)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        string legacy = GetWindowsLegacyDataPath(companyName, productName);
        if (string.IsNullOrEmpty(legacy)) return;

        DeleteDirectoryIfEmpty(legacy);
        DeleteDirectoryIfEmpty(Path.GetDirectoryName(legacy));
#endif
    }

    static void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            foreach (string _ in Directory.EnumerateFileSystemEntries(path))
                return;

            Directory.Delete(path, false);
            Debug.Log($"[PortablePaths] Removed empty legacy directory: {path}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PortablePaths] Failed to remove empty legacy directory '{path}': {e.Message}");
        }
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

#if !UNITY_EDITOR
    static void ConfigureUnityCache()
    {
        try
        {
            string cachePath = Path.Combine(userDataRoot, "Cache", "UnityCache");
            Directory.CreateDirectory(cachePath);

            var existingCache = Caching.GetCacheByPath(cachePath);
            if (!existingCache.valid)
            {
                Cache newCache = Caching.AddCache(cachePath);
                if (newCache.valid)
                    Caching.currentCacheForWriting = newCache;
            }
            else
            {
                Caching.currentCacheForWriting = existingCache;
            }

            Debug.Log($"[PortablePaths] Unity Cache configured at: {cachePath}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PortablePaths] Failed to configure Unity Cache: {e.Message}");
        }
    }
#endif
}
