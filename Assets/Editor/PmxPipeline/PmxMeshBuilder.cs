// M1b/M4: build a Unity GameObject (skinned mesh + skeleton + blendshapes +
// UTS2 toon materials) from a parsed PmxModel, and persist it as a prefab + assets.
//
// Coordinate conversion (MMD -> Unity): 180° rotation about Y = negate X and Z on
// positions/normals, keeping original triangle winding. This is a proper rotation
// (determinant +1), so it introduces NO left-right mirror and preserves face
// orientation. (Negating a single axis would be a reflection and would mirror the
// model — e.g. bangs ending up on the wrong eye.) Vertices AND bone bind transforms
// use the SAME conversion, so skinning stays consistent.
//
// Humanoid avatar is M2, physics is M3/M3.5, preset-driven NPR material mapping is M4.
//
// Part of the MateEngine PMX offline import pipeline. See Docs/DECISIONS_RECORD.md ADR-0008.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public class PmxBuildResult
    {
        public GameObject Prefab;
        public string OutputFolder;
        public string PrefabPath;
        public int BoneCount, BlendShapeCount, SubMeshCount;
        public bool AvatarValid;
        public int MappedBoneCount;
        public PmxMaterialBuildStats MaterialStats;
    }

    public static class PmxMeshBuilder
    {
        // 1 MMD unit ≈ 0.08 m (de-facto MMD->Unity scale; ~20-unit model -> ~1.6 m).
        public const float DefaultScale = 0.08f;

        private static Vector3 Conv(Vector3 v, float s) => new(-v.x * s, v.y * s, -v.z * s);
        private static Vector3 ConvDir(Vector3 v) => new(-v.x, v.y, -v.z);

        public static PmxBuildResult Build(PmxModel model, string pmxPath, PmxPipelineOptions options, PmxRenderPreset renderPreset)
        {
            float scale = options.Scale;
            string modelName = options.ResolveModelName(model, pmxPath);

            string outFolder = $"{options.OutputRoot}/{modelName}";
            EnsureAssetFolder(outFolder);

            string pmxDir = Path.GetDirectoryName(pmxPath);

            // --- textures ---------------------------------------------------------
            var texAssets = ImportTextures(model, pmxDir, outFolder);
            ImportPresetTextures(renderPreset, options.PresetPath, outFolder, texAssets);

            // --- skeleton (must exist before bindposes) --------------------------
            var root = new GameObject(modelName);
            var boneNames = MakeUniqueBoneNames(model.Bones);
            var boneTf = new Transform[model.Bones.Count];
            for (int i = 0; i < model.Bones.Count; i++)
                boneTf[i] = new GameObject(boneNames[i]).transform;
            for (int i = 0; i < model.Bones.Count; i++)
            {
                int p = model.Bones[i].ParentIndex;
                boneTf[i].SetParent(p >= 0 && p < boneTf.Length ? boneTf[p] : root.transform, false);
            }
            // Positions are world-space bind positions; set after full parenting.
            for (int i = 0; i < model.Bones.Count; i++)
            {
                boneTf[i].position = Conv(model.Bones[i].Position, scale);
                boneTf[i].rotation = Quaternion.identity;
            }

            // --- mesh -------------------------------------------------------------
            // Remap skin weights off MMD inherit-driven "D" deform bones (付与) onto their
            // source control bones, so engine humanoid animation actually deforms the skin.
            int[] skinRemap = BuildSkinRemap(model);
            int remappedBones = 0;
            for (int i = 0; i < skinRemap.Length; i++) if (skinRemap[i] != i) remappedBones++;
            Debug.Log($"[PmxBuilder] skin remap: {remappedBones} inherit/deform bones repointed to control bones");

            // Drop faces weighted to non-deforming bones (操作中心 / IK targets). These are
            // typically remnants of mesh deleted in a PMX editor that got reassigned to bone 0,
            // which sits at the origin and never follows the body -> pinned/stretched geometry.
            bool[] orphanVert = FindOrphanVerts(model);

            var mesh = BuildMesh(model, scale, modelName, skinRemap, orphanVert);
            AssetDatabase.CreateAsset(mesh, $"{outFolder}/{modelName}.asset");

            // SMR lives on a child named "Body" so dance-clip blendshape curves (which bind
            // to the "Body" transform path) drive our morphs. The engine's dance system plays
            // clips by generic path; a root-level SMR would never be hit. Bones stay under root.
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(root.transform, false);

            // bindposes relative to the SMR transform (Body).
            var bindposes = new Matrix4x4[model.Bones.Count];
            for (int i = 0; i < model.Bones.Count; i++)
                bindposes[i] = boneTf[i].worldToLocalMatrix * bodyGo.transform.localToWorldMatrix;
            mesh.bindposes = bindposes;

            // --- materials --------------------------------------------------------
            var mats = PmxMaterialMapper.BuildMaterials(model, texAssets, outFolder, renderPreset, options.MatchMode, options.Style);
            var matStats = PmxMaterialMapper.LastStats;
            Debug.Log($"[PmxBuilder] Materials: total={matStats.Total} uts2={matStats.Uts2} " +
                      $"transClipping={matStats.TransClipping} matcap={matStats.Matcap} " +
                      $"highColor={matStats.HighColor} rim={matStats.Rim} outline={matStats.Outline} " +
                      $"presetMatches={matStats.PresetMatches} fallback={matStats.StandardFallbacks}");

            // --- skinned renderer -------------------------------------------------
            var smr = bodyGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = boneTf;
            smr.rootBone = boneTf.Length > 0 ? boneTf[0] : root.transform;
            smr.sharedMaterials = mats;
            smr.updateWhenOffscreen = true;
            smr.localBounds = mesh.bounds;

            // --- humanoid avatar (M2): enforces T-pose internally, after bindposes) ----
            var humanoid = PmxHumanoid.Build(model, root.transform, boneTf, boneNames);
            Animator animator = root.AddComponent<Animator>();
            if (humanoid.Valid)
            {
                AssetDatabase.CreateAsset(humanoid.Avatar, $"{outFolder}/{modelName}_Avatar.asset");
                animator.avatar = humanoid.Avatar;
            }
            else
            {
                Debug.LogError($"[PmxBuilder] Humanoid avatar invalid; prefab saved without avatar. " +
                               $"Missing required: {string.Join(", ", humanoid.MissingRequired)}");
            }

            // --- physics (M3): PMX rigid bodies/joints -> DynamicBone chains ----------
            var physics = PmxPhysics.Build(model, root.transform, boneTf, animator);
            Debug.Log($"[PmxBuilder] DynamicBone: components={physics.DynamicBones} chains={physics.ChainRoots} " +
                      $"colliders={physics.Colliders} " +
                      $"(hair={physics.Hair} skirt={physics.Skirt} breast={physics.Breast} other={physics.Other})");

            AddRenderProfile(root, renderPreset);

            if (options.Style?.materialProfile == "transpiled")
                ApplyTranspiled(root, smr, mesh, animator, options, outFolder);

            // --- persist prefab ---------------------------------------------------
            string prefabPath = $"{outFolder}/{modelName}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new PmxBuildResult
            {
                Prefab = prefab,
                OutputFolder = outFolder,
                PrefabPath = prefabPath,
                BoneCount = model.Bones.Count,
                BlendShapeCount = mesh.blendShapeCount,
                SubMeshCount = mesh.subMeshCount,
                AvatarValid = humanoid.Valid,
                MappedBoneCount = humanoid.Mapped.Count,
                MaterialStats = matStats
            };
        }

        // ----------------------------------------------------------------------

        private static void ApplyTranspiled(GameObject root, SkinnedMeshRenderer smr, Mesh mesh,
                                            Animator animator, PmxPipelineOptions options, string folder)
        {
            if (!animator.isHuman)
                throw new InvalidOperationException("转译模型必须具有有效的 Humanoid Avatar");
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) throw new InvalidOperationException("找不到 Humanoid 头骨");

            var metadata = JObject.Parse(File.ReadAllText(options.OutlinePath));
            if ((int)metadata["baseVertexCount"] != mesh.vertexCount)
                throw new InvalidOperationException("描边与 PMX 基础顶点数不一致，请重新烘焙");
            Vector3 Direction(string key)
            {
                var driver = metadata["drivers"]?[key];
                if (driver == null || (bool?)driver["constant"] != true ||
                    driver["value"] is not JArray v || v.Count != 3)
                    throw new InvalidOperationException($"描边清单缺少恒定方向 {key}，请重新烘焙");
                var value = MERefRenderer.BlenderToUnity(new Vector3((float)v[0], (float)v[1], (float)v[2]));
                if (value.sqrMagnitude < 1e-8f)
                    throw new InvalidOperationException($"预设方向 {key} 为零");
                return value.normalized;
            }
            var sun = Direction("SUN");
            var back = Direction("FRONT");
            var right = Direction("RIGHT");

            foreach (var line in MEOutlineShell.Apply(root, options.OutlinePath, options.TranspiledBindingsPath))
                Debug.Log(line);
            // 保留原网格资产与 GUID，只把合并结果写回；prefab 的 Body 路径和形态键不变。
            var merged = smr.sharedMesh;
            EditorUtility.CopySerialized(merged, mesh);
            smr.sharedMesh = mesh;
            smr.localBounds = mesh.bounds;
            UnityEngine.Object.DestroyImmediate(merged);
            EditorUtility.SetDirty(mesh);

            var placeholders = smr.sharedMaterials.Where(m => m != null && !AssetDatabase.Contains(m)).ToArray();
            var materials = METranspiledMaterials.Load(options.TranspiledBindingsPath, strict: true);
            try
            {
                materials.SetDirections(sun, back, right);
                materials.SetGeneratedBounds(root);
                materials.Apply(root);
                materials.SaveAssets(folder + "/Transpiled");
                foreach (var line in materials.Log) Debug.Log(line);
                var directions = root.AddComponent<MEModelDirections>();
                directions.Configure(head, root.GetComponentsInChildren<Renderer>(true), sun, back, right);
            }
            finally
            {
                materials.Dispose();
                foreach (var placeholder in placeholders) UnityEngine.Object.DestroyImmediate(placeholder);
            }
        }

        // Verts whose primary weight is a non-deforming bone (操作中心 / IK) = deletion garbage.
        private static bool[] FindOrphanVerts(PmxModel model)
        {
            var orphanBones = new HashSet<int>();
            for (int i = 0; i < model.Bones.Count; i++)
            {
                var b = model.Bones[i];
                string n = b.NameLocal ?? "";
                if (b.Has(PmxBoneFlags.IK) || n.Contains("操作") || n.Contains("ＩＫ") || n.Contains("IK親"))
                    orphanBones.Add(i);
            }
            var orphan = new bool[model.Vertices.Count];
            for (int i = 0; i < model.Vertices.Count; i++)
            {
                var v = model.Vertices[i];
                int pb = -1; float pw = -1f;
                for (int k = 0; k < 4; k++)
                    if (v.BoneIndices[k] >= 0 && v.BoneWeights[k] > pw) { pw = v.BoneWeights[k]; pb = v.BoneIndices[k]; }
                if (pb >= 0 && orphanBones.Contains(pb)) orphan[i] = true;
            }
            return orphan;
        }

        private static Mesh BuildMesh(PmxModel model, float scale, string name, int[] skinRemap, bool[] orphanVert)
        {
            int vc = model.Vertices.Count;
            var verts = new Vector3[vc];
            var norms = new Vector3[vc];
            var uvs = new Vector2[vc];
            var weights = new BoneWeight[vc];

            for (int i = 0; i < vc; i++)
            {
                var v = model.Vertices[i];
                verts[i] = Conv(v.Position, scale);
                norms[i] = ConvDir(v.Normal).normalized;
                // PMX V coordinate is top-down; Unity is bottom-up.
                uvs[i] = new Vector2(v.Uv.x, 1f - v.Uv.y);
                weights[i] = ToBoneWeight(v, skinRemap);
            }

            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.boneWeights = weights;

            // Submesh per material = contiguous range of the index buffer.
            // 180°-about-Y is a proper rotation: keep original PMX winding. Skip orphan faces.
            mesh.subMeshCount = model.Materials.Count;
            int cursor = 0, dropped = 0;
            for (int m = 0; m < model.Materials.Count; m++)
            {
                int icount = model.Materials[m].SurfaceCount;
                var tris = new List<int>(icount);
                for (int t = 0; t < icount; t += 3)
                {
                    int a = model.Indices[cursor + t], b = model.Indices[cursor + t + 1], c = model.Indices[cursor + t + 2];
                    if (orphanVert[a] || orphanVert[b] || orphanVert[c]) { dropped++; continue; }
                    tris.Add(a); tris.Add(b); tris.Add(c);
                }
                mesh.SetTriangles(tris, m, false);
                cursor += icount;
            }
            if (dropped > 0) Debug.Log($"[PmxBuilder] dropped {dropped} orphan faces (weighted to 操作中心/IK; deletion remnants)");

            mesh.RecalculateBounds();
            AddBlendShapes(mesh, model, scale);
            mesh.RecalculateTangents();
            return mesh;
        }

        private static void AddBlendShapes(Mesh mesh, PmxModel model, float scale)
        {
            int vc = model.Vertices.Count;
            var used = new HashSet<string>();
            foreach (var morph in model.Morphs)
            {
                if (morph.Type != PmxMorphType.Vertex || morph.VertexOffsets == null) continue;
                // Keep the original Japanese name so AvatarDanceShapeConverter can drive it.
                string shapeName = morph.NameLocal;
                if (string.IsNullOrEmpty(shapeName) || !used.Add(shapeName)) continue;

                var dv = new Vector3[vc];
                foreach (var off in morph.VertexOffsets)
                    if (off.VertexIndex >= 0 && off.VertexIndex < vc)
                        dv[off.VertexIndex] = Conv(off.Translation, scale); // linear delta, same convention as positions

                mesh.AddBlendShapeFrame(shapeName, 100f, dv, null, null);
            }
        }

        private static BoneWeight ToBoneWeight(PmxVertex v, int[] skinRemap)
        {
            // Gather up to 4 (index,weight) pairs, drop invalids, normalize.
            // Remap inherit-driven deform bones onto their driven source (skinRemap).
            var pairs = new List<(int idx, float w)>(4);
            for (int k = 0; k < 4; k++)
                if (v.BoneIndices[k] >= 0 && v.BoneWeights[k] > 0f)
                    pairs.Add((skinRemap[v.BoneIndices[k]], v.BoneWeights[k]));
            if (pairs.Count == 0) pairs.Add((skinRemap[Mathf.Max(0, v.BoneIndices[0])], 1f));

            // Merge duplicate bone indices produced by remapping.
            if (pairs.Count > 1)
            {
                var merged = new Dictionary<int, float>(4);
                foreach (var p in pairs) merged[p.idx] = (merged.TryGetValue(p.idx, out var w) ? w : 0f) + p.w;
                pairs = merged.Select(kv => (kv.Key, kv.Value)).ToList();
            }

            pairs.Sort((a, b) => b.w.CompareTo(a.w));
            if (pairs.Count > 4) pairs.RemoveRange(4, pairs.Count - 4);
            float sum = pairs.Sum(p => p.w);
            if (sum <= 0f) sum = 1f;

            var bw = new BoneWeight();
            for (int k = 0; k < pairs.Count; k++)
            {
                float w = pairs[k].w / sum;
                switch (k)
                {
                    case 0: bw.boneIndex0 = pairs[k].idx; bw.weight0 = w; break;
                    case 1: bw.boneIndex1 = pairs[k].idx; bw.weight1 = w; break;
                    case 2: bw.boneIndex2 = pairs[k].idx; bw.weight2 = w; break;
                    case 3: bw.boneIndex3 = pairs[k].idx; bw.weight3 = w; break;
                }
            }
            return bw;
        }

        // ----------------------------------------------------------------------

        private static Dictionary<int, Texture2D> ImportTextures(PmxModel model, string pmxDir, string outFolder)
        {
            var result = new Dictionary<int, Texture2D>();
            for (int i = 0; i < model.TexturePaths.Count; i++)
            {
                string rel = model.TexturePaths[i]?.Replace('\\', '/');
                if (string.IsNullOrEmpty(rel)) continue;
                string src = Path.Combine(pmxDir, rel);
                if (!File.Exists(src)) { Debug.LogWarning($"[PmxBuilder] Texture missing: {src}"); continue; }

                string fileName = SanitizeName(Path.GetFileNameWithoutExtension(rel)) + Path.GetExtension(rel);
                string destAsset = $"{outFolder}/{fileName}";
                string destAbs = Path.GetFullPath(destAsset);
                try
                {
                    File.Copy(src, destAbs, true);
                    AssetDatabase.ImportAsset(destAsset, ImportAssetOptions.ForceUpdate);
                    if (AssetImporter.GetAtPath(destAsset) is TextureImporter importer)
                    {
                        importer.textureType = TextureImporterType.Default;
                        importer.sRGBTexture = true;
                        importer.alphaIsTransparency = true;
                        importer.SaveAndReimport();
                    }
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(destAsset);
                    if (tex != null) result[i] = tex;
                }
                catch (System.Exception e) { Debug.LogWarning($"[PmxBuilder] Texture import failed {src}: {e.Message}"); }
            }
            return result;
        }

        private static void ImportPresetTextures(PmxRenderPreset preset, string presetPath, string outFolder,
            Dictionary<int, Texture2D> result)
        {
            if (preset == null || preset.materials == null || string.IsNullOrWhiteSpace(presetPath)) return;
            string presetDir = Path.GetDirectoryName(presetPath);
            if (string.IsNullOrWhiteSpace(presetDir) || !Directory.Exists(presetDir)) return;

            var candidates = new HashSet<string>();
            foreach (var material in preset.materials)
            {
                if (material == null) continue;
                AddTextureCandidate(candidates, material.baseTexture);
                AddTextureCandidate(candidates, material.shadeTexture);
                AddTextureCandidate(candidates, material.matcapTexture);
                AddTextureCandidate(candidates, material.emissionTexture);
                AddTextureCandidate(candidates, material.highColorTexture);
                AddTextureCandidate(candidates, material.highColorMaskTexture);
                AddTextureCandidate(candidates, material.maskTexture);
                AddTextureCandidate(candidates, material.toonTexture);
                if (material.images == null) continue;
                foreach (string image in material.images)
                    AddTextureCandidate(candidates, image);
            }

            int nextKey = -1000;
            var existingNames = new HashSet<string>(result.Values
                .Select(t => Path.GetFileName(AssetDatabase.GetAssetPath(t)).ToLowerInvariant()));

            foreach (string candidate in candidates.OrderBy(s => s))
            {
                string src = ResolvePresetTexturePath(candidate, presetDir);
                if (string.IsNullOrEmpty(src)) continue;

                string fileName = SanitizeName(Path.GetFileNameWithoutExtension(src)) + Path.GetExtension(src).ToLowerInvariant();
                string lowerName = fileName.ToLowerInvariant();
                if (existingNames.Contains(lowerName)) continue;

                string destAsset = $"{outFolder}/{fileName}";
                try
                {
                    File.Copy(src, Path.GetFullPath(destAsset), true);
                    AssetDatabase.ImportAsset(destAsset, ImportAssetOptions.ForceUpdate);
                    ConfigureImportedTexture(destAsset, fileName);
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(destAsset);
                    if (tex != null)
                    {
                        result[nextKey--] = tex;
                        existingNames.Add(lowerName);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PmxBuilder] Preset texture import failed {candidate}: {e.Message}");
                }
            }
        }

        private static void AddTextureCandidate(HashSet<string> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".dds" or ".tif" or ".tiff")
                candidates.Add(path.Replace('\\', '/'));
        }

        private static string ResolvePresetTexturePath(string candidate, string presetDir)
        {
            string normalized = candidate.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
            {
                string direct = Path.GetFullPath(normalized);
                if (File.Exists(direct)) return direct;
            }
            else
            {
                string projectRelative = Path.GetFullPath(Path.Combine(PmxPipelineOptions.ProjectRoot, normalized));
                if (File.Exists(projectRelative)) return projectRelative;

                string presetRelative = Path.GetFullPath(Path.Combine(presetDir, normalized));
                if (File.Exists(presetRelative)) return presetRelative;
            }

            string fileName = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            try
            {
                return Directory.EnumerateFiles(presetDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static void ConfigureImportedTexture(string assetPath, string fileName)
        {
            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) return;
            string lower = fileName.ToLowerInvariant();
            if (lower.Contains("normal"))
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.sRGBTexture = false;
            }
            else
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = !(lower.Contains("mask") || lower.Contains("lightmap") || lower.Contains("metal"));
                importer.alphaIsTransparency = true;
            }
            importer.SaveAndReimport();
        }

        private static void AddRenderProfile(GameObject root, PmxRenderPreset renderPreset)
        {
            // Post-processing is no longer baked per-model. The runtime
            // RenderStyleManager owns a dedicated global PostProcessVolume and the
            // user picks a style (默认/鲜艳/柔和) in Settings, so we no longer attach
            // a PmxModelRenderProfile here (it would only fight the manager for the
            // scene volumes). The component class is kept for backward-compat with
            // already-baked .me files; it self-disables when the manager exists.
            if (renderPreset == null || renderPreset.scene == null || !renderPreset.scene.HasPostProcess) return;
            Debug.Log("[PmxBuilder] Skipping baked render profile; post-processing handled at runtime by RenderStyleManager.");
        }

        // ----------------------------------------------------------------------

        // Maps each bone to the bone whose transform actually drives it, so skin can be
        // re-pointed off inherit-only ("D"/付与) deform bones onto driven control bones.
        private static int[] BuildSkinRemap(PmxModel model)
        {
            int n = model.Bones.Count;
            var rep = new int[n];
            for (int i = 0; i < n; i++) rep[i] = i;

            // Pass 1: inherit-rotation bone whose source is NOT an ancestor = detached deform
            // bone (e.g. 足D, sibling of 足). It only moves via 付与, which we don't run -> remap.
            for (int i = 0; i < n; i++)
            {
                var b = model.Bones[i];
                if (b.Has(PmxBoneFlags.InheritRotation) && b.InheritParentIndex >= 0 && b.InheritParentIndex < n
                    && !IsAncestor(model.Bones, b.InheritParentIndex, i))
                    rep[i] = b.InheritParentIndex;
            }
            for (int i = 0; i < n; i++) rep[i] = Resolve(rep, i);

            // Pass 2: propagate into dead branches (e.g. 足先EX, child of the remapped 足首D).
            for (int i = 0; i < n; i++)
            {
                if (rep[i] != i) continue;
                int p = model.Bones[i].ParentIndex;
                if (p >= 0 && rep[p] != p && !model.Bones[i].Has(PmxBoneFlags.InheritRotation))
                    rep[i] = rep[p];
            }
            for (int i = 0; i < n; i++) rep[i] = Resolve(rep, i);
            return rep;
        }

        private static bool IsAncestor(List<PmxBone> bones, int ancestor, int of)
        {
            int p = bones[of].ParentIndex, guard = 0;
            while (p >= 0 && guard++ < 100000) { if (p == ancestor) return true; p = bones[p].ParentIndex; }
            return false;
        }

        private static int Resolve(int[] rep, int i)
        {
            int guard = 0;
            while (rep[i] != i && guard++ < 100000) i = rep[i];
            return i;
        }

        // Humanoid mapping matches bones by these names, so they must be unique.
        private static string[] MakeUniqueBoneNames(List<PmxBone> bones)
        {
            var names = new string[bones.Count];
            var seen = new HashSet<string>();
            for (int i = 0; i < bones.Count; i++)
            {
                string n = string.IsNullOrEmpty(bones[i].NameLocal) ? $"bone_{i}" : bones[i].NameLocal;
                string unique = n;
                int suffix = 1;
                while (!seen.Add(unique)) unique = $"{n}_{suffix++}";
                names[i] = unique;
            }
            return names;
        }

        private static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
