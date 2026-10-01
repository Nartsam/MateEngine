// 驱动 Hidden/MateEngine/PostCompose：搭 bloom 金字塔，然后合成 + 视图变换。
//
// 结构照 EEVEE 的 effect_bloom.c：全分辨率取亮部 → 逐级减半降采样 → 自小往大用
// tent 核上采样并**累加** → 末端一次性加到原图上再过视图变换。层数由 Blender 的
// bloom_radius 与分辨率一起决定，公式同 EEVEE：iter = (radius - 8) + log2(minDim)。
//
// 增益为什么要额外标定：EEVEE 的 intensity 不是「辉光能量占比」，多级累加本身会把
// 能量放大数倍。实测（Tools/PmxPipeline/probe_bloom_kernel.py，1024² 下 16px 方块、
// 发光强度 10）：Blender 的辉光能量 / 过阈值后的源能量 = 0.5289，而 intensity 只有
// 0.08 —— 放大约 6.6 倍，与 8 级累加的量级吻合。所以这里留一个可标定的 Gain。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MateEngine.PmxPipeline
{
    [Serializable]
    public struct MEBloomSettings
    {
        public bool enabled;
        public float intensity;
        public Color color;        // 线性值（插件用 srgb_to_linearrgb 转过）
        public float threshold;
        public float knee;
        public float radius;
        public float clamp;

        public static MEBloomSettings Default => new()
        {
            enabled = true,
            intensity = 0.08f,
            color = new Color(1f, 0.7605f, 0.6939f, 1f),   // #FFE4D9 转线性
            threshold = 0.8f,
            knee = 0.5f,
            radius = 6.5f,
            clamp = 0f,
        };
    }

    public sealed class MEPostCompose : IDisposable
    {
        public const string ShaderName = "Hidden/MateEngine/PostCompose";
        private const int MaxIterations = 16;

        // 多级累加带来的放大倍数。由 probe_bloom_kernel.py 实测的能量占比标定：
        // Blender 侧 辉光能量/过阈值源能量 = 0.5289 而 intensity = 0.08，即金字塔自身
        // 放大 6.61 倍；本实现的放大倍数由 MeasureGain 实测（见下），两者相除即此值。
        // 改动它之前先重跑那个探针，别凭手感调。
        // 本实现实测放大 8.9035 倍（1024²、radius 6.5 下 8 级），6.61 / 8.9035 = 0.7424
        public static float AccumulationGain = 0.7424f;

        /// <summary>Blender 实测：辉光能量 / 过阈值后的源能量 ÷ intensity。</summary>
        public const float BlenderPyramidGain = 6.61f;

        /// <summary>打开后会回读金字塔首末两端的能量并算出本实现的放大倍数（慢，仅诊断用）。</summary>
        public static bool MeasureGain = false;

        private static readonly int SrcTex = Shader.PropertyToID("_MainTex");
        private static readonly int BloomTex = Shader.PropertyToID("_BloomTex");
        private static readonly int BloomColor = Shader.PropertyToID("_BloomColor");
        private static readonly int BloomClamp = Shader.PropertyToID("_BloomClamp");
        private static readonly int CurveThreshold = Shader.PropertyToID("_CurveThreshold");
        private static readonly int SampleScale = Shader.PropertyToID("_SampleScale");
        private static readonly int LutTex = Shader.PropertyToID("_MELut");
        private static readonly int ViewTransform = Shader.PropertyToID("_ViewTransform");
        private static readonly int PremultipliedFlag = Shader.PropertyToID("_MEPremultipliedOutput");
        private static readonly int ModelMask = Shader.PropertyToID("_MEModelMask");
        private static readonly int DepthCull = Shader.PropertyToID("_MEDepthCull");
        private static readonly int MaterialCull = Shader.PropertyToID("_Cull");

        private readonly Material material;
        private readonly Dictionary<int, Material> depthMaterials = new();
        private readonly RenderTexture[] pyramid = new RenderTexture[MaxIterations];

        /// <summary>
        /// 输出给按预乘 alpha 合成的桌面窗口：视图变换前先还原未预乘颜色，再乘回覆盖率。
        /// 离线差分保持 false，沿用与 Blender 基准对照过的原公式。
        /// </summary>
        public bool PremultipliedOutput { get; set; }

        public MEPostCompose()
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException($"找不到可用着色器 {ShaderName}");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public void Dispose()
        {
            ReleaseObject(material);
            foreach (var depth in depthMaterials.Values) ReleaseObject(depth);
            depthMaterials.Clear();
        }

        /// <summary>
        /// 录制写回主相机的命令：先按合成结果的覆盖率给模型补深度，再写入合成颜色。
        /// 合成图本身不带深度，之后由主相机绘制的光环、拖尾、粒子等要靠这份深度被模型遮挡。
        /// </summary>
        public void Present(CommandBuffer commands, RenderTexture source, Renderer[] depthRenderers)
        {
            // 放在颜色 Blit 之前：此时主相机的视图/投影矩阵仍然有效，DrawRenderer 直接可用。
            commands.SetGlobalTexture(ModelMask, source);
            foreach (var renderer in depthRenderers)
            {
                if (renderer == null) continue;
                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
                else if (renderer.TryGetComponent(out MeshFilter filter)) mesh = filter.sharedMesh;
                if (mesh == null || mesh.subMeshCount == 0) continue;
                var slots = renderer.sharedMaterials;
                for (int i = 0; i < slots.Length; i++)
                {
                    // 剔除方式随原材质：反向描边外壳和单面布料不能在深度里双面生效。
                    int cull = slots[i] != null && slots[i].HasProperty(MaterialCull)
                        ? Mathf.RoundToInt(slots[i].GetFloat(MaterialCull)) : (int)CullMode.Back;
                    commands.DrawRenderer(renderer, DepthMaterial(cull), Mathf.Min(i, mesh.subMeshCount - 1), 5);
                }
            }
            commands.Blit(source, BuiltinRenderTextureType.CameraTarget, material, 4);
        }

        private Material DepthMaterial(int cull)
        {
            if (depthMaterials.TryGetValue(cull, out var depth)) return depth;
            depth = new Material(material.shader) { hideFlags = HideFlags.HideAndDontSave };
            depth.SetFloat(DepthCull, cull);
            depthMaterials.Add(cull, depth);
            return depth;
        }

        private static void ReleaseObject(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        /// <summary>
        /// 把线性 HDR 缓冲合成到目标（通常是 sRGB 读写的 ARGB32）。
        /// 离线调用保留一次性入口；Player 持有实例复用材质，避免每帧创建/销毁 Unity 对象。
        /// </summary>
        public static void Compose(RenderTexture source, RenderTexture dest,
                                   MEBloomSettings bloom, Texture lut,
                                   int lutRow, int lutRows, float lo, float hi,
                                   Action<string> log = null)
        {
            using var compose = new MEPostCompose();
            compose.Render(source, dest, bloom, lut, lutRow, lutRows, lo, hi, log);
        }

        public void Render(RenderTexture source, RenderTexture dest,
                           MEBloomSettings bloom, Texture lut,
                           int lutRow, int lutRows, float lo, float hi,
                           Action<string> log = null)
        {
            if (lut == null) throw new InvalidOperationException("Compose 需要视图变换 LUT");
            var mat = material;
            var previousTarget = RenderTexture.active;
            RenderTexture blit = null;

            try
            {
                Texture bloomTex = Texture2D.blackTexture;
                int levels = 0;
                if (bloom.enabled && bloom.intensity > 0f)
                {
                    levels = BuildPyramid(source, mat, bloom, pyramid, ref blit, log);
                    bloomTex = blit;
                }

                float gain = AccumulationGain;
                mat.SetTexture(BloomTex, bloomTex);
                mat.SetVector(BloomColor, new Vector4(
                    bloom.color.r * bloom.intensity * gain,
                    bloom.color.g * bloom.intensity * gain,
                    bloom.color.b * bloom.intensity * gain, 1f));
                mat.SetFloat(BloomClamp, bloom.clamp);

                mat.SetTexture(LutTex, lut);
                mat.SetVector(ViewTransform, new Vector4(lutRow, lutRows, lo, hi));
                mat.SetFloat(PremultipliedFlag, PremultipliedOutput ? 1f : 0f);

                log?.Invoke($"合成：bloom={(bloom.enabled ? $"开（{levels} 级，强度 {bloom.intensity:F3}）" : "关")}" +
                            $" 视图变换行={lutRow}/{lutRows}");
                Graphics.Blit(source, dest, mat, 3);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                if (blit != null) RenderTexture.ReleaseTemporary(blit);
                for (int i = 0; i < pyramid.Length; i++)
                {
                    if (pyramid[i] != null) RenderTexture.ReleaseTemporary(pyramid[i]);
                    pyramid[i] = null;
                }
            }
        }

        private static int BuildPyramid(RenderTexture source, Material mat,
                                        MEBloomSettings b, RenderTexture[] pyramid,
                                        ref RenderTexture blit, Action<string> log)
        {
            int w = source.width, h = source.height;
            float knee = Mathf.Max(b.knee * b.threshold, 1e-5f);
            mat.SetVector(CurveThreshold, new Vector4(
                b.threshold - knee, knee * 2f, 0.25f / knee, b.threshold));

            // 与 EEVEE 同一个公式。radius 越大，参与的层级越多、尾巴越长。
            float minDim = Mathf.Min(w, h);
            float maxIter = (b.radius - 8f) + Mathf.Log(minDim, 2f);
            int levels = Mathf.Clamp(Mathf.FloorToInt(maxIter), 1, MaxIterations);
            mat.SetFloat(SampleScale, 0.5f + maxIter - Mathf.Floor(maxIter));

            var fmt = RenderTextureFormat.ARGBHalf;
            blit = RenderTexture.GetTemporary(w, h, 0, fmt, RenderTextureReadWrite.Linear);
            blit.filterMode = FilterMode.Bilinear;
            Graphics.Blit(source, blit, mat, 0);          // 取亮部

            float energyIn = MeasureGain ? TotalEnergy(blit) : 0f;

            var src = blit;
            for (int i = 0; i < levels; i++)
            {
                w = Mathf.Max(1, w / 2);
                h = Mathf.Max(1, h / 2);
                pyramid[i] = RenderTexture.GetTemporary(w, h, 0, fmt, RenderTextureReadWrite.Linear);
                pyramid[i].filterMode = FilterMode.Bilinear;
                Graphics.Blit(src, pyramid[i], mat, 1);
                src = pyramid[i];
            }

            // 自小往大 tent 上采样并累加（pass 2 是 Blend One One）
            for (int i = levels - 2; i >= 0; i--)
                Graphics.Blit(pyramid[i + 1], pyramid[i], mat, 2);
            Graphics.Blit(pyramid[0], blit, mat, 2);      // 最后一级叠回全分辨率

            if (MeasureGain)
            {
                float g = TotalEnergy(blit) / Mathf.Max(energyIn, 1e-9f);
                log?.Invoke($"金字塔实测放大倍数 = {g:F4}（{levels} 级，sampleScale=" +
                            $"{0.5f + maxIter - Mathf.Floor(maxIter):F3}）；Blender 实测 " +
                            $"{BlenderPyramidGain:F2} → AccumulationGain 应取 " +
                            $"{BlenderPyramidGain / Mathf.Max(g, 1e-6f):F4}，当前 {AccumulationGain:F4}");
            }
            return levels;
        }

        // 回读整张图的 RGB 总能量。只在诊断时用——1024² 的回读会同步等 GPU。
        private static float TotalEnergy(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                double sum = 0;
                var px = tex.GetPixels();
                foreach (var c in px) sum += c.r + c.g + c.b;
                return (float)(sum / 3.0);
            }
            finally
            {
                RenderTexture.active = prev;
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>从 Blender 报告的 scene_effect 段取 bloom 参数。</summary>
        public static MEBloomSettings FromReport(Func<string, float?> getFloat,
                                                 Func<string, bool?> getBool,
                                                 Func<string, Color?> getColor)
        {
            var b = MEBloomSettings.Default;
            b.enabled = getBool("use_bloom") ?? b.enabled;
            b.intensity = getFloat("bloom_intensity") ?? b.intensity;
            b.threshold = getFloat("bloom_threshold") ?? b.threshold;
            b.knee = getFloat("bloom_knee") ?? b.knee;
            b.radius = getFloat("bloom_radius") ?? b.radius;
            b.clamp = getFloat("bloom_clamp") ?? b.clamp;
            b.color = getColor("bloom_color") ?? b.color;
            return b;
        }
    }
}
