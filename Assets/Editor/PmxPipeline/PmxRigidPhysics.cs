// PMX 刚体与关节原样写进 MmdPhysics，运行时由 Bullet 模拟（ADR-0017）。
//
// 这里只做坐标与单位的搬运，不做任何参数换算：质量、阻尼、限位、弹簧、分组掩码都保持 PMX 原值，
// 运行时与 MMD 用同一种物理引擎解释它们。PmxRigidPhysicsOptions 里的锁根节、去掉悬挂刚体、偏移锁归零是有意
// 偏离 MMD 的通用处理；放宽限位、改为跟随骨骼、去掉与某些分组的碰撞只用于逐角色调整。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    [Serializable]
    public class PmxRigidPhysicsOptions
    {
        // 去掉没有骨骼、也不处在「跟随骨骼的刚体 → 驱动骨骼的刚体」之间的动态刚体。PMX 里删了骨骼和网格
        // （如裁短裙摆）后，这些刚体仍会被模拟，不驱动任何东西，只作为看不见的配重拖拽可见部分。
        // 胸部常见的无骨连接件夹在两者之间，不受影响。
        public bool pruneHanging = true;
        // 与跟随骨骼的刚体之间六轴全锁死（限位都是 0）的动态刚体改为跟随骨骼。Bullet 的约束有弹性，
        // 下面整串刚体一甩，锁死的第一节也会被拉偏，带走绑在它骨骼上的腰头与上衣下沿。
        public bool lockAnchors = true;
        // 逐角色调整：子刚体名匹配该正则的关节，原本允许转动的轴放宽到至少 ±loosenDegrees，锁死的轴不变。
        // 用于裁短后只剩上段、显得太硬的裙摆（上段限位常只有 ±1°～±2°）。
        public string loosenPattern = "";
        public float loosenDegrees = 0f;
        // 平移限位上下限相等但不为 0 的轴（锁在偏离绑定位置的地方）：keep 原样、zero 锁在绑定位置、free 不限制。
        // 这种设置常与同一刚体上其他关节的限位互相矛盾（乳摇模板要求胸部比绑定位置高 0.34，主关节却只允许
        // ±0.11），Bullet 在矛盾之间取折中，静止时胸部也被抬高约 2 cm、上翘 10°～26°，与 MMD 里的样子不符；
        // zero 时偏离降到 0.6 cm 以内，晃动保留。
        public string offsetLocks = "zero";
        // 逐角色调整：刚体名匹配该正则的动态刚体改为跟随骨骼。用于每节限位只有 ±1°～±5° 的刘海这类短链：作者的本意是
        // 几乎不动，但这类刚体的角阻尼常为 1，每步角速度清零，头一快速转动就先越过限位、再被拉回，跳舞时相对头部突然
        // 偏出十几度又弹回。
        public string followPattern = "";
        // 逐角色调整：刚体名匹配 maskOffPattern 的刚体不再与 maskOffGroups 里的分组（PMX 的 0～15）碰撞。
        public string maskOffPattern = "";
        public int[] maskOffGroups = Array.Empty<int>();
    }

    public class RigidPhysicsBuildResult
    {
        public int Bodies, Joints, Pruned, Anchored, Loosened, OffsetLocks, Followed, MaskedOff;
    }

    public static class PmxRigidPhysics
    {
        // MMD→Unity 是绕 Y 转 180°。刚体与关节的坐标系整体随之旋转、局部轴保持不变，定义在局部轴上的
        // 限位和弹簧因此原样沿用。不能用 PmxPhysics.ToUnityRotation 的共轭形式：它把局部 X、Z 轴也翻了
        // 过来，对称的碰撞形状不受影响，但不对称的关节限位会被镜像。
        private static readonly Quaternion MmdToUnity = Quaternion.Euler(0f, 180f, 0f);

        // 逐角色物理配置：Tools/PmxPipeline/physics/<模型名>.physics.json（导出时模型名取 PMX 文件名），只写要改的项。
        // 字段为 PmxRigidPhysicsOptions（导出时生效）或 MmdPhysics（写进组件、运行时生效）的公开字段，与
        // MEPhysicsValidation 的 -physicsRigid 相同。JsonUtility 会静默忽略拼错的字段，这里先逐个核对字段名。
        public static string ConfigPath(string modelName)
            => Path.Combine(PmxPipelineOptions.ProjectRoot, "Tools", "PmxPipeline", "physics", modelName + ".physics.json");

        public static string LoadConfig(string modelName, out string path)
        {
            path = ConfigPath(modelName);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            var known = new HashSet<string>(typeof(PmxRigidPhysicsOptions).GetFields().Concat(typeof(MmdPhysics).GetFields())
                                             .Select(f => f.Name));
            var unknown = JObject.Parse(json).Properties().Select(p => p.Name).Where(n => !known.Contains(n)).ToList();
            if (unknown.Count > 0) throw new InvalidDataException($"物理配置 {path} 有未知字段：{string.Join(", ", unknown)}");
            return json;
        }

        /// <summary>按逐角色配置（没有时用默认值）建好 MmdPhysics。</summary>
        public static RigidPhysicsBuildResult BuildWithConfig(PmxModel model, GameObject root, Transform[] boneTf, float scale,
                                                             string config)
        {
            var options = new PmxRigidPhysicsOptions();
            if (config != null) JsonUtility.FromJsonOverwrite(config, options);
            var result = Build(model, root, boneTf, scale, options);
            if (config != null && root.TryGetComponent<MmdPhysics>(out var physics)) JsonUtility.FromJsonOverwrite(config, physics);
            return result;
        }

        public static RigidPhysicsBuildResult Build(PmxModel model, GameObject root, Transform[] boneTf, float scale,
                                                   PmxRigidPhysicsOptions options = null)
        {
            options ??= new PmxRigidPhysicsOptions();
            var result = new RigidPhysicsBuildResult();
            var rbs = model.RigidBodies;
            if (rbs.Count == 0) return result;
            bool HasBone(int i) => rbs[i].BoneIndex >= 0 && rbs[i].BoneIndex < boneTf.Length;
            // PMX 2.1 还定义了其他关节类型，实际模型几乎只用 6DOF 弹簧，统一按它处理。
            var joints = model.Joints.Where(j => j.RigidBodyA >= 0 && j.RigidBodyA < rbs.Count && j.RigidBodyB >= 0 &&
                                                 j.RigidBodyB < rbs.Count && j.RigidBodyA != j.RigidBodyB).ToList();

            var keep = Enumerable.Repeat(true, rbs.Count).ToArray();
            if (options.pruneHanging)
            {
                foreach (int i in HangingBodies(rbs, joints, HasBone))
                {
                    keep[i] = false;
                    result.Pruned++;
                }
            }
            var mode = rbs.Select(rb => (int)rb.PhysicsMode).ToArray();
            if (options.lockAnchors)
            {
                foreach (var j in joints)
                {
                    if (!FullyLocked(j)) continue;
                    int a = j.RigidBodyA, b = j.RigidBodyB;
                    // 只看与 PMX 原本就跟随骨骼的刚体相连的那一节，不沿链传递：外套这类整链锁死的衣物
                    // 仍需由碰撞推开，整链改为跟随骨骼会被大腿穿过。
                    int anchor = rbs[a].PhysicsMode == 0 && rbs[b].PhysicsMode != 0 ? b
                               : rbs[b].PhysicsMode == 0 && rbs[a].PhysicsMode != 0 ? a : -1;
                    if (anchor < 0 || !HasBone(anchor) || mode[anchor] == 0) continue;
                    mode[anchor] = 0;
                    result.Anchored++;
                }
            }
            if (!string.IsNullOrEmpty(options.followPattern))
            {
                var follow = new System.Text.RegularExpressions.Regex(options.followPattern);
                for (int i = 0; i < rbs.Count; i++)
                {
                    if (!keep[i] || mode[i] == 0 || !HasBone(i) || !follow.IsMatch(rbs[i].NameLocal ?? "")) continue;
                    mode[i] = 0;
                    result.Followed++;
                }
            }
            var maskOff = string.IsNullOrEmpty(options.maskOffPattern) ? null : new System.Text.RegularExpressions.Regex(options.maskOffPattern);
            int maskOffBits = (options.maskOffGroups ?? Array.Empty<int>()).Where(g => g >= 0 && g < 16).Aggregate(0, (m, g) => m | 1 << g);

            var newIndex = new int[rbs.Count];
            var bodies = new List<MmdPhysics.Body>();
            for (int i = 0; i < rbs.Count; i++)
            {
                newIndex[i] = keep[i] ? bodies.Count : -1;
                if (!keep[i]) continue;
                var rb = rbs[i];
                int mask = rb.NonCollisionMask;
                if (maskOff != null && maskOffBits != 0 && maskOff.IsMatch(rb.NameLocal ?? ""))
                {
                    mask &= ~maskOffBits;
                    result.MaskedOff++;
                }
                bodies.Add(new MmdPhysics.Body
                {
                    name = rb.NameLocal,
                    bone = HasBone(i) ? boneTf[rb.BoneIndex] : null,
                    shape = rb.Shape,
                    size = rb.Size,
                    position = PmxMeshBuilder.Conv(rb.Position, 1f),
                    rotation = Rotation(rb.Rotation),
                    bonePosition = HasBone(i) ? PmxMeshBuilder.Conv(model.Bones[rb.BoneIndex].Position, 1f) : Vector3.zero,
                    mass = rb.Mass,
                    linearDamping = rb.LinearDamping,
                    angularDamping = rb.AngularDamping,
                    restitution = rb.Restitution,
                    friction = rb.Friction,
                    group = rb.Group,
                    mask = mask,
                    mode = mode[i],
                });
            }
            var loosen = string.IsNullOrEmpty(options.loosenPattern) || options.loosenDegrees <= 0f
                ? null : new System.Text.RegularExpressions.Regex(options.loosenPattern);
            float loosenRadians = options.loosenDegrees * Mathf.Deg2Rad;
            // 两端都跟随骨骼的关节不起作用，Bullet 也无法给两个无质量刚体求解约束。
            var kept = joints.Where(j => keep[j.RigidBodyA] && keep[j.RigidBodyB] &&
                                         (mode[j.RigidBodyA] != 0 || mode[j.RigidBodyB] != 0))
                             .Select(j =>
            {
                Vector3 lower = j.RotLimitLower, upper = j.RotLimitUpper;
                if (loosen != null && loosen.IsMatch(rbs[j.RigidBodyB].NameLocal ?? ""))
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (lower[axis] >= upper[axis]) continue;   // 锁死或自由的轴保持原样
                        lower[axis] = Mathf.Min(lower[axis], -loosenRadians);
                        upper[axis] = Mathf.Max(upper[axis], loosenRadians);
                    }
                    result.Loosened++;
                }
                Vector3 linLower = j.PosLimitLower, linUpper = j.PosLimitUpper;
                if (options.offsetLocks == "zero" || options.offsetLocks == "free")
                {
                    bool changed = false;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (linLower[axis] != linUpper[axis] || Mathf.Abs(linLower[axis]) < 1e-6f) continue;
                        // 下限大于上限即该轴自由，与 PMX 的约定相同。
                        if (options.offsetLocks == "zero") { linLower[axis] = 0f; linUpper[axis] = 0f; }
                        else { linLower[axis] = 1f; linUpper[axis] = -1f; }
                        changed = true;
                    }
                    if (changed) result.OffsetLocks++;
                }
                return new MmdPhysics.Joint
                {
                    name = j.NameLocal,
                    bodyA = newIndex[j.RigidBodyA],
                    bodyB = newIndex[j.RigidBodyB],
                    position = PmxMeshBuilder.Conv(j.Position, 1f),
                    rotation = Rotation(j.Rotation),
                    linearLower = linLower,
                    linearUpper = linUpper,
                    angularLower = lower,
                    angularUpper = upper,
                    linearSpring = j.PosSpring,
                    angularSpring = j.RotSpring,
                };
            }).ToArray();

            var physics = root.AddComponent<MmdPhysics>();
            physics.unitLength = scale;
            physics.bodies = bodies.ToArray();
            physics.joints = kept;
            result.Bodies = bodies.Count;
            result.Joints = kept.Length;
            return result;
        }

        // 没有骨骼、经由其他无骨动态刚体也无法同时连到跟随骨骼的刚体与驱动骨骼的刚体的动态刚体。
        private static IEnumerable<int> HangingBodies(List<PmxRigidBody> rbs, List<PmxJoint> joints, Func<int, bool> hasBone)
        {
            var adjacent = Enumerable.Range(0, rbs.Count).Select(_ => new List<int>()).ToArray();
            foreach (var j in joints)
            {
                adjacent[j.RigidBodyA].Add(j.RigidBodyB);
                adjacent[j.RigidBodyB].Add(j.RigidBodyA);
            }
            bool Boneless(int i) => rbs[i].PhysicsMode != 0 && !hasBone(i);
            bool Reaches(int start, Func<int, bool> target)
            {
                var seen = new HashSet<int> { start };
                var stack = new Stack<int>();
                stack.Push(start);
                while (stack.Count > 0)
                    foreach (int v in adjacent[stack.Pop()])
                    {
                        if (target(v)) return true;
                        if (Boneless(v) && seen.Add(v)) stack.Push(v);
                    }
                return false;
            }
            for (int i = 0; i < rbs.Count; i++)
            {
                if (!Boneless(i)) continue;
                bool between = Reaches(i, v => rbs[v].PhysicsMode == 0) && Reaches(i, v => rbs[v].PhysicsMode != 0 && hasBone(v));
                if (!between) yield return i;
            }
        }

        private static bool FullyLocked(PmxJoint j)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(j.PosLimitLower[axis]) > 1e-6f || Mathf.Abs(j.PosLimitUpper[axis]) > 1e-6f) return false;
                if (Mathf.Abs(j.RotLimitLower[axis]) > 1e-6f || Mathf.Abs(j.RotLimitUpper[axis]) > 1e-6f) return false;
            }
            return true;
        }

        // PMX 欧拉角（弧度）按 Z、X、Y 顺序作用，与 Quaternion.Euler 相同。
        internal static Quaternion Rotation(Vector3 euler) => MmdToUnity * Quaternion.Euler(euler * Mathf.Rad2Deg);
    }
}
