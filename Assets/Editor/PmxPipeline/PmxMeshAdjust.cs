// 导出网格时的逐角色调整：Tools/PmxPipeline/mesh/<PMX 文件名>.mesh.json，只写要改的项，字段为 PmxMeshOptions 的
// 公开字段，拼错时报错（JsonUtility 会静默忽略）。没有配置文件时网格与 PMX 完全一致。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    [Serializable]
    public class PmxMeshOptions
    {
        // 材质名匹配该正则的顶点法线在 smoothNormalsRadius（米）内取平均。用于硬阈值明暗被相邻发束法线切碎的头发：
        // 预设的头发暗面是 dot(SUN, N) 的硬阈值，正视时阴影带连贯，鼠标跟踪转头后有的发束越过阈值、有的没有，
        // 阴影断成几截。
        public string smoothNormalsPattern = "";
        public float smoothNormalsRadius = 0f;
        // 平滑之后，同一批顶点的法线再向头部的近似形状（椭球）混合 proxyNormalsWeight（0～1）。平滑只能把相邻发束
        // 拉齐：垂在脸侧的发束发片朝前，照样被照亮，紧挨着的侧面头发却在暗面里，转头时成了硬边断层。按头部形状取
        // 法线，明暗分界顺着头的轮廓走。椭球取这批顶点里高于 proxyNormalsBone（PMX 骨骼名）的部分的包围盒。
        public float proxyNormalsWeight = 0f;
        public string proxyNormalsBone = "頭";
    }

    public static class PmxMeshAdjust
    {
        public static string ConfigPath(string modelName)
            => Path.Combine(PmxPipelineOptions.ProjectRoot, "Tools", "PmxPipeline", "mesh", modelName + ".mesh.json");

        /// <summary>读取逐角色网格配置；没有时返回默认值（不做任何调整）。</summary>
        public static PmxMeshOptions LoadConfig(string modelName, out string path)
        {
            path = ConfigPath(modelName);
            var options = new PmxMeshOptions();
            if (!File.Exists(path)) { path = null; return options; }
            string json = File.ReadAllText(path);
            var known = new HashSet<string>(typeof(PmxMeshOptions).GetFields().Select(f => f.Name));
            var unknown = JObject.Parse(json).Properties().Select(p => p.Name).Where(n => !known.Contains(n)).ToList();
            if (unknown.Count > 0) throw new InvalidDataException($"网格配置 {path} 有未知字段：{string.Join(", ", unknown)}");
            JsonUtility.FromJsonOverwrite(json, options);
            return options;
        }

        /// <summary>按配置调整网格（子网格序号与 PMX 材质一一对应），返回日志说明。bonePosition 按 PMX 骨骼名给出
        /// 与网格同一空间的骨骼位置，找不到时返回 null。</summary>
        public static string Apply(Mesh mesh, IReadOnlyList<string> materialNames, PmxMeshOptions options,
                                   Func<string, Vector3?> bonePosition)
        {
            int smoothed = SmoothNormals(mesh, materialNames, options.smoothNormalsPattern, options.smoothNormalsRadius);
            string log = $"smoothNormals={smoothed}";
            if (options.proxyNormalsWeight > 0f)
            {
                var head = bonePosition(options.proxyNormalsBone)
                           ?? throw new InvalidDataException($"找不到骨骼 {options.proxyNormalsBone}");
                int blended = ProxyNormals(mesh, materialNames, options.smoothNormalsPattern, head, options.proxyNormalsWeight,
                                           out var center, out var radius);
                log += $" proxyNormals={blended} weight={options.proxyNormalsWeight} center={center:F3} radius={radius:F3}";
            }
            return log;
        }

        // 只与朝向相近（夹角小于 90°）的邻居平均：发片常是正反两面各一份顶点，法线相反，混在一起会互相抵消。
        // 权重随距离线性衰减到半径处为 0，平滑范围的边界上不会出现台阶。
        public static int SmoothNormals(Mesh mesh, IReadOnlyList<string> materialNames, string pattern, float radius)
        {
            if (radius <= 0f || string.IsNullOrEmpty(pattern)) return 0;
            var selected = Selected(mesh, materialNames, pattern);
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var cells = selected.GroupBy(i => Vector3Int.FloorToInt(positions[i] / radius))
                                .ToDictionary(g => g.Key, g => g.ToArray());
            var smoothed = (Vector3[])normals.Clone();
            float r2 = radius * radius;
            foreach (int i in selected)
            {
                var cell = Vector3Int.FloorToInt(positions[i] / radius);
                var sum = Vector3.zero;
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                        {
                            if (!cells.TryGetValue(cell + new Vector3Int(x, y, z), out var neighbours)) continue;
                            foreach (int j in neighbours)
                            {
                                float d2 = (positions[j] - positions[i]).sqrMagnitude;
                                if (d2 >= r2 || Vector3.Dot(normals[j], normals[i]) <= 0f) continue;
                                sum += normals[j] * (1f - Mathf.Sqrt(d2) / radius);
                            }
                        }
                if (sum.sqrMagnitude > 1e-12f) smoothed[i] = sum.normalized;
            }
            mesh.normals = smoothed;
            return selected.Count;
        }

        // 椭球法线取 (p − c)/r² 的方向；低于椭球中心的顶点（披到肩背的长发）只取水平方向，即竖直的椭圆柱面。
        // 发片背面那份顶点的法线朝内，混合时椭球法线也取朝内的一侧，否则两者互相抵消。
        public static int ProxyNormals(Mesh mesh, IReadOnlyList<string> materialNames, string pattern, Vector3 headBone, float weight,
                                       out Vector3 center, out Vector3 radius)
        {
            center = radius = Vector3.zero;
            if (weight <= 0f || string.IsNullOrEmpty(pattern)) return 0;
            var selected = Selected(mesh, materialNames, pattern);
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var top = selected.Where(i => positions[i].y >= headBone.y).ToList();
            if (top.Count == 0) throw new InvalidDataException($"匹配 {pattern} 的顶点都低于头骨");
            var min = top.Aggregate(positions[top[0]], (m, i) => Vector3.Min(m, positions[i]));
            var max = top.Aggregate(positions[top[0]], (m, i) => Vector3.Max(m, positions[i]));
            center = (min + max) * 0.5f;
            radius = Vector3.Max((max - min) * 0.5f, Vector3.one * 1e-3f);
            var inverse = new Vector3(1f / (radius.x * radius.x), 1f / (radius.y * radius.y), 1f / (radius.z * radius.z));
            weight = Mathf.Clamp01(weight);
            foreach (int i in selected)
            {
                var d = positions[i] - center;
                if (d.y < 0f) d.y = 0f;
                var proxy = Vector3.Scale(d, inverse);
                if (proxy.sqrMagnitude < 1e-12f) continue;
                proxy.Normalize();
                if (Vector3.Dot(proxy, normals[i]) < 0f) proxy = -proxy;
                var blended = Vector3.Lerp(normals[i], proxy, weight);
                if (blended.sqrMagnitude > 1e-12f) normals[i] = blended.normalized;
            }
            mesh.normals = normals;
            return selected.Count;
        }

        private static HashSet<int> Selected(Mesh mesh, IReadOnlyList<string> materialNames, string pattern)
        {
            var regex = new Regex(pattern);
            var selected = new HashSet<int>();
            for (int sub = 0; sub < Mathf.Min(mesh.subMeshCount, materialNames.Count); sub++)
                if (regex.IsMatch(materialNames[sub] ?? "")) selected.UnionWith(mesh.GetTriangles(sub));
            if (selected.Count == 0) throw new InvalidDataException($"没有材质名匹配 {pattern}");
            return selected;
        }
    }
}
