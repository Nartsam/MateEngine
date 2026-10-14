using System;
using System.Runtime.InteropServices;

namespace MateEngine.PmxPipeline
{
    // Assets/Plugins/x86_64/MateMmdPhysics.dll 的导入声明（源码与构建脚本在 Tools/MmdPhysicsNative/）。
    // 变换数组统一为每个刚体 7 个 float：px py pz qx qy qz qw。
    internal static class MmdPhysicsNative
    {
        private const string Dll = "MateMmdPhysics";

        [DllImport(Dll)] public static extern IntPtr mp_world_create(float gravityY);
        [DllImport(Dll)] public static extern void mp_world_destroy(IntPtr world);
        [DllImport(Dll)] public static extern void mp_world_set_iterations(IntPtr world, int iterations);

        [DllImport(Dll)]
        public static extern int mp_body_add(IntPtr world, int shape, float[] size, float[] pose, float mass,
                                             float linearDamping, float angularDamping, float restitution, float friction,
                                             int group, int mask, int kinematic, int additionalDamping);

        [DllImport(Dll)]
        public static extern int mp_joint_add(IntPtr world, int a, int b, float[] pose,
                                              float[] linearLower, float[] linearUpper,
                                              float[] angularLower, float[] angularUpper,
                                              float[] linearSpring, float[] angularSpring,
                                              float stopErp, int disableLinkedCollision);

        [DllImport(Dll)] public static extern void mp_set_anchors(IntPtr world, int count, int[] ids, int[] anchors);
        [DllImport(Dll)] public static extern void mp_set_kinematic(IntPtr world, int count, int[] ids, float[] poses);
        [DllImport(Dll)] public static extern void mp_reset(IntPtr world, int count, int[] ids, float[] poses);
        [DllImport(Dll)] public static extern int mp_step(IntPtr world, float dt, int maxSubSteps, float fixedTimeStep);
        [DllImport(Dll)] public static extern void mp_get_transforms(IntPtr world, int count, int[] ids, float[] poses);
    }
}
