// 把 Tools/PmxPipeline/bake_outline_shell.py 烘焙出的描边外壳并进模型网格。
//
// 计划见 Docs/plans/2026-09-10-blender-graph-transpile.md Phase 3。描边是**实体化的
// 反向外壳**，不是着色器 pass：几何节点按 1e-4 焊接顶点、重算法线、沿法线外扩
// `宽度 × 遮罩权重`、划分三个 edge_* 子材质、再 Flip Faces。几何由 Blender 算好，
// 这边只负责搬进来并补上外壳自己没有的两样东西：
//
//   骨骼权重      —— 按焊接组把基础顶点的权重加起来，取前 4 个重新归一化
//   BlendShape    —— 同样按焊接组取平均增量
//
// 外壳不继承顶点组（实测求值后的网格上 `vertices[i].groups` 是空的），所以这两样
// 只能从基础网格推。焊接组由烘焙脚本用「描边权重=0」那次求值 + KD 树对回来。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public static class MEOutlineShell
    {
        private sealed class Shell
        {
            public int VertexCount;
            public Vector3[] Positions;      // 已转成 Unity 坐标
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public int[] Triangles;          // 已翻转绕序
            public int[] WeldOffsets;        // 长度 V+1
            public int[] WeldIndices;
            public List<(string material, int start, int count)> Submeshes = new();
            public float Width;
        }

        /// <param name="bindingsPath">给定时按图集修正描边材质（仅导出 .me 用）；离线差分不传，保持与 Blender 一致。</param>
        public static List<string> Apply(GameObject root, string metaPath, string bindingsPath = null)
        {
            var log = new List<string>();
            var shell = Load(metaPath, log);

            var smr = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                          .FirstOrDefault(r => r.sharedMesh != null);
            if (smr == null)
            {
                log.Add("!! 模型上没有带网格的 SkinnedMeshRenderer，描边未应用");
                return log;
            }

            var baseMesh = smr.sharedMesh;
            if (bindingsPath != null) RetargetAtlas(shell, baseMesh, smr.sharedMaterials, bindingsPath, log);
            var merged = Merge(baseMesh, shell, log);
            smr.sharedMesh = merged;

            // 先给外壳子网格挂上**按 Blender 材质名命名**的占位材质，随后
            // METranspiledMaterials.Apply 会按名字把它们换成转译材质。
            var mats = smr.sharedMaterials.ToList();
            var placeholder = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            foreach (var sm in shell.Submeshes)
                mats.Add(new Material(placeholder) { name = sm.material });
            smr.sharedMaterials = mats.ToArray();

            log.Add($"描边外壳已并入：顶点 +{shell.VertexCount}，子网格 +{shell.Submeshes.Count}，" +
                    $"最大外扩 {shell.Width:F5}（本地单位）");
            return log;
        }

        // ---- 读烘焙产物 -----------------------------------------------------

        private static Shell Load(string metaPath, List<string> log)
        {
            if (!File.Exists(metaPath))
                throw new FileNotFoundException($"描边清单不存在：{metaPath}");
            var meta = JObject.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
            string binPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(metaPath)) ?? ".",
                                          (string)meta["bin"]);
            if (!File.Exists(binPath))
                throw new FileNotFoundException($"描边数据不存在：{binPath}");

            var s = new Shell
            {
                VertexCount = (int)meta["vertexCount"],
                Width = (float)meta["outlineWidth"],
            };
            int v = s.VertexCount;
            int ic = (int)meta["indexCount"];
            int wc = (int)meta["weldIndexCount"];

            using var br = new BinaryReader(File.OpenRead(binPath));
            s.Positions = new Vector3[v];
            for (int i = 0; i < v; i++)
                s.Positions[i] = MERefRenderer.BlenderToUnity(
                    new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
            s.Normals = new Vector3[v];
            for (int i = 0; i < v; i++)
                s.Normals[i] = MERefRenderer.BlenderToUnity(
                    new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle())).normalized;
            s.Uvs = new Vector2[v];
            for (int i = 0; i < v; i++)
                s.Uvs[i] = new Vector2(br.ReadSingle(), br.ReadSingle());

            // Blender→Unity 是行列式 -1 的映射（右手变左手），绕序必须翻一次，
            // 否则外壳的正反面整个反过来，配上 Cull Back 就是「一根描边都看不见」。
            var tri = new int[ic];
            for (int i = 0; i < ic; i += 3)
            {
                int a = br.ReadInt32(), b = br.ReadInt32(), c = br.ReadInt32();
                tri[i] = a; tri[i + 1] = c; tri[i + 2] = b;
            }
            s.Triangles = tri;

            s.WeldOffsets = new int[v + 1];
            for (int i = 0; i <= v; i++) s.WeldOffsets[i] = br.ReadInt32();
            s.WeldIndices = new int[wc];
            for (int i = 0; i < wc; i++) s.WeldIndices[i] = br.ReadInt32();

            foreach (var sm in (JArray)meta["submeshes"])
                s.Submeshes.Add(((string)sm["material"], (int)sm["start"], (int)sm["count"]));

            log.Add($"读入描边外壳：顶点 {v} 索引 {ic} 焊接引用 {wc} " +
                    $"子网格 {string.Join("/", s.Submeshes.Select(x => $"{x.material}:{x.count / 3}"))}");
            return s;
        }

        // ---- 按图集修正描边材质 ---------------------------------------------
        //
        // 预设可能把一个描边材质挂到不同贴图图集的部位上。丽塔的 edge_clothes 采样 Body 光照图，
        // 却也覆盖了 UV 在 Body_Parts 图集里的袜子/腿带/鞋子；按 alpha 分档取色时，约 5% 的
        // 腿部描边碰巧落进 0.05 的橙色皮肤档，深色背景上露出零星橙线（Blender 里同样存在）。
        // 预设已备有采样 Body_Parts 的 edge_clothes2，只是没挂上。这里逐三角形按焊接组找回
        // 底层材质：若它不用描边材质采样的那张贴图，优先换成结构相同（节点数一致、只采样一张
        // 贴图）、没有被任何部位使用、且采样的正是底层材质那张贴图的描边材质；没有的话（如
        // 寻梦者只有采样 Body 光照图的描边，腿部却在 Leg 图集），就用同一描边材质换图的变体，
        // 新贴图取底层材质里色彩空间相同、文件名共同后缀（如 `_Lightmap`）最长且唯一的那张。
        // 两种都判定不出则不动。
        private sealed class BindingInfo
        {
            public int NodeCount;
            public string[] Images;
            public bool[] Srgb;
        }

        private static string VariantFor(string material, BindingInfo edge, BindingInfo baseInfo)
        {
            string role = Path.GetFileNameWithoutExtension(edge.Images[0]);
            var scored = Enumerable.Range(0, baseInfo.Images.Length)
                .Where(i => baseInfo.Srgb[i] == edge.Srgb[0])
                .Select(i => (image: baseInfo.Images[i],
                              score: CommonSuffix(role, Path.GetFileNameWithoutExtension(baseInfo.Images[i]))))
                .OrderByDescending(x => x.score).ToArray();
            // 至少共享 4 个字符的后缀，且最高分唯一，否则视为认不出对应关系。
            if (scored.Length == 0 || scored[0].score < 4 ||
                (scored.Length > 1 && scored[1].score == scored[0].score)) return null;
            return material + METranspiledMaterials.VariantSeparator + scored[0].image;
        }

        private static int CommonSuffix(string a, string b)
        {
            int n = 0;
            while (n < a.Length && n < b.Length &&
                   char.ToLowerInvariant(a[a.Length - 1 - n]) == char.ToLowerInvariant(b[b.Length - 1 - n])) n++;
            return n;
        }

        private static void RetargetAtlas(Shell shell, Mesh baseMesh, Material[] baseMaterials,
                                          string bindingsPath, List<string> log)
        {
            var bindings = new Dictionary<string, BindingInfo>();
            var data = JObject.Parse(File.ReadAllText(bindingsPath, Encoding.UTF8));
            foreach (JObject m in (JArray)data["materials"])
                bindings[(string)m["name"]] = new BindingInfo
                {
                    NodeCount = (int?)m["nodeCount"] ?? -1,
                    Images = ((JArray)m["textures"]).Select(t => (string)t["image"]).ToArray(),
                    Srgb = ((JArray)m["textures"]).Select(t => (bool?)t["srgb"] ?? true).ToArray(),
                };

            int bn = baseMesh.vertexCount;
            var baseNames = new string[baseMesh.subMeshCount];
            var vertexSubmesh = Enumerable.Repeat(-1, bn).ToArray();
            for (int m = 0; m < baseMesh.subMeshCount; m++)
            {
                if (m < baseMaterials.Length && baseMaterials[m] != null)
                    baseNames[m] = METranspiledMaterials.ResolveName(baseMaterials[m].name, bindings.ContainsKey);
                foreach (int v in baseMesh.GetTriangles(m)) vertexSubmesh[v] = m;
            }
            var used = new HashSet<string>(baseNames.Where(n => n != null));
            used.UnionWith(shell.Submeshes.Select(s => s.material));

            var groups = new List<(string material, List<int> tris)>();
            var moved = new Dictionary<string, int>();
            List<int> Group(string name)
            {
                int k = groups.FindIndex(g => g.material == name);
                if (k >= 0) return groups[k].tris;
                groups.Add((name, new List<int>()));
                return groups[^1].tris;
            }
            var votes = new Dictionary<int, int>();
            foreach (var (material, start, count) in shell.Submeshes)
            {
                var target = Group(material);
                bindings.TryGetValue(material, out var edge);
                for (int t = start; t < start + count; t += 3)
                {
                    string dest = material;
                    if (edge != null && edge.Images.Length == 1)
                    {
                        votes.Clear();
                        for (int c = 0; c < 3; c++)
                        {
                            int sv = shell.Triangles[t + c];
                            for (int k = shell.WeldOffsets[sv]; k < shell.WeldOffsets[sv + 1]; k++)
                            {
                                int bv = shell.WeldIndices[k];
                                if (bv < 0 || bv >= bn || vertexSubmesh[bv] < 0) continue;
                                votes.TryGetValue(vertexSubmesh[bv], out int n);
                                votes[vertexSubmesh[bv]] = n + 1;
                            }
                        }
                        string baseName = votes.Count == 0 ? null
                            : baseNames[votes.OrderByDescending(kv => kv.Value).First().Key];
                        if (baseName != null && bindings.TryGetValue(baseName, out var baseInfo) &&
                            !baseInfo.Images.Contains(edge.Images[0]))
                        {
                            dest = bindings.Where(kv => !used.Contains(kv.Key) &&
                                                        kv.Value.NodeCount == edge.NodeCount &&
                                                        kv.Value.Images.Length == 1 &&
                                                        baseInfo.Images.Contains(kv.Value.Images[0]))
                                           .Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal)
                                           .FirstOrDefault()
                                   ?? VariantFor(material, edge, baseInfo) ?? material;
                        }
                    }
                    var list = dest == material ? target : Group(dest);
                    list.Add(shell.Triangles[t]);
                    list.Add(shell.Triangles[t + 1]);
                    list.Add(shell.Triangles[t + 2]);
                    if (dest != material)
                    {
                        string key = $"{material}→{dest}";
                        moved.TryGetValue(key, out int n);
                        moved[key] = n + 1;
                    }
                }
            }
            if (moved.Count == 0) return;

            var tris = new List<int>(shell.Triangles.Length);
            shell.Submeshes.Clear();
            foreach (var (material, list) in groups)
            {
                if (list.Count == 0) continue;
                shell.Submeshes.Add((material, tris.Count, list.Count));
                tris.AddRange(list);
            }
            shell.Triangles = tris.ToArray();
            log.Add("描边按底层图集改挂：" + string.Join("，", moved.Select(kv => $"{kv.Key} {kv.Value} 面")));
        }

        // ---- 合并 -----------------------------------------------------------

        private static Mesh Merge(Mesh baseMesh, Shell shell, List<string> log)
        {
            int bn = baseMesh.vertexCount;
            int sn = shell.VertexCount;
            int total = bn + sn;

            var mesh = new Mesh
            {
                name = baseMesh.name + "+outline",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };

            var pos = new Vector3[total];
            Array.Copy(baseMesh.vertices, pos, bn);
            Array.Copy(shell.Positions, 0, pos, bn, sn);
            mesh.vertices = pos;

            var nrm = new Vector3[total];
            var baseNrm = baseMesh.normals;
            if (baseNrm != null && baseNrm.Length == bn) Array.Copy(baseNrm, nrm, bn);
            Array.Copy(shell.Normals, 0, nrm, bn, sn);
            mesh.normals = nrm;

            var uv = new Vector2[total];
            var baseUv = baseMesh.uv;
            if (baseUv != null && baseUv.Length == bn) Array.Copy(baseUv, uv, bn);
            Array.Copy(shell.Uvs, 0, uv, bn, sn);
            mesh.uv = uv;

            var bw = new BoneWeight[total];
            var baseBw = baseMesh.boneWeights;
            if (baseBw != null && baseBw.Length == bn) Array.Copy(baseBw, bw, bn);
            for (int i = 0; i < sn; i++)
                bw[bn + i] = AverageWeights(baseBw, shell, i);
            mesh.boneWeights = bw;
            mesh.bindposes = baseMesh.bindposes;

            mesh.subMeshCount = baseMesh.subMeshCount + shell.Submeshes.Count;
            for (int m = 0; m < baseMesh.subMeshCount; m++)
                mesh.SetTriangles(baseMesh.GetTriangles(m), m, false);
            for (int k = 0; k < shell.Submeshes.Count; k++)
            {
                var (_, start, count) = shell.Submeshes[k];
                var tris = new int[count];
                for (int i = 0; i < count; i++) tris[i] = shell.Triangles[start + i] + bn;
                mesh.SetTriangles(tris, baseMesh.subMeshCount + k, false);
            }

            CopyBlendShapes(baseMesh, mesh, shell, bn, sn, log);

            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // 焊接组里的权重逐骨骼相加，取前 4 个重新归一化。
        // 取平均而不是「挑最近的那个顶点」：描边顶点落在缝上时，两侧顶点可能绑在不同
        // 骨骼上，挑一个会让描边在动起来时从缝里甩出去。
        private static BoneWeight AverageWeights(BoneWeight[] baseBw, Shell shell, int i)
        {
            var acc = new Dictionary<int, float>();
            int a = shell.WeldOffsets[i], b = shell.WeldOffsets[i + 1];
            for (int k = a; k < b; k++)
            {
                int src = shell.WeldIndices[k];
                if (baseBw == null || src < 0 || src >= baseBw.Length) continue;
                var w = baseBw[src];
                Add(acc, w.boneIndex0, w.weight0);
                Add(acc, w.boneIndex1, w.weight1);
                Add(acc, w.boneIndex2, w.weight2);
                Add(acc, w.boneIndex3, w.weight3);
            }
            var top = acc.Where(kv => kv.Value > 0f)
                         .OrderByDescending(kv => kv.Value).Take(4).ToArray();
            float sum = top.Sum(kv => kv.Value);
            var r = new BoneWeight();
            if (sum <= 0f) { r.boneIndex0 = 0; r.weight0 = 1f; return r; }
            for (int k = 0; k < top.Length; k++)
            {
                float w = top[k].Value / sum;
                switch (k)
                {
                    case 0: r.boneIndex0 = top[k].Key; r.weight0 = w; break;
                    case 1: r.boneIndex1 = top[k].Key; r.weight1 = w; break;
                    case 2: r.boneIndex2 = top[k].Key; r.weight2 = w; break;
                    case 3: r.boneIndex3 = top[k].Key; r.weight3 = w; break;
                }
            }
            return r;
        }

        private static void Add(Dictionary<int, float> acc, int bone, float w)
        {
            if (w <= 0f) return;
            acc.TryGetValue(bone, out float cur);
            acc[bone] = cur + w;
        }

        private static void CopyBlendShapes(Mesh src, Mesh dst, Shell shell,
                                            int bn, int sn, List<string> log)
        {
            int n = src.blendShapeCount;
            if (n == 0) return;
            int total = bn + sn;
            var dv = new Vector3[bn];
            var dn = new Vector3[bn];
            var dt = new Vector3[bn];
            var ov = new Vector3[total];
            var on = new Vector3[total];
            var ot = new Vector3[total];

            for (int s = 0; s < n; s++)
            {
                string name = src.GetBlendShapeName(s);
                int frames = src.GetBlendShapeFrameCount(s);
                for (int f = 0; f < frames; f++)
                {
                    src.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    Array.Clear(ov, 0, total);
                    Array.Clear(on, 0, total);
                    Array.Clear(ot, 0, total);
                    Array.Copy(dv, ov, bn);
                    Array.Copy(dn, on, bn);
                    Array.Copy(dt, ot, bn);
                    for (int i = 0; i < sn; i++)
                    {
                        int a = shell.WeldOffsets[i], b = shell.WeldOffsets[i + 1];
                        if (b <= a) continue;
                        Vector3 v = Vector3.zero, nn = Vector3.zero, tt = Vector3.zero;
                        for (int k = a; k < b; k++)
                        {
                            int p = shell.WeldIndices[k];
                            if (p < 0 || p >= bn) continue;
                            v += dv[p]; nn += dn[p]; tt += dt[p];
                        }
                        float inv = 1f / (b - a);
                        ov[bn + i] = v * inv;
                        on[bn + i] = nn * inv;
                        ot[bn + i] = tt * inv;
                    }
                    dst.AddBlendShapeFrame(name, src.GetBlendShapeFrameWeight(s, f), ov, on, ot);
                }
            }
            log.Add($"BlendShape 已按焊接组扩到外壳：{n} 个");
        }
    }
}
