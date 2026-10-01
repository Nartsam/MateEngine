using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MateEngine.PmxPipeline
{
    // 保留主相机与相机空间 UI 的引用：仅把转译模型先渲到 HDR，再在主相机绘制前合入。
    // 入口位于 OnPreCull，因此普通帧和 MateScreenshotHandler 的 Camera.Render 都走同一路径。
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    public sealed class MEModelBloom : MonoBehaviour
    {
        // 转译器的资产契约：LUT 第 0 行是固定 Filmic High Contrast，后续行属于各材质节点。
        public const float ViewLo = -12.473931188f;
        public const float ViewHi = 4.026068812f;
        private const int ModelLayer = 10;
        private const int ModelMask = 1 << ModelLayer;
        private static readonly int DeferViewTransform = Shader.PropertyToID("_MEDeferViewTransform");
        private static MEModelBloom active;

        private Camera mainCamera;
        private Camera modelCamera;
        private MEModelDirections model;
        private GameObject[] modelObjects;
        private int[] originalLayers;
        private int originalModelMask;
        private int captureMask;
        private Texture lut;
        private RenderTexture hdr;
        private RenderTexture composed;
        private CommandBuffer present;
        private MEPostCompose compose;
        private bool bloomEnabled = true;

        public bool IsBoundTo(MEModelDirections owner) => model == owner;

        public static void ApplySetting(GameObject legacyBloom, bool value)
        {
            if (active != null) active.bloomEnabled = value;
            // 同一开关在转译模型上控制新合成，避免旧 PostProcess Bloom 再处理一次并破坏 alpha。
            if (legacyBloom != null) legacyBloom.SetActive(value && active == null);
        }

        private static void RefreshSetting()
        {
            if (!Application.isPlaying || SaveLoadHandler.Instance == null) return;
            bool value = SaveLoadHandler.Instance.data.bloom;
            ApplySetting(null, value);
            foreach (var handler in FindObjectsByType<SettingsHandlerToggles>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                ApplySetting(handler.bloomObject, value);
        }

        public bool SetModel(MEModelDirections owner)
        {
            if (model == owner) return true;
            // 先校验再拆除：不合格的模型不能打断当前模型的绑定。
            // App 会给角色注入提示/粒子等辅助 Renderer；只处理导出时明确绑定的模型渲染器。
            var renderers = owner.Renderers;
            Texture modelLut = null;
            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || !mat.shader.name.StartsWith("Hidden/MateEngine/Transpiled/", StringComparison.Ordinal))
                        return false;
                    var candidate = mat.GetTexture("_MELut");
                    if (candidate == null || (modelLut != null && candidate != modelLut)) return false;
                    modelLut = candidate;
                }
            }
            if (modelLut == null) return false;

            ClearModel();
            mainCamera = GetComponent<Camera>();
            compose = new MEPostCompose { PremultipliedOutput = true };
            modelCamera = new GameObject("PMX Bloom Camera").AddComponent<Camera>();
            modelCamera.gameObject.hideFlags = HideFlags.HideAndDontSave;
            modelCamera.enabled = false;
            model = owner;
            lut = modelLut;
            originalModelMask = mainCamera.cullingMask & ModelMask;
            // 遮挡 quad 只写深度，必须与模型一起渲；主相机事后写深度擦不掉已合入的颜色。
            captureMask = ModelMask | LayerMask.GetMask("WindowOcclusion");
            modelObjects = new GameObject[renderers.Length];
            originalLayers = new int[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                modelObjects[i] = renderers[i].gameObject;
                originalLayers[i] = modelObjects[i].layer;
                modelObjects[i].layer = ModelLayer;
            }
            mainCamera.cullingMask &= ~ModelMask;
            present = new CommandBuffer { name = "PMX Bloom before UI" };
            mainCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, present);
            enabled = true;
            active = this;
            RefreshSetting();
            Debug.Log($"[PMX Bloom] Model {owner.name}; bloom={bloomEnabled}; original alpha retained");
            return true;
        }

        public void ClearModel(MEModelDirections owner)
        {
            // 新模型先 Instantiate、旧模型随后禁用；旧实例不能拆掉新实例的渲染配置。
            if (model == owner) ClearModel();
        }

        private void OnPreCull()
        {
            if (model == null || !model.isActiveAndEnabled) return;
            int width = Mathf.Max(1, mainCamera.pixelWidth);
            int height = Mathf.Max(1, mainCamera.pixelHeight);
            int samples = mainCamera.allowMSAA ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;
            if (mainCamera.allowMSAA && mainCamera.targetTexture != null) samples = mainCamera.targetTexture.antiAliasing;
            EnsureTargets(width, height, samples);

            modelCamera.CopyFrom(mainCamera);
            modelCamera.enabled = false;
            modelCamera.transform.SetPositionAndRotation(mainCamera.transform.position, mainCamera.transform.rotation);
            modelCamera.targetTexture = hdr;
            modelCamera.rect = new Rect(0f, 0f, 1f, 1f);
            modelCamera.worldToCameraMatrix = mainCamera.worldToCameraMatrix;
            modelCamera.projectionMatrix = mainCamera.projectionMatrix;
            modelCamera.cullingMask = captureMask;
            modelCamera.clearFlags = CameraClearFlags.SolidColor;
            // 主场景的透明背景 RGB 非零；HDR 必须清成黑，避免透明背景本身产生 bloom。
            modelCamera.backgroundColor = Color.clear;
            modelCamera.allowHDR = true;
            modelCamera.allowMSAA = samples > 1;

            float previous = Shader.GetGlobalFloat(DeferViewTransform);
            var previousTarget = RenderTexture.active;
            try
            {
                Shader.SetGlobalFloat(DeferViewTransform, 1f);
                modelCamera.Render();
            }
            finally
            {
                Shader.SetGlobalFloat(DeferViewTransform, previous);
                RenderTexture.active = previousTarget;
            }
            try
            {
                var settings = MEBloomSettings.Default;
                settings.enabled = bloomEnabled;
                compose.Render(hdr, composed, settings, lut, 0, lut.height, ViewLo, ViewHi);
            }
            finally { RenderTexture.active = previousTarget; }
        }

        private void EnsureTargets(int width, int height, int samples)
        {
            if (hdr != null && hdr.IsCreated() && composed != null && composed.IsCreated() &&
                hdr.width == width && hdr.height == height && hdr.antiAliasing == samples) return;
            present.Clear();
            ReleaseTargets();
            hdr = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "PMX scene linear HDR",
                antiAliasing = samples,
                filterMode = FilterMode.Bilinear,
            };
            hdr.Create();
            composed = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "PMX bloom and Filmic",
                filterMode = FilterMode.Bilinear,
            };
            composed.Create();
            // 模型单独完成 Filmic，主相机后续绘制的菜单不再经过它；同时补回模型深度，
            // 让之后绘制的光环、拖尾等辅助物体仍被模型遮挡。
            compose.Present(present, composed, model.Renderers);
        }

        private void OnDisable() => ClearModel();

        private void ClearModel()
        {
            bool wasActive = active == this;
            if (wasActive) active = null;
            if (present != null)
            {
                if (mainCamera != null) mainCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, present);
                present.Release();
                present = null;
            }
            if (modelObjects != null)
            {
                for (int i = 0; i < modelObjects.Length; i++)
                    if (modelObjects[i] != null) modelObjects[i].layer = originalLayers[i];
                if (mainCamera != null)
                    mainCamera.cullingMask = (mainCamera.cullingMask & ~ModelMask) | originalModelMask;
            }
            modelObjects = null;
            originalLayers = null;
            model = null;
            lut = null;
            ReleaseTargets();
            if (modelCamera != null) ReleaseObject(modelCamera.gameObject);
            modelCamera = null;
            compose?.Dispose();
            compose = null;
            if (wasActive) RefreshSetting();
        }

        private void ReleaseTargets()
        {
            if (modelCamera != null) modelCamera.targetTexture = null;
            if (hdr != null) { hdr.Release(); ReleaseObject(hdr); }
            if (composed != null) { composed.Release(); ReleaseObject(composed); }
            hdr = composed = null;
        }

        private static void ReleaseObject(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
