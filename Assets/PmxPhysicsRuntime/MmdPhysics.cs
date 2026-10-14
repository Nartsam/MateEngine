using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    // PMX 刚体物理的运行时（ADR-0017）：导出时把 PMX 的刚体与关节原样存进本组件，运行时交给 Bullet
    // 模拟，与 MMD 用同一种物理引擎、同一组参数。每帧在动画与鼠标跟随 IK 之后执行：模式 0 的刚体
    // 跟随骨骼，步进后把模式 1/2 的刚体姿态写回骨骼。
    //
    // 物理空间沿用 Unity 世界的坐标轴，长度换回 PMX 原始单位（世界长度 ÷ unitLength ÷ 模型缩放）：
    // Bullet 的碰撞余量、休眠阈值都是绝对量，换成米会与 MMD 差一个数量级。MMD→Unity 是绕 Y 转 180°，
    // 属于整体旋转，不改变任何动力学结果。
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class MmdPhysics : MonoBehaviour
    {
        public enum DampingFormula
        {
            // Bullet 2.74 起的写法：每步速度乘 (1 - d)^dt。
            Exponential,
            // 更早的写法：每步速度乘 (1 - d·dt)。查不到 MMD 内置的 Bullet 是哪一版，两种都保留以便对照。
            Linear,
        }

        // 模式 1 的刚体怎样写回骨骼。Bullet 的约束有弹性：衣物被甩动、被大腿顶住时关节会被拉开，相邻刚体的
        // 距离远大于骨段长度。链根且关节允许平移的骨骼（胸部这类靠平移晃动的结构）三种方式都写回位置。
        public enum BoneWriteMode
        {
            // MMD 原样写回位置与旋转：网格随刚体让开身体，但会像弹力绳一样被拉长（实测骨段最多拉长到原来的数倍）。
            Position,
            // 只写旋转：骨段长度不变，但刚体被推开的位移一并丢掉，网格停在原处穿进身体。
            Rotation,
            // 只写旋转，并把父骨转向本骨在物理里的位置：网格随刚体让开，骨段长度仍不变。
            Aim,
        }

        [Serializable]
        public sealed class Body
        {
            public string name;
            // 为空时随模型根节点：PMX 里删掉骨骼后留下的刚体仍参与模拟，只是不驱动任何骨骼。
            public Transform bone;
            public int shape;                          // 0 球、1 盒、2 胶囊
            public Vector3 size;                       // PMX 单位：球半径 / 盒半边长 / 胶囊半径与圆柱段高
            // 绑定姿态，模型空间（根节点为原点），PMX 单位、Unity 坐标轴。绑定姿态下骨骼世界旋转为单位旋转，
            // 所以刚体相对骨骼的偏移就是两者位置之差加上刚体自身的旋转。
            public Vector3 position;
            public Quaternion rotation = Quaternion.identity;
            public Vector3 bonePosition;
            public float mass, linearDamping, angularDamping, restitution, friction;
            public int group, mask;
            public int mode;                           // 0 跟随骨骼、1 物理、2 物理但骨骼位置仍取动画
        }

        [Serializable]
        public sealed class Joint
        {
            public string name;
            public int bodyA, bodyB;
            public Vector3 position;                   // 绑定姿态，模型空间，PMX 单位、Unity 坐标轴
            public Quaternion rotation = Quaternion.identity;
            // 均定义在关节局部轴上（弧度 / PMX 单位）；下限大于上限表示该轴自由。
            public Vector3 linearLower, linearUpper, angularLower, angularUpper;
            public Vector3 linearSpring, angularSpring;
        }

        [Tooltip("模型缩放为 1 时一个 PMX 单位对应的米数，与导出时的缩放一致")]
        public float unitLength = 0.08f;
        [Tooltip("重力加速度，PMX 单位/秒²；MMD 为 9.8×10")]
        public float gravity = 98f;
        public float fixedTimeStep = 1f / 60f;
        public int maxSubSteps = 5;
        [Tooltip("约束求解的迭代次数，Bullet 默认 10")]
        // 沿用 Bullet 默认的 10 次（MMD 也是）。30 次时运动中关节被拉开的量少三到五成，但锁死的衣物更像一块硬板：
        // 165 帧/秒下寻梦者坐下时外套下摆被大腿掀到腿上、不再垂下，静止时与身体来回推挤的抖动也更明显；
        // 关节弹簧也会变弱：btGeneric6DofSpringConstraint 的速度系数除以迭代次数，30 次时只剩 1/3。
        public int solverIterations = 10;
        public DampingFormula dampingFormula = DampingFormula.Exponential;
        [Tooltip("关节触及限位时的纠正强度（BT_CONSTRAINT_STOP_ERP），沿用 MMD 系播放器的 0.475；≤0 时用 Bullet 默认值")]
        // 调低（如 0.2）可压住衣物被身体顶住时与限位来回推的抖动，锁死的衣物坐下时也更容易顺着大腿垂下；但短发之类
        // 靠限位撑住形状的部分也随之变软（寻梦者头顶那缕翘起的头发在重力下耷拉）。只作为逐角色调整。
        public float jointStopErp = 0.475f;
        [Tooltip("子刚体名匹配该正则的关节改用 jointStopErpMatched；为空时全部用 jointStopErp")]
        // 同一角色里衣物与头发要不同软硬时用，如衣物调软、头发保持。
        public string jointStopErpPattern = "";
        public float jointStopErpMatched = 0.475f;
        [Tooltip("Bullet 的低速附加阻尼，抑制静止时的微小抖动")]
        public bool additionalDamping = true;
        [Tooltip("关节连接的两个刚体之间不碰撞")]
        public bool disableLinkedCollision = true;
        [Tooltip("刚体写回骨骼的方式；Position 为 MMD 原样")]
        public BoneWriteMode boneWrite = BoneWriteMode.Aim;
        [Tooltip("动态刚体输出时，按它挂着的运动学刚体补上比本帧晚的那一个子步的整体运动")]
        // 子步之间插值使动态刚体的输出固定比动画晚一个子步，头一快速转动，贴着限位的刘海就落后头部、转向一变像在甩，
        // 胸部也拖在胸骨后面。MMD 按帧时刻推进物理，没有这个延迟。关掉只用于对照。
        public bool followAnchors = true;
        [Tooltip("模型根节点一帧内移动超过该距离（PMX 单位）时视为瞬移，物理重置到当前姿态")]
        public float teleportDistance = 10f;

        public Body[] bodies = Array.Empty<Body>();
        public Joint[] joints = Array.Empty<Joint>();

        private IntPtr world;
        private bool failed, needsReset;
        private int[] allIds, kinematicIds, writeIds, aimParent;
        private bool[] writePosition;
        private float[] allPoses, kinematicPoses, writePoses;
        private Transform[] restoreBones;
        private Vector3[] restorePositions;
        private Quaternion[] restoreRotations;
        private float lastUnit;
        private Vector3 lastRoot;

        public bool IsRunning => world != IntPtr.Zero;

        // 动画只写人形骨骼，物理骨骼保持上一帧写入的姿态：先还原成导出时的局部姿态，再交给动画与 IK。
        private void Update() => RestorePose();
        private void LateUpdate() => Simulate(Time.deltaTime);
        private void OnDisable()
        {
            RestorePose();
            Shutdown();
        }

        /// <summary>创建物理世界；LateUpdate 首次运行时自动调用，编辑器验证装置直接调用。</summary>
        public bool Initialize()
        {
            if (world != IntPtr.Zero) return true;
            if (failed) return false;
            try
            {
                world = MmdPhysicsNative.mp_world_create(-gravity);
                MmdPhysicsNative.mp_world_set_iterations(world, solverIterations);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                failed = true;
                Debug.LogError($"[MmdPhysics] 无法加载物理库 MateMmdPhysics.dll，物理不生效：{ex.Message}");
                return false;
            }

            // 刚体与关节都按绑定姿态创建：关节坐标系相对两刚体的位置由此确定，之后再整体重置到当前姿态。
            var size = new float[3];
            var pose = new float[7];
            for (int i = 0; i < bodies.Length; i++)
            {
                var b = bodies[i];
                Write(size, b.size);
                Write(pose, 0, b.position, b.rotation);
                int id = MmdPhysicsNative.mp_body_add(world, b.shape, size, pose, b.mass,
                                                      Damping(b.linearDamping), Damping(b.angularDamping),
                                                      b.restitution, b.friction, b.group, b.mask,
                                                      b.mode == 0 ? 1 : 0, additionalDamping ? 1 : 0);
                if (id != i) throw new InvalidOperationException($"刚体序号错位：{id} ≠ {i}");
            }
            float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
            var matched = string.IsNullOrEmpty(jointStopErpPattern) ? null : new Regex(jointStopErpPattern);
            foreach (var j in joints)
            {
                Write(pose, 0, j.position, j.rotation);
                bool useMatched = matched != null && j.bodyB >= 0 && j.bodyB < bodies.Length && matched.IsMatch(bodies[j.bodyB].name ?? "");
                MmdPhysicsNative.mp_joint_add(world, j.bodyA, j.bodyB, pose, V(j.linearLower), V(j.linearUpper),
                                              V(j.angularLower), V(j.angularUpper), V(j.linearSpring), V(j.angularSpring),
                                              useMatched ? jointStopErpMatched : jointStopErp, disableLinkedCollision ? 1 : 0);
            }

            allIds = Enumerable.Range(0, bodies.Length).ToArray();
            kinematicIds = allIds.Where(i => bodies[i].mode == 0).ToArray();
            // 每根骨骼只由第一个动态刚体驱动；按层级深度从父到子写回，子骨才能接在父骨的新姿态上。
            writeIds = allIds.Where(i => bodies[i].mode != 0 && bodies[i].bone != null)
                             .GroupBy(i => bodies[i].bone).Select(g => g.First())
                             .OrderBy(i => Depth(bodies[i].bone)).ToArray();
            // 模式 2 只取旋转；不按 MMD 原样写回时，模式 1 也只有「链根且关节允许平移」才写回位置。
            var linked = Enumerable.Range(0, bodies.Length).Select(_ => new List<int>()).ToArray();
            var translatable = new bool[bodies.Length];
            foreach (var j in joints)
            {
                if (j.bodyA < 0 || j.bodyB < 0 || j.bodyA >= bodies.Length || j.bodyB >= bodies.Length) continue;
                linked[j.bodyA].Add(j.bodyB);
                linked[j.bodyB].Add(j.bodyA);
                bool moves = j.linearLower != Vector3.zero || j.linearUpper != Vector3.zero;
                translatable[j.bodyA] |= moves;
                translatable[j.bodyB] |= moves;
            }
            if (followAnchors)
            {
                // 参照刚体取沿关节连到的最近一个运动学刚体：刘海连到头，胸部连到胸骨。
                var anchor = Enumerable.Repeat(-1, bodies.Length).ToArray();
                var queue = new Queue<int>(kinematicIds);
                foreach (int i in kinematicIds) anchor[i] = i;
                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    foreach (int k in linked[u])
                        if (anchor[k] < 0)
                        {
                            anchor[k] = anchor[u];
                            queue.Enqueue(k);
                        }
                }
                var dynamicIds = allIds.Where(i => bodies[i].mode != 0).ToArray();
                MmdPhysicsNative.mp_set_anchors(world, dynamicIds.Length, dynamicIds, dynamicIds.Select(i => anchor[i]).ToArray());
            }
            // 链中间：某个祖先骨骼上挂着与本刚体有关节相连的刚体。
            bool InChain(int i)
            {
                var neighbours = new HashSet<Transform>(linked[i].Select(k => bodies[k].bone).Where(t => t != null));
                for (var p = bodies[i].bone.parent; p != null && p != transform; p = p.parent)
                    if (neighbours.Contains(p)) return true;
                return false;
            }
            bool keepLengths = boneWrite != BoneWriteMode.Position;
            writePosition = writeIds.Select(i => bodies[i].mode == 1 &&
                                                 (!keepLengths || (!InChain(i) && translatable[i]))).ToArray();
            // 父骨也由物理驱动时，由本骨把它转向自己；父骨有多个这样的子骨时方向互相冲突，不转。
            aimParent = Enumerable.Repeat(-1, writeIds.Length).ToArray();
            if (boneWrite == BoneWriteMode.Aim)
            {
                var slot = new Dictionary<Transform, int>();
                for (int k = 0; k < writeIds.Length; k++) slot[bodies[writeIds[k]].bone] = k;
                var children = new int[writeIds.Length];
                for (int k = 0; k < writeIds.Length; k++)
                {
                    var bone = bodies[writeIds[k]].bone;
                    if (bodies[writeIds[k]].mode != 1 || writePosition[k] || bone.parent == null ||
                        !slot.TryGetValue(bone.parent, out int parent)) continue;
                    aimParent[k] = parent;
                    children[parent]++;
                }
                for (int k = 0; k < writeIds.Length; k++)
                    if (aimParent[k] >= 0 && children[aimParent[k]] > 1) aimParent[k] = -1;
            }
            allPoses = new float[allIds.Length * 7];
            kinematicPoses = new float[kinematicIds.Length * 7];
            writePoses = new float[writeIds.Length * 7];
            // 只在第一次记录：停用后再启用时，骨骼可能还停在物理写入的姿态上。
            if (restoreBones == null)
            {
                restoreBones = writeIds.Select(i => bodies[i].bone).ToArray();
                restorePositions = restoreBones.Select(t => t.localPosition).ToArray();
                restoreRotations = restoreBones.Select(t => t.localRotation).ToArray();
            }
            needsReset = true;
            return true;
        }

        public void Shutdown()
        {
            if (world == IntPtr.Zero) return;
            MmdPhysicsNative.mp_world_destroy(world);
            world = IntPtr.Zero;
        }

        /// <summary>把物理驱动的骨骼还原成导出时的局部姿态，需在动画求值之前调用。</summary>
        public void RestorePose()
        {
            if (restoreBones == null) return;
            for (int i = 0; i < restoreBones.Length; i++)
            {
                if (restoreBones[i] == null) continue;
                restoreBones[i].localPosition = restorePositions[i];
                restoreBones[i].localRotation = restoreRotations[i];
            }
        }

        /// <summary>读出全部刚体当前的位姿（物理空间、PMX 单位，每个 7 个数），供编辑器验证装置量关节被拉开的距离。</summary>
        public void ReadBodyPoses(float[] poses)
        {
            if (world != IntPtr.Zero) MmdPhysicsNative.mp_get_transforms(world, allIds.Length, allIds, poses);
        }

        /// <summary>下一次步进前把所有刚体放到当前骨骼姿态、清零速度。</summary>
        public void ResetToPose() => needsReset = true;

        /// <summary>步进并写回骨骼，需在动画与 IK 之后调用。</summary>
        public void Simulate(float deltaTime)
        {
            if (!Initialize() || deltaTime <= 0f) return;
            float unit = unitLength * Mathf.Abs(transform.lossyScale.x);
            if (unit <= 1e-6f) return;
            var root = transform.position / unit;
            if (needsReset || Mathf.Abs(unit - lastUnit) > lastUnit * 1e-3f ||
                (root - lastRoot).sqrMagnitude > teleportDistance * teleportDistance)
            {
                for (int i = 0; i < allIds.Length; i++) BodyPose(bodies[allIds[i]], unit, allPoses, i);
                MmdPhysicsNative.mp_reset(world, allIds.Length, allIds, allPoses);
                needsReset = false;
            }
            lastUnit = unit;
            lastRoot = root;

            for (int i = 0; i < kinematicIds.Length; i++) BodyPose(bodies[kinematicIds[i]], unit, kinematicPoses, i);
            MmdPhysicsNative.mp_set_kinematic(world, kinematicIds.Length, kinematicIds, kinematicPoses);
            MmdPhysicsNative.mp_step(world, deltaTime, maxSubSteps, fixedTimeStep);
            MmdPhysicsNative.mp_get_transforms(world, writeIds.Length, writeIds, writePoses);

            for (int i = 0; i < writeIds.Length; i++)
            {
                var b = bodies[writeIds[i]];
                int o = i * 7;
                var p = new Vector3(writePoses[o], writePoses[o + 1], writePoses[o + 2]);
                var q = new Quaternion(writePoses[o + 3], writePoses[o + 4], writePoses[o + 5], writePoses[o + 6]);
                var r = q * Quaternion.Inverse(b.rotation);
                if (aimParent[i] >= 0)
                {
                    // 父骨已按它自己的刚体写好旋转（按深度从父到子写回），这里只补一个摆动，扭转保持不变。
                    var parent = b.bone.parent;
                    var from = b.bone.position - parent.position;
                    var to = (p - r * (b.position - b.bonePosition)) * unit - parent.position;
                    if (from.sqrMagnitude > 1e-12f && to.sqrMagnitude > 1e-12f)
                        parent.rotation = Quaternion.FromToRotation(from, to) * parent.rotation;
                }
                b.bone.rotation = r;
                // 只取旋转时位置仍由父骨与局部偏移决定（模式 2 即 MMD 的「物理+骨骼位置合わせ」）。
                if (writePosition[i]) b.bone.position = (p - r * (b.position - b.bonePosition)) * unit;
            }
        }

        // 骨骼当前姿态下刚体的物理空间位姿。
        private void BodyPose(Body b, float unit, float[] poses, int index)
        {
            var t = b.bone != null ? b.bone : transform;
            var r = t.rotation;
            Write(poses, index, t.position / unit + r * (b.position - b.bonePosition), r * b.rotation);
        }

        private float Damping(float d)
        {
            if (dampingFormula == DampingFormula.Exponential) return d;
            // 步长固定，旧写法每步的衰减系数可以精确换算成新写法的等效值。
            float perStep = Mathf.Max(1f - d * fixedTimeStep, 0f);
            return 1f - Mathf.Pow(perStep, 1f / fixedTimeStep);
        }

        private int Depth(Transform t)
        {
            int depth = 0;
            for (var p = t; p != null && p != transform; p = p.parent) depth++;
            return depth;
        }

        private static void Write(float[] dst, Vector3 v)
        {
            dst[0] = v.x; dst[1] = v.y; dst[2] = v.z;
        }

        private static void Write(float[] dst, int index, Vector3 p, Quaternion q)
        {
            int o = index * 7;
            dst[o] = p.x; dst[o + 1] = p.y; dst[o + 2] = p.z;
            dst[o + 3] = q.x; dst[o + 4] = q.y; dst[o + 5] = q.z; dst[o + 6] = q.w;
        }
    }
}
