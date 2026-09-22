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

        if (!BuildAddressablesContent())
        {
            EditorApplication.Exit(1);
            return;
        }

        var options = new BuildPlayerOptions
        {
            scenes = new[] { LoadingScenePath, MainScenePath },
            locationPathName = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Build", "MateEngine.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        int exitCode = report.summary.result == BuildResult.Succeeded ? 0 : 1;
        if (exitCode == 0)
        {
            if (BuildPortableLauncher(options.locationPathName))
                CreateLaunchBatch();
            else
                exitCode = 1;
        }

        if (exitCode != 0)
            EditorApplication.Exit(exitCode);
    }

    // Convenience launcher only; the exe itself owns portable startup.
    static void CreateLaunchBatch()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string batPath = Path.Combine(projectRoot, "Build", "MateEngine.bat");

        try
        {
            string content =
                "@echo off\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                "start \"\" \"MateEngine.exe\" %*\r\n";

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

    static bool BuildPortableLauncher(string playerPath)
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string script = Path.Combine(projectRoot, "Tools", "PortableRuntime", "build_launcher.ps1");
        string player = Path.GetFullPath(playerPath);
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -PlayerPath \"{player}\"",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                Debug.Log(output.Result);
                if (process.ExitCode == 0) return true;
                Debug.LogError("[BuildScript] Portable entry point build failed: " + error.Result);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[BuildScript] Portable entry point build failed: " + e.Message);
        }

        // Do not leave a launchable, non-portable Unity bootstrap after failure.
        if (File.Exists(player)) File.Delete(player);
        return false;
    }
}
