// Batch entry points for the MateEngine PMX offline import pipeline.
// See Docs/DECISIONS_RECORD.md ADR-0008.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

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

        [MenuItem("MateEngine/PMX Pipeline/Validate Parse (local settings)")]
        public static void ValidateParseMenu()
        {
            ValidateParseInternal(PmxPipelineOptions.FromCommandLine());
        }

        // Batch-callable: -executeMethod ...ValidateParse  -pmx <path>
        public static void ValidateParse()
        {
            bool ok = ValidateParseInternal(PmxPipelineOptions.FromCommandLine());
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        [MenuItem("MateEngine/PMX Pipeline/Build Model (local settings)")]
        public static void BuildModelMenu()
        {
            BuildInternal(PmxPipelineOptions.FromCommandLine(), out _);
        }

        // Batch-callable:
        //   -executeMethod ...BuildModel -pmx <path> [-preset <blend>] [-blender <exe>]
        //       [-scale <f>] [-outputRoot <assetFolder>] [-modelName <name>]
        public static void BuildModel()
        {
            bool ok = BuildInternal(PmxPipelineOptions.FromCommandLine(), out _);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // Batch-callable:
        //   -executeMethod ...BuildAndExport -pmx <path> [-preset <blend>] [-blender <exe>] [-out <file.me>]
        public static void BuildAndExport()
        {
            var options = PmxPipelineOptions.FromCommandLine();
            bool ok = BuildInternal(options, out var result)
                   && ExportInternal(options, result.PrefabPath, out _);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // ===== Legacy `.me` export (RETAINED, not deleted) =====
        // 用途：把构建好的 prefab 连同贴图/材质/着色器打包成单文件 `.me`(ZIP 内含 AssetBundle)，
        // 走 App 现有 `.me` 加载链直接显示；自带渲染，不依赖 App 侧风格系统。
        //
        // 架构演进：主产物正转向通用 VRM + App 内置可切换渲染风格(见 Docs/RENDER_STYLE_DESIGN.md
        // 与 ADR-0009)。本 `.me` 路径**保留为可选/兼容**，VRM 导出落地前它仍是唯一可用产物，
        // 故此处不屏蔽；不要删除。完整流程见 Docs/PMX_TO_VRM.md。
        //
        // 实现时踩过的坑 + 解决：
        //  1) AssetBundle 不能含 C# 脚本——脚本对玩家程序集解析；ExportInternal 里依赖收集已排除 .cs。
        //  2) 着色器变体剥离：UTS2/lilToon 打进 bundle 后若变体被剥离会渲染成粉红；
        //     需 Always Included Shaders / shader variant collection 兜底(实施 VRM 前如继续用 .me 需复核)。
        //  3) Addressables content 重建副作用：导出过程可能顺带删除
        //     Assets/AddressableAssetsData/link.xml；提交前需 git 还原该文件。
        //  4) 导出文件名被本机 settings 的 modelName/exportPath 钉死，曾导致输出名为
        //     丽塔_m4_stylized.me 而非预期名；用 -out 显式指定或清理本机配置可控。
        //
        // Batch-callable: pack a built prefab into a .me AssetBundle (M5).
        //   -executeMethod ...ExportMe [-prefab <assetPath>] [-out <file.me>]
        public static void ExportMe()
        {
            var options = PmxPipelineOptions.FromCommandLine();
            bool ok = ExportInternal(options, options.PrefabPath, out _);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool ValidateParseInternal(PmxPipelineOptions options)
        {
            try
            {
                return Report(options.RequirePmxPath());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MEPmxPipeline] ValidateParse failed: {ex.Message}");
                return false;
            }
        }

        private static bool BuildInternal(PmxPipelineOptions options, out PmxBuildResult result)
        {
            result = null;
            try
            {
                string pmxPath = options.RequirePmxPath();
                var renderPreset = LoadRenderPreset(options);
                var model = new PmxReader().ReadFile(pmxPath);

                string modelName = options.ResolveModelName(model, pmxPath);
                string stylePath = options.ResolveAndLoadStyle(modelName);
                Debug.Log(options.Style != null
                    ? $"[MEPmxPipeline] Loaded style config: {stylePath} (profile={options.Style.materialProfile})"
                    : $"[MEPmxPipeline] No style config at {stylePath}; using code defaults.");

                result = PmxMeshBuilder.Build(model, pmxPath, options, renderPreset);
                options.SaveLastBuild(result.PrefabPath);
                Debug.Log($"[MEPmxPipeline] Built '{result.Prefab.name}' -> {result.PrefabPath}\n" +
                          $"  bones={result.BoneCount} submeshes={result.SubMeshCount} blendshapes={result.BlendShapeCount}\n" +
                          $"  humanoid: valid={result.AvatarValid} mappedBones={result.MappedBoneCount}");
                return result.Prefab != null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MEPmxPipeline] Build failed: {ex}");
                return false;
            }
        }

        private static PmxRenderPreset LoadRenderPreset(PmxPipelineOptions options)
        {
            if (!options.HasPreset) return null;

            options.ValidatePresetInputs();
            string jsonPath = options.ResolveRenderPresetJsonPath();
            if (!RunBlenderPresetDump(options, jsonPath))
                throw new InvalidOperationException("Blender render preset dump failed.");

            var preset = PmxRenderPreset.Load(jsonPath);
            if (preset == null)
                throw new InvalidOperationException($"Render preset JSON could not be read: {jsonPath}");

            Debug.Log($"[MEPmxPipeline] Loaded render preset: materials={preset.materials?.Count ?? 0} source={preset.sourceBlendName}");
            return preset;
        }

        private static bool RunBlenderPresetDump(PmxPipelineOptions options, string jsonPath)
        {
            string scriptPath = options.ResolveBlenderDumpScriptPath();
            if (!File.Exists(scriptPath))
            {
                Debug.LogError($"[MEPmxPipeline] Blender dump script not found: {scriptPath}");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));
            string textureRoot = Path.Combine(Path.GetDirectoryName(jsonPath), "pmx_render_preset_textures");
            if (Directory.Exists(textureRoot)) Directory.Delete(textureRoot, true);
            Directory.CreateDirectory(textureRoot);
            var psi = new ProcessStartInfo
            {
                FileName = options.BlenderPath,
                Arguments = "--background --factory-startup --disable-autoexec --python " + Quote(scriptPath) +
                            " -- --preset " + Quote(options.PresetPath) + " --out " + Quote(jsonPath) +
                            " --texture-root " + Quote(textureRoot),
                WorkingDirectory = PmxPipelineOptions.ProjectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(120000))
            {
                try { process.Kill(); } catch { }
                Debug.LogError("[MEPmxPipeline] Blender dump timed out.");
                return false;
            }
            process.WaitForExit();

            if (stdout.Length > 0) Debug.Log(stdout.ToString().Trim());
            if (stderr.Length > 0) Debug.LogWarning(stderr.ToString().Trim());
            if (process.ExitCode != 0)
            {
                Debug.LogError($"[MEPmxPipeline] Blender dump exited with code {process.ExitCode}");
                return false;
            }

            return File.Exists(jsonPath);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static bool ExportInternal(PmxPipelineOptions options, string prefabPath, out string exportedPath)
        {
            exportedPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(prefabPath))
                {
                    Debug.LogError("[MEPmxPipeline] Prefab path is required. Pass -prefab <assetPath>, run BuildAndExport, or set local settings.");
                    return false;
                }

                prefabPath = PmxPipelineOptions.ToProjectRelativeAssetPath(prefabPath);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) { Debug.LogError($"[MEPmxPipeline] Prefab not found: {prefabPath}"); return false; }

                string name = Path.GetFileNameWithoutExtension(prefabPath);
                // Bundle every dependency except C# scripts (scripts resolve against the player).
                var deps = AssetDatabase.GetDependencies(prefabPath, true)
                    .Where(p => !string.IsNullOrEmpty(p) && !p.EndsWith(".cs")).ToArray();

                string tempDir = options.ResolveTempRoot();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                var build = new AssetBundleBuild { assetBundleName = name, assetNames = deps };
                BuildPipeline.BuildAssetBundles(tempDir, new[] { build },
                    BuildAssetBundleOptions.ChunkBasedCompression, EditorUserBuildSettings.activeBuildTarget);

                string built = Path.Combine(tempDir, name);
                string outPath = options.ResolveDefaultOutPath(prefabPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.Copy(built, outPath, true);
                Directory.Delete(tempDir, true);

                exportedPath = outPath;
                options.SaveLastBuild(prefabPath, outPath);
                Debug.Log($"[MEPmxPipeline] Exported .me -> {outPath}  (deps={deps.Length})");
                return true;
            }
            catch (Exception ex) { Debug.LogError($"[MEPmxPipeline] Export failed: {ex}"); return false; }
        }

        // Batch-callable: diagnose skinning vs inheritance/physics -> Logs/pmx_skin.txt
        public static void DumpSkinning()
        {
            bool ok = true;
            try
            {
                var options = PmxPipelineOptions.FromCommandLine();
                string path = options.RequirePmxPath();
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
                string outPath = Path.Combine(PmxPipelineOptions.NormalizeProjectDirectory(options.LogsRoot), "pmx_skin.txt");
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[MEPmxPipeline] Dumped skinning ({order.Count()} skinned bones) -> {outPath}");
            }
            catch (Exception ex) { Debug.LogError($"[MEPmxPipeline] DumpSkinning failed: {ex}"); ok = false; }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // Batch-callable: find vertices far from their primary bone (orphan/stretched geometry,
        // typically left behind by incomplete deletion in a PMX editor). -> Logs/pmx_orphans.txt
        public static void DumpOrphans()
        {
            float threshold = float.TryParse(GetArg("-dist"), out var d) ? d : 3.0f; // MMD units
            bool ok = true;
            try
            {
                var options = PmxPipelineOptions.FromCommandLine();
                string path = options.RequirePmxPath();
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
                string outPath = Path.Combine(PmxPipelineOptions.NormalizeProjectDirectory(options.LogsRoot), "pmx_orphans.txt");
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[MEPmxPipeline] Orphan scan: {orphanTotal} verts beyond {threshold}u -> {outPath}");
            }
            catch (Exception ex) { Debug.LogError($"[MEPmxPipeline] DumpOrphans failed: {ex}"); ok = false; }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // Batch-callable: dump full bone hierarchy to Logs/pmx_bones.txt for M2 mapping design.
        public static void DumpSkeleton()
        {
            bool ok = true;
            try
            {
                var options = PmxPipelineOptions.FromCommandLine();
                string path = options.RequirePmxPath();
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
                string outPath = Path.Combine(PmxPipelineOptions.NormalizeProjectDirectory(options.LogsRoot), "pmx_bones.txt");
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[MEPmxPipeline] Dumped {model.Bones.Count} bones -> {outPath}");
            }
            catch (Exception ex) { Debug.LogError($"[MEPmxPipeline] DumpSkeleton failed: {ex}"); ok = false; }
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
                        ? Path.GetFileName(model.TexturePaths[mat.TextureIndex]) : "(none)";
                    string sph = mat.EnvironmentIndex >= 0 && mat.EnvironmentIndex < model.TexturePaths.Count
                        ? Path.GetFileName(model.TexturePaths[mat.EnvironmentIndex]) : "(none)";
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
