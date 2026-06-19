// Batch entry points for the MateEngine PMX offline import pipeline.
// See Docs/DECISIONS_RECORD.md ADR-0008.
//
// M1a (this file): parse a PMX and report its structure, to validate the parser
// headless. Later milestones add mesh/Humanoid/physics/material/.me steps.
//
// Usage (headless):
//   Unity.exe -batchmode -quit -projectPath <proj> \
//       -executeMethod MateEngine.PmxPipeline.MEPmxPipeline.ValidateParse \
//       -pmx "<path-to>.pmx"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public static class MEPmxPipeline
    {
        // Reads a CLI arg of the form "-key value"; returns null if absent.
        private static string GetArg(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return null;
        }

        [MenuItem("MateEngine/PMX Pipeline/Validate Parse (test model)")]
        public static void ValidateParseMenu()
        {
            const string testPmx = @"D:\Program Files\MMD\MateEngine\models\特工丽塔\删披风删骨骼.pmx";
            Report(testPmx);
        }

        // Batch-callable: -executeMethod ...ValidateParse  (reads -pmx arg, falls back to test model)
        public static void ValidateParse()
        {
            string path = GetArg("-pmx") ?? @"D:\Program Files\MMD\MateEngine\models\特工丽塔\删披风删骨骼.pmx";
            bool ok = Report(path);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        [MenuItem("MateEngine/PMX Pipeline/Build Model (test model)")]
        public static void BuildModelMenu()
        {
            const string testPmx = @"D:\Program Files\MMD\MateEngine\models\特工丽塔\删披风删骨骼.pmx";
            BuildInternal(testPmx, PmxMeshBuilder.DefaultScale);
        }

        // Batch-callable: -executeMethod ...BuildModel  (args: -pmx <path> [-scale <f>])
        public static void BuildModel()
        {
            string path = GetArg("-pmx") ?? @"D:\Program Files\MMD\MateEngine\models\特工丽塔\删披风删骨骼.pmx";
            float scale = float.TryParse(GetArg("-scale"), out var s) ? s : PmxMeshBuilder.DefaultScale;
            bool ok = BuildInternal(path, scale);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool BuildInternal(string pmxPath, float scale)
        {
            try
            {
                var model = new PmxReader().ReadFile(pmxPath);
                var result = PmxMeshBuilder.Build(model, pmxPath, scale, "Assets/PmxImported");
                Debug.Log($"[MEPmxPipeline] Built '{result.Prefab.name}' -> {result.OutputFolder}/{result.Prefab.name}.prefab\n" +
                          $"  bones={result.BoneCount} submeshes={result.SubMeshCount} blendshapes={result.BlendShapeCount}\n" +
                          $"  humanoid: valid={result.AvatarValid} mappedBones={result.MappedBoneCount}");
                return result.Prefab != null;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MEPmxPipeline] Build failed for '{pmxPath}': {ex}");
                return false;
            }
        }

        // Batch-callable: pack a built prefab into a .me AssetBundle (M5).
        //   -executeMethod ...ExportMe  [-prefab <assetPath>] [-out <file.me>]
        public static void ExportMe()
        {
            string prefabPath = GetArg("-prefab") ?? @"Assets/PmxImported/丽塔/丽塔.prefab";
            bool ok = ExportInternal(prefabPath, GetArg("-out"));
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool ExportInternal(string prefabPath, string outPath)
        {
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) { Debug.LogError($"[MEPmxPipeline] Prefab not found: {prefabPath}"); return false; }

                string name = System.IO.Path.GetFileNameWithoutExtension(prefabPath);
                // Bundle every dependency except C# scripts (scripts resolve against the player).
                var deps = AssetDatabase.GetDependencies(prefabPath, true)
                    .Where(p => !string.IsNullOrEmpty(p) && !p.EndsWith(".cs")).ToArray();

                string tempDir = "TempPmxBundle";
                if (System.IO.Directory.Exists(tempDir)) System.IO.Directory.Delete(tempDir, true);
                System.IO.Directory.CreateDirectory(tempDir);

                var build = new AssetBundleBuild { assetBundleName = name, assetNames = deps };
                BuildPipeline.BuildAssetBundles(tempDir, new[] { build },
                    BuildAssetBundleOptions.ChunkBasedCompression, EditorUserBuildSettings.activeBuildTarget);

                string built = System.IO.Path.Combine(tempDir, name);
                if (string.IsNullOrEmpty(outPath))
                {
                    string dir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Build", "PmxModels");
                    System.IO.Directory.CreateDirectory(dir);
                    outPath = System.IO.Path.Combine(dir, name + ".me");
                }
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath));
                System.IO.File.Copy(built, outPath, true);
                System.IO.Directory.Delete(tempDir, true);

                Debug.Log($"[MEPmxPipeline] Exported .me -> {outPath}  (deps={deps.Length})");
                return true;
            }
            catch (System.Exception ex) { Debug.LogError($"[MEPmxPipeline] Export failed: {ex}"); return false; }
        }

        // Batch-callable: diagnose skinning vs inheritance/physics -> Logs/pmx_skin.txt
        public static void DumpSkinning()
        {
            string path = GetArg("-pmx") ?? @"D:\Program Files\MMD\MateEngine\models\特工丽塔\删披风删骨骼.pmx";
            bool ok = true;
            try
            {
                var model = new PmxReader().ReadFile(path);
                int nb = model.Bones.Count;
                var vcount = new int[nb];
                foreach (var v in model.Vertices)
                    for (int k = 0; k < 4; k++)
                        if (v.BoneIndices[k] >= 0 && v.BoneWeights[k] > 0f) vcount[v.BoneIndices[k]]++;

                var dyn = new HashSet<int>();
                foreach (var rb in model.RigidBodies)
                    if (rb.PhysicsMode != 0 && rb.BoneIndex >= 0) dyn.Add(rb.BoneIndex);

                var sb = new StringBuilder();
                sb.AppendLine("idx\tverts\tname\tinheritRot\tinheritTrans\tinheritSrc\tdynamic");
                var order = Enumerable.Range(0, nb).Where(i => vcount[i] > 0).OrderByDescending(i => vcount[i]);
                foreach (int i in order)
                {
                    var b = model.Bones[i];
                    bool ir = b.Has(PmxBoneFlags.InheritRotation), it = b.Has(PmxBoneFlags.InheritTranslation);
                    string src = (ir || it) && b.InheritParentIndex >= 0 && b.InheritParentIndex < nb
                        ? $"{b.InheritParentIndex}:{model.Bones[b.InheritParentIndex].NameLocal}({b.InheritWeight:0.##})" : "-";
                    sb.AppendLine($"{i}\t{vcount[i]}\t{b.NameLocal}\t{ir}\t{it}\t{src}\t{dyn.Contains(i)}");
                }
                string outPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Logs", "pmx_skin.txt");
                System.IO.File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[MEPmxPipeline] Dumped skinning ({order.Count()} skinned bones) -> {outPath}");
            }
            catch (System.Exception ex) { Debug.LogError($"[MEPmxPipeline] DumpSkinning failed: {ex}"); ok = false; }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // Batch-callable: find vertices far from their primary bone (orphan/stretched geometry,
        // typically left behind by incomplete deletion in a PMX editor). -> Logs/pmx_orphans.txt
        public static void DumpOrphans()
        {
            string path = GetArg("-pmx") ?? @"D:\Program Files\MMD\MateEngine\models\特工丽塔\删披风删骨骼.pmx";
            float threshold = float.TryParse(GetArg("-dist"), out var d) ? d : 3.0f; // MMD units
            bool ok = true;
            try
            {
                var model = new PmxReader().ReadFile(path);
                int nb = model.Bones.Count;

                // vertex -> material (via face ranges).
                var vMat = new int[model.Vertices.Count];
                for (int i = 0; i < vMat.Length; i++) vMat[i] = -1;
                int cursor = 0;
                for (int m = 0; m < model.Materials.Count; m++)
                {
                    int ic = model.Materials[m].SurfaceCount;
                    for (int t = 0; t < ic; t++) { int vi = model.Indices[cursor + t]; if (vi >= 0 && vi < vMat.Length) vMat[vi] = m; }
                    cursor += ic;
                }

                var byBone = new Dictionary<int, (int count, int matSample, float maxDist)>();
                int orphanTotal = 0;
                for (int i = 0; i < model.Vertices.Count; i++)
                {
                    var v = model.Vertices[i];
                    int pb = -1; float pw = -1f;
                    for (int k = 0; k < 4; k++) if (v.BoneIndices[k] >= 0 && v.BoneWeights[k] > pw) { pw = v.BoneWeights[k]; pb = v.BoneIndices[k]; }
                    if (pb < 0 || pb >= nb) continue;
                    float dist = Vector3.Distance(v.Position, model.Bones[pb].Position);
                    if (dist <= threshold) continue;
                    orphanTotal++;
                    var cur = byBone.TryGetValue(pb, out var e) ? e : (count: 0, matSample: vMat[i], maxDist: 0f);
                    byBone[pb] = (cur.count + 1, cur.matSample, Mathf.Max(cur.maxDist, dist));
                }

                var sb = new StringBuilder();
                sb.AppendLine($"# orphan verts (dist>{threshold} MMD units from primary bone): {orphanTotal} / {model.Vertices.Count}");
                sb.AppendLine("primaryBone\tcount\tmaxDist\tmaterial\tdynamic");
                var dyn = new HashSet<int>();
                foreach (var rb in model.RigidBodies) if (rb.PhysicsMode != 0 && rb.BoneIndex >= 0) dyn.Add(rb.BoneIndex);
                foreach (var kv in byBone.OrderByDescending(x => x.Value.count))
                {
                    string mat = kv.Value.matSample >= 0 && kv.Value.matSample < model.Materials.Count ? model.Materials[kv.Value.matSample].NameLocal : "?";
                    sb.AppendLine($"{kv.Key}:{model.Bones[kv.Key].NameLocal}\t{kv.Value.count}\t{kv.Value.maxDist:0.0}\t{mat}\t{dyn.Contains(kv.Key)}");
                }
                string outPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Logs", "pmx_orphans.txt");
                System.IO.File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[MEPmxPipeline] Orphan scan: {orphanTotal} verts beyond {threshold}u -> {outPath}");
            }
            catch (System.Exception ex) { Debug.LogError($"[MEPmxPipeline] DumpOrphans failed: {ex}"); ok = false; }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // Batch-callable: dump full bone hierarchy to Logs/pmx_bones.txt for M2 mapping design.
        public static void DumpSkeleton()
        {
            string path = GetArg("-pmx") ?? @"D:\Program Files\MMD\MateEngine\models\特工丽塔\删披风删骨骼.pmx";
            bool ok = true;
            try
            {
                var model = new PmxReader().ReadFile(path);
                var sb = new StringBuilder();
                sb.AppendLine($"# {model.Bones.Count} bones from {path}");
                for (int i = 0; i < model.Bones.Count; i++)
                {
                    var b = model.Bones[i];
                    string parent = b.ParentIndex >= 0 && b.ParentIndex < model.Bones.Count
                        ? $"{b.ParentIndex}:{model.Bones[b.ParentIndex].NameLocal}" : "-1:(root)";
                    sb.AppendLine($"{i}\t{b.NameLocal}\t<{b.NameUniversal}>\tparent={parent}\tik={b.Has(PmxBoneFlags.IK)}");
                }
                string outPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Logs", "pmx_bones.txt");
                System.IO.File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[MEPmxPipeline] Dumped {model.Bones.Count} bones -> {outPath}");
            }
            catch (System.Exception ex) { Debug.LogError($"[MEPmxPipeline] DumpSkeleton failed: {ex}"); ok = false; }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool Report(string path)
        {
            try
            {
                var model = new PmxReader().ReadFile(path);
                var sb = new StringBuilder();
                sb.AppendLine("===== PMX PARSE REPORT =====");
                sb.AppendLine($"File        : {path}");
                sb.AppendLine($"Version     : {model.Version}");
                sb.AppendLine($"Encoding    : {(model.TextEncoding == 0 ? "UTF-16LE" : "UTF-8")}");
                sb.AppendLine($"Add'l UV    : {model.AdditionalUvCount}");
                sb.AppendLine($"Name (JP)   : {model.NameLocal}");
                sb.AppendLine($"Name (EN)   : {model.NameUniversal}");
                sb.AppendLine($"Vertices    : {model.Vertices.Count}");
                sb.AppendLine($"Triangles   : {model.Indices.Count / 3}");
                sb.AppendLine($"Textures    : {model.TexturePaths.Count}");
                sb.AppendLine($"Materials   : {model.Materials.Count}");
                sb.AppendLine($"Bones       : {model.Bones.Count}");
                sb.AppendLine($"Morphs      : {model.Morphs.Count}");
                sb.AppendLine($"RigidBodies : {model.RigidBodies.Count}");
                sb.AppendLine($"Joints      : {model.Joints.Count}");

                int vertexMorphs = model.Morphs.Count(m => m.Type == PmxMorphType.Vertex);
                int dynamicBodies = model.RigidBodies.Count(rb => rb.PhysicsMode != 0);
                sb.AppendLine($"  vertex morphs (-> blendshapes): {vertexMorphs}");
                sb.AppendLine($"  dynamic rigidbodies (-> DynamicBone candidates): {dynamicBodies}");

                // Lip-sync / blink morphs the dance system drives by name.
                string[] lipsync = { "まばたき", "あ", "い", "う", "え", "お", "にこり", "怒り", "困る", "真面目", "笑い" };
                var found = lipsync.Where(t => model.Morphs.Any(m => m.NameLocal != null && m.NameLocal.Contains(t))).ToArray();
                sb.AppendLine($"  dance/lip-sync morphs present: {found.Length}/{lipsync.Length}  [{string.Join(", ", found)}]");

                sb.AppendLine("--- materials ---");
                foreach (var mat in model.Materials)
                {
                    string tex = mat.TextureIndex >= 0 && mat.TextureIndex < model.TexturePaths.Count
                        ? System.IO.Path.GetFileName(model.TexturePaths[mat.TextureIndex]) : "(none)";
                    string sph = mat.EnvironmentIndex >= 0 && mat.EnvironmentIndex < model.TexturePaths.Count
                        ? System.IO.Path.GetFileName(model.TexturePaths[mat.EnvironmentIndex]) : "(none)";
                    sb.AppendLine($"  [{mat.NameLocal}] tris={mat.SurfaceCount / 3} tex={tex} sphere={sph} blend={mat.EnvironmentBlendMode} toonRef={mat.ToonReference}");
                }

                sb.AppendLine("--- first 20 morphs ---");
                foreach (var mo in model.Morphs.Take(20))
                    sb.AppendLine($"  ({mo.Type}) {mo.NameLocal}  [{mo.RawOffsetCount}]");

                sb.AppendLine("--- root/key bones (first 20) ---");
                foreach (var b in model.Bones.Take(20))
                    sb.AppendLine($"  {b.NameLocal}  parent={b.ParentIndex} ik={b.Has(PmxBoneFlags.IK)}");

                sb.AppendLine("===== END REPORT =====");
                Debug.Log(sb.ToString());
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MEPmxPipeline] Parse failed for '{path}': {ex}");
                return false;
            }
        }
    }
}
