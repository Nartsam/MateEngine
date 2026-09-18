// 差分验收闭环的 Unity 一侧：用与 Blender 基准图完全对齐的相机渲出对照图。
// 计划见 Docs/plans/2026-09-10-blender-graph-transpile.md Phase 0。
//
// Blender 一侧是 Tools/PmxPipeline/blender_apply_preset.py。本脚本读取它产出的
// report JSON，直接沿用其相机参数，从而免去两边各自估算取景带来的偏差。
//
// 批处理调用：
//   Unity.exe -batchmode -quit -projectPath <proj> \
//     -executeMethod MateEngine.PmxPipeline.MERefRenderer.RenderRef \
//     -pmx "<角色>.pmx" -blenderReport <blender侧report.json> \
//     -out <对照图.png> -report <unity侧report.json>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MateEngine.PmxPipeline
{
    public static class MERefRenderer
    {
        // ===== Blender ↔ Unity 坐标映射 =====
        //
        // 两边都从同一份 PMX 出发，各自的转换都是确定的：
        //   mmd_tools:        B = (px, pz, py) * 0.08      (importer.py: Vector(pv.co).xzy)
        //   PmxMeshBuilder:   U = (-px, py, -pz) * 0.08    (Conv)
        // 消去 PMX 坐标即得：
        //   U = (-B.x, B.z, -B.y)
        // 这是个行列式为 -1 的映射——正是右手系(Blender)到左手系(Unity)该有的样子。
        public static Vector3 BlenderToUnity(Vector3 b) => new(-b.x, b.z, -b.y);

        // Blender 侧相机方位角 yaw 的定义：yaw=0 时相机在 -Y 侧（模型正面）。
        // 把它的偏移向量 (-sin θ·d, -cos θ·d, 0) 过一遍上面的映射，得到 Unity 侧：
        //   (sin θ·d, 0, cos θ·d)
        // 于是 yaw=0 → 相机在 +Z，即 Unity 里模型正面朝 +Z。yaw 数值两边通用。
        public static Vector3 CameraOffset(float yawDeg, float dist)
        {
            float r = yawDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r) * dist, 0f, Mathf.Cos(r) * dist);
        }

        private const string DefaultOutputRoot = "Assets/PmxImported";

        private static string GetArg(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return null;
        }

        private static float GetFloatArg(string key, float fallback) =>
            float.TryParse(GetArg(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : fallback;

        private static int GetIntArg(string key, int fallback) =>
            int.TryParse(GetArg(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : fallback;

        private static readonly List<string> Log = new();

        private static void Say(string msg)
        {
            Log.Add(msg);
            Debug.Log("[MERefRenderer] " + msg);
            Console.Out.Write("[MERefRenderer] " + msg + "\n");
            Console.Out.Flush();
        }

        [MenuItem("MateEngine/PMX Pipeline/Render Reference View")]
        public static void RenderRefMenu()
        {
            if (!EditorUtility.DisplayDialog("渲染对照图",
                    "这会新建一个空场景（当前场景会被替换，未保存的改动将丢失）。继续？",
                    "继续", "取消"))
                return;
            RenderInternal();
        }

        // 批处理入口
        public static void RenderRef()
        {
            bool ok = RenderInternal();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool RenderInternal()
        {
            try
            {
                // 批处理下 Debug.Log 默认连带写一份调用栈，日志里每条会出现两遍
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);

                string outPng = GetArg("-out");
                if (string.IsNullOrEmpty(outPng))
                    throw new InvalidOperationException("需要 -out <file.png>");

                var blenderReport = LoadBlenderReport(GetArg("-blenderReport"));

                // 1) 拿到 prefab：要么用现成的，要么从 PMX 现建。
                //    现建时不走 PmxPipelineOptions.FromCommandLine()——那会读本机
                //    settings.json，把 presetPath 之类的陈旧值带进来，触发已弃用的
                //    Blender dump 路径。这里全部用显式参数。
                string prefabPath = GetArg("-prefab");
                if (string.IsNullOrEmpty(prefabPath))
                    prefabPath = BuildFromPmx(GetArg("-pmx"));

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                    throw new InvalidOperationException($"prefab 读取失败：{prefabPath}");
                Say($"prefab = {prefabPath}");

                // 2) 空场景，隔离掉当前打开场景里的灯光/天空盒
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Color.black;
                RenderSettings.skybox = null;
                RenderSettings.fog = false;

                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                root.transform.position = Vector3.zero;
                root.transform.rotation = Quaternion.identity;

                string pose = GetArg("-pose") ?? "bind";
                if (pose == "bind") RestoreBindPose(root);
                else Say($"保留 prefab 自带姿势（-pose {pose}）");

                var bounds = ComputeWorldBounds(root);
                Say($"Unity 世界包围盒 中心=({bounds.center.x:F4}, {bounds.center.y:F4}, " +
                    $"{bounds.center.z:F4}) 尺寸=({bounds.size.x:F4}, {bounds.size.y:F4}, {bounds.size.z:F4})");

                var report = new Dictionary<string, object>
                {
                    ["prefab"] = prefabPath,
                    ["bounds_center"] = V3(bounds.center),
                    ["bounds_size"] = V3(bounds.size),
                    ["pose"] = pose,
                };

                SetupSun(blenderReport, report);
                var cam = SetupCamera(bounds, blenderReport, report);

                int res = GetIntArg("-res", 1024);
                string bg = GetArg("-bg") ?? "transparent";
                Render(cam, res, bg, outPng);
                report["render"] = new Dictionary<string, object>
                {
                    ["path"] = Path.GetFullPath(outPng), ["res"] = res, ["bg"] = bg,
                    ["color_space"] = PlayerSettings.colorSpace.ToString(),
                };
                Say($"已渲染 {Path.GetFullPath(outPng)}");

                string idOut = GetArg("-idOut");
                if (!string.IsNullOrEmpty(idOut))
                    report["material_ids"] = RenderIdPass(root, cam, res, idOut);

                string reportPath = GetArg("-report");
                if (!string.IsNullOrEmpty(reportPath))
                {
                    report["unity"] = Application.unityVersion;
                    report["log"] = Log;
                    WriteJson(reportPath, report);
                    Say($"已写报告 {Path.GetFullPath(reportPath)}");
                }

                Say("完成");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MERefRenderer] 失败: {ex}");
                Console.Out.Write("[MERefRenderer] !! 失败: " + ex.Message + "\n");
                Console.Out.Flush();
                return false;
            }
        }

        private static string BuildFromPmx(string pmxPath)
        {
            if (string.IsNullOrEmpty(pmxPath))
                throw new InvalidOperationException("需要 -pmx <file.pmx> 或 -prefab <assetPath>");
            if (!File.Exists(pmxPath))
                throw new FileNotFoundException($"PMX 不存在：{pmxPath}");

            var options = new PmxPipelineOptions
            {
                PmxPath = pmxPath,
                OutputRoot = GetArg("-outputRoot") ?? DefaultOutputRoot,
                ModelName = GetArg("-modelName") ?? "",
                MatchMode = PmxMaterialMatchMode.TrimSuffix,
                Scale = GetFloatArg("-scale", PmxMeshBuilder.DefaultScale),
            };

            var model = new PmxReader().ReadFile(pmxPath);
            // renderPreset 传 null：预设的语义由节点图转译承担，不再走关键词猜测的 dump
            var built = PmxMeshBuilder.Build(model, pmxPath, options, null);
            if (built?.Prefab == null)
                throw new InvalidOperationException("PMX 构建失败");
            Say($"已从 PMX 构建：bones={built.BoneCount} submeshes={built.SubMeshCount} " +
                $"blendshapes={built.BlendShapeCount}");
            AssetDatabase.SaveAssets();
            return built.PrefabPath;
        }

        // 把骨架摆回 bindpose，也就是 PMX 的原始 A 姿势。
        //
        // 为什么需要：PmxHumanoid 为了让 Humanoid Avatar 的肌肉轴向正确，会在捕获
        // bindposes **之后**把手臂掰成 T 姿势（见 PmxHumanoid.cs 的 EnforceTPose），
        // prefab 的骨架因此停在 T 姿势。Blender 那边是 PMX 原始姿势，两者对不上，
        // 逐像素差分就没有意义。
        //
        // 由于 bindposes 是在掰之前捕获的，它精确保存了原始姿势：
        //   bindpose[i] = B_i⁻¹ · G   （B_i = 骨骼世界矩阵，G = SMR 所在物体的世界矩阵）
        // 于是 B_i = G · bindpose[i]⁻¹。逐骨骼写回这个世界矩阵即可还原。
        // prefab 本身不动——游戏侧仍然需要 T 姿势。
        private static void RestoreBindPose(GameObject root)
        {
            int total = 0;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                var bones = smr.bones;
                if (mesh == null || bones == null) continue;
                var bp = mesh.bindposes;
                if (bp == null || bp.Length == 0) continue;

                int n = Mathf.Min(bp.Length, bones.Length);
                var g = smr.transform.localToWorldMatrix;
                // 写的是世界矩阵，父级必须先于子级设置，否则父级的改动会带偏子级
                var order = Enumerable.Range(0, n)
                                      .Where(i => bones[i] != null)
                                      .OrderBy(HierarchyDepthOf(bones))
                                      .ToArray();
                foreach (int i in order)
                {
                    var m = g * bp[i].inverse;
                    bones[i].SetPositionAndRotation(m.GetColumn(3), m.rotation);
                }
                total += order.Length;
            }
            Say($"已恢复 bindpose（PMX 原始姿势），处理骨骼 {total} 根");
        }

        private static Func<int, int> HierarchyDepthOf(Transform[] bones) => i =>
        {
            int d = 0;
            for (var t = bones[i]; t != null; t = t.parent) d++;
            return d;
        };

        private static Bounds ComputeWorldBounds(GameObject root)
        {
            var rends = root.GetComponentsInChildren<Renderer>(false)
                            .Where(r => r.enabled && !(r is ParticleSystemRenderer)).ToList();
            if (rends.Count == 0)
                throw new InvalidOperationException("模型上没有可渲染的 Renderer");

            // 蒙皮网格的 bounds 默认是烘好的粗包围盒；打开 updateWhenOffscreen 才会
            // 按当前姿势算出贴合的世界包围盒。
            foreach (var smr in rends.OfType<SkinnedMeshRenderer>())
                smr.updateWhenOffscreen = true;

            var b = rends[0].bounds;
            for (int i = 1; i < rends.Count; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }

        private static void SetupSun(JObject blenderReport, Dictionary<string, object> report)
        {
            // 默认取 Blender 实测的 SUN（几何节点写在网格上的属性），过一遍坐标映射。
            // SUN 是「从表面指向光源」的方向，而 Unity 平行光的 forward 是光线传播
            // 方向，二者相反。
            Vector3 sunBlender = new(0f, -0.86603f, 0.5f);
            string argSun = GetArg("-sunBlender");
            if (!string.IsNullOrEmpty(argSun)) sunBlender = ParseV3(argSun);
            else if (TryGetVec(blenderReport, "drivers", "SUN", out var v)) sunBlender = v;

            var sunUnity = BlenderToUnity(sunBlender).normalized;
            var go = new GameObject("RefSun");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1f;
            light.shadows = LightShadows.None;
            go.transform.forward = -sunUnity;

            report["sun_blender"] = V3(sunBlender);
            report["sun_unity"] = V3(sunUnity);
            Say($"日光 Blender=({sunBlender.x:F5}, {sunBlender.y:F5}, {sunBlender.z:F5}) " +
                $"→ Unity=({sunUnity.x:F5}, {sunUnity.y:F5}, {sunUnity.z:F5})");
        }

        private static Camera SetupCamera(Bounds bounds, JObject blenderReport,
                                          Dictionary<string, object> report)
        {
            float yaw;
            float orthoScale;
            Vector3 center;

            var camSection = blenderReport?["cam"] as JObject;
            if (camSection != null)
            {
                // 用 Blender 侧算好的取景，两边像素才可能对齐
                yaw = (float)camSection["yaw_deg"];
                orthoScale = (float)camSection["ortho_scale"];
                center = BlenderToUnity(ToV3(camSection["center"]));
                Say($"取景沿用 Blender 报告：yaw={yaw:F1}° ortho_scale={orthoScale:F4}");

                // 交叉校验：两边的模型如果真是同一份，包围盒中心应当重合
                var d = center - bounds.center;
                Say($"包围盒中心偏差 = ({d.x:F4}, {d.y:F4}, {d.z:F4}) 米，模长 {d.magnitude:F4}");
                if (d.magnitude > 0.01f)
                    Say("!! 偏差超过 1 厘米——两边很可能不是同一份 PMX，差分结果不可信");
                report["center_delta"] = V3(d);
            }
            else
            {
                yaw = ParseView(GetArg("-view") ?? "front");
                float margin = GetFloatArg("-margin", 1.06f);
                orthoScale = Mathf.Max(bounds.size.y, Mathf.Max(bounds.size.x, bounds.size.z)) * margin;
                center = bounds.center;
                Say($"未提供 -blenderReport，自行取景：yaw={yaw:F1}° ortho_scale={orthoScale:F4}");
            }

            string viewArg = GetArg("-view");
            if (!string.IsNullOrEmpty(viewArg)) yaw = ParseView(viewArg);

            var go = new GameObject("RefCam");
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            // Blender 的 ortho_scale 是画面较长边的尺寸；Unity 的 orthographicSize 是
            // 纵向半高。正方形出图时二者关系就是除以 2。
            cam.orthographicSize = orthoScale * 0.5f;
            float dist = Mathf.Max(orthoScale * 4f, 10f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist * 3f;
            go.transform.position = center + CameraOffset(yaw, dist);
            go.transform.LookAt(center, Vector3.up);

            report["cam"] = new Dictionary<string, object>
            {
                ["yaw_deg"] = yaw,
                ["ortho_scale"] = orthoScale,
                ["orthographic_size"] = cam.orthographicSize,
                ["center"] = V3(center),
                ["position"] = V3(go.transform.position),
            };
            Say($"相机 yaw={yaw:F1}° orthographicSize={cam.orthographicSize:F4} " +
                $"位置=({go.transform.position.x:F4}, {go.transform.position.y:F4}, {go.transform.position.z:F4})");
            return cam;
        }

        // 与 Blender 脚本的 VIEWS 保持一致
        private static float ParseView(string view) => view switch
        {
            "front" => 0f,
            "back" => 180f,
            "left" => 90f,
            "right" => -90f,
            "three_quarter" => 35f,
            _ => float.Parse(view, CultureInfo.InvariantCulture),
        };

        private static void Render(Camera cam, int res, string bg, string outPng)
        {
            bool transparent = bg.Equals("transparent", StringComparison.OrdinalIgnoreCase);
            RenderToPng(cam, res, transparent ? new Color(0, 0, 0, 0) : ParseHexColor(bg),
                        outPng, msaa: 8);
        }

        private static void RenderToPng(Camera cam, int res, Color clear, string outPng, int msaa)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = clear;
            cam.allowHDR = true;

            // sRGB 读写：工程是 Linear 色彩空间，渲染在线性域进行、写出时转 sRGB，
            // 与 Blender 的输出编码一致（Blender 那边额外还有 Filmic，见 Phase 1）。
            var rt = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32,
                                       RenderTextureReadWrite.sRGB) { antiAliasing = msaa };
            var prev = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, false);
                tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
                tex.Apply();
                string dir = Path.GetDirectoryName(Path.GetFullPath(outPng));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(outPng, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                RenderTexture.active = prev;
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        // ===== 材质 ID 通道 =====
        //
        // 给每个子网格涂一个唯一的平涂色渲一遍，比对脚本据此把画面按材质分区。
        // 只需要 Unity 这一侧出这张图：两边取景已证明精确对齐（轮廓统计里「仅对照
        // 有」恒为 0 px），同一坐标的像素在两张图里属于同一块材质。
        //
        // 已知偏差：Blender 的描边有一部分压在轮廓**内侧**，这些像素会被算到它盖住
        // 的那个材质头上。这不是 bug——描边差异本来就该记在被它影响的材质名下。
        private static Dictionary<string, object> RenderIdPass(GameObject root, Camera cam,
                                                               int res, string outPng)
        {
            var ids = new Dictionary<string, int>();
            var table = new Dictionary<string, object>();
            var rends = root.GetComponentsInChildren<Renderer>(false)
                            .Where(r => r.enabled).ToArray();
            var backup = rends.ToDictionary(r => r, r => r.sharedMaterials);
            var temps = new List<UnityEngine.Object>();

            try
            {
                foreach (var r in rends)
                {
                    var src = backup[r];
                    var flat = new Material[src.Length];
                    for (int i = 0; i < src.Length; i++)
                    {
                        string name = src[i] != null ? BaseMaterialName(src[i].name) : $"__null{i}";
                        if (!ids.TryGetValue(name, out int id))
                        {
                            id = ids.Count;
                            ids[name] = id;
                            var c = PaletteColor(id);
                            // 调色板由这边写进报告，比对脚本直接读——省掉「两份实现
                            // 必须保持一致」这个隐患
                            table[name] = new Dictionary<string, object>
                            {
                                ["id"] = id,
                                ["rgb"] = new[] { (int)c.r, (int)c.g, (int)c.b },
                            };
                        }
                        flat[i] = MakeFlatMaterial(src[i], PaletteColor(id));
                        temps.Add(flat[i]);
                    }
                    r.sharedMaterials = flat;
                }

                // ID 通道必须关抗锯齿：边缘混色会产生调色板里不存在的颜色，分类时
                // 只能丢弃，等于把每块材质的边界像素都吃掉。
                RenderToPng(cam, res, new Color(0, 0, 0, 0), outPng, msaa: 1);
                Say($"已渲染材质 ID 通道 {Path.GetFullPath(outPng)}（{ids.Count} 种材质）");
                return table;
            }
            finally
            {
                foreach (var kv in backup) kv.Key.sharedMaterials = kv.Value;
                foreach (var t in temps) UnityEngine.Object.DestroyImmediate(t);
            }
        }

        // 调色板：黄金比例分割色相，相邻编号的颜色也隔得足够开，便于最近邻分类。
        // 比对脚本 compare_renders.py 里有一份等价实现，改这里必须同步改那边。
        public static Color32 PaletteColor(int index)
        {
            float h = (index * 0.6180339887f) % 1f;
            float s = index % 2 == 0 ? 1.0f : 0.65f;   // 再拉开一个维度
            float v = index % 3 == 0 ? 1.0f : 0.80f;
            var c = Color.HSVToRGB(h, s, v);
            return new Color32((byte)Mathf.Round(c.r * 255), (byte)Mathf.Round(c.g * 255),
                               (byte)Mathf.Round(c.b * 255), 255);
        }

        private const string IdPassShader = "Hidden/MateEngine/IdPass";

        private static Material MakeFlatMaterial(Material src, Color32 rgb)
        {
            var shader = Shader.Find(IdPassShader);
            if (shader == null)
                throw new InvalidOperationException(
                    $"找不到着色器 {IdPassShader}（应在 Assets/Editor/PmxPipeline/IdPass.shader）");
            var mat = new Material(shader);

            // 直接给 sRGB 值，**不要**自己转线性：Linear 色彩空间下，Unity 会把着色器
            // 里声明为 Color 类型的属性自动做 sRGB→线性转换，再自己转一次就是双重转换
            // （实测调色板 204 会变成 154，正好是 GammaToLinear(0.8)×255）。
            // 注意相机 backgroundColor 不同——它不走着色器属性系统，那边确实要手动转。
            mat.SetColor("_Color", new Color(rgb.r / 255f, rgb.g / 255f, rgb.b / 255f, 1f));

            // 头发、裙摆这类材质靠 alpha 镂空。ID 通道必须照样裁剪，否则镂空处会被
            // 涂实，把它后面的脸和腿误标成前者。
            var tex = src != null && src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            if (tex != null)
            {
                mat.SetFloat("_UseAlphaClip", 1f);
                mat.SetTexture("_MainTex", tex);
                mat.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                mat.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
                mat.SetFloat("_Cutoff", src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f);
            }
            return mat;
        }

        private static string BaseMaterialName(string name)
        {
            int i = name.IndexOf(" (Instance)", StringComparison.Ordinal);
            return i >= 0 ? name.Substring(0, i) : name;
        }

        private static Color ParseHexColor(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6) throw new InvalidOperationException($"-bg 需要 6 位十六进制：{hex}");
            // 工程是 Linear 色彩空间，backgroundColor 按线性值解释，所以要先反 sRGB
            float C(int i) => Mathf.GammaToLinearSpace(
                int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255f);
            return new Color(C(0), C(2), C(4), 1f);
        }

        // ===== 报告读写（用工程里已有的 Newtonsoft.Json）=====

        private static JObject LoadBlenderReport(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!File.Exists(path)) throw new FileNotFoundException($"Blender 报告不存在：{path}");
            var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            Say($"已读入 Blender 报告 {Path.GetFileName(path)}");
            return obj;
        }

        private static bool TryGetVec(JObject report, string a, string b, out Vector3 v)
        {
            v = default;
            var node = report?[a]?[b]?["value"];
            if (node is not JArray arr || arr.Count < 3) return false;
            v = new Vector3((float)arr[0], (float)arr[1], (float)arr[2]);
            return true;
        }

        private static Vector3 ToV3(JToken t) =>
            t is JArray a && a.Count >= 3
                ? new Vector3((float)a[0], (float)a[1], (float)a[2])
                : default;

        private static Vector3 ParseV3(string s)
        {
            var p = s.Split(',');
            return new Vector3(
                float.Parse(p[0], CultureInfo.InvariantCulture),
                float.Parse(p[1], CultureInfo.InvariantCulture),
                float.Parse(p[2], CultureInfo.InvariantCulture));
        }

        private static float[] V3(Vector3 v) => new[] { v.x, v.y, v.z };

        private static void WriteJson(string path, object value)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented),
                              new UTF8Encoding(false));
        }
    }
}
