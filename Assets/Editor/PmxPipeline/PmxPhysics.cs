// M3/M3.5: convert PMX rigid-body/joint physics into DynamicBone chains and colliders.
//
// MMD cloth/hair physics are chains of *dynamic* rigid bodies (PhysicsMode 1/2)
// anchored to a *static* rigid body (mode 0) on a skeleton bone. Each joint limits how
// far a body may rotate relative to the one above it, and the static bodies double as
// the body colliders, filtered by group/mask.
//
// DynamicBone has no per-joint limits: its stiffness caps how far each particle may
// leave the pose the animation gives it. So joint limits are accumulated down each
// chain into a per-particle maximum swing angle and converted to a stiffness curve;
// colliders are the model's own static bodies. Uniform per-category parameters (the
// earlier heuristic) let locked skirts swing ~90° and tear the top's hem, let ±4° bangs
// flop, and held a ±225° braid rigid against gravity — see ADR-0016.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MateEngine.PmxPipeline
{
    public class PhysicsBuildResult
    {
        public int DynamicBones, Hair, Skirt, Breast, Other, ChainRoots, Colliders;
        // 只有根粒子（无可模拟的子骨骼）、全程锁死且无碰撞而省去组件的链，绑定姿态下即嵌入而未挂上的碰撞体对，
        // 以及头发在 App 待机姿势里已搭在其中而未挂上的碰撞体对。
        public int SkippedChains, RigidChains, RestExclusions, IdleExclusions;
    }

    // 不随模型数据变化的手感参数，集中在这里便于验证装置对比多组取值。
    [Serializable]
    public class PmxPhysicsTuning
    {
        // PMX 阻尼是 Bullet 的每秒衰减，1.0 时每步清零速度；DynamicBone 是每步速度衰减。
        // 硬换算会让 0.99 与 1.0 一个几乎不衰减、一个完全没有惯性，所以按链上刚体的角阻尼在两端之间插值。
        public float dampingAtZero = 0.05f;
        public float dampingAtOne = 0.4f;
        // 拉回动画姿态的力度。MMD 关节弹簧多为 0，链的形状只由重力和限位决定。
        public float elasticity = 0f;
        public float inert = 0.2f;
        // 现实重力（m/s²），DynamicBone 按每步位移施加。
        public float gravity = 9.8f;
        // true 时用不扣除静止分量的 m_Force，与 MMD 一样始终朝下拉，长辫在限位内自然下垂；
        // false 时用 m_Gravity，它扣掉静止姿态下的重力，作者摆成斜线的辫子静止时完全不受重力。
        public bool absoluteGravity = true;
        // 根节与身体之间的关节锁死时，从根骨的子骨开始模拟，根骨完全跟随动画。
        public bool lockAnchorSegment = true;
        public float updateRate = 60f;
        // 累加偏角达到该值后不再受刚度约束。
        public float maxSwingDegrees = 180f;
        // 静止姿态下粒子嵌入碰撞体超过该深度（米）时，该碰撞体不作用于这条链，否则一加载就被弹开。
        public float restPenetrationTolerance = 0.002f;
        // App 的待机片段。头发（挂在头骨下方的链）在这个姿势里已经嵌入的身体碰撞体不参与碰撞：
        // 头发本就搭在胸前、肩上，碰撞胶囊又比实际网格粗，碰撞只会把发梢从原位顶出去形成内勾。
        // 衣物不适用：外套被大腿顶穿是真正的穿模，必须靠碰撞推开。为空时只检查绑定姿态。
        public string idlePoseClip = "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE.anim";
    }

    public static class PmxPhysics
    {
        private enum Cat { Hair, Skirt, Breast, Other }

        private sealed class ColliderShape
        {
            public PmxRigidBody Body;
            public Vector3 A, B;      // 绑定姿态下胶囊轴线两端（球体两端重合），Unity 坐标
            public float Radius;
            public DynamicBoneCollider Component;
        }

        public static PhysicsBuildResult Build(PmxModel model, Transform root, Transform[] boneTf, float scale,
                                               PmxPhysicsTuning tuning = null)
        {
            tuning ??= new PmxPhysicsTuning();
            var result = new PhysicsBuildResult();
            var bones = model.Bones;
            var dynamicBodies = DynamicBodiesByBone(model);
            if (dynamicBodies.Count == 0) return result;

            float[] bodyLimit = UpstreamSwingLimits(model, out int unconstrained);
            if (unconstrained > 0)
                Debug.LogWarning($"[PmxPhysics] {unconstrained} 个动态刚体没有连向上游的关节，按不受限处理");

            Vector3 BindPos(int bone) => PmxMeshBuilder.Conv(bones[bone].Position, scale);

            var chains = FindChains(model, dynamicBodies);
            var inChain = new HashSet<int>(chains.SelectMany(c => c.subtree));
            result.ChainRoots = chains.Count;

            var colliders = StaticColliders(model, inChain, scale)
                .Select(c => new ColliderShape { Body = c.body, A = c.a, B = c.b, Radius = c.radius }).ToList();

            var animator = root.GetComponent<Animator>();
            var idlePose = SampleIdlePose(animator, boneTf, tuning.idlePoseClip);
            var headTf = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            int head = headTf != null ? Array.IndexOf(boneTf, headTf) : -1;

            var holder = new GameObject("DynamicBones");
            holder.transform.SetParent(root, false);

            foreach (var (chainRoot, subtree) in chains)
            {
                var cat = Classify(model, chainRoot, dynamicBodies);
                switch (cat)
                {
                    case Cat.Hair: result.Hair++; break;
                    case Cat.Skirt: result.Skirt++; break;
                    case Cat.Breast: result.Breast++; break;
                    default: result.Other++; break;
                }
                if (subtree.Count < 2)
                {
                    // 单骨链没有可模拟的子粒子，DynamicBone 只会固定住根骨。
                    result.SkippedChains++;
                    continue;
                }

                // 第 k 节（父骨→本骨）的朝向由父骨上的刚体决定，它相对上游最多转 bodyLimit；
                // DynamicBone 的刚度约束的是相对动画姿态的绝对偏角，所以沿链累加。
                var swing = new Dictionary<int, float> { [chainRoot] = 0f };
                var length = new Dictionary<int, float> { [chainRoot] = 0f };
                for (int k = 1; k < subtree.Count; k++)
                {
                    int bone = subtree[k], parent = bones[bone].ParentIndex;
                    swing[bone] = swing[parent] + BoneLimit(parent, dynamicBodies, bodyLimit);
                    length[bone] = length[parent] + (BindPos(bone) - BindPos(parent)).magnitude;
                }
                // 根节与身体之间的关节锁死时（裙摆、外套常见），根骨属于动画骨架：从它的子骨开始模拟。
                // 否则 DynamicBone 会按下一节粒子去转根骨，大腿一顶，绑在根骨上的腰头、上衣下沿就离开身体。
                var simRoots = tuning.lockAnchorSegment && BoneLimit(chainRoot, dynamicBodies, bodyLimit) <= 1e-6f
                    ? subtree.Where(b => bones[b].ParentIndex == chainRoot).ToList()
                    : new List<int> { chainRoot };
                foreach (int simRoot in simRoots)
                {
                    var unit = new List<int> { simRoot };
                    var inUnit = new HashSet<int> { simRoot };
                    foreach (int bone in subtree)
                        if (inUnit.Contains(bones[bone].ParentIndex) && inUnit.Add(bone)) unit.Add(bone);
                    if (unit.Count < 2)
                    {
                        result.SkippedChains++;
                        continue;
                    }
                    var pose = head >= 0 && IsAncestor(bones, head, chainRoot) ? idlePose : null;
                    if (AddDynamicBone(unit, swing, length, dynamicBodies, colliders, pose, holder.transform, model, boneTf, scale, tuning, result))
                        result.DynamicBones++;
                }
            }
            result.Colliders = colliders.Count(c => c.Component != null);
            if (holder.transform.childCount == 0) UnityEngine.Object.DestroyImmediate(holder);
            return result;
        }

        private static bool AddDynamicBone(List<int> unit, Dictionary<int, float> swing, Dictionary<int, float> length,
                                           Dictionary<int, List<int>> dynamicBodies, List<ColliderShape> colliders,
                                           Matrix4x4[] idlePose, Transform holder, PmxModel model, Transform[] boneTf, float scale,
                                           PmxPhysicsTuning tuning, PhysicsBuildResult result)
        {
            Vector3 BindPos(int bone) => PmxMeshBuilder.Conv(model.Bones[bone].Position, scale);
            int simRoot = unit[0];
            float total = unit.Skip(1).Max(b => length[b] - length[simRoot]);
            float maxSwing = tuning.maxSwingDegrees * Mathf.Deg2Rad;
            var keys = new List<(float a, float s)>();
            foreach (int bone in unit.Skip(1))
            {
                float a = total > 0f ? (length[bone] - length[simRoot]) / total : 1f;
                float theta = Mathf.Min(swing[bone], maxSwing);
                // 粒子偏离动画位置的上限是 2L(1-s)，即转角 θ 对应的弦长 2L·sin(θ/2)。
                float s = theta >= maxSwing ? 0f : 1f - Mathf.Sin(theta * 0.5f);
                keys.Add((a, s));
            }
            bool rigid = keys.All(k => k.s >= 0.999f);

            var unitBodies = ChainBodies(model, dynamicBodies, unit);
            float radius = unitBodies.Average(BodyRadius) * scale;
            var particles = unit.Skip(1).Select(BindPos).ToList();
            var unitColliders = new List<DynamicBoneColliderBase>();
            foreach (var c in colliders)
            {
                if (!unitBodies.Any(b => Collides(b, c.Body))) continue;
                if (particles.Any(p => c.Radius + radius - DistanceToSegment(p, c.A, c.B) > tuning.restPenetrationTolerance))
                {
                    result.RestExclusions++;
                    continue;
                }
                if (idlePose != null)
                {
                    // 绑定姿态下骨骼世界旋转为单位四元数，碰撞体相对骨骼的偏移就是绑定空间的差值。
                    var owner = idlePose[c.Body.BoneIndex];
                    var origin = BindPos(c.Body.BoneIndex);
                    Vector3 a = owner.MultiplyPoint3x4(c.A - origin), b = owner.MultiplyPoint3x4(c.B - origin);
                    if (unit.Skip(1).Any(bone => c.Radius + radius - DistanceToSegment(idlePose[bone].GetColumn(3), a, b) > tuning.restPenetrationTolerance))
                    {
                        result.IdleExclusions++;
                        continue;
                    }
                }
                c.Component ??= AttachCollider(c, boneTf[c.Body.BoneIndex], BindPos(c.Body.BoneIndex));
                unitColliders.Add(c.Component);
            }
            if (rigid && unitColliders.Count == 0)
            {
                // 全程锁死且没有碰撞：DynamicBone 每步都把粒子放回原位，等于不挂。
                result.RigidChains++;
                return false;
            }

            // 不与骨骼同名，避免按名字找骨骼的代码（Humanoid 映射、调试工具）误中。
            var go = new GameObject("DB_" + boneTf[simRoot].name);
            go.transform.SetParent(holder, false);
            var db = go.AddComponent<DynamicBone>();
            db.m_Roots = new List<Transform> { boneTf[simRoot] };
            db.m_UpdateRate = tuning.updateRate;
            db.m_FreezeAxis = DynamicBone.FreezeAxis.None;
            float mmdDamping = Mathf.Clamp01(unitBodies.Average(b => b.AngularDamping));
            db.m_Damping = Mathf.Lerp(tuning.dampingAtZero, tuning.dampingAtOne, mmdDamping);
            db.m_Elasticity = tuning.elasticity;
            db.m_Inert = tuning.inert;
            db.m_Stiffness = 1f;
            db.m_StiffnessDistrib = LinearCurve(keys);
            db.m_Radius = radius;
            var gravity = new Vector3(0f, -tuning.gravity / (tuning.updateRate * tuning.updateRate), 0f);
            if (tuning.absoluteGravity) db.m_Force = gravity;
            else db.m_Gravity = gravity;
            db.m_Colliders = unitColliders;
            return true;
        }

        // 用 App 的待机片段给模型摆一次姿势，取各骨骼的世界矩阵后把所有 Transform 原样还原。
        internal static Matrix4x4[] SampleIdlePose(Animator animator, Transform[] boneTf, string clipPath)
        {
            if (animator == null || !animator.isHuman || string.IsNullOrEmpty(clipPath)) return null;
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null || !clip.humanMotion)
            {
                Debug.LogWarning($"[PmxPhysics] 找不到待机片段 {clipPath}，头发只按绑定姿态检查碰撞体");
                return null;
            }
            var saved = animator.GetComponentsInChildren<Transform>(true)
                                .Select(t => (t, t.localPosition, t.localRotation, t.localScale)).ToList();
            bool rootMotion = animator.applyRootMotion;
            var graph = PlayableGraph.Create("PmxPhysics idle pose");
            try
            {
                animator.applyRootMotion = false;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(graph, "pose", animator);
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false);
                output.SetSourcePlayable(playable);
                graph.Evaluate();
                return boneTf.Select(t => t.localToWorldMatrix).ToArray();
            }
            finally
            {
                graph.Destroy();
                animator.applyRootMotion = rootMotion;
                foreach (var (t, position, rotation, scale) in saved)
                {
                    t.localPosition = position;
                    t.localRotation = rotation;
                    t.localScale = scale;
                }
            }
        }

        internal static Dictionary<int, List<int>> DynamicBodiesByBone(PmxModel model)
        {
            var dynamicBodies = new Dictionary<int, List<int>>();
            for (int i = 0; i < model.RigidBodies.Count; i++)
            {
                var rb = model.RigidBodies[i];
                if (rb.PhysicsMode == 0 || rb.BoneIndex < 0 || rb.BoneIndex >= model.Bones.Count) continue;
                if (!dynamicBodies.TryGetValue(rb.BoneIndex, out var list)) dynamicBodies[rb.BoneIndex] = list = new List<int>();
                list.Add(i);
            }
            return dynamicBodies;
        }

        // Chain root = dynamic bone whose parent is not dynamic. DynamicBone simulates the
        // root's whole transform subtree, so the subtree is the unit for every per-chain value.
        internal static List<(int root, List<int> subtree)> FindChains(PmxModel model, Dictionary<int, List<int>> dynamicBodies)
        {
            var bones = model.Bones;
            var children = new List<int>[bones.Count];
            for (int i = 0; i < bones.Count; i++) children[i] = new List<int>();
            for (int i = 0; i < bones.Count; i++)
                if (bones[i].ParentIndex >= 0 && bones[i].ParentIndex < bones.Count) children[bones[i].ParentIndex].Add(i);

            var chains = new List<(int root, List<int> subtree)>();
            foreach (int bi in dynamicBodies.Keys.OrderBy(b => b))
            {
                int parent = bones[bi].ParentIndex;
                if (parent >= 0 && dynamicBodies.ContainsKey(parent)) continue;
                var subtree = new List<int> { bi };
                for (int k = 0; k < subtree.Count; k++) subtree.AddRange(children[subtree[k]]);
                chains.Add((bi, subtree));
            }
            return chains;
        }

        // 决定碰撞与粒子半径的刚体：根骨固定不动，取其下各骨的刚体；链里只有根骨带刚体时退回根骨。
        internal static List<PmxRigidBody> ChainBodies(PmxModel model, Dictionary<int, List<int>> dynamicBodies, List<int> subtree)
        {
            var list = subtree.Skip(1).Where(dynamicBodies.ContainsKey)
                              .SelectMany(b => dynamicBodies[b]).Select(i => model.RigidBodies[i]).ToList();
            return list.Count > 0 ? list : dynamicBodies[subtree[0]].Select(i => model.RigidBodies[i]).ToList();
        }

        // 碰撞体只取挂在不参与模拟的骨骼上的静态刚体：挂进链里会被 DynamicBone 当成粒子。
        internal static List<(PmxRigidBody body, Vector3 a, Vector3 b, float radius)> StaticColliders(
            PmxModel model, HashSet<int> inChain, float scale)
        {
            var list = new List<(PmxRigidBody, Vector3, Vector3, float)>();
            foreach (var rb in model.RigidBodies)
            {
                if (rb.PhysicsMode != 0 || rb.BoneIndex < 0 || rb.BoneIndex >= model.Bones.Count || inChain.Contains(rb.BoneIndex)) continue;
                var (a, b, radius) = ColliderGeometry(rb, scale);
                list.Add((rb, a, b, radius));
            }
            return list;
        }

        // 每个动态刚体相对其上游刚体的最大转角（弧度）。上游指骨骼为其祖先的刚体；
        // 相邻链之间的横向关节 DynamicBone 无法表达，忽略。多个上游关节取最严的一个。
        private static float[] UpstreamSwingLimits(PmxModel model, out int unconstrained)
        {
            var bodies = model.RigidBodies;
            var limit = Enumerable.Repeat(float.PositiveInfinity, bodies.Count).ToArray();
            foreach (var j in model.Joints)
            {
                if (j.RigidBodyA < 0 || j.RigidBodyB < 0 || j.RigidBodyA >= bodies.Count || j.RigidBodyB >= bodies.Count) continue;
                int boneA = bodies[j.RigidBodyA].BoneIndex, boneB = bodies[j.RigidBodyB].BoneIndex;
                if (boneA < 0 || boneB < 0 || boneA == boneB) continue;
                float swing = JointSwing(j);
                if (bodies[j.RigidBodyB].PhysicsMode != 0 && IsAncestor(model.Bones, boneA, boneB))
                    limit[j.RigidBodyB] = Mathf.Min(limit[j.RigidBodyB], swing);
                else if (bodies[j.RigidBodyA].PhysicsMode != 0 && IsAncestor(model.Bones, boneB, boneA))
                    limit[j.RigidBodyA] = Mathf.Min(limit[j.RigidBodyA], swing);
            }
            unconstrained = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i].PhysicsMode == 0 || !float.IsPositiveInfinity(limit[i])) continue;
                limit[i] = Mathf.PI;
                unconstrained++;
            }
            return limit;
        }

        // 取三轴中最大的转动范围；某轴下限大于上限表示该轴不受限。
        private static float JointSwing(PmxJoint j)
        {
            float swing = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                float lo = j.RotLimitLower[axis], hi = j.RotLimitUpper[axis];
                if (lo > hi) return Mathf.PI;
                swing = Mathf.Max(swing, Mathf.Max(Mathf.Abs(lo), Mathf.Abs(hi)));
            }
            return swing;
        }

        private static float BoneLimit(int bone, Dictionary<int, List<int>> dynamicBodies, float[] bodyLimit)
            => dynamicBodies.TryGetValue(bone, out var list) ? list.Min(i => bodyLimit[i]) : 0f;

        private static bool IsAncestor(List<PmxBone> bones, int ancestor, int of)
        {
            int p = bones[of].ParentIndex, guard = 0;
            while (p >= 0 && guard++ < 100000) { if (p == ancestor) return true; p = bones[p].ParentIndex; }
            return false;
        }

        // PMX 的分组掩码按位表示「与该组碰撞」，双方都允许才碰撞（与 MMD/Bullet 的过滤一致）。
        internal static bool Collides(PmxRigidBody a, PmxRigidBody b)
            => ((a.NonCollisionMask >> b.Group) & 1) != 0 && ((b.NonCollisionMask >> a.Group) & 1) != 0;

        // 胶囊/球体取半径；盒体取较薄的两个半边长中较小者，作为粒子到表面的距离。
        internal static float BodyRadius(PmxRigidBody b)
            => b.Shape == 1 ? Mathf.Min(b.Size.x, b.Size.z) : b.Size.x;

        // 绑定姿态下的胶囊轴线两端（球体两端重合）与半径，Unity 坐标。
        internal static (Vector3 a, Vector3 b, float radius) ColliderGeometry(PmxRigidBody rb, float scale)
        {
            var center = PmxMeshBuilder.Conv(rb.Position, scale);
            var rotation = ToUnityRotation(rb.Rotation);
            float radius, half;
            Vector3 axis = Vector3.up;
            switch (rb.Shape)
            {
                case 0: radius = rb.Size.x; half = 0f; break;
                case 2: radius = rb.Size.x; half = rb.Size.y * 0.5f; break;
                default:
                    // 盒体近似成沿最长边的胶囊，半径取另两边中较大者，保证不比盒体小。
                    var e = rb.Size;
                    int k = e.x >= e.y && e.x >= e.z ? 0 : e.y >= e.z ? 1 : 2;
                    axis = k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward;
                    radius = k == 0 ? Mathf.Max(e.y, e.z) : k == 1 ? Mathf.Max(e.x, e.z) : Mathf.Max(e.x, e.y);
                    half = Mathf.Max(e[k] - radius, 0f);
                    break;
            }
            var offset = rotation * axis * (half * scale);
            return (center - offset, center + offset, radius * scale);
        }

        // PMX 刚体旋转是 MMD 空间的欧拉角（先 Z、再 X、最后 Y，与 Quaternion.Euler 相同顺序）；
        // MMD→Unity 是绕 Y 转 180°，对四元数而言等于 x、z 分量取反。
        internal static Quaternion ToUnityRotation(Vector3 euler)
        {
            var q = Quaternion.Euler(euler * Mathf.Rad2Deg);
            return new Quaternion(-q.x, q.y, -q.z, q.w);
        }

        private static DynamicBoneCollider AttachCollider(ColliderShape shape, Transform bone, Vector3 bindBonePos)
        {
            var go = new GameObject("DBCollider_" + shape.Body.NameLocal);
            // 骨骼在绑定姿态下世界旋转为单位四元数，绑定空间的差值就是本地坐标；Humanoid 之后手臂已转成 T-pose，不能用世界坐标。
            go.transform.SetParent(bone, false);
            var axis = shape.B - shape.A;
            go.transform.localPosition = (shape.A + shape.B) * 0.5f - bindBonePos;
            go.transform.localRotation = axis.sqrMagnitude > 1e-12f ? Quaternion.FromToRotation(Vector3.up, axis) : Quaternion.identity;
            var collider = go.AddComponent<DynamicBoneCollider>();
            collider.m_Bound = DynamicBoneColliderBase.Bound.Outside;
            collider.m_Direction = DynamicBoneColliderBase.Direction.Y;
            collider.m_Center = Vector3.zero;
            collider.m_Radius = shape.Radius;
            // m_Height 含两端半球；为 0 或不超过直径时按球体处理。
            collider.m_Height = axis.magnitude + 2f * shape.Radius;
            shape.Component = collider;
            return collider;
        }

        internal static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        // 分段线性曲线：同一位置（分叉链）取较大的刚度，宁可偏紧也不放开作者锁住的部分。
        private static AnimationCurve LinearCurve(List<(float a, float s)> points)
        {
            var merged = new List<(float a, float s)>();
            foreach (var p in points.OrderBy(p => p.a))
            {
                int last = merged.Count - 1;
                if (last >= 0 && p.a - merged[last].a < 1e-4f)
                    merged[last] = (merged[last].a, Mathf.Max(merged[last].s, p.s));
                else merged.Add(p);
            }
            if (merged[0].a > 0f) merged.Insert(0, (0f, merged[0].s));
            var keys = new Keyframe[merged.Count];
            for (int i = 0; i < merged.Count; i++)
            {
                float inSlope = i > 0 ? Slope(merged[i - 1], merged[i]) : 0f;
                float outSlope = i + 1 < merged.Count ? Slope(merged[i], merged[i + 1]) : 0f;
                keys[i] = new Keyframe(merged[i].a, merged[i].s, inSlope, outSlope);
            }
            return new AnimationCurve(keys);
        }

        private static float Slope((float a, float s) p0, (float a, float s) p1)
            => p1.a - p0.a > 1e-6f ? (p1.s - p0.s) / (p1.a - p0.a) : 0f;

        // 仅用于统计日志：按链根骨名及其第一个非动态祖先归类。
        private static Cat Classify(PmxModel model, int boneIndex, Dictionary<int, List<int>> dynamicBodies)
        {
            string self = model.Bones[boneIndex].NameLocal ?? "";
            int a = model.Bones[boneIndex].ParentIndex;
            while (a >= 0 && dynamicBodies.ContainsKey(a)) a = model.Bones[a].ParentIndex;
            string anchor = a >= 0 ? (model.Bones[a].NameLocal ?? "") : "";
            string s = self + "|" + anchor;
            if (Has(s, "胸", "Bust", "Breast")) return Cat.Breast;
            if (Has(s, "Hair", "髪", "頭", "Head", "Earring", "メガネ")) return Cat.Hair;
            if (Has(s, "Skirt", "スカート", "下半身", "腰", "qz", "qh", "yb", "zb", "裙")) return Cat.Skirt;
            return Cat.Other;
        }

        private static bool Has(string s, params string[] keys) => keys.Any(k => s.Contains(k));
    }
}
