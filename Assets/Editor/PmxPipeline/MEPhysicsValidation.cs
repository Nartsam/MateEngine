using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MateEngine.PmxPipeline
{
    // 物理转换的复现与对照。同一个实际 .me 实例化多份：包内原有的物理（current）、按 PMX 关节/刚体
    // 重新生成的 DynamicBone（rebuilt）、PMX 刚体原样交给 Bullet（rigid，ADR-0017）、不跑物理（rest）。
    // 各份用 App 的真实动画片段同步推进，量出物理带来的骨骼偏角、衣物与躯干交界处被拉开的距离、
    // 长链相对重力的下垂、骨骼嵌入身体碰撞体的深度，并在各项最差的一帧出图。
    //
    //   -executeMethod MateEngine.PmxPipeline.MEPhysicsValidation.Run
    //       -physicsModel <.me> -pmx <.pmx> [-physicsOut <目录>]
    //   其余开关（多组参数对比、场景筛选、腰部定时出图、逐粒子输出）见 Tools/PmxPipeline/README.md。
    //
    // 需要图形设备（不能带 -nographics）。DynamicBone 在编辑模式下不会自行运行，这里按 App 的
    // 调用顺序反射调用它的 FixedUpdate/Update/LateUpdate；用 AnimatePhysics 模式让每帧步长取
    // Time.fixedDeltaTime，物理按 60 Hz 累加步进，结果与帧率无关且可复现。
    public static class MEPhysicsValidation
    {
        private const float Scale = PmxMeshBuilder.DefaultScale;
        private const float LongChain = 0.2f;     // 超过该长度（米）的链统计相对重力的下垂
        private const float ClipDepth = 0.01f;    // 嵌入超过 1 cm 计作穿模帧
        private const int ImageSize = 768;

        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo FixedStep = typeof(DynamicBone).GetMethod("FixedUpdate", Private);
        private static readonly MethodInfo UpdateStep = typeof(DynamicBone).GetMethod("Update", Private);
        private static readonly MethodInfo LateStep = typeof(DynamicBone).GetMethod("LateUpdate", Private);
        private static readonly MethodInfo InitTransforms = typeof(DynamicBone).GetMethod("InitTransforms", Private, null, Type.EmptyTypes, null);
        private static readonly MethodInfo ResetParticles = typeof(DynamicBone).GetMethod("ResetParticlesPosition", Private, null, Type.EmptyTypes, null);

        private sealed class Variant
        {
            public string Name;
            public int Layer;
            public GameObject Root;
            public Animator Animator;
            public Transform[] Bones;
            public SkinnedMeshRenderer Skin;
            public Mesh Baked;
            public readonly List<Vector3> Vertices = new();
            public readonly List<Vector3> Normals = new();
            public DynamicBone[] Dynamics;
            public MmdPhysics Rigid;
            // 平移锁死的关节：两端刚体上各自的关节点（刚体局部坐标），两点分开的距离就是关节被拉开的量。
            public (int a, int b, Vector3 onA, Vector3 onB, string label)[] LockedJoints;
            public float[] BodyPoses, PreviousBodyPoses;
            public float[] BodyJitter;
            public readonly List<(int body, Transform shape)> Overlay = new();
            public readonly List<Vector3> Previous = new();
            // 相对不跑物理那份的顶点偏移，前两帧；用于量运动中的抖动。
            public Vector3[] Offset1, Offset2;
            public double SimulateMs;
            public int SimulateCalls;
            public MEModelDirections Directions;
            public PlayableGraph Graph;
            public AnimationMixerPlayable Mixer;
            public AnimationClipPlayable ClipA, ClipB;
            public AnimationClipPlayable[] Clips;
        }

        private sealed class Scenario
        {
            public string Name, ClipA, ClipB;
            public float Seconds;
            public bool Hold;                       // 片段 A 固定在第 0 帧
            public bool PlayB;                      // 片段 B 随时间播放，否则固定在第 0 帧
            public float BlendSeconds = 1.5f;       // 从 A 过渡到 B 的时长
            public Action<Variant, float> Additive; // 模拟 App 在动画之后叠加的转动
            // 按顺序轮播的片段（代替 A/B）：每段 SegmentSeconds 秒，开头 BlendSeconds 秒从上一段过渡过来。
            public string[] Sequence;
            public float SegmentSeconds;
        }

        private sealed class Chain
        {
            public string Label;
            public int Root;
            public List<int> Subtree;
            // 骨骼按 PMX 父子关系建成 Transform 层级，DynamicBone 的粒子父子与之相同。
            public Dictionary<int, int> Parents;
            public float Radius, Length;
            public List<(Vector3 a, Vector3 b, float radius, int bone, string name)> Colliders;
        }

        // 每个统计量：最大值、逐帧最大值的均值、超过阈值的帧数。
        private sealed class Stat
        {
            public float Max, Sum;
            public int Frames, Over;
            public void Add(float frameMax, bool over)
            {
                Max = Mathf.Max(Max, frameMax);
                Sum += frameMax;
                Frames++;
                if (over) Over++;
            }
            public float Mean => Frames > 0 ? Sum / Frames : 0f;
        }

        public static void Run()
        {
            int exitCode = 1;
            AssetBundle bundle = null;
            var variants = new List<Variant>();
            try
            {
                string modelPath = Path.GetFullPath(RequireArg("-physicsModel"));
                string pmxPath = Path.GetFullPath(RequireArg("-pmx"));
                string outDir = Arg("-physicsOut") ?? Path.Combine(PmxPipelineOptions.ProjectRoot, "Build", "PhysicsValidation",
                                                                  Path.GetFileNameWithoutExtension(modelPath));
                Directory.CreateDirectory(outDir);
                // -physicsDt 0.00606 按 App 的帧率（如 165 帧/秒）推进；缺省为 Time.fixedDeltaTime（1/50 秒）。
                float dt = float.TryParse(Arg("-physicsDt"), System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture, out var argDt) ? argDt : Time.fixedDeltaTime;
                Require(dt > 0.001f && dt <= 0.05f, $"帧步长异常：{dt}");

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                bundle = AssetBundle.LoadFromFile(modelPath);
                Require(bundle != null, "无法加载 .me");
                var prefab = bundle.LoadAllAssets<GameObject>().FirstOrDefault(g => g.GetComponent<Animator>() != null);
                Require(prefab != null, ".me 中没有带 Animator 的模型");
                var model = new PmxReader().ReadFile(pmxPath);
                var boneNames = PmxMeshBuilder.MakeUniqueBoneNames(model.Bones);

                // 第 0 份是不跑物理的参照，所有偏离都相对它计算。
                var log = new StringBuilder();
                // 刚体方案先套用与导出相同的逐角色配置（按 PMX 文件名查找），-physicsRigid 的设置再覆盖其上。
                string config = PmxRigidPhysics.LoadConfig(Path.GetFileNameWithoutExtension(pmxPath), out string configPath);
                log.AppendLine($"physics config: {(config != null ? configPath : "none")}");
                variants.Add(MakeVariant(prefab, "rest", 20, model, boneNames, null, null, null, log));
                variants.Add(MakeVariant(prefab, "current", 21, model, boneNames, null, null, null, log));
                int layer = 22;
                // -physicsTunings none 时不跑 DynamicBone 重建，只对照刚体物理。
                if (Arg("-physicsTunings") != "none")
                    foreach (var (name, tuning) in ParseTunings(Arg("-physicsTunings")))
                        variants.Add(MakeVariant(prefab, name, layer++, model, boneNames, tuning, null, null, log));
                foreach (var (name, rigid) in ParseRigid(Arg("-physicsRigid")))
                    variants.Add(MakeVariant(prefab, name, layer++, model, boneNames, null, rigid, config, log));
                // -physicsSmoothNormals "rigid-s2=^头发$:0.02;rigid-p7=^头发$:0.05:0.7"：给这些方案换一份调整过法线的网格
                // （PmxMeshAdjust，平滑半径单位为米，可选的第三项为按头部形状混合的比例），对照头发阴影。
                if (Arg("-physicsSmoothNormals") is string smoothSpec)
                    foreach (var entry in smoothSpec.Split(';').Where(x => x.Contains('=')))
                    {
                        var kv = entry.Split(new[] { '=' }, 2);
                        var parts = kv[1].Split(':');
                        var target = variants.FirstOrDefault(x => x.Name == kv[0].Trim());
                        Require(target != null && parts.Length >= 2, $"-physicsSmoothNormals 写法不对或没有方案 {kv[0]}");
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        var options = new PmxMeshOptions
                        {
                            smoothNormalsPattern = parts[0],
                            smoothNormalsRadius = float.Parse(parts[1], inv),
                            proxyNormalsWeight = parts.Length > 2 ? float.Parse(parts[2], inv) : 0f,
                        };
                        Vector3? BonePosition(string bone)
                        {
                            int i = model.Bones.FindIndex(b => b.NameLocal == bone);
                            return i >= 0 ? PmxMeshBuilder.Conv(model.Bones[i].Position, Scale) : null;
                        }
                        var mesh = UnityEngine.Object.Instantiate(target.Skin.sharedMesh);
                        string result = PmxMeshAdjust.Apply(mesh, model.Materials.Select(m => m.NameLocal).ToList(), options, BonePosition);
                        target.Skin.sharedMesh = mesh;
                        log.AppendLine($"{target.Name}: {result}, radius {options.smoothNormalsRadius} m");
                    }
                // -physicsTintMaterials "皮肤|裙内侧"：名称匹配的 PMX 材质各画成一种纯色（对应关系写进日志），看出图里露出来的是哪个材质。
                if (Arg("-physicsTintMaterials") is string tintPattern)
                {
                    var palette = new[] { Color.green, Color.blue, Color.yellow, Color.cyan, Color.red, Color.magenta, Color.white, Color.black };
                    var tints = new Dictionary<string, Material>();
                    foreach (var v in variants)
                    {
                        var mats = v.Skin.sharedMaterials;
                        for (int i = 0; i < Mathf.Min(mats.Length, model.Materials.Count); i++)
                        {
                            string name = model.Materials[i].NameLocal;
                            if (!System.Text.RegularExpressions.Regex.IsMatch(name, tintPattern)) continue;
                            if (!tints.TryGetValue(name, out var tint))
                            {
                                var color = palette[tints.Count % palette.Length];
                                tints[name] = tint = new Material(Shader.Find("Unlit/Color")) { color = color };
                                log.AppendLine($"tint {name}: {color}");
                            }
                            mats[i] = tint;
                        }
                        v.Skin.sharedMaterials = mats;
                    }
                }
                var chains = BuildChains(model);
                log.AppendLine($"chains={chains.Count} longChains={chains.Count(c => c.Length > LongChain)} " +
                               $"dt={dt:F4}s frames/s={1f / dt:F0}");
                foreach (var v in variants)
                    log.AppendLine(v.Rigid != null
                        ? $"{v.Name}: MmdPhysics bodies={v.Rigid.bodies.Length} joints={v.Rigid.joints.Length} boneWrite={v.Rigid.boneWrite} " +
                          $"solverIterations={v.Rigid.solverIterations} jointStopErp={v.Rigid.jointStopErp}"
                        : $"{v.Name}: DynamicBone={v.Dynamics.Length} colliders={v.Root.GetComponentsInChildren<DynamicBoneCollider>(true).Length}");

                var seams = SeamVertices(variants[0].Skin, chains, model);
                var anchors = AnchorVertices(variants[0].Skin, chains, model);
                var materials = MaterialVertices(variants[0].Skin, model);
                var tears = TearPairs(variants[0].Skin, model);
                var pokes = PokePairs(variants[0].Skin, model, Arg("-physicsPokeThrough"));
                if (pokes != null) log.AppendLine($"poke-through pairs: {pokes.Sum(kv => kv.Value.Length)} in {pokes.Count} groups");
                log.AppendLine($"tear pairs: {tears.Sum(kv => kv.Value.Length)} in {tears.Count} material pairs");
                foreach (var s in anchors)
                    log.AppendLine($"anchor material {s.Key}: {s.Value.Length} vertices");
                foreach (var s in seams)
                    log.AppendLine($"seam material {s.Key}: {s.Value.Length} vertices");

                var report = new StringBuilder();
                // -physicsScenarios rest,drag 只跑指定场景，便于反复对比。
                var only = Arg("-physicsScenarios")?.Split(',').Select(x => x.Trim()).ToHashSet();
                foreach (var scenario in Scenarios().Where(sc => only == null || only.Contains(sc.Name)))
                    RunScenario(scenario, variants, chains, seams, anchors, materials, tears, pokes, dt, outDir, report, model);
                // 验证装置每帧步长 1/50 秒、物理按 1/60 秒步进，平均每帧约 1.2 个子步。
                foreach (var v in variants.Where(x => x.SimulateCalls > 0))
                    report.AppendLine($"{v.Name}: 刚体物理每帧平均 {v.SimulateMs / v.SimulateCalls:F2} ms（{v.SimulateCalls} 帧）");

                string text = log + "\n" + report;
                File.WriteAllText(Path.Combine(outDir, "summary.txt"), text, new UTF8Encoding(false));
                Debug.Log("[PhysicsValidation]\n" + text);
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PhysicsValidation] 失败：{ex}");
            }
            finally
            {
                foreach (var v in variants)
                {
                    if (v.Rigid != null) v.Rigid.Shutdown();
                    if (v.Graph.IsValid()) v.Graph.Destroy();
                    if (v.Baked != null) UnityEngine.Object.DestroyImmediate(v.Baked);
                    if (v.Root != null) UnityEngine.Object.DestroyImmediate(v.Root);
                }
                if (bundle != null) bundle.Unload(true);
            }
            if (Application.isBatchMode) EditorApplication.Exit(exitCode);
        }

        private static IEnumerable<Scenario> Scenarios()
        {
            const string anims = "Assets/MATE ENGINE - Animations/";
            string idle = anims + "PET_IDLE/PET_IDLE.anim";
            yield return new Scenario { Name = "rest", ClipA = idle, Seconds = 3f, Hold = true };
            // 待机动画照常播放（呼吸、轻微摆动），App 里大部分时间是这个状态。
            yield return new Scenario { Name = "idle", ClipA = idle, Seconds = 8f };
            yield return new Scenario { Name = "head", ClipA = idle, Seconds = 8f, Hold = true, Additive = HeadSweep };
            // 与 App 一样从待机用 0.25 秒过渡进拖拽：直接从拖拽姿势开始时，大腿已经压在前裙里，刚体一开始就嵌在大腿内。
            yield return new Scenario { Name = "drag", ClipA = idle, Hold = true, ClipB = anims + "PET_MISC/PET_DRAGGING.anim", PlayB = true,
                                        BlendSeconds = 0.25f, Seconds = 10f, Additive = DragSway };
            yield return new Scenario { Name = "dance1", ClipA = anims + "PET_DANCING/PET_DANCING.anim", Seconds = 14f };
            yield return new Scenario { Name = "dance4", ClipA = anims + "PET_DANCING/PET_DANCING_4.anim", Seconds = 9f };
            yield return new Scenario { Name = "dance9", ClipA = anims + "PET_DANCING/PET_DANCING_9.anim", Seconds = 9f };
            // 从站立过渡到坐姿，覆盖抬腿时大腿顶到裙摆/衣摆的过程。
            yield return new Scenario { Name = "sit", ClipA = idle, ClipB = anims + "PET_SITTING/PET_SITTING_DEMO.anim", Seconds = 5f, Hold = true };
            // App 的待机：主场景里 AvatarAnimatorController 设为 20 段轮播、每段 10 秒、过渡 1 秒，片段取载入模型所用控制器
            // 的女性待机混合树。-physicsIdleSeconds 4 缩短每段时长，便于快速过一遍。
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float segment = float.TryParse(Arg("-physicsIdleSeconds"), System.Globalization.NumberStyles.Float, inv, out var s) ? s : 10f;
            var idles = IdleSequence(anims + "AvatarAnimatorControllerV2 1.controller", "Female");
            yield return new Scenario { Name = "idles", Sequence = idles, SegmentSeconds = segment, BlendSeconds = 1f, Seconds = segment * idles.Length };
        }

        // 控制器里以 IdleIndex 为参数、名为 treeName 的混合树，按阈值排好的片段路径。
        private static string[] IdleSequence(string controllerPath, string treeName)
        {
            var controller = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(controllerPath);
            Require(controller != null, $"缺少动画控制器 {controllerPath}");
            IEnumerable<UnityEditor.Animations.BlendTree> Trees(Motion motion)
            {
                if (motion is not UnityEditor.Animations.BlendTree tree) yield break;
                yield return tree;
                foreach (var child in tree.children)
                    foreach (var sub in Trees(child.motion)) yield return sub;
            }
            IEnumerable<UnityEditor.Animations.AnimatorState> States(UnityEditor.Animations.AnimatorStateMachine machine)
                => machine.states.Select(x => x.state).Concat(machine.stateMachines.SelectMany(x => States(x.stateMachine)));
            var found = controller.layers.SelectMany(l => States(l.stateMachine)).SelectMany(st => Trees(st.motion))
                                  .FirstOrDefault(tr => tr.name == treeName && tr.blendParameter == "IdleIndex");
            Require(found != null, $"{controllerPath} 中没有以 IdleIndex 为参数的混合树 {treeName}");
            return found.children.OrderBy(c => c.threshold).Select(c => AssetDatabase.GetAssetPath(c.motion)).ToArray();
        }

        // 鼠标跟踪：头部左右 ±45°、上下 ±30°（AvatarMouseTracking 的上限），约 110°/s。
        private static void HeadSweep(Variant v, float t)
        {
            var head = v.Animator.GetBoneTransform(HumanBodyBones.Head);
            float yaw = 45f * Mathf.Sin(2f * Mathf.PI * 0.4f * t);
            float pitch = 30f * Mathf.Sin(2f * Mathf.PI * 0.27f * t);
            // -physicsHeadRoll 15：再叠加左右歪头（度），待机动画里常见。
            float roll = float.TryParse(Arg("-physicsHeadRoll"), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var r) ? r * Mathf.Sin(2f * Mathf.PI * 0.19f * t) : 0f;
            head.localRotation = Quaternion.Euler(-pitch, yaw, roll) * head.localRotation;
        }

        // 拖拽：AvatarSwayController 按窗口速度给髋部叠加侧倾（≤25°）与前后倾（≤12°）。
        private static void DragSway(Variant v, float t)
        {
            var hips = v.Animator.GetBoneTransform(HumanBodyBones.Hips);
            var root = v.Root.transform;
            float leanZ = 25f * Mathf.Sin(2f * Mathf.PI * 1.1f * t);
            float leanX = 12f * Mathf.Sin(2f * Mathf.PI * 0.7f * t + 1f);
            hips.rotation = Quaternion.AngleAxis(leanZ, root.forward) * Quaternion.AngleAxis(leanX, root.right) * hips.rotation;
        }

        // -physicsTunings "名称:字段=值,字段=值;名称2:..."，字段为 PmxPhysicsTuning 的公开字段；缺省时只比一组默认值。
        private static List<(string name, PmxPhysicsTuning tuning)> ParseTunings(string spec)
        {
            var list = new List<(string, PmxPhysicsTuning)>();
            if (string.IsNullOrWhiteSpace(spec)) { list.Add(("rebuilt", new PmxPhysicsTuning())); return list; }
            foreach (var entry in spec.Split(';').Where(s => s.Trim().Length > 0))
            {
                var parts = entry.Split(new[] { ':' }, 2);
                var tuning = new PmxPhysicsTuning();
                if (parts.Length > 1)
                    foreach (var assignment in parts[1].Split(',').Where(s => s.Contains('=')))
                    {
                        var kv = assignment.Split('=');
                        var field = typeof(PmxPhysicsTuning).GetField(kv[0].Trim()) ?? throw new ArgumentException($"未知参数 {kv[0]}");
                        object value = field.FieldType == typeof(bool) ? bool.Parse(kv[1])
                                     : (object)float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
                        field.SetValue(tuning, value);
                    }
                list.Add(("rebuilt-" + parts[0].Trim(), tuning));
            }
            return list;
        }

        // 一组刚体物理设置：字段属于 PmxRigidPhysicsOptions 的在导出前生效，属于 MmdPhysics 的在组件建好后生效。
        private sealed class RigidSpec
        {
            public readonly List<(FieldInfo field, object value)> Assignments = new();

            public void ApplyTo(object target)
            {
                foreach (var (field, value) in Assignments)
                    if (field.DeclaringType == target.GetType()) field.SetValue(target, value);
            }
        }

        // -physicsRigid "名称:字段=值,…;…"，字段为 MmdPhysics 或 PmxRigidPhysicsOptions 的公开字段（浮点、整数、
        // 布尔、枚举名、字符串，整数数组用 + 连接如 maskOffGroups=0+1）；缺省时跑一组默认设置，取 none 时不跑。
        private static List<(string name, RigidSpec spec)> ParseRigid(string spec)
        {
            var list = new List<(string, RigidSpec)>();
            if (spec == "none") return list;
            if (string.IsNullOrWhiteSpace(spec)) { list.Add(("rigid", new RigidSpec())); return list; }
            foreach (var entry in spec.Split(';').Where(s => s.Trim().Length > 0))
            {
                var parts = entry.Split(new[] { ':' }, 2);
                var rigid = new RigidSpec();
                if (parts.Length > 1)
                    foreach (var assignment in parts[1].Split(',').Where(s => s.Contains('=')))
                    {
                        var kv = assignment.Split('=');
                        string key = kv[0].Trim(), text = kv[1].Trim();
                        var field = typeof(MmdPhysics).GetField(key) ?? typeof(PmxRigidPhysicsOptions).GetField(key)
                                    ?? throw new ArgumentException($"未知参数 {key}");
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        object value = field.FieldType == typeof(bool) ? bool.Parse(text)
                                     : field.FieldType == typeof(int) ? int.Parse(text, inv)
                                     : field.FieldType == typeof(string) ? text
                                     : field.FieldType == typeof(int[]) ? text.Split('+').Select(x => int.Parse(x, inv)).ToArray()
                                     : field.FieldType.IsEnum ? Enum.Parse(field.FieldType, text)
                                     : (object)float.Parse(text, inv);
                        rigid.Assignments.Add((field, value));
                    }
                list.Add(("rigid-" + parts[0].Trim(), rigid));
            }
            return list;
        }

        private static Variant MakeVariant(GameObject prefab, string name, int layer, PmxModel model, string[] boneNames,
                                           PmxPhysicsTuning rebuild, RigidSpec rigid, string config, StringBuilder log)
        {
            var go = UnityEngine.Object.Instantiate(prefab);
            go.name = name;
            var v = new Variant { Name = name, Layer = layer, Root = go, Animator = go.GetComponent<Animator>() };
            var map = new Dictionary<string, Transform>();
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t == go.transform || IsPhysicsHelper(t, go.transform)) continue;
                if (!map.ContainsKey(t.name)) map[t.name] = t;
            }
            v.Bones = boneNames.Select(n => map.TryGetValue(n, out var t) ? t : throw new InvalidOperationException($"找不到骨骼 {n}")).ToArray();
            CheckScale(model, v.Bones);

            if (rebuild != null || rigid != null)
            {
                foreach (var c in go.GetComponentsInChildren<DynamicBoneCollider>(true)) UnityEngine.Object.DestroyImmediate(c.gameObject);
                foreach (var holder in go.GetComponentsInChildren<DynamicBone>(true).Select(d => d.gameObject).Distinct().ToList())
                    UnityEngine.Object.DestroyImmediate(holder);
                foreach (var existing in go.GetComponentsInChildren<MmdPhysics>(true)) UnityEngine.Object.DestroyImmediate(existing);
            }
            if (rigid != null)
            {
                var options = new PmxRigidPhysicsOptions();
                if (config != null) JsonUtility.FromJsonOverwrite(config, options);
                rigid.ApplyTo(options);
                var built = PmxRigidPhysics.Build(model, go, v.Bones, Scale, options);
                v.Rigid = go.GetComponent<MmdPhysics>();
                Require(v.Rigid != null, "PMX 没有刚体");
                if (config != null) JsonUtility.FromJsonOverwrite(config, v.Rigid);
                rigid.ApplyTo(v.Rigid);
                // -physicsMaskOff "rigid-x=^Bone_C1_Head_Hair_F:0"：名称匹配的刚体不再与该分组碰撞，用来试某处碰撞是不是抖动的来源。
                foreach (var entry in (Arg("-physicsMaskOff") ?? "").Split(';').Where(x => x.StartsWith(name + "=")))
                {
                    var spec = entry.Substring(name.Length + 1);
                    int colon = spec.LastIndexOf(':');
                    var regex = new System.Text.RegularExpressions.Regex(spec.Substring(0, colon));
                    int bit = int.Parse(spec.Substring(colon + 1));
                    int changed = 0;
                    foreach (var body in v.Rigid.bodies.Where(b => regex.IsMatch(b.name ?? "")))
                    {
                        body.mask &= ~(1 << bit);
                        changed++;
                    }
                    log.AppendLine($"{name}: {changed} bodies no longer collide with group {bit}");
                }
                log.AppendLine($"{name}: {JsonUtility.ToJson(options)} {JsonUtility.ToJson(v.Rigid).Split(new[] { ",\"bodies\"" }, StringSplitOptions.None)[0]}}}\n" +
                               $"  bodies={built.Bodies} joints={built.Joints} pruned={built.Pruned} anchored={built.Anchored} loosened={built.Loosened} offsetLocks={built.OffsetLocks} followed={built.Followed} maskedOff={built.MaskedOff}");
            }
            else if (rebuild != null)
            {
                var result = PmxPhysics.Build(model, go.transform, v.Bones, Scale, rebuild);
                log.AppendLine($"{name}: {JsonUtility.ToJson(rebuild)}\n  components={result.DynamicBones} chains={result.ChainRoots} " +
                               $"colliders={result.Colliders} skipped={result.SkippedChains} rigid={result.RigidChains} " +
                               $"restExcluded={result.RestExclusions} idleExcluded={result.IdleExclusions}");
            }
            if (name == "rest")
                foreach (var existing in go.GetComponentsInChildren<MmdPhysics>(true)) UnityEngine.Object.DestroyImmediate(existing);
            v.Dynamics = name == "rest" ? Array.Empty<DynamicBone>() : go.GetComponentsInChildren<DynamicBone>(true);
            if (name == "current" && v.Rigid == null) v.Rigid = go.GetComponent<MmdPhysics>();
            if (v.Rigid != null)
            {
                Require(v.Rigid.Initialize(), "物理库加载失败");
                var bodies = v.Rigid.bodies;
                Vector3 On(int body, MmdPhysics.Joint j) => Quaternion.Inverse(bodies[body].rotation) * (j.position - bodies[body].position);
                v.LockedJoints = v.Rigid.joints
                    .Where(j => j.linearLower == Vector3.zero && j.linearUpper == Vector3.zero && (bodies[j.bodyA].mode != 0 || bodies[j.bodyB].mode != 0))
                    .Select(j => (j.bodyA, j.bodyB, On(j.bodyA, j), On(j.bodyB, j), Label(bodies[j.bodyB].name)))
                    .ToArray();
                v.BodyPoses = new float[bodies.Length * 7];
                v.PreviousBodyPoses = new float[bodies.Length * 7];
                v.BodyJitter = new float[bodies.Length];
            }
            foreach (var db in v.Dynamics)
            {
                db.m_UpdateMode = DynamicBone.UpdateMode.AnimatePhysics;
                db.SetupParticles();
            }
            v.Skin = go.GetComponentInChildren<SkinnedMeshRenderer>();
            // 批处理里整个验证都在同一帧内完成，蒙皮默认每帧只算一次，之后的出图都会定格在第一次渲染的
            // 姿势上（此前的对照图都受此影响；数值指标用 BakeMesh 在 CPU 上按当前骨骼计算，不受影响）。
            v.Skin.forceMatrixRecalculationPerRender = true;
            v.Baked = new Mesh();
            v.Directions = go.GetComponent<MEModelDirections>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.gameObject.layer = v.Layer;
            v.Animator.applyRootMotion = false;
            v.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (v.Rigid != null && Arg("-physicsShowBodies") is string pattern) AddOverlay(v, new System.Text.RegularExpressions.Regex(pattern));
            return v;
        }

        // -physicsShowBodies <正则>：名称匹配的动态刚体（橙）与会和它们碰撞的运动学刚体（青）画成半透明形状，
        // 不受深度遮挡，叠在模型上看刚体是否真的挡住了网格。
        private static void AddOverlay(Variant v, System.Text.RegularExpressions.Regex pattern)
        {
            var bodies = v.Rigid.bodies;
            var shown = Enumerable.Range(0, bodies.Length).Where(i => bodies[i].mode != 0 && pattern.IsMatch(bodies[i].name)).ToList();
            bool Collides(MmdPhysics.Body a, MmdPhysics.Body b) => ((1 << a.group) & b.mask) != 0 && ((1 << b.group) & a.mask) != 0;
            shown.AddRange(Enumerable.Range(0, bodies.Length).Where(i => bodies[i].mode == 0 && shown.Any(k => Collides(bodies[i], bodies[k]))).ToList());
            var shader = Shader.Find("Hidden/Internal-Colored");
            Material Make(Color c)
            {
                var m = new Material(shader) { color = c, hideFlags = HideFlags.DontSave };
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                m.SetInt("_ZWrite", 0);
                m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                return m;
            }
            var dynamicMaterial = Make(new Color(1f, 0.5f, 0f, 0.35f));
            var kinematicMaterial = Make(new Color(0f, 0.8f, 1f, 0.25f));
            float unit = v.Rigid.unitLength;
            foreach (int i in shown)
            {
                var b = bodies[i];
                var type = b.shape == 0 ? PrimitiveType.Sphere : b.shape == 1 ? PrimitiveType.Cube : PrimitiveType.Capsule;
                var go = GameObject.CreatePrimitive(type);
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                go.name = "overlay " + b.name;
                go.layer = v.Layer;
                go.transform.SetParent(v.Root.transform, false);
                // Unity 的胶囊默认半径 0.5、总高 2，Bullet 的胶囊高度不含两端半球。
                go.transform.localScale = (b.shape == 0 ? Vector3.one * (2f * b.size.x)
                                          : b.shape == 1 ? 2f * b.size
                                          : new Vector3(2f * b.size.x, (b.size.y + 2f * b.size.x) * 0.5f, 2f * b.size.x)) * unit;
                go.GetComponent<Renderer>().sharedMaterial = b.mode == 0 ? kinematicMaterial : dynamicMaterial;
                v.Overlay.Add((i, go.transform));
            }
        }

        private static bool IsPhysicsHelper(Transform t, Transform root)
        {
            for (var p = t; p != null && p != root; p = p.parent)
                if (p.name == "DynamicBones" || p.name.StartsWith("DBCollider_", StringComparison.Ordinal)) return true;
            return false;
        }

        // 导出时的缩放必须与这里一致，否则碰撞体几何与粒子半径都会错位。
        private static void CheckScale(PmxModel model, Transform[] bones)
        {
            int neck = model.Bones.FindIndex(b => b.NameLocal == "首");
            int head = model.Bones.FindIndex(b => b.NameLocal == "頭");
            Require(neck >= 0 && head >= 0, "PMX 缺少 首/頭");
            float expected = (model.Bones[head].Position - model.Bones[neck].Position).magnitude * Scale;
            float actual = (bones[head].position - bones[neck].position).magnitude;
            Require(Mathf.Abs(actual - expected) < expected * 0.01f, $"模型缩放与 {Scale} 不符：{actual} vs {expected}");
        }

        private static List<Chain> BuildChains(PmxModel model)
        {
            var dynamicBodies = PmxPhysics.DynamicBodiesByBone(model);
            var found = PmxPhysics.FindChains(model, dynamicBodies);
            var inChain = new HashSet<int>(found.SelectMany(c => c.subtree));
            var statics = PmxPhysics.StaticColliders(model, inChain, Scale);
            Vector3 Bind(int bone) => PmxMeshBuilder.Conv(model.Bones[bone].Position, Scale);
            var chains = new List<Chain>();
            foreach (var (root, subtree) in found)
            {
                if (subtree.Count < 2) continue;
                var bodies = PmxPhysics.ChainBodies(model, dynamicBodies, subtree);
                float radius = bodies.Average(PmxPhysics.BodyRadius) * Scale;
                var particles = subtree.Skip(1).Select(Bind).ToList();
                var length = new Dictionary<int, float> { [root] = 0f };
                foreach (int bone in subtree.Skip(1))
                    length[bone] = length[model.Bones[bone].ParentIndex] + (Bind(bone) - Bind(model.Bones[bone].ParentIndex)).magnitude;
                // 与导出端相同：只统计按分组掩码会碰撞、且绑定姿态下没有嵌入的身体碰撞体。
                var colliders = statics
                    .Where(c => bodies.Any(b => PmxPhysics.Collides(b, c.body)))
                    .Where(c => particles.All(p => c.radius + radius - PmxPhysics.DistanceToSegment(p, c.a, c.b) <= 0.002f))
                    .Select(c => (c.a - Bind(c.body.BoneIndex), c.b - Bind(c.body.BoneIndex), c.radius, c.body.BoneIndex, c.body.NameLocal))
                    .ToList();
                chains.Add(new Chain
                {
                    Label = Label(model.Bones[root].NameLocal), Root = root, Subtree = subtree,
                    Parents = subtree.Skip(1).ToDictionary(b => b, b => model.Bones[b].ParentIndex),
                    Radius = radius, Length = length.Values.Max(), Colliders = colliders,
                });
            }
            return chains;
        }

        // 去掉序号，把同一组链（如 qz_0_0…qz_0_10）归到一个标签。
        private static string Label(string name)
        {
            var s = new string((name ?? "").Where(c => !char.IsDigit(c)).ToArray());
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s.Trim('_');
        }

        // 同时绑在物理骨骼与非物理骨骼上的顶点，就是衣物与躯干交界处：物理把它们拉离躯干时露出下面的皮肤。
        private static Dictionary<string, int[]> SeamVertices(SkinnedMeshRenderer skin, List<Chain> chains, PmxModel model)
        {
            var physicsBones = new HashSet<int>(chains.SelectMany(c => c.Subtree));
            var weights = skin.sharedMesh.boneWeights;
            var result = new Dictionary<string, int[]>();
            var materials = skin.sharedMaterials;
            for (int sub = 0; sub < skin.sharedMesh.subMeshCount; sub++)
            {
                var verts = skin.sharedMesh.GetTriangles(sub).Distinct().Where(i =>
                {
                    var w = weights[i];
                    float onPhysics = 0f, offPhysics = 0f;
                    void Add(int bone, float weight)
                    {
                        if (weight <= 0f) return;
                        if (physicsBones.Contains(bone)) onPhysics += weight; else offPhysics += weight;
                    }
                    Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1);
                    Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
                    return onPhysics >= 0.05f && offPhysics >= 0.05f;
                }).ToArray();
                if (verts.Length == 0) continue;
                // 前面的子网格与 PMX 材质一一对应，之后是并入的描边外壳。
                string name = sub < model.Materials.Count ? model.Materials[sub].NameLocal
                            : sub < materials.Length && materials[sub] != null ? "描边:" + materials[sub].name : $"submesh{sub}";
                result[result.ContainsKey(name) ? $"{name}#{sub}" : name] = verts;
            }
            return result;
        }

        // 各 PMX 材质的全部顶点，用于按材质统计静止时的抖动。
        private static Dictionary<string, int[]> MaterialVertices(SkinnedMeshRenderer skin, PmxModel model)
        {
            var result = new Dictionary<string, int[]>();
            for (int sub = 0; sub < Mathf.Min(skin.sharedMesh.subMeshCount, model.Materials.Count); sub++)
            {
                string name = model.Materials[sub].NameLocal;
                result[result.ContainsKey(name) ? $"{name}#{sub}" : name] = skin.sharedMesh.GetTriangles(sub).Distinct().ToArray();
            }
            return result;
        }

        // 绑定姿态下位置重合、骨骼权重不同的顶点对（材质交界、UV 接缝处 PMX 会把顶点拆成几份）。两份被拉开
        // 就是网格裂开、露出后面的东西；按「材质|材质」分组。
        private static Dictionary<string, (int a, int b)[]> TearPairs(SkinnedMeshRenderer skin, PmxModel model)
        {
            var mesh = skin.sharedMesh;
            var positions = mesh.vertices;
            var weights = mesh.boneWeights;
            var material = new string[positions.Length];
            for (int sub = Mathf.Min(mesh.subMeshCount, model.Materials.Count) - 1; sub >= 0; sub--)
                foreach (int i in mesh.GetTriangles(sub)) material[i] = model.Materials[sub].NameLocal;
            float Weight(BoneWeight w, int bone) => (w.boneIndex0 == bone ? w.weight0 : 0f) + (w.boneIndex1 == bone ? w.weight1 : 0f) +
                                                    (w.boneIndex2 == bone ? w.weight2 : 0f) + (w.boneIndex3 == bone ? w.weight3 : 0f);
            bool SameWeights(BoneWeight x, BoneWeight y)
            {
                float diff = 0f;
                foreach (int bone in new[] { x.boneIndex0, x.boneIndex1, x.boneIndex2, x.boneIndex3, y.boneIndex0, y.boneIndex1, y.boneIndex2, y.boneIndex3 }.Distinct())
                    diff += Mathf.Abs(Weight(x, bone) - Weight(y, bone));
                return diff < 0.02f;
            }
            var result = new Dictionary<string, List<(int, int)>>();
            // 量化到 0.01 mm：拆开的顶点坐标通常逐位相同，这里只容忍浮点舍入。
            foreach (var group in Enumerable.Range(0, positions.Length).Where(i => material[i] != null)
                                            .GroupBy(i => (Mathf.RoundToInt(positions[i].x * 1e5f), Mathf.RoundToInt(positions[i].y * 1e5f),
                                                           Mathf.RoundToInt(positions[i].z * 1e5f))))
            {
                var list = group.ToList();
                for (int x = 0; x < list.Count; x++)
                    for (int y = x + 1; y < list.Count; y++)
                    {
                        int a = list[x], b = list[y];
                        if (SameWeights(weights[a], weights[b])) continue;
                        string key = string.CompareOrdinal(material[a], material[b]) <= 0 ? $"{material[a]}|{material[b]}" : $"{material[b]}|{material[a]}";
                        if (!result.TryGetValue(key, out var pairs)) result[key] = pairs = new List<(int, int)>();
                        pairs.Add((a, b));
                    }
            }
            return result.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        }

        // -physicsPokeThrough "^皮肤2$>^(衣饰|衣服)$"：内层材质的每个顶点配上 1 cm 内最近的外层顶点，只留绑定姿态下
        // 内层在外层表面之下的配对，按「内层材质/内层顶点的主骨骼」分组。动画中内层越过外层顶点的切面，即皮肤从
        // 衣服里穿出来；网格裂开量不到这种情况，因为两层本来就是分开的网格。蒙皮本身会让很多地方越过零点几毫米，
        // 要看分组里的最大值与它在不跑物理时是否同样存在。
        private static Dictionary<string, (int inner, int outer)[]> PokePairs(SkinnedMeshRenderer skin, PmxModel model, string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            var parts = spec.Split('>');
            Require(parts.Length == 2, $"-physicsPokeThrough 应写成 内层正则>外层正则：{spec}");
            var mesh = skin.sharedMesh;
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var weights = mesh.boneWeights;
            var materialOf = new Dictionary<int, string>();
            HashSet<int> Vertices(string pattern)
            {
                var set = new HashSet<int>();
                for (int sub = 0; sub < Mathf.Min(mesh.subMeshCount, model.Materials.Count); sub++)
                    if (System.Text.RegularExpressions.Regex.IsMatch(model.Materials[sub].NameLocal, pattern))
                        foreach (int i in mesh.GetTriangles(sub))
                            if (set.Add(i)) materialOf[i] = model.Materials[sub].NameLocal;
                return set;
            }
            var inner = Vertices(parts[0]);
            var outer = Vertices(parts[1]).Except(inner).ToArray();
            const float radius = 0.01f;
            var cells = outer.GroupBy(i => Vector3Int.FloorToInt(positions[i] / radius)).ToDictionary(g => g.Key, g => g.ToArray());
            var pairs = new Dictionary<string, List<(int, int)>>();
            foreach (int i in inner)
            {
                var cell = Vector3Int.FloorToInt(positions[i] / radius);
                int best = -1;
                float bestDistance = radius * radius;
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                        {
                            if (!cells.TryGetValue(cell + new Vector3Int(x, y, z), out var candidates)) continue;
                            foreach (int j in candidates)
                            {
                                float d = (positions[i] - positions[j]).sqrMagnitude;
                                if (d < bestDistance) { bestDistance = d; best = j; }
                            }
                        }
                if (best < 0 || Vector3.Dot(positions[i] - positions[best], normals[best]) >= 0f) continue;
                string key = $"{materialOf[i]}/{model.Bones[weights[i].boneIndex0].NameLocal}";
                if (!pairs.TryGetValue(key, out var list)) pairs[key] = list = new List<(int, int)>();
                list.Add((i, best));
            }
            return pairs.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        }

        // 最大权重落在链根骨上的顶点：根骨本应随父骨（MMD 里根节多为锁死），一旦被带着转，
        // 贴着躯干的衣物边缘（如短裙腰头）就会离开身体。
        private static Dictionary<string, int[]> AnchorVertices(SkinnedMeshRenderer skin, List<Chain> chains, PmxModel model)
        {
            // 头发链不算：这里关心的是衣物边缘离开躯干，头发根部随头转动不会露出皮肤。
            int head = model.Bones.FindIndex(b => b.NameLocal == "頭");
            bool UnderHead(int bone)
            {
                for (int p = model.Bones[bone].ParentIndex; p >= 0; p = model.Bones[p].ParentIndex)
                    if (p == head) return true;
                return false;
            }
            var roots = new HashSet<int>(chains.Where(c => !UnderHead(c.Root)).Select(c => c.Root));
            var weights = skin.sharedMesh.boneWeights;
            var result = new Dictionary<string, int[]>();
            for (int sub = 0; sub < Mathf.Min(skin.sharedMesh.subMeshCount, model.Materials.Count); sub++)
            {
                var verts = skin.sharedMesh.GetTriangles(sub).Distinct().Where(i => roots.Contains(weights[i].boneIndex0) && weights[i].weight0 >= 0.5f).ToArray();
                if (verts.Length == 0) continue;
                string name = model.Materials[sub].NameLocal;
                result[result.ContainsKey(name) ? $"{name}#{sub}" : name] = verts;
            }
            return result;
        }

        private static void RunScenario(Scenario scenario, List<Variant> variants, List<Chain> chains,
                                        Dictionary<string, int[]> seams, Dictionary<string, int[]> anchors,
                                        Dictionary<string, int[]> materials, Dictionary<string, (int a, int b)[]> tears,
                                        Dictionary<string, (int inner, int outer)[]> pokes,
                                        float dt, string outDir, StringBuilder report, PmxModel model)
        {
            var clips = (scenario.Sequence ?? new[] { scenario.ClipA, scenario.ClipB }.Where(p => p != null).ToArray())
                        .Select(LoadClip).ToList();
            foreach (var v in variants) Bind(v, clips);

            // 先回到动画第 0 帧并让粒子贴合，避免上一个场景的残留运动。
            foreach (var v in variants) foreach (var db in v.Dynamics) InitTransforms.Invoke(db, null);
            foreach (var v in variants) v.Rigid?.RestorePose();
            foreach (var v in variants) Pose(v, scenario, 0f);
            foreach (var v in variants) foreach (var db in v.Dynamics) ResetParticles.Invoke(db, null);
            foreach (var v in variants) v.Rigid?.ResetToPose();

            var deviation = new Dictionary<(string, string), Stat>();
            var seam = new Dictionary<(string, string), Stat>();
            var hang = new Dictionary<(string, string), Stat>();
            var depth = new Dictionary<(string, string), Stat>();
            var rootDev = new Dictionary<(string, string), Stat>();
            var stretch = new Dictionary<(string, string), Stat>();
            var anchor = new Dictionary<(string, string), Stat>();
            var apart = new Dictionary<(string, string), Stat>();
            var jitter = new Dictionary<(string, string), Stat>();
            var shake = new Dictionary<(string, string), Stat>();
            var tear = new Dictionary<(string, string), Stat>();
            // 裂开超过 0.3 cm 的时段（各方案分开记），对照轮播场景的片段序号找出是哪段动作。
            var tearSpans = new Dictionary<string, List<(float from, float to, float max, string key)>>();
            float worstTear = 0f, worstTearTime = 0f;
            string worstTearKey = null, worstTearVariant = null;
            // -physicsTrackBones 胸上2：名称匹配的骨骼相对不跑物理时的位移与转角，直接看某个部位被拉到哪里去了。
            var tracked = Arg("-physicsTrackBones") is string trackPattern
                ? Enumerable.Range(0, model.Bones.Count).Where(b => System.Text.RegularExpressions.Regex.IsMatch(model.Bones[b].NameLocal, trackPattern)).ToList()
                : new List<int>();
            var trackMove = new Dictionary<(string, string), Stat>();
            var trackTurn = new Dictionary<(string, string), Stat>();
            // 指定骨骼相对不跑物理的转动，逐帧转角的变化量（转动的二阶差分）：平滑摆动很小，来回抖才大。
            var trackShake = new Dictionary<(string, string), Stat>();
            var trackLast = new Dictionary<(string, int), (Quaternion rel, Quaternion step)>();
            // 每根指定骨骼抖得最厉害的几个时刻，对照动画看跳动是不是由某个动作引起的。
            var trackPeaks = new Dictionary<(string, string), List<(float angle, float time)>>();
            // -physicsTrackCsv 1：指定骨骼的逐帧偏离另存为 track-<场景>.csv（转动向量取在不跑物理那份骨骼的局部轴上，单位度），
            // 看偏离随时间怎样变、与头部转动是什么关系。
            var trackCsv = Arg("-physicsTrackCsv") != null && tracked.Count > 0 ? new StringBuilder("t,variant,bone,angle,rx,ry,rz\n") : null;
            var trackRestLast = new Dictionary<int, Quaternion>();
            foreach (var v in variants) v.Offset1 = v.Offset2 = null;
            var worstAnchor = new Dictionary<string, float>();
            Stat Get(Dictionary<(string, string), Stat> d, string variant, string key)
                => d.TryGetValue((variant, key), out var s) ? s : d[(variant, key)] = new Stat();
            float worstSeam = 0f, worstTilt = 0f, worstDepth = 0f, renderedTear = 0f, renderedPoke = 0f;
            var poke = new Dictionary<(string, string), Stat>();
            var pokeSpans = new Dictionary<string, List<(float from, float to, float max, string key)>>();
            // -physicsPokeFocus 皮肤2/下半身：时段列表与出图只看名称匹配的分组。
            var pokeFocus = Arg("-physicsPokeFocus") is string focusPattern ? new System.Text.RegularExpressions.Regex(focusPattern) : null;
            // 出图跟踪最长的一条链（寻梦者即麻花辫）。
            var probe = chains.Where(c => c.Length > LongChain).OrderByDescending(c => c.Length).FirstOrDefault();

            int frames = Mathf.RoundToInt(scenario.Seconds / dt);
            int snapshots = int.TryParse(Arg("-physicsSnapshots"), out var snapCount) ? snapCount : 0;
            // -physicsVideo dance1,drag：这些场景逐帧把各方案并排渲染，用 ffmpeg 合成视频看动态效果。
            using var video = Arg("-physicsVideo")?.Split(',').Select(x => x.Trim()).Contains(scenario.Name) == true
                ? new FrameStrip(Path.Combine(outDir, $"video-{scenario.Name}"), variants.Count)
                : null;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            float ArgSeconds(string key, float fallback)
                => float.TryParse(Arg(key), System.Globalization.NumberStyles.Float, invariant, out var x) ? x : fallback;
            int videoEvery = Mathf.Max(1, int.TryParse(Arg("-physicsVideoEvery"), out var every) ? every : 1);
            float videoFrom = ArgSeconds("-physicsVideoFrom", 0f), videoTo = ArgSeconds("-physicsVideoTo", float.MaxValue);
            int stillFrames = 0;
            foreach (var v in variants.Where(x => x.Rigid != null)) Array.Clear(v.BodyJitter, 0, v.BodyJitter.Length);
            for (int f = 1; f <= frames; f++)
            {
                float t = f * dt;
                // 姿势固定且不叠加转动的场景，最后 2 秒物理应已静止，逐帧位移即抖动。
                bool still = scenario.Hold && scenario.Additive == null && t >= scenario.Seconds - 2f;
                if (still) stillFrames++;
                foreach (var v in variants) foreach (var db in v.Dynamics) FixedStep.Invoke(db, null);
                foreach (var v in variants) v.Rigid?.RestorePose();
                foreach (var v in variants) Pose(v, scenario, t);
                foreach (var v in variants) foreach (var db in v.Dynamics) UpdateStep.Invoke(db, null);
                foreach (var v in variants) foreach (var db in v.Dynamics) LateStep.Invoke(db, null);
                foreach (var v in variants.Where(x => x.Rigid != null))
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    v.Rigid.Simulate(dt);
                    v.SimulateMs += watch.Elapsed.TotalMilliseconds;
                    v.SimulateCalls++;

                    v.Rigid.ReadBodyPoses(v.BodyPoses);
                    float unit = v.Rigid.unitLength * Mathf.Abs(v.Root.transform.lossyScale.x);
                    var frameApart = new Dictionary<string, float>();
                    foreach (var (a, b, onA, onB, label) in v.LockedJoints)
                    {
                        float d = (BodyPoint(v.BodyPoses, a, onA) - BodyPoint(v.BodyPoses, b, onB)).magnitude * unit;
                        frameApart[label] = Mathf.Max(frameApart.TryGetValue(label, out var m) ? m : 0f, d);
                    }
                    foreach (var kv in frameApart) Get(apart, v.Name, kv.Key).Add(kv.Value, kv.Value > 0.01f);
                    if (still)
                        for (int i = 0; i < v.BodyJitter.Length; i++)
                            v.BodyJitter[i] += (BodyPoint(v.BodyPoses, i, Vector3.zero) - BodyPoint(v.PreviousBodyPoses, i, Vector3.zero)).magnitude * unit;
                    Array.Copy(v.BodyPoses, v.PreviousBodyPoses, v.BodyPoses.Length);
                    foreach (var (body, shape) in v.Overlay)
                    {
                        int o = body * 7;
                        shape.SetPositionAndRotation(new Vector3(v.BodyPoses[o], v.BodyPoses[o + 1], v.BodyPoses[o + 2]) * unit,
                                                     new Quaternion(v.BodyPoses[o + 3], v.BodyPoses[o + 4], v.BodyPoses[o + 5], v.BodyPoses[o + 6]));
                    }
                }

                foreach (var v in variants)
                {
                    v.Skin.BakeMesh(v.Baked);
                    v.Baked.GetVertices(v.Vertices);
                    if (pokes != null)
                    {
                        v.Baked.GetNormals(v.Normals);
                        float max = 0f;
                        int argMax = -1;
                        string where = null;
                        foreach (var kv in pokes)
                        {
                            float groupMax = 0f;
                            foreach (var (a, b) in kv.Value)
                            {
                                float d = Vector3.Dot(v.Vertices[a] - v.Vertices[b], v.Normals[b]);
                                if (d > groupMax) groupMax = d;
                                if (d > max && (pokeFocus == null || pokeFocus.IsMatch(kv.Key))) { max = d; argMax = a; where = kv.Key; }
                            }
                            Get(poke, v.Name, kv.Key).Add(groupMax, groupMax > 0.001f);
                        }
                        if (max > 0.001f)
                        {
                            if (!pokeSpans.TryGetValue(v.Name, out var spans)) pokeSpans[v.Name] = spans = new();
                            if (spans.Count > 0 && t - spans[spans.Count - 1].to < 0.2f)
                            {
                                var last = spans[spans.Count - 1];
                                spans[spans.Count - 1] = (last.from, t, Mathf.Max(last.max, max), max > last.max ? where : last.key);
                            }
                            else spans.Add((t, t, max, where));
                        }
                        if (max > renderedPoke * 1.05f && max > 0.001f)
                        {
                            renderedPoke = max;
                            RenderAll(variants, outDir, $"{scenario.Name}-poke-{v.Name}", v.Vertices[argMax], ViewToward(variants[0], v.Vertices[argMax]), 0.12f);
                        }
                    }
                    if (still && v != variants[0] && v.Previous.Count == v.Vertices.Count)
                        foreach (var kv in materials)
                        {
                            float max = 0f;
                            foreach (int i in kv.Value) max = Mathf.Max(max, (v.Vertices[i] - v.Previous[i]).sqrMagnitude);
                            Get(jitter, v.Name, kv.Key).Add(Mathf.Sqrt(max), false);
                        }
                    // 物理带来的偏移逐帧的二阶差分：动画本身的运动已减掉，平滑的摆动二阶差分很小，来回跳才大。
                    if (v != variants[0] && v.Vertices.Count == variants[0].Vertices.Count)
                    {
                        var offset = new Vector3[v.Vertices.Count];
                        for (int i = 0; i < offset.Length; i++) offset[i] = v.Vertices[i] - variants[0].Vertices[i];
                        if (v.Offset2 != null && t >= 1f)
                            foreach (var kv in materials)
                            {
                                float max = 0f;
                                foreach (int i in kv.Value) max = Mathf.Max(max, (offset[i] - 2f * v.Offset1[i] + v.Offset2[i]).sqrMagnitude);
                                Get(shake, v.Name, kv.Key).Add(Mathf.Sqrt(max), false);
                            }
                        v.Offset2 = v.Offset1;
                        v.Offset1 = offset;
                    }
                    v.Previous.Clear();
                    v.Previous.AddRange(v.Vertices);
                }

                var rest = variants[0];
                if (trackCsv != null)
                    foreach (int b in tracked)
                    {
                        // 不跑物理那份记的是骨骼本身的转动角速度（°/s，轴取在骨骼局部），对照偏离是不是随它变。
                        var now = rest.Bones[b].rotation;
                        if (trackRestLast.TryGetValue(b, out var before))
                        {
                            var spin = Quaternion.Inverse(before) * now;
                            if (spin.w < 0f) spin = new Quaternion(-spin.x, -spin.y, -spin.z, -spin.w);
                            float speed = Angle(spin) / dt;
                            var axis = new Vector3(spin.x, spin.y, spin.z);
                            var vec = axis.sqrMagnitude > 1e-12f ? axis.normalized * speed : Vector3.zero;
                            trackCsv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4},{1},{2},{3:F3},{4:F3},{5:F3},{6:F3}",
                                                              t, rest.Name, model.Bones[b].NameLocal, speed, vec.x, vec.y, vec.z));
                        }
                        trackRestLast[b] = now;
                    }
                foreach (var v in variants.Skip(1))
                    foreach (int b in tracked)
                    {
                        string key = model.Bones[b].NameLocal;
                        float move = (v.Bones[b].position - rest.Bones[b].position).magnitude;
                        Get(trackMove, v.Name, key).Add(move, move > 0.01f);
                        Get(trackTurn, v.Name, key).Add(Quaternion.Angle(v.Bones[b].rotation, rest.Bones[b].rotation), false);
                        var rel = v.Bones[b].rotation * Quaternion.Inverse(rest.Bones[b].rotation);
                        if (trackCsv != null)
                        {
                            var local = Quaternion.Inverse(rest.Bones[b].rotation) * v.Bones[b].rotation;
                            if (local.w < 0f) local = new Quaternion(-local.x, -local.y, -local.z, -local.w);
                            float angle = Angle(local);
                            var axis = new Vector3(local.x, local.y, local.z);
                            var vec = axis.sqrMagnitude > 1e-12f ? axis.normalized * angle : Vector3.zero;
                            trackCsv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4},{1},{2},{3:F3},{4:F3},{5:F3},{6:F3}",
                                                              t, v.Name, key, angle, vec.x, vec.y, vec.z));
                        }
                        if (trackLast.TryGetValue((v.Name, b), out var last))
                        {
                            var step = rel * Quaternion.Inverse(last.rel);
                            if (t >= 1f)
                            {
                                float shakeAngle = Angle(step * Quaternion.Inverse(last.step));
                                Get(trackShake, v.Name, key).Add(shakeAngle, false);
                                if (!trackPeaks.TryGetValue((v.Name, key), out var peaks)) trackPeaks[(v.Name, key)] = peaks = new();
                                peaks.Add((shakeAngle, t));
                                if (peaks.Count > 64) { peaks.Sort((x, y) => y.angle.CompareTo(x.angle)); peaks.RemoveRange(16, peaks.Count - 16); }
                            }
                            trackLast[(v.Name, b)] = (rel, step);
                        }
                        else trackLast[(v.Name, b)] = (rel, Quaternion.identity);
                    }
                if (f == frames / 2)
                {
                    // 姿态抽查：各场景中间时刻的头与双手位置（模型根节点空间），便于核对出图是否与骨骼一致。
                    Vector3 Local(HumanBodyBones b) => rest.Root.transform.InverseTransformPoint(rest.Animator.GetBoneTransform(b).position);
                    report.AppendLine($"[{scenario.Name}] t={t:F1}s 头={Local(HumanBodyBones.Head):F2} 左手={Local(HumanBodyBones.LeftHand):F2} 右手={Local(HumanBodyBones.RightHand):F2}");
                }
                float frameSeam = 0f, frameTilt = 0f, frameDepth = 0f, frameTear = 0f;
                Vector3 tearAt = Vector3.zero;
                Vector3 seamAt = Vector3.zero, depthAt = Vector3.zero;
                foreach (var v in variants)
                {
                    foreach (var chain in chains)
                    {
                        if (v != rest)
                        {
                            float maxDev = 0f;
                            foreach (int bone in chain.Subtree.Skip(1))
                            {
                                int parent = chain.Parents[bone];
                                var a = v.Bones[bone].position - v.Bones[parent].position;
                                var b = rest.Bones[bone].position - rest.Bones[parent].position;
                                if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f) continue;
                                float angle = Vector3.Angle(a, b);
                                maxDev = Mathf.Max(maxDev, angle);
                            }
                            Get(deviation, v.Name, chain.Label).Add(maxDev, false);
                            float firstDev = 0f;
                            foreach (int bone in chain.Subtree.Skip(1).Where(b => chain.Parents[b] == chain.Root))
                            {
                                var a = v.Bones[bone].position - v.Bones[chain.Root].position;
                                var b = rest.Bones[bone].position - rest.Bones[chain.Root].position;
                                if (a.sqrMagnitude > 1e-10f && b.sqrMagnitude > 1e-10f) firstDev = Mathf.Max(firstDev, Vector3.Angle(a, b));
                            }
                            Get(rootDev, v.Name, chain.Label).Add(firstDev, false);
                            // 骨段比绑定时长出的比例：衣物被拉长的直接量度。
                            float maxStretch = 0f;
                            foreach (int bone in chain.Subtree.Skip(1))
                            {
                                int parent = chain.Parents[bone];
                                float restLen = (rest.Bones[bone].position - rest.Bones[parent].position).magnitude;
                                if (restLen < 1e-4f) continue;
                                float len = (v.Bones[bone].position - v.Bones[parent].position).magnitude;
                                maxStretch = Mathf.Max(maxStretch, len / restLen - 1f);
                            }
                            Get(stretch, v.Name, chain.Label).Add(maxStretch, maxStretch > 0.05f);
                        }

                        if (chain.Length > LongChain)
                        {
                            var tip = chain.Subtree.OrderByDescending(b => (v.Bones[b].position - v.Bones[chain.Root].position).sqrMagnitude).First();
                            float tilt = Vector3.Angle(v.Bones[tip].position - v.Bones[chain.Root].position, Vector3.down);
                            Get(hang, v.Name, chain.Label).Add(tilt, false);
                            if (v == rest && chain == probe) frameTilt = tilt;
                        }

                        float chainDepth = 0f;
                        Vector3 chainAt = Vector3.zero;
                        foreach (int bone in chain.Subtree.Skip(1))
                        {
                            var p = v.Bones[bone].position;
                            foreach (var c in chain.Colliders)
                            {
                                var owner = v.Bones[c.bone];
                                float d = c.radius + chain.Radius -
                                          PmxPhysics.DistanceToSegment(p, owner.TransformPoint(c.a), owner.TransformPoint(c.b));
                                if (d > chainDepth) { chainDepth = d; chainAt = p; }
                            }
                        }
                        Get(depth, v.Name, chain.Label).Add(chainDepth, chainDepth > ClipDepth);
                        if (v.Name == "current" && chainDepth > frameDepth) { frameDepth = chainDepth; depthAt = chainAt; }
                    }

                    if (v == rest) continue;
                    float anchorMax = 0f;
                    Vector3 anchorAt = Vector3.zero;
                    foreach (var kv in anchors)
                    {
                        float max = 0f;
                        int argMax = -1;
                        foreach (int i in kv.Value)
                        {
                            float d = (v.Vertices[i] - rest.Vertices[i]).magnitude;
                            if (d > max) { max = d; argMax = i; }
                        }
                        Get(anchor, v.Name, kv.Key).Add(max, false);
                        if (max > anchorMax) { anchorMax = max; anchorAt = rest.Vertices[argMax]; }
                    }
                    if (t >= 1f && anchorMax > 0.005f && anchorMax > (worstAnchor.TryGetValue(v.Name, out var w) ? w : 0f) * 1.05f)
                    {
                        worstAnchor[v.Name] = anchorMax;
                        // 取景框住全部根骨顶点（腰部衣物），朝向位移最大的那一侧；动画里身体会倾斜、整体抬起或下沉，不能按骨骼位置定中心。
                        RenderAll(variants, outDir, $"{scenario.Name}-anchor-worst-of-{v.Name}", Median(rest, anchors), ViewToward(rest, anchorAt), 0.3f);
                    }
                    float variantTear = 0f;
                    string variantTearKey = null;
                    foreach (var kv in tears)
                    {
                        float max = 0f;
                        int argMax = -1;
                        foreach (var (a, b) in kv.Value)
                        {
                            float d = (v.Vertices[a] - v.Vertices[b]).magnitude - (rest.Vertices[a] - rest.Vertices[b]).magnitude;
                            if (d > max) { max = d; argMax = a; }
                        }
                        Get(tear, v.Name, kv.Key).Add(max, max > 0.003f);
                        if (max > variantTear) { variantTear = max; variantTearKey = kv.Key; }
                        if (max > frameTear) { frameTear = max; tearAt = rest.Vertices[argMax]; }
                        if (max > worstTear) { worstTear = max; worstTearTime = t; worstTearKey = kv.Key; worstTearVariant = v.Name; }
                    }
                    if (variantTear > 0.003f)
                    {
                        if (!tearSpans.TryGetValue(v.Name, out var spans)) tearSpans[v.Name] = spans = new();
                        // 相隔不到 0.2 秒的算同一段。
                        if (spans.Count > 0 && t - spans[spans.Count - 1].to < 0.2f)
                        {
                            var last = spans[spans.Count - 1];
                            spans[spans.Count - 1] = (last.from, t, Mathf.Max(last.max, variantTear), variantTear > last.max ? variantTearKey : last.key);
                        }
                        else spans.Add((t, t, variantTear, variantTearKey));
                    }
                    foreach (var kv in seams)
                    {
                        float max = 0f;
                        int argMax = -1;
                        foreach (int i in kv.Value)
                        {
                            float d = (v.Vertices[i] - rest.Vertices[i]).magnitude;
                            if (d > max) { max = d; argMax = i; }
                        }
                        Get(seam, v.Name, kv.Key).Add(max, false);
                        if (v.Name == "current" && max > frameSeam) { frameSeam = max; seamAt = rest.Vertices[argMax]; }
                    }
                }

                // 默认从右后方斜看全身：背后的长辫与身前的鬓发、衣摆都在画面里。-physicsVideoZoom 0.3 改为对准腰部衣物
                // 放大（取景半高 0.3 米），-physicsVideoYaw 改变机位绕竖轴的角度（0 为正面）。
                // 长场景只录一部分：-physicsVideoEvery 165 每 165 帧录一帧，-physicsVideoFrom/-physicsVideoTo 只录这段时间（秒）。
                if (video != null && (f - 1) % videoEvery == 0 && t >= videoFrom && t <= videoTo)
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    float zoom = float.TryParse(Arg("-physicsVideoZoom"), System.Globalization.NumberStyles.Float, inv, out var z) ? z : 0f;
                    float yaw = float.TryParse(Arg("-physicsVideoYaw"), System.Globalization.NumberStyles.Float, inv, out var y) ? y : 150f;
                    var from = Quaternion.AngleAxis(yaw, Vector3.up) * rest.Root.transform.forward;
                    // -physicsVideoBone Chest：对准该人形骨骼放大（取景半高用 -physicsVideoZoom，缺省 0.25 米）。
                    if (Enum.TryParse<HumanBodyBones>(Arg("-physicsVideoBone"), out var focus) && rest.Animator.GetBoneTransform(focus) != null)
                        video.Write(variants, rest.Animator.GetBoneTransform(focus).position, from, zoom > 0f ? zoom : 0.25f);
                    else if (zoom > 0f && anchors.Count > 0) video.Write(variants, Median(rest, anchors), from, zoom);
                    else video.Write(variants, Center(rest), from, 0.95f);
                }

                // 固定时刻从正面拍腰部，便于逐帧对照各方案（-physicsSnapshots 6 表示均匀取 6 个时刻）。
                if (snapshots > 0 && anchors.Count > 0 && f % Mathf.Max(1, frames / snapshots) == 0)
                {
                    RenderAll(variants, outDir, $"{scenario.Name}-snap{f / Mathf.Max(1, frames / snapshots):D2}", Median(rest, anchors),
                              rest.Root.transform.forward, 0.32f);
                }

                // 只在明显刷新最差值时出图，避免每帧都渲染；开头 1 秒物理尚未展开，不参与。
                if (t < 1f) continue;
                if (frameSeam > worstSeam * 1.05f && frameSeam > 0.005f)
                {
                    worstSeam = frameSeam;
                    RenderAll(variants, outDir, $"{scenario.Name}-seam", seamAt, ViewToward(rest, seamAt), 0.28f);
                }
                if (frameTilt > worstTilt * 1.05f && frameTilt > 20f)
                {
                    worstTilt = frameTilt;
                    // 辫子在背后，身体又多是左右倾斜，从背面才看得出它是否顺着重力垂下。
                    RenderAll(variants, outDir, $"{scenario.Name}-hang", Center(rest), -rest.Root.transform.forward, 0.95f);
                }
                if (frameTear > renderedTear * 1.05f && frameTear > 0.003f)
                {
                    renderedTear = frameTear;
                    RenderAll(variants, outDir, $"{scenario.Name}-tear", tearAt, ViewToward(rest, tearAt), 0.15f);
                }
                if (frameDepth > worstDepth * 1.05f && frameDepth > ClipDepth)
                {
                    worstDepth = frameDepth;
                    RenderAll(variants, outDir, $"{scenario.Name}-clip", depthAt, ViewToward(rest, depthAt), 0.45f);
                }
            }

            // 场景结束时的姿态：侧视全身看长链下垂，正视头部看刘海与鬓发。
            var finalRest = variants[0];
            RenderAll(variants, outDir, $"{scenario.Name}-end-side", Center(finalRest), finalRest.Root.transform.right, 0.95f);
            RenderAll(variants, outDir, $"{scenario.Name}-end-back", Center(finalRest), -finalRest.Root.transform.forward, 0.95f);
            var headAt = finalRest.Animator.GetBoneTransform(HumanBodyBones.Head).position + Vector3.up * 0.05f;
            RenderAll(variants, outDir, $"{scenario.Name}-end-head", headAt, finalRest.Root.transform.forward, 0.2f);

            // -physicsDebugChain <链根骨名>：场景结束时逐粒子输出位置、相对参照的偏移与最近的碰撞体。
            string debugChain = Arg("-physicsDebugChain");
            foreach (var chain in chains.Where(c => model.Bones[c.Root].NameLocal == debugChain))
                foreach (var v in variants)
                {
                    report.AppendLine($"[{scenario.Name}] {debugChain} @ {v.Name}");
                    foreach (int bone in chain.Subtree)
                    {
                        var pos = v.Bones[bone].position;
                        var restPos = variants[0].Bones[bone].position;
                        string near = string.Join(" ", chain.Colliders
                            .Select(c => (c.name, d: PmxPhysics.DistanceToSegment(pos, v.Bones[c.bone].TransformPoint(c.a), v.Bones[c.bone].TransformPoint(c.b)) - c.radius - chain.Radius))
                            .Where(c => c.d < 0.02f).OrderBy(c => c.d).Take(2).Select(c => $"{c.name}:{c.d * 100:F1}cm"));
                        report.AppendLine($"    {model.Bones[bone].NameLocal,-10} pos={pos.ToString("F3")} Δrest={(pos - restPos) * 100:F1}cm {near}");
                    }
                }

            // 场景结束时各链正贴着哪些身体碰撞体（取第一个重建方案），用于追查被推出的异常形状。
            var probeVariant = variants.Count > 2 ? variants[2] : null;
            if (probeVariant != null)
            {
                var contacts = new List<string>();
                foreach (var chain in chains)
                {
                    var touched = chain.Colliders.Where(c => chain.Subtree.Skip(1).Any(bone =>
                    {
                        var owner = probeVariant.Bones[c.bone];
                        return PmxPhysics.DistanceToSegment(probeVariant.Bones[bone].position, owner.TransformPoint(c.a), owner.TransformPoint(c.b))
                               < c.radius + chain.Radius + 0.002f;
                    })).Select(c => c.name).Distinct().ToList();
                    if (touched.Count > 0) contacts.Add($"{model.Bones[chain.Root].NameLocal}→{string.Join("/", touched)}");
                }
                report.AppendLine($"结束时接触（{probeVariant.Name}）：{string.Join("  ", contacts)}");
            }

            var moving = variants.Skip(1).Select(v => v.Name).ToList();
            var all = moving.Append("rest").ToList();
            string Row(IEnumerable<string> names, Func<string, string> cell) => string.Join("  |", names.Select(cell));
            report.AppendLine($"== {scenario.Name} ({scenario.Seconds:F0}s)");
            report.AppendLine($"骨骼偏离动画姿态（°）：最大 / 逐帧最大的均值   [{string.Join(" | ", moving)}]");
            foreach (var label in chains.Select(c => c.Label).Distinct())
                report.AppendLine($"  {label,-28}" + Row(moving, n => { var s = Get(deviation, n, label); return $"{s.Max,6:F1} / {s.Mean,5:F1}"; }));
            report.AppendLine($"根节（根骨→第一层子骨）偏离动画姿态（°）：最大 / 逐帧最大的均值   [{string.Join(" | ", moving)}]");
            foreach (var label in chains.Select(c => c.Label).Distinct())
                report.AppendLine($"  {label,-28}" + Row(moving, n => { var s = Get(rootDev, n, label); return $"{s.Max,6:F1} / {s.Mean,5:F1}"; }));
            report.AppendLine($"骨段伸长（%）：最大 / 伸长超过 5% 的帧占比   [{string.Join(" | ", moving)}]");
            foreach (var label in chains.Select(c => c.Label).Distinct())
            {
                if (moving.All(n => Get(stretch, n, label).Max < 0.01f)) continue;
                report.AppendLine($"  {label,-28}" + Row(moving, n => { var s = Get(stretch, n, label); return $"{s.Max * 100,6:F1} / {Ratio(s),4:P0}"; }));
            }
            var rigidNames = variants.Where(v => v.Rigid != null).Select(v => v.Name).ToList();
            if (rigidNames.Count > 0)
            {
                report.AppendLine($"关节被拉开（cm，平移锁死的关节，按 B 端刚体分组）：最大 / 超过 1 cm 的帧占比   [{string.Join(" | ", rigidNames)}]");
                foreach (var label in apart.Keys.Select(k => k.Item2).Distinct().ToList())
                {
                    if (rigidNames.All(n => Get(apart, n, label).Max < 0.003f)) continue;
                    report.AppendLine($"  {label,-28}" + Row(rigidNames, n => { var s = Get(apart, n, label); return $"{s.Max * 100,6:F2} / {Ratio(s),4:P0}"; }));
                }
            }
            if (stillFrames > 0)
                foreach (var v in variants.Where(x => x.Rigid != null))
                {
                    var top = Enumerable.Range(0, v.BodyJitter.Length).OrderByDescending(i => v.BodyJitter[i]).Take(8)
                                        .Select(i => $"{v.Rigid.bodies[i].name} {v.BodyJitter[i] / stillFrames * 100:F2}");
                    report.AppendLine($"静止时逐帧位移最大的刚体（cm/帧，{v.Name}）：{string.Join("  ", top)}");
                    // 按刚体组列出各组抖得最厉害的一个，避免某一组霸占前几名、看不到别处。
                    var byGroup = Enumerable.Range(0, v.BodyJitter.Length).Where(i => v.Rigid.bodies[i].mode != 0)
                                            .GroupBy(i => Label(v.Rigid.bodies[i].name))
                                            .Select(g => g.OrderByDescending(i => v.BodyJitter[i]).First())
                                            .Where(i => v.BodyJitter[i] / stillFrames > 0.0003f)
                                            .OrderByDescending(i => v.BodyJitter[i])
                                            .Select(i => $"{v.Rigid.bodies[i].name} {v.BodyJitter[i] / stillFrames * 100:F2}");
                    report.AppendLine($"  各组最大：{string.Join("  ", byGroup)}");
                }
            if (trackCsv != null) File.WriteAllText(Path.Combine(outDir, $"track-{scenario.Name}.csv"), trackCsv.ToString(), new UTF8Encoding(false));
            if (tracked.Count > 0)
            {
                report.AppendLine($"指定骨骼相对不跑物理：位移（cm）均值 / 最大，转角（°）均值 / 最大，转动二阶差分（°，量抖动）均值 / 最大   [{string.Join(" | ", moving)}]");
                foreach (int b in tracked)
                {
                    string key = model.Bones[b].NameLocal;
                    report.AppendLine($"  {key,-28}" + Row(moving, n => $"{Get(trackMove, n, key).Mean * 100,5:F2} / {Get(trackMove, n, key).Max * 100,5:F2}  " +
                                                                       $"{Get(trackTurn, n, key).Mean,5:F1} / {Get(trackTurn, n, key).Max,5:F1}  " +
                                                                       $"{Get(trackShake, n, key).Mean,5:F2} / {Get(trackShake, n, key).Max,5:F2}"));
                }
                foreach (var kv in trackPeaks.Where(x => x.Key.Item1 == moving[0]))
                    report.AppendLine($"  {kv.Key.Item2} 抖得最厉害的时刻（{kv.Key.Item1}）：" +
                                      string.Join("  ", kv.Value.OrderByDescending(x => x.angle).Take(6).Select(x => $"{x.time:F2}s {x.angle:F1}°")));
            }
            if (shake.Count > 0)
            {
                report.AppendLine($"物理偏移逐帧二阶差分（cm，量抖动，与帧率有关）：最大 / 均值   [{string.Join(" | ", moving)}]");
                foreach (var key in materials.Keys)
                {
                    if (moving.All(n => Get(shake, n, key).Max < 0.001f)) continue;
                    report.AppendLine($"  {key,-28}" + Row(moving, n => { var s = Get(shake, n, key); return $"{s.Max * 100,6:F2} / {s.Mean * 100,5:F3}"; }));
                }
            }
            if (jitter.Count > 0)
            {
                report.AppendLine($"静止姿势下顶点逐帧抖动（cm，最后 2 秒）：最大 / 逐帧最大的均值   [{string.Join(" | ", moving)}]");
                foreach (var key in materials.Keys)
                {
                    if (moving.All(n => Get(jitter, n, key).Max < 0.0005f)) continue;
                    report.AppendLine($"  {key,-28}" + Row(moving, n => { var s = Get(jitter, n, key); return $"{s.Max * 100,6:F2} / {s.Mean * 100,5:F2}"; }));
                }
            }
            report.AppendLine($"主要绑在链根骨上的顶点位移（cm）：最大 / 逐帧最大的均值   [{string.Join(" | ", moving)}]");
            foreach (var key in anchors.Keys)
                report.AppendLine($"  {key,-28}" + Row(moving, n => { var s = Get(anchor, n, key); return $"{s.Max * 100,6:F2} / {s.Mean * 100,5:F2}"; }));
            if (pokes != null)
            {
                report.AppendLine($"内层穿出外层（cm，越过外层最近顶点切面的距离，按内层材质/主骨骼分组）：最大 / 超过 0.1 cm 的帧占比   [{string.Join(" | ", all)}]");
                foreach (var key in pokes.Keys.OrderBy(k => k))
                {
                    if (all.All(n => Get(poke, n, key).Max < 0.001f)) continue;
                    report.AppendLine($"  {key,-28}" + Row(all, n => { var s = Get(poke, n, key); return $"{s.Max * 100,6:F2} / {Ratio(s),4:P0}"; }));
                }
                foreach (var kv in pokeSpans)
                    report.AppendLine($"  超过 0.1 cm 的时段（{kv.Key}，共 {kv.Value.Count} 段，标注最深处的分组）：" +
                                      string.Join("  ", kv.Value.OrderByDescending(x => x.max).Take(12).OrderBy(x => x.from)
                                                          .Select(x => $"{x.from:F1}-{x.to:F1}s {x.max * 100:F2} {x.key}")));
            }
            if (tears.Count > 0)
            {
                report.AppendLine($"网格裂开（cm，绑定时重合、权重不同的顶点对比不跑物理多拉开的距离）：最大 / 超过 0.3 cm 的帧占比   [{string.Join(" | ", moving)}]");
                foreach (var key in tears.Keys)
                {
                    if (moving.All(n => Get(tear, n, key).Max < 0.001f)) continue;
                    report.AppendLine($"  {key,-28}" + Row(moving, n => { var s = Get(tear, n, key); return $"{s.Max * 100,6:F2} / {Ratio(s),4:P0}"; }));
                }
                if (worstTearKey != null) report.AppendLine($"  最大一帧：t={worstTearTime:F2}s {worstTearKey}（{worstTearVariant}）");
                foreach (var kv in tearSpans)
                    report.AppendLine($"  超过 0.3 cm 的时段（{kv.Key}，共 {kv.Value.Count} 段）：" +
                                      string.Join("  ", kv.Value.OrderByDescending(x => x.max).Take(12).OrderBy(x => x.from)
                                                          .Select(x => $"{x.from:F1}-{x.to:F1}s {x.max * 100:F1} {x.key}")));
            }
            report.AppendLine($"交界顶点被拉离（cm）：最大 / 逐帧最大的均值   [{string.Join(" | ", moving)}]");
            foreach (var key in seams.Keys)
                report.AppendLine($"  {key,-28}" + Row(moving, n => { var s = Get(seam, n, key); return $"{s.Max * 100,6:F2} / {s.Mean * 100,5:F2}"; }));
            if (chains.Any(c => c.Length > LongChain))
            {
                report.AppendLine($"长链根→梢与竖直向下的夹角（°）：均值 / 最大   [{string.Join(" | ", all)}]");
                foreach (var label in chains.Where(c => c.Length > LongChain).Select(c => c.Label).Distinct())
                    report.AppendLine($"  {label,-28}" + Row(all, n => { var s = Get(hang, n, label); return $"{s.Mean,5:F1} / {s.Max,5:F1}"; }));
            }
            report.AppendLine($"粒子嵌入身体碰撞体（cm）：最大 / 超过 {ClipDepth * 100:F0} cm 的帧占比   [{string.Join(" | ", all)}]");
            foreach (var label in chains.Select(c => c.Label).Distinct())
            {
                if (all.All(n => Get(depth, n, label).Max < 0.002f)) continue;
                report.AppendLine($"  {label,-28}" + Row(all, n => { var s = Get(depth, n, label); return $"{s.Max * 100,5:F2} / {Ratio(s),4:P0}"; }));
            }
            report.AppendLine();
        }

        private static float Ratio(Stat s) => s.Frames > 0 ? (float)s.Over / s.Frames : 0f;

        // 旋转的转角（度）。Quaternion.Angle 在 0.16° 以下直接返回 0，量逐帧的小转动必须用这个。
        private static float Angle(Quaternion q)
            => 2f * Mathf.Rad2Deg * Mathf.Atan2(new Vector3(q.x, q.y, q.z).magnitude, Mathf.Abs(q.w));

        private static Vector3 BodyPoint(float[] poses, int body, Vector3 local)
        {
            int o = body * 7;
            var q = new Quaternion(poses[o + 3], poses[o + 4], poses[o + 5], poses[o + 6]);
            return new Vector3(poses[o], poses[o + 1], poses[o + 2]) + q * local;
        }

        private static AnimationClip LoadClip(string path)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            Require(clip != null && clip.humanMotion, $"缺少人形动画片段 {path}");
            return clip;
        }

        private static void Bind(Variant v, List<AnimationClip> clips)
        {
            if (v.Graph.IsValid()) v.Graph.Destroy();
            v.Graph = PlayableGraph.Create($"physics-{v.Name}");
            v.Graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(v.Graph, "pose", v.Animator);
            v.Mixer = AnimationMixerPlayable.Create(v.Graph, Mathf.Max(2, clips.Count));
            v.Clips = clips.Select((clip, i) =>
            {
                var playable = AnimationClipPlayable.Create(v.Graph, clip);
                playable.SetApplyFootIK(false);
                v.Graph.Connect(playable, 0, v.Mixer, i);
                return playable;
            }).ToArray();
            v.ClipA = v.Clips[0];
            v.ClipB = v.Clips.Length > 1 ? v.Clips[1] : default;
            output.SetSourcePlayable(v.Mixer);
        }

        private static void Pose(Variant v, Scenario s, float t)
        {
            if (s.Sequence != null)
            {
                // 与 App 的 1D 混合树相同：IdleIndex 在过渡时长内从上一段线性移到这一段，两段按比例混合。
                int k = Mathf.Min((int)(t / s.SegmentSeconds), v.Clips.Length - 1);
                float w = k > 0 ? Mathf.Clamp01((t - k * s.SegmentSeconds) / s.BlendSeconds) : 1f;
                for (int i = 0; i < v.Clips.Length; i++)
                {
                    v.Mixer.SetInputWeight(i, i == k ? w : i == k - 1 ? 1f - w : 0f);
                    v.Clips[i].SetTime(t % Mathf.Max(v.Clips[i].GetAnimationClip().length, 1e-3f));
                }
                v.Graph.Evaluate();
                s.Additive?.Invoke(v, t);
                return;
            }
            float blend = v.ClipB.IsValid() && s.ClipB != null ? Mathf.Clamp01(t / s.BlendSeconds) : 0f;
            v.Mixer.SetInputWeight(0, 1f - blend);
            v.Mixer.SetInputWeight(1, blend);
            float length = Mathf.Max(v.ClipA.GetAnimationClip().length, 1e-3f);
            v.ClipA.SetTime(s.Hold ? 0.0 : t % length);
            if (v.ClipB.IsValid())
                v.ClipB.SetTime(s.PlayB ? t % Mathf.Max(v.ClipB.GetAnimationClip().length, 1e-3f) : 0.0);
            v.Graph.Evaluate();
            s.Additive?.Invoke(v, t);
        }

        // 腰部衣物顶点的逐轴中位数：动画里身体会倾斜、抬起或下沉，不能按骨骼定中心；包围盒又会被零星离群顶点拉偏。
        private static Vector3 Median(Variant v, Dictionary<string, int[]> sets)
        {
            var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
            foreach (var set in sets.Values)
                foreach (int i in set) { var p = v.Vertices[i]; xs.Add(p.x); ys.Add(p.y); zs.Add(p.z); }
            if (xs.Count == 0) return Center(v);
            xs.Sort(); ys.Sort(); zs.Sort();
            return new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);
        }

        private static Vector3 Center(Variant v)
        {
            var hips = v.Animator.GetBoneTransform(HumanBodyBones.Hips).position;
            var head = v.Animator.GetBoneTransform(HumanBodyBones.Head).position;
            return (hips + head) * 0.5f + Vector3.down * 0.15f;
        }

        // 从躯干中轴指向目标点的水平方向看过去，交界处与穿模点都在朝向镜头的一侧。
        private static Vector3 ViewToward(Variant v, Vector3 target)
        {
            var hips = v.Animator.GetBoneTransform(HumanBodyBones.Hips).position;
            var dir = Vector3.ProjectOnPlane(target - hips, Vector3.up);
            return dir.sqrMagnitude > 1e-6f ? dir.normalized : v.Root.transform.forward;
        }

        private static void RenderAll(List<Variant> variants, string outDir, string name, Vector3 center, Vector3 from, float size)
        {
            foreach (var v in variants)
            {
                if (v.Directions != null) v.Directions.UpdateDirections();
                var cam = new GameObject("physics validation camera").AddComponent<Camera>();
                var rt = new RenderTexture(ImageSize, ImageSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
                try
                {
                    cam.orthographic = true;
                    cam.orthographicSize = size;
                    cam.transform.position = center + from * 5f;
                    cam.transform.LookAt(center, Vector3.up);
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = 20f;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.82f, 0.84f, 0.86f, 1f);
                    cam.cullingMask = 1 << v.Layer;
                    cam.targetTexture = rt;
                    cam.Render();
                    var previous = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(ImageSize, ImageSize, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, ImageSize, ImageSize), 0, 0);
                    tex.Apply();
                    RenderTexture.active = previous;
                    File.WriteAllBytes(Path.Combine(outDir, $"{name}-{v.Name}.png"), tex.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(tex);
                }
                finally
                {
                    cam.targetTexture = null;
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                    UnityEngine.Object.DestroyImmediate(cam.gameObject);
                }
            }
        }

        // 每帧一张 JPG，各方案按变体顺序从左到右并排；相机与贴图在整个场景内复用。
        private sealed class FrameStrip : IDisposable
        {
            private const int Panel = 480;
            private readonly string dir;
            private readonly Camera cam;
            private readonly RenderTexture rt;
            private readonly Texture2D strip;
            private int frame;

            public FrameStrip(string dir, int panels)
            {
                this.dir = dir;
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                cam = new GameObject("physics video camera").AddComponent<Camera>();
                cam.orthographic = true;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 20f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.82f, 0.84f, 0.86f, 1f);
                rt = new RenderTexture(Panel, Panel, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
                cam.targetTexture = rt;
                strip = new Texture2D(Panel * panels, Panel, TextureFormat.RGB24, false);
            }

            public void Write(List<Variant> variants, Vector3 center, Vector3 from, float size)
            {
                cam.orthographicSize = size;
                cam.transform.position = center + from * 5f;
                cam.transform.LookAt(center, Vector3.up);
                var previous = RenderTexture.active;
                for (int i = 0; i < variants.Count; i++)
                {
                    if (variants[i].Directions != null) variants[i].Directions.UpdateDirections();
                    cam.cullingMask = 1 << variants[i].Layer;
                    cam.Render();
                    RenderTexture.active = rt;
                    strip.ReadPixels(new Rect(0, 0, Panel, Panel), i * Panel, 0);
                }
                RenderTexture.active = previous;
                strip.Apply();
                File.WriteAllBytes(Path.Combine(dir, $"f{frame++:D5}.jpg"), strip.EncodeToJPG(90));
            }

            public void Dispose()
            {
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(strip);
                UnityEngine.Object.DestroyImmediate(cam.gameObject);
            }
        }

        private static string Arg(string key)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static string RequireArg(string key) => Arg(key) ?? throw new ArgumentException($"缺少参数 {key}");

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
