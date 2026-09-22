using UnityEngine;

namespace MateEngine.PmxPipeline
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class MEModelDirections : MonoBehaviour
    {
        [SerializeField] private Transform head;
        [SerializeField] private Renderer[] renderers;
        [SerializeField] private Vector3 sunDirection;
        [SerializeField] private Vector3 headBackLocal;
        [SerializeField] private Vector3 headRightLocal;

        private MaterialPropertyBlock properties;
        private static readonly int Sun = Shader.PropertyToID("_MESunDir");
        private static readonly int HeadBack = Shader.PropertyToID("_MEHeadFront");
        private static readonly int HeadRight = Shader.PropertyToID("_MEHeadRight");

        public void Configure(Transform headBone, Renderer[] targets, Vector3 sun,
                              Vector3 headBack, Vector3 headRight)
        {
            head = headBone;
            renderers = targets;
            sunDirection = sun.normalized;
            // Blender 的 FRONT 指向头后方。保存到头骨局部空间，后续随动画和鼠标跟踪
            // 更新；SUN 保持预设的世界方向。PropertyBlock 避免多个实例共享材质时互相覆盖。
            headBackLocal = head.InverseTransformDirection(headBack).normalized;
            headRightLocal = head.InverseTransformDirection(headRight).normalized;
        }

        private void OnEnable() => UpdateDirections();
        private void LateUpdate() => UpdateDirections();

        public void UpdateDirections()
        {
            if (head == null || renderers == null) return;
            properties ??= new MaterialPropertyBlock();
            var back = head.TransformDirection(headBackLocal).normalized;
            var right = head.TransformDirection(headRightLocal).normalized;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(properties);
                properties.SetVector(Sun, sunDirection);
                properties.SetVector(HeadBack, back);
                properties.SetVector(HeadRight, right);
                renderer.SetPropertyBlock(properties);
            }
        }
    }
}
