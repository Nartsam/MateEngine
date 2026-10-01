using UnityEngine;

namespace MateEngine.PmxPipeline
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class MEModelDirections : MonoBehaviour
    {
        [SerializeField] private Transform head;
        [SerializeField] private Renderer[] renderers;
        // 模型根节点局部方向。导出时根节点为单位旋转，旧包里存的世界方向数值与此相同。
        [SerializeField] private Vector3 sunDirection;
        [SerializeField] private Vector3 headBackLocal;
        [SerializeField] private Vector3 headRightLocal;

        public Renderer[] Renderers => renderers;

        private MaterialPropertyBlock properties;
        private MEModelBloom bloom;
        private bool bloomRejected;
        private static readonly int Sun = Shader.PropertyToID("_MESunDir");
        private static readonly int HeadBack = Shader.PropertyToID("_MEHeadFront");
        private static readonly int HeadRight = Shader.PropertyToID("_MEHeadRight");

        public void Configure(Transform headBone, Renderer[] targets, Vector3 sun,
                              Vector3 headBack, Vector3 headRight)
        {
            head = headBone;
            renderers = targets;
            // SUN 跟随模型根节点而不是 App 世界：主场景的 Model 节点绕 Y 转了 180°，按世界方向
            // 使用会让角色变成逆光。根节点坐标系即预设世界相对角色的关系，任何相机角度都与
            // Blender 一致；动画只转骨骼，不影响它。
            sunDirection = transform.InverseTransformDirection(sun).normalized;
            // Blender 的 FRONT 指向头后方。保存到头骨局部空间，后续随动画和鼠标跟踪
            // 更新。PropertyBlock 避免多个实例共享材质时互相覆盖。
            headBackLocal = head.InverseTransformDirection(headBack).normalized;
            headRightLocal = head.InverseTransformDirection(headRight).normalized;
        }

        private void OnEnable() => UpdateDirections();
        private void LateUpdate()
        {
            UpdateDirections();
            if (Application.isPlaying) BindBloom();
        }

        /// <summary>接入主相机的模型合成；LateUpdate 每帧调用，编辑器验收直接调用以覆盖同一路径。</summary>
        public bool BindBloom()
        {
            if (bloomRejected) return false;
            if (bloom != null && bloom.IsBoundTo(this)) return true;
            var camera = Camera.main;
            if (camera == null) return false;
            // Unity 对象不用 ??：它只判断 C# 引用，绕过 Unity 自己的对象存活判断。
            if (!camera.TryGetComponent(out MEModelBloom effect))
                effect = camera.gameObject.AddComponent<MEModelBloom>();
            // 先置位：校验不通过或抛异常后都不再每帧重试。
            bloomRejected = true;
            if (!effect.SetModel(this)) return false;
            bloomRejected = false;
            bloom = effect;
            return true;
        }

        private void OnDisable()
        {
            if (bloom != null) bloom.ClearModel(this);
            bloom = null;
        }

        public void UpdateDirections()
        {
            if (head == null || renderers == null) return;
            properties ??= new MaterialPropertyBlock();
            var sun = transform.TransformDirection(sunDirection).normalized;
            var back = head.TransformDirection(headBackLocal).normalized;
            var right = head.TransformDirection(headRightLocal).normalized;
            // 脸部 SDF 只认头部左右转向，去掉俯仰与侧倾：鼠标在脸下方时头会低头，日光相对脸部
            // 变成从头顶照下，硬边阴影铺满半张脸，且鼠标稍一左右移动就整块换边。FRONT/RIGHT
            // 只在脸部材质里与 SUN 做点积，身体不受影响；头部正视时与预设数值逐位相同。
            var up = transform.up;
            var level = Vector3.ProjectOnPlane(back, up);
            // 躺倒等头部前后方向接近竖直的姿势无法定义水平朝向，沿用原方向。
            if (level.sqrMagnitude > 0.04f)
            {
                back = level.normalized;
                right = Vector3.Cross(up, back);
            }
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(properties);
                properties.SetVector(Sun, sun);
                properties.SetVector(HeadBack, back);
                properties.SetVector(HeadRight, right);
                renderer.SetPropertyBlock(properties);
            }
        }
    }
}
