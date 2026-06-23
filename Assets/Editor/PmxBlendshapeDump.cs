using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-off editor helper: loads a built `.me` AssetBundle and dumps every
/// SkinnedMeshRenderer blendshape (morph) name to Build/blendshape_names.txt.
/// Used to get ground-truth morph names for accurate JP→CN localization and for
/// baking exactly the glyphs the Blendshapes menu will display.
///
///   -executeMethod PmxBlendshapeDump.DumpRita
/// </summary>
public static class PmxBlendshapeDump
{
    public static void DumpRita() => Dump("Build/PmxModels/丽塔_m4.me", "Build/blendshape_names.txt");

    public static void Dump(string mePath, string outPath)
    {
        if (!File.Exists(mePath))
        {
            Debug.LogError($"[PmxBlendshapeDump] .me not found: {mePath}");
            return;
        }

        var bundle = AssetBundle.LoadFromFile(mePath);
        if (bundle == null)
        {
            Debug.LogError($"[PmxBlendshapeDump] Failed to load bundle: {mePath}");
            return;
        }

        var sb = new StringBuilder();
        int total = 0;
        try
        {
            var prefab = bundle.LoadAllAssets<GameObject>().FirstOrDefault();
            if (prefab == null)
            {
                Debug.LogError("[PmxBlendshapeDump] No GameObject in bundle.");
                return;
            }

            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                int n = smr.sharedMesh.blendShapeCount;
                for (int i = 0; i < n; i++)
                {
                    sb.AppendLine(smr.sharedMesh.GetBlendShapeName(i));
                    total++;
                }
            }
        }
        finally
        {
            bundle.Unload(true);
        }

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Debug.Log($"[PmxBlendshapeDump] Dumped {total} blendshape names to {outPath}");
    }
}
