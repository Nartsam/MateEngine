using System;
using System.IO;
using UnityEngine;

public static class PortableMigration
{
    static readonly string MigratedMarker = ".migrated";

    public static void MigrateIfNeeded()
    {
        string markerPath = Path.Combine(PortablePaths.UserDataRoot, MigratedMarker);
        if (File.Exists(markerPath)) return;

        string legacy = PortablePaths.LegacyDataPath;
        if (PortablePaths.UserDataRoot == legacy) return;

        if (Directory.Exists(legacy))
        {
            Debug.Log($"[PortableMigration] Migrating data from '{legacy}' ...");
            MigrateLegacyData(legacy);
        }

        MigrateLLMStore();
        MigratePlayerPrefs();

        try
        {
            File.WriteAllText(markerPath, $"Migrated on {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Debug.Log("[PortableMigration] Migration complete.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[PortableMigration] Failed to write migration marker: {e.Message}");
        }
    }

    static void MigrateLegacyData(string legacy)
    {
        CopyFileIfExists(legacy, "settings.json", PortablePaths.UserDataRoot);
        CopyFileIfExists(legacy, "avatars.json", PortablePaths.UserDataRoot);
        CopyFileIfExists(legacy, "modded_settings.json", PortablePaths.UserDataRoot);
        CopyFileIfExists(legacy, "favorite_songs.json", PortablePaths.UserDataRoot);

        CopyDirectoryIfExists(Path.Combine(legacy, "Thumbnails"), PortablePaths.ThumbnailsDir);
        CopyDirectoryIfExists(Path.Combine(legacy, "Mods"), PortablePaths.ModsDir);
        CopyDirectoryIfExists(Path.Combine(legacy, "VRM"), PortablePaths.VRMDir);
        CopyDirectoryIfExists(Path.Combine(legacy, "Blendshapes"), PortablePaths.BlendshapesDir);
        CopyDirectoryIfExists(Path.Combine(legacy, "Sync"), PortablePaths.SyncDir);
        CopyDirectoryIfExists(Path.Combine(legacy, "MEValueChanger"), PortablePaths.ValueChangerDir);

        CopyFileIfExists(legacy, "ZomeAI_prompt.txt", PortablePaths.AIDir);
        foreach (var f in Directory.GetFiles(legacy, "*.json"))
        {
            string name = Path.GetFileName(f);
            if (name.StartsWith("ZomeAI", StringComparison.OrdinalIgnoreCase))
                CopyFileTo(f, Path.Combine(PortablePaths.AIDir, name));
        }
        foreach (var f in Directory.GetFiles(legacy, "*.cache"))
        {
            string name = Path.GetFileName(f);
            if (name.StartsWith("ZomeAI", StringComparison.OrdinalIgnoreCase))
                CopyFileTo(f, Path.Combine(PortablePaths.AIDir, name));
        }
    }

    static void MigrateLLMStore()
    {
        try
        {
            string oldLLM = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLMUnity");
            if (Directory.Exists(oldLLM))
            {
                CopyDirectoryIfExists(Path.Combine(oldLLM, "models"), PortablePaths.LLMModelsDir);
                Debug.Log("[PortableMigration] LLM models migrated.");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PortableMigration] LLM store migration skipped: {e.Message}");
        }
    }

    static void MigratePlayerPrefs()
    {
        try
        {
            if (PlayerPrefs.HasKey("FPSLimit") ||
                PlayerPrefs.HasKey("DebugMode") ||
                PlayerPrefs.HasKey("FullLlamaLib"))
            {
                var data = new LLMSettingsData
                {
                    debugMode = PlayerPrefs.GetInt("DebugMode", 0),
                    fullLlamaLib = PlayerPrefs.GetInt("FullLlamaLib", 0) == 1
                };
                string json = JsonUtility.ToJson(data, true);
                string path = Path.Combine(PortablePaths.LLMDir, "llm_settings.json");
                File.WriteAllText(path, json);
                Debug.Log("[PortableMigration] PlayerPrefs migrated to file.");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PortableMigration] PlayerPrefs migration skipped: {e.Message}");
        }
    }

    static void CopyFileIfExists(string srcDir, string fileName, string dstDir)
    {
        string src = Path.Combine(srcDir, fileName);
        if (File.Exists(src))
            CopyFileTo(src, Path.Combine(dstDir, fileName));
    }

    static void CopyFileTo(string src, string dst)
    {
        try
        {
            if (!File.Exists(dst))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, false);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PortableMigration] Failed to copy '{src}': {e.Message}");
        }
    }

    static void CopyDirectoryIfExists(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        try
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
            {
                string destFile = Path.Combine(dst, Path.GetFileName(file));
                if (!File.Exists(destFile))
                    File.Copy(file, destFile, false);
            }
            foreach (var dir in Directory.GetDirectories(src))
            {
                CopyDirectoryIfExists(dir, Path.Combine(dst, Path.GetFileName(dir)));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PortableMigration] Failed to copy directory '{src}': {e.Message}");
        }
    }
}

[Serializable]
public class LLMSettingsData
{
    public int debugMode;
    public bool fullLlamaLib;
}
