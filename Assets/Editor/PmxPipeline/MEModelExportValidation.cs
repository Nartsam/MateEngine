using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    // 在发布 .me 前重载实际 bundle，核对序列化契约并用其自带 shader 出图。
    public static class MEModelExportValidation
    {
        public static void Validate(string bundlePath, GameObject source)
        {
            // 换场景会卸载未使用资源，必须先换场景再加载 bundle，避免它的 MonoScript
            // 尚未实例化就被场景清理卸载，导致测试自身制造脚本字段丢失。
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            Require(bundle != null, "无法重新加载导出的 AssetBundle");
            GameObject instance = null;
            try
            {
                var prefab = bundle.LoadAllAssets<GameObject>().Single(p => p.GetComponent<Animator>() != null);
                var expected = source.GetComponentInChildren<SkinnedMeshRenderer>(true);
                var actual = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Require(actual != null, "bundle 缺少蒙皮网格");
                Require(actual.sharedMesh.vertexCount == expected.sharedMesh.vertexCount &&
                        actual.sharedMesh.subMeshCount == expected.sharedMesh.subMeshCount &&
                        actual.sharedMesh.blendShapeCount == expected.sharedMesh.blendShapeCount,
                        "网格/描边/形态键未完整序列化");
                Require(actual.sharedMaterials.Length == expected.sharedMaterials.Length, "材质槽数量变化");
                for (int i = 0; i < actual.sharedMaterials.Length; i++)
                {
                    var a = actual.sharedMaterials[i];
                    var e = expected.sharedMaterials[i];
                    Require(a != null && a.shader != null && a.shader.isSupported &&
                            !ShaderUtil.ShaderHasError(a.shader), $"bundle 着色器不可用：槽位 {i}");
                    Require(a.shader.name == e.shader.name && a.renderQueue == e.renderQueue,
                            $"着色器或绘制队列变化：{e.name}");
                    foreach (var key in new[] { "_MESunDir", "_MEHeadFront", "_MEHeadRight", "_MEGenMin", "_MEGenInvSize" })
                        Require(a.GetVector(key) == e.GetVector(key), $"材质方向或包围盒未保存：{key}");
                    foreach (string key in e.GetTexturePropertyNames())
                    {
                        var et = e.GetTexture(key) as Texture2D;
                        var at = a.GetTexture(key) as Texture2D;
                        if (et == null) continue;
                        Require(at != null && at.width == et.width && at.height == et.height &&
                                at.format == et.format && at.isDataSRGB == et.isDataSRGB &&
                                at.mipmapCount == et.mipmapCount && at.filterMode == et.filterMode &&
                                at.wrapMode == et.wrapMode, $"贴图格式/色彩空间变化：{e.name}/{key}");
                        Require(at.GetPixel(at.width / 2, at.height / 2) == et.GetPixel(et.width / 2, et.height / 2),
                                $"贴图采样变化：{e.name}/{key}");
                    }
                }

                var reference = Render(source);
                var loaded = Render(prefab);
                try
                {
                    var ep = reference.GetPixels32();
                    var ap = loaded.GetPixels32();
                    long difference = 0;
                    int visible = 0;
                    for (int i = 0; i < ep.Length; i++)
                    {
                        if (ap[i].a > 0) visible++;
                        difference += Math.Abs(ap[i].r - ep[i].r) + Math.Abs(ap[i].g - ep[i].g) +
                                      Math.Abs(ap[i].b - ep[i].b) + Math.Abs(ap[i].a - ep[i].a);
                    }
                    double mean = (double)difference / (ep.Length * 4);
                    Require(visible > 100, "bundle 渲染为空");
                    Require(mean < 0.1, $"bundle 与资产渲染不一致：平均通道误差 {mean:F4}/255");
                    var args = Environment.GetCommandLineArgs();
                    int logIndex = Array.IndexOf(args, "-logFile");
                    if (logIndex >= 0 && logIndex + 1 < args.Length)
                        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(args[logIndex + 1]), "export-preview.png"), loaded.EncodeToPNG());
                    Debug.Log($"[MEPmxPipeline] Bundle roundtrip: mesh/material/texture verified, visible={visible}, pixel error={mean:F6}/255");
                }
                finally { UnityEngine.Object.DestroyImmediate(reference); UnityEngine.Object.DestroyImmediate(loaded); }

                instance = UnityEngine.Object.Instantiate(prefab);
                var directions = instance.GetComponent<MEModelDirections>();
                Require(directions != null, "方向组件未进入 bundle");
                var animator = instance.GetComponent<Animator>();
                Require(animator.isHuman && animator.avatar.isValid, "Humanoid Avatar 未进入 bundle");
                // 编辑态实例尚未经过 Player 的 Animator 初始化；先绑定，GetBoneTransform
                // 才能代表这个实例的骨骼，而非缓存的 Avatar 绑定。
                animator.Rebind();
                animator.Update(0f);
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                var renderer = instance.GetComponentInChildren<SkinnedMeshRenderer>();
                var serialized = new SerializedObject(directions);
                var boundHead = serialized.FindProperty("head").objectReferenceValue as Transform;
                Require(boundHead != null && boundHead.IsChildOf(instance.transform),
                        "方向组件的头骨引用没有绑定到导出模型实例");
                Require(boundHead == head,
                        $"方向组件与 Humanoid 头骨不一致：{boundHead.name}({boundHead.GetInstanceID()}) / {head.name}({head.GetInstanceID()})");
                Require(serialized.FindProperty("renderers").GetArrayElementAtIndex(0).objectReferenceValue == renderer,
                        "方向组件的渲染器引用没有绑定到导出模型实例");
                var properties = new MaterialPropertyBlock();
                directions.UpdateDirections();
                renderer.GetPropertyBlock(properties);
                Vector4 before = properties.GetVector("_MEHeadFront");
                Vector4 materialBefore = renderer.sharedMaterial.GetVector("_MEHeadFront");
                head.rotation = Quaternion.Euler(0, 35, 0) * head.rotation;
                directions.UpdateDirections();
                renderer.GetPropertyBlock(properties);
                Require(Vector4.Distance(before, properties.GetVector("_MEHeadFront")) > 0.1f,
                        $"转头后脸部方向未更新：{before} -> {properties.GetVector("_MEHeadFront")}，头骨 {head.name}");
                Require(renderer.sharedMaterial.GetVector("_MEHeadFront") == materialBefore,
                        "运行时方向修改了共享材质");
                Debug.Log("[MEPmxPipeline] Head direction update and shared material isolation verified");
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                bundle.Unload(true);
            }
        }

        private static Texture2D Render(GameObject prefab)
        {
            var root = UnityEngine.Object.Instantiate(prefab);
            var cameraObject = new GameObject("ExportValidationCamera");
            var camera = cameraObject.AddComponent<Camera>();
            var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var bounds = root.GetComponentInChildren<SkinnedMeshRenderer>().bounds;
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(bounds.extents.y, bounds.extents.x) * 1.08f;
                camera.transform.position = bounds.center + Vector3.forward * 10f;
                camera.transform.LookAt(bounds.center);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.allowHDR = false;
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                var image = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                image.Apply();
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
