using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildScript
{
    const string LoadingScenePath = "Assets/MATE ENGINE - Scenes/Mate Engine Loading.unity";
    const string MainScenePath = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";

    [MenuItem("Build/Build Windows x64")]
    public static void BuildWindows()
    {
        PlayerSettings.usePlayerLog = false;
        EditorApplication.quitting -= CleanupEmptyLegacyDataPath;
        EditorApplication.quitting += CleanupEmptyLegacyDataPath;
        CleanupEmptyLegacyDataPath();

        int exitCode = 0;

        try
        {
            if (!BuildAddressablesContent())
            {
                exitCode = 1;
                return;
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { LoadingScenePath, MainScenePath },
                locationPathName = "Build/MateEngine.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                exitCode = 1;
            }
            else
            {
                CreateLaunchBatch();
            }
        }
        finally
        {
            CleanupEmptyLegacyDataPath();
        }

        if (exitCode != 0)
            EditorApplication.Exit(exitCode);
    }

    /// <summary>
    /// Creates a <c>MateEngine.bat</c> next to the built executable that launches
    /// the game with <c>-cache-path=../UserData/Cache/UnityCache</c>.
    ///
    /// Unlike <c>boot.config</c> keys, <c>-cache-path</c> IS read by Unity's C++
    /// engine at startup and redirects the entire native cache system (including
    /// <c>Caching.defaultCache</c>) to the portable path.  This prevents
    /// <c>LocalLow\Shinymoon\MateEngineX</c> from ever being created — on either
    /// startup or exit — without needing a background cleanup process.
    /// </summary>
    static void CreateLaunchBatch()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string batPath = Path.Combine(projectRoot, "Build", "MateEngine.bat");

        try
        {
            string content =
                "@echo off\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                "start \"\" \"MateEngine.exe\" -cache-path=../UserData/Cache/UnityCache %*\r\n";

            File.WriteAllText(batPath, content);
            Debug.Log($"[BuildScript] Created launch batch: {batPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[BuildScript] Failed to create launch batch: {e.Message}");
        }
    }

    static bool BuildAddressablesContent()
    {
        if (AddressableAssetSettingsDefaultObject.Settings == null)
        {
            UnityEngine.Debug.LogError("[BuildScript] AddressableAssetSettings not found.");
            return false;
        }

        // Addressables content is built outside BuildPipeline.BuildPlayer.
        // Rebuilding here prevents stale catalog data from reintroducing non-portable runtime behavior.
        AddressableAssetSettings.BuildPlayerContent(out var result);
        if (!string.IsNullOrEmpty(result.Error))
        {
            UnityEngine.Debug.LogError("[BuildScript] Addressables build failed: " + result.Error);
            return false;
        }

        return true;
    }

    static void CleanupEmptyLegacyDataPath()
    {
        PortablePaths.CleanupEmptyLegacyDataPath(PlayerSettings.companyName, PlayerSettings.productName);
    }
}
