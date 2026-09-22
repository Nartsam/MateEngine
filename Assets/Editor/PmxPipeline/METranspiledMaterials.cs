// 装配由 Tools/PmxPipeline/transpile.py 生成的转译材质。
//
// 计划见 Docs/plans/2026-09-10-blender-graph-transpile.md Phase 1。分工是：Python
// 侧做图分析与代码生成，产出 `<角色>.bindings.json` + 每材质一个着色器；这一侧只
// 负责「按清单装配」——建 LUT 贴图、读贴图文件、建材质、按 Blender 材质名替换。
//
// 刻意不在这里做任何图语义判断：一旦 Unity 侧也开始理解节点图，两边就会各自演化。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public sealed class METranspiledMaterials
    {
        public readonly Dictionary<string, Material> ByName = new();
        public readonly List<string> Log = new();

        /// <summary>合成 pass 也要查视图变换表，这里把行号与参数一起交出去。</summary>
        public Texture2D Lut => _lut;
        public int LutRows { get; private set; }
        public int ViewTransformRow { get; private set; } = -1;
        public float ViewLo { get; private set; }
        public float ViewHi { get; private set; }

        private readonly JObject _data;
        private readonly string _imagesDir;
        private readonly Dictionary<string, Texture2D> _texCache = new();
        private readonly List<UnityEngine.Object> _owned = new();
        private Texture2D _lut;
        private bool _strict;

        private METranspiledMaterials(string bindingsPath, JObject data)
        {
            _data = data;
            string dir = (string)data["imagesDir"];
            // 清单里的贴图目录是相对仓库根写的（Build/... 不在 Assets 下，Unity 不会
            // 去导入它们，正好避免一堆 .meta 与导入设置的纠缠）
            _imagesDir = Path.IsPathRooted(dir) ? dir
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), dir));
        }

        public static METranspiledMaterials Load(string bindingsPath, bool strict = false)
        {
            if (!File.Exists(bindingsPath))
                throw new FileNotFoundException($"材质清单不存在：{bindingsPath}");
            var data = JObject.Parse(File.ReadAllText(bindingsPath, Encoding.UTF8));
            var self = new METranspiledMaterials(bindingsPath, data);
            self._strict = strict;

            // 生成的 .shader 可能是刚写出来的，先让 Unity 导入，否则 Shader.Find 取空
            AssetDatabase.Refresh();

            self.BuildLut(data["lut"] as JObject);
            foreach (var m in (JArray)data["materials"]) self.BuildMaterial((JObject)m);

            var unsupported = data["unsupported"] as JArray;
            if (unsupported != null && unsupported.Count > 0)
                foreach (var u in unsupported)
                    self.Say($"!! 未转译：{u["name"]} —— {u["reason"]}");
            self.Say($"已装配转译材质 {self.ByName.Count} 个");
            return self;
        }

        private void Say(string msg) => Log.Add(msg);

        // LUT 一张贴图装下所有一维表：宽 257（EEVEE 的 CM_TABLE + 1），高 = 表数。
        // 用 RGBAFloat 而不是 PNG：表值在线性域，8 位量化在 Filmic 曲线最陡的暗部会
        // 放大成可见色差。第 0 行固定是视图变换。
        private void BuildLut(JObject lut)
        {
            int w = (int)lut["width"];
            var rows = (JArray)lut["rows"];
            var flat = (JArray)lut["data"];
            int h = rows.Count;
            if (flat.Count != w * h * 4)
                throw new InvalidOperationException(
                    $"LUT 数据长度 {flat.Count} 与 {w}×{h}×4 不符");

            _lut = new Texture2D(w, h, TextureFormat.RGBAFloat, mipChain: false, linear: true)
            {
                name = "MELut",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color((float)flat[i * 4], (float)flat[i * 4 + 1],
                                  (float)flat[i * 4 + 2], (float)flat[i * 4 + 3]);
            _lut.SetPixels(px);
            _lut.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _owned.Add(_lut);

            LutRows = h;
            for (int i = 0; i < h; i++)
                if ((string)rows[i]["kind"] == "view") { ViewTransformRow = i; break; }
            var vt = _data["viewTransform"] as JObject;
            ViewLo = vt != null ? (float)vt["lo"] : -12.473931188f;
            ViewHi = vt != null ? (float)vt["hi"] : 4.026068812f;
            Say($"LUT 贴图 {w}×{h}：" + string.Join(", ", rows.Select(r => (string)r["name"])));
        }

        private void BuildMaterial(JObject m)
        {
            string name = (string)m["name"];
            string shaderName = (string)m["shader"];
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                if (_strict) throw new InvalidOperationException($"找不到着色器 {shaderName}（材质 {name}）");
                Say($"!! 找不到着色器 {shaderName}（材质 {name}），该材质保持原样");
                return;
            }

            var mat = new Material(shader) { name = $"{name} (transpiled)" };
            mat.SetTexture("_MELut", _lut);
            foreach (var t in (JArray)m["textures"])
            {
                string prop = (string)t["prop"];
                var tex = LoadTexture((string)t["file"], (bool)t["srgb"],
                                      (string)t["interpolation"], (string)t["wrap"]);
                if (tex != null) mat.SetTexture(prop, tex);
            }
            _owned.Add(mat);
            ByName[name] = mat;
        }

        private Texture2D LoadTexture(string file, bool srgb, string interpolation, string wrap)
        {
            // Smart / Cubic 走自写的双三次采样，它自己做 4 次抽头。开 mipmap 会让这
            // 四次抽头各自算出隐式 LOD，取到不同层级——所以这类贴图不生成 mipmap。
            bool bicubic = interpolation == "Smart" || interpolation == "Cubic";
            string key = $"{file}|{srgb}|{bicubic}|{wrap}";
            if (_texCache.TryGetValue(key, out var cached)) return cached;

            string path = Path.Combine(_imagesDir, file);
            if (!File.Exists(path))
            {
                if (_strict) throw new FileNotFoundException("转译贴图不存在", path);
                Say($"!! 贴图文件不存在：{path}");
                _texCache[key] = null;
                return null;
            }

            // linear: !srgb ——Blender 的 Image Texture 对 sRGB 图返回 scene-linear，
            // 对 Non-Color 返回原始值。交给硬件采样器做这件事就和 Blender 一致了。
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: !bicubic, linear: !srgb);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path), markNonReadable: false))
            {
                Say($"!! 贴图解码失败：{path}");
                UnityEngine.Object.DestroyImmediate(tex);
                if (_strict) throw new InvalidOperationException($"贴图解码失败：{path}");
                _texCache[key] = null;
                return null;
            }
            tex.name = Path.GetFileNameWithoutExtension(file);
            tex.wrapMode = wrap == "extend" ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            tex.filterMode = bicubic ? FilterMode.Bilinear : FilterMode.Trilinear;
            tex.anisoLevel = bicubic ? 0 : 8;
            tex.Apply(updateMipmaps: !bicubic, makeNoLongerReadable: false);
            _owned.Add(tex);
            _texCache[key] = tex;
            return tex;
        }

        /// <summary>
        /// 把三个方向 uniform 写进所有转译材质。它们在 Blender 侧由几何节点修改器写成
        /// 网格命名属性、实测全网格恒定，到这边就是三个 float3。
        /// </summary>
        public void SetDirections(Vector3 sunUnity, Vector3 frontUnity, Vector3 rightUnity)
        {
            foreach (var mat in ByName.Values)
            {
                mat.SetVector("_MESunDir", sunUnity);
                mat.SetVector("_MEHeadFront", frontUnity);
                mat.SetVector("_MEHeadRight", rightUnity);
            }
            Say($"方向 uniform：SUN=({sunUnity.x:F4}, {sunUnity.y:F4}, {sunUnity.z:F4}) " +
                $"FRONT=({frontUnity.x:F4}, {frontUnity.y:F4}, {frontUnity.z:F4}) " +
                $"RIGHT=({rightUnity.x:F4}, {rightUnity.y:F4}, {rightUnity.z:F4})");
        }

        /// <summary>
        /// 写 Generated（ORCO）坐标所需的包围盒。Blender 的 Generated 是物体包围盒
        /// 归一化坐标，程序化纹理（Noise / Voronoi）全靠它定位。
        ///
        /// 包围盒必须换算到 **Blender 物体空间**：Unity 的局部坐标已经过
        /// U = (-Bx, Bz, -By)，直接拿 Unity 的 bounds 会把轴对错，花纹方向就全变。
        /// </summary>
        public void SetGeneratedBounds(GameObject root)
        {
            Bounds? local = null;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var b = smr.sharedMesh.bounds;
                local = local.HasValue ? Encapsulate(local.Value, b) : b;
            }
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                local = local.HasValue ? Encapsulate(local.Value, b) : b;
            }
            if (!local.HasValue)
            {
                Say("!! 没找到网格，Generated 坐标退化为单位立方体");
                local = new Bounds(Vector3.zero, Vector3.one);
            }

            // U = (-Bx, Bz, -By) 的逆：B = (-Ux, -Uz, Uy)。它是带符号的轴置换，
            // 逐角点映射后取 min/max 即可。
            var lo = local.Value.min;
            var hi = local.Value.max;
            var a = new Vector3(-hi.x, -hi.z, lo.y);
            var c = new Vector3(-lo.x, -lo.z, hi.y);
            var bmin = Vector3.Min(a, c);
            var bmax = Vector3.Max(a, c);
            var size = bmax - bmin;
            var inv = new Vector3(
                Mathf.Abs(size.x) > 1e-9f ? 1f / size.x : 0f,
                Mathf.Abs(size.y) > 1e-9f ? 1f / size.y : 0f,
                Mathf.Abs(size.z) > 1e-9f ? 1f / size.z : 0f);

            foreach (var mat in ByName.Values)
            {
                mat.SetVector("_MEGenMin", bmin);
                mat.SetVector("_MEGenInvSize", inv);
            }
            Say($"Generated 包围盒（Blender 物体空间）min=({bmin.x:F4}, {bmin.y:F4}, {bmin.z:F4}) " +
                $"size=({size.x:F4}, {size.y:F4}, {size.z:F4})");
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            a.Encapsulate(b);
            return a;
        }

        /// <summary>
        /// 按材质名把渲染器上的材质换成转译材质。返回替换掉的槽位数。
        /// </summary>
        public int Apply(GameObject root)
        {
            var blendByName = new Dictionary<string, string>();
            foreach (var m in (JArray)_data["materials"])
                blendByName[(string)m["name"]] = (string)m["blendMethod"];

            int replaced = 0;
            var missing = new List<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string key = ResolveName(mats[i].name);
                    if (key == null)
                    {
                        missing.Add(mats[i].name);
                        continue;
                    }
                    var repl = ByName[key];
                    // 绘制顺序沿用现有管线排好的（它按 PMX 材质顺序排，脸部那几片共面
                    // 的眉毛/眼睛靠这个顺序才不会 z 冲突）。只有 Blender 标了 BLEND 的
                    // 材质要挪进透明队列——`眼睛2` 那个 bug 的一半原因就是它被排进了
                    // alpha-test 队列。
                    int q = mats[i].renderQueue;
                    if (blendByName.TryGetValue(key, out var bm) && bm == "BLEND")
                        q = 3000 + Mathf.Clamp(q - 2000, 0, 500);
                    if (repl.renderQueue != q) repl.renderQueue = q;
                    mats[i] = repl;
                    changed = true;
                    replaced++;
                }
                if (changed) r.sharedMaterials = mats;
            }
            foreach (var n in missing.Distinct())
                Say($"!! 清单里没有材质 {n}，该槽位保持原样");
            if (_strict && missing.Count > 0)
                throw new InvalidOperationException("模型使用了未转译的材质：" + string.Join(", ", missing.Distinct()));
            Say($"已替换材质槽 {replaced} 个");
            return replaced;
        }

        // Unity 侧材质名来自 PMX 材质名，重名时 UniqueMaterialName 会加后缀；
        // 实例化后还会带 " (Instance)"。
        private string ResolveName(string unityName)
        {
            string n = unityName;
            int i = n.IndexOf(" (Instance)", StringComparison.Ordinal);
            if (i >= 0) n = n.Substring(0, i);
            if (ByName.ContainsKey(n)) return n;
            var m = System.Text.RegularExpressions.Regex.Match(n, @"^(.*?)(?:[ _]\d+)$");
            if (m.Success && ByName.ContainsKey(m.Groups[1].Value)) return m.Groups[1].Value;
            return null;
        }

        public void SaveAssets(string folder)
        {
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            // 先保存贴图，后保存引用它们的材质。保留 Texture2D 原始格式与色彩空间，
            // 不经 PNG 往返，避免 LUT 量化或 sRGB 导入默认值改变节点语义。
            int index = 0;
            foreach (var texture in _owned.OfType<Texture2D>())
                AssetDatabase.CreateAsset(texture, $"{folder}/Texture{index++:D3}.asset");
            index = 0;
            foreach (var material in ByName.Values)
                AssetDatabase.CreateAsset(material, $"{folder}/Material{index++:D3}.mat");
            AssetDatabase.SaveAssets();
        }

        public void Dispose()
        {
            foreach (var o in _owned)
                if (o != null && !AssetDatabase.Contains(o)) UnityEngine.Object.DestroyImmediate(o);
            _owned.Clear();
            ByName.Clear();
        }

        public static Vector3 ParseVec(string s, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var p = s.Split(',');
            if (p.Length < 3) return fallback;
            return new Vector3(
                float.Parse(p[0], CultureInfo.InvariantCulture),
                float.Parse(p[1], CultureInfo.InvariantCulture),
                float.Parse(p[2], CultureInfo.InvariantCulture));
        }
    }
}
