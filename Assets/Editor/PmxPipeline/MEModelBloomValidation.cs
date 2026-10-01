using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace MateEngine.PmxPipeline
{
    // 使用实际 .me，比较主相机接入与离线合成；同时覆盖菜单隔离、截图尺寸和退出模型后的恢复。
    public static class MEModelBloomValidation
    {
        public static void Run()
        {
            int exitCode = 1;
            AssetBundle bundle = null;
            try
            {
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-bloomModel");
                Require(index >= 0 && index + 1 < args.Length, "缺少 -bloomModel <.me>");
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                bundle = AssetBundle.LoadFromFile(Path.GetFullPath(args[index + 1]));
                Require(bundle != null, "无法加载测试 bundle");
                var root = UnityEngine.Object.Instantiate(bundle.LoadAllAssets<GameObject>()[0]);
                var directions = root.GetComponent<MEModelDirections>();
                Require(directions != null, "模型缺少方向组件");
                directions.UpdateDirections();
                var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>();
                var helper = GameObject.CreatePrimitive(PrimitiveType.Cube);
                helper.transform.SetParent(root.transform, false);
                helper.SetActive(false);
                int originalLayer = renderer.gameObject.layer;
                var bounds = renderer.bounds;
                var cam = new GameObject("Bloom validation camera").AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.15f;
                cam.transform.position = bounds.center + Vector3.forward * 10f;
                cam.transform.LookAt(bounds.center);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.clear;
                cam.allowHDR = true;
                var output = Target(512, 512, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 4);
                cam.targetTexture = output;
                var effect = cam.gameObject.AddComponent<MEModelBloom>();
                Require(effect.SetModel(directions), "未识别转译模型");
                Require(helper.layer == 0, "角色辅助 Renderer 被误归入 Bloom");
                cam.Render();
                var actual = Read(output);
                Require(Shader.GetGlobalFloat("_MEDeferViewTransform") == 0f, "渲染后全局变换开关泄漏");

                // 先卸下运行时合入，再用同一相机/采样独立构造离线参考。
                effect.ClearModel(directions);
                var hdr = Target(512, 512, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, 4);
                cam.targetTexture = hdr;
                Shader.SetGlobalFloat("_MEDeferViewTransform", 1f);
                try { cam.Render(); }
                finally { Shader.SetGlobalFloat("_MEDeferViewTransform", 0f); }
                var lut = renderer.sharedMaterial.GetTexture("_MELut");
                var noBloom = MEBloomSettings.Default;
                noBloom.enabled = false;
                Color32[] expected, withoutBloom;
                // 参考端用同一套预乘输出公式独立合成，核对主相机接入本身没有改变数值。
                using (var reference = new MEPostCompose { PremultipliedOutput = true })
                {
                    reference.Render(hdr, output, MEBloomSettings.Default, lut, 0, lut.height,
                                     MEModelBloom.ViewLo, MEModelBloom.ViewHi);
                    expected = Read(output);
                    reference.Render(hdr, output, noBloom, lut, 0, lut.height, MEModelBloom.ViewLo, MEModelBloom.ViewHi);
                    withoutBloom = Read(output);
                }
                // 离线原公式：直接对预乘值做视图变换，仅用于下面的边缘对照。
                MEPostCompose.Compose(hdr, output, noBloom, lut, 0, lut.height, MEModelBloom.ViewLo, MEModelBloom.ViewHi);
                var straightWithoutBloom = Read(output);
                double error = Difference(actual, expected, out int alphaError, out int visible);
                Require(visible > 100, "主程序渲染为空");
                Require(error < 0.2 && alphaError <= 1, $"运行时与离线不同：误差 {error:F6}，alpha 最大误差 {alphaError}");
                double bloomChange = Difference(expected, withoutBloom, out int bloomAlphaError, out _);
                Require(bloomChange > 0.1 && bloomAlphaError == 0, "Bloom 未生效或改变了 alpha");

                // 旧路径：不接合成、由材质自己做视图变换，MSAA 在显示值上解析。关 Bloom 时
                // 部分覆盖的边缘像素应与之一致，不能比它更亮。
                cam.targetTexture = output;
                cam.Render();
                var legacy = Read(output);
                double edgePremultiplied = EdgeDifference(withoutBloom, legacy, out int edgePixels, out int edgeAlphaError);
                double edgeStraight = EdgeDifference(straightWithoutBloom, legacy, out _, out _);
                Debug.Log($"[BloomValidation] 边缘 {edgePixels} px 对旧路径的平均误差：预乘输出 {edgePremultiplied:F3}/255，" +
                          $"原公式 {edgeStraight:F3}/255；alpha 最大误差 {edgeAlphaError}");
                Require(edgePixels > 100 && edgeAlphaError <= 2 && edgePremultiplied < edgeStraight,
                        $"边缘未与旧路径对齐：预乘 {edgePremultiplied:F3}，原公式 {edgeStraight:F3}，alpha {edgeAlphaError}");
                var folder = Path.Combine(PmxPipelineOptions.ProjectRoot, "Build", "BloomValidation");
                Directory.CreateDirectory(folder);
                Write(actual, 512, 512, Path.Combine(folder, "runtime.png"));
                Write(withoutBloom, 512, 512, Path.Combine(folder, "without-bloom.png"));

                // 菜单必须在模型的 Filmic 之后绘制；用明确的半透明颜色检查叠加前后的 UI 像素。
                cam.targetTexture = output;
                var canvasObject = new GameObject("Camera space UI", typeof(RectTransform), typeof(Canvas));
                canvasObject.layer = 5;
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 1f;
                var uiObject = new GameObject("UI probe", typeof(RectTransform), typeof(Image));
                uiObject.layer = 5;
                uiObject.transform.SetParent(canvas.transform, false);
                var image = uiObject.GetComponent<Image>();
                image.color = new Color(0.2f, 0.7f, 0.4f, 0.6f);
                var rect = image.rectTransform;
                rect.anchorMin = new Vector2(0f, 0.7f);
                rect.anchorMax = new Vector2(0.15f, 0.95f);
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                Canvas.ForceUpdateCanvases();
                cam.Render();
                var beforeUI = Read(output)[420 * 512 + 32];
                Require(effect.SetModel(directions), "重新启用失败");
                cam.Render();
                var withUI = Read(output);
                var afterUI = withUI[420 * 512 + 32];
                Require(beforeUI.Equals(afterUI) && afterUI.a > 100,
                        $"菜单颜色/alpha 变化：{beforeUI} -> {afterUI}");
                Write(withUI, 512, 512, Path.Combine(folder, "with-ui.png"));

                canvasObject.SetActive(false);
                var legacyBloom = new GameObject("Legacy Bloom probe");
                MEModelBloom.ApplySetting(legacyBloom, false);
                cam.Render();
                double offError = Difference(Read(output), withoutBloom, out int offAlpha, out _);
                Require(offError < 0.2 && offAlpha == 0 && !legacyBloom.activeSelf,
                        "关闭 Bloom 没有保留正确的线性混合/Filmic");
                MEModelBloom.ApplySetting(legacyBloom, true);
                Require(!legacyBloom.activeSelf, "转译模型同时开启了旧 Bloom");

                var occluder = GameObject.CreatePrimitive(PrimitiveType.Quad);
                occluder.layer = LayerMask.NameToLayer("WindowOcclusion");
                occluder.transform.SetPositionAndRotation(bounds.center - cam.transform.forward +
                    cam.transform.right * cam.orthographicSize * 0.5f, cam.transform.rotation);
                occluder.transform.localScale = new Vector3(cam.orthographicSize, cam.orthographicSize * 2f, 1f);
                var occlusionMaterial = new Material(Shader.Find("Unlit/TransparencyShader"));
                occluder.GetComponent<Renderer>().sharedMaterial = occlusionMaterial;
                cam.Render();
                var hidden = Read(output);
                Difference(hidden, hidden, out _, out int occludedVisible);
                Require(occludedVisible > visible * 0.25f && occludedVisible < visible * 0.7f,
                        $"窗口遮挡失效：{visible} -> {occludedVisible}");
                Write(hidden, 512, 512, Path.Combine(folder, "window-occlusion.png"));
                occluder.SetActive(false);

                // 光环、拖尾、粒子等辅助物体由主相机在合成之后绘制，必须仍被模型挡住，在模型前方
                // 时照常可见。先在画面平面内转动左上臂：深度若取自绑定姿势，手臂一带会对不上。
                var animator = root.GetComponentInChildren<Animator>();
                var arm = animator != null ? animator.GetBoneTransform(HumanBodyBones.LeftUpperArm) : null;
                Require(arm != null, "找不到左上臂骨骼");
                var armRotation = arm.rotation;
                // 批处理的编辑模式没有逐帧蒙皮更新，两次手动 Render 之间改骨骼不会重新蒙皮。
                var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var smr in skinned) smr.forceMatrixRecalculationPerRender = true;
                arm.rotation = Quaternion.AngleAxis(50f, cam.transform.forward) * armRotation;
                cam.Render();
                var posed = Read(output);
                int poseChanged = 0;
                for (int i = 0; i < posed.Length; i++)
                    if ((posed[i].a == 0) != (actual[i].a == 0)) poseChanged++;
                Require(poseChanged > 200, $"姿势没有生效，轮廓只变化了 {poseChanged} px");
                var helperMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.green };
                var behind = GameObject.CreatePrimitive(PrimitiveType.Quad);
                behind.GetComponent<Renderer>().sharedMaterial = helperMaterial;
                behind.transform.SetPositionAndRotation(bounds.center + cam.transform.forward * (bounds.extents.z + 0.3f),
                                                        cam.transform.rotation);
                behind.transform.localScale = Vector3.one * cam.orthographicSize * 4f;
                var inFront = GameObject.CreatePrimitive(PrimitiveType.Quad);
                inFront.GetComponent<Renderer>().sharedMaterial = helperMaterial;
                inFront.transform.SetPositionAndRotation(bounds.center - cam.transform.forward * (bounds.extents.z + 0.3f),
                                                         cam.transform.rotation);
                inFront.transform.localScale = Vector3.one * cam.orthographicSize * 0.1f;
                cam.Render();
                var withHelpers = Read(output);
                Write(withHelpers, 512, 512, Path.Combine(folder, "helper-occlusion.png"));
                int opaquePixels = 0, helperLeaks = 0, emptyPixels = 0, helperShown = 0;
                for (int y = 0; y < 512; y++)
                for (int x = 0; x < 512; x++)
                {
                    // 画面中央约 26 px 见方由前方辅助物体覆盖，单独检查。
                    if (Math.Abs(x - 256) <= 16 && Math.Abs(y - 256) <= 16) continue;
                    int i = y * 512 + x;
                    if (posed[i].a == 255)
                    {
                        opaquePixels++;
                        if (!Same(withHelpers[i], posed[i])) helperLeaks++;
                    }
                    else if (posed[i].a == 0)
                    {
                        emptyPixels++;
                        if (withHelpers[i].g > 200 && withHelpers[i].r < 30 && withHelpers[i].a == 255) helperShown++;
                    }
                }
                var center = withHelpers[256 * 512 + 256];
                Debug.Log($"[BloomValidation] 辅助物体（姿势改变 {poseChanged} px）：不透明 {opaquePixels} px 中透出 {helperLeaks}，" +
                          $"空白 {emptyPixels} px 中显示 {helperShown}，前方中心像素 {center}");
                Require(opaquePixels > 1000 && helperLeaks <= opaquePixels / 1000 && helperShown >= emptyPixels * 0.99,
                        $"辅助物体遮挡错误：透出 {helperLeaks}/{opaquePixels}，空白处显示 {helperShown}/{emptyPixels}");
                Require(center.g > 200 && center.r < 30, $"模型前方的辅助物体被错误遮挡：{center}");
                UnityEngine.Object.DestroyImmediate(behind);
                UnityEngine.Object.DestroyImmediate(inFront);
                UnityEngine.Object.DestroyImmediate(helperMaterial);
                arm.rotation = armRotation;
                cam.Render();   // 先按还原后的姿势重新蒙皮，后续检查才与原 T 字姿势一致

                // 主场景把模型挂在绕 Y 转了 180° 的 Model 节点下。父节点与相机一起旋转后画面必须
                // 不变；日光若按 App 世界方向使用，相对角色会被转到背面，整体明显变暗。
                var appFrame = new GameObject("App model frame");
                var turn = Quaternion.Euler(0f, 180f, 0f);
                appFrame.transform.rotation = turn;
                root.transform.SetParent(appFrame.transform, false);
                var camPosition = cam.transform.position;
                var camRotation = cam.transform.rotation;
                cam.transform.SetPositionAndRotation(turn * camPosition, turn * camRotation);
                directions.UpdateDirections();
                cam.Render();
                var turned = Read(output);
                Write(turned, 512, 512, Path.Combine(folder, "app-frame.png"));
                double frameError = Difference(turned, actual, out int frameAlphaError, out _);
                Debug.Log($"[BloomValidation] 模型父节点旋转 180° 后与原画面的平均误差 {frameError:F3}/255，alpha 最大误差 {frameAlphaError}");
                Require(frameError < 0.5, $"模型父节点旋转后光照改变：误差 {frameError:F3}");
                root.transform.SetParent(null, false);
                cam.transform.SetPositionAndRotation(camPosition, camRotation);
                UnityEngine.Object.DestroyImmediate(appFrame);
                directions.UpdateDirections();
                cam.Render();
                foreach (var smr in skinned) smr.forceMatrixRecalculationPerRender = false;

                // 脸部朝向只随头部左右转动：低头 30° 后 FRONT 不变，左右转 30° 后跟着转 30°。
                var headBone = animator.GetBoneTransform(HumanBodyBones.Head);
                Require(headBone != null, "找不到头部骨骼");
                var block = new MaterialPropertyBlock();
                Vector3 HeadFront()
                {
                    directions.UpdateDirections();
                    renderer.GetPropertyBlock(block);
                    return block.GetVector("_MEHeadFront");
                }
                var headRotation = headBone.rotation;
                var restFront = HeadFront();
                Vector3 restRight = block.GetVector("_MEHeadRight");
                headBone.rotation = Quaternion.AngleAxis(30f, restRight) * headRotation;
                float pitchDrift = Vector3.Angle(restFront, HeadFront());
                headBone.rotation = Quaternion.AngleAxis(30f, root.transform.up) * headRotation;
                float yawTurn = Vector3.Angle(restFront, HeadFront());
                headBone.rotation = headRotation;
                directions.UpdateDirections();
                Debug.Log($"[BloomValidation] 脸部朝向：低头 30° 偏移 {pitchDrift:F3}°，左右转 30° 转动 {yawTurn:F3}°");
                Require(pitchDrift < 0.5f && Mathf.Abs(yawTurn - 30f) < 0.5f,
                        $"脸部朝向未去掉俯仰：低头偏移 {pitchDrift:F3}°，转头 {yawTurn:F3}°");

                double bloomMs = Measure(cam, output);
                MEModelBloom.ApplySetting(legacyBloom, false);
                double noBloomMs = Measure(cam, output);
                MEModelBloom.ApplySetting(legacyBloom, true);

                // 原截图路径会改 targetTexture，尺寸与窗口不同；新目标不能读取旧帧或旧尺寸。
                var resized = Target(320, 240, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 2);
                cam.targetTexture = resized;
                cam.orthographicSize *= 1.2f;
                cam.Render();
                var resizedImage = Read(resized);
                Difference(resizedImage, resizedImage, out _, out int resizedVisible);
                Require(resizedVisible > 100, "改变尺寸/投影后渲染为空");
                effect.ClearModel(directions);
                Require(renderer.gameObject.layer == originalLayer && cam.cullingMask == -1,
                        "切换模型未恢复 layer/culling mask");
                Require(cam.GetCommandBuffers(UnityEngine.Rendering.CameraEvent.BeforeForwardOpaque).Length == 0,
                        "切换模型后残留合入命令");
                MEModelBloom.ApplySetting(legacyBloom, true);
                Require(legacyBloom.activeSelf, "普通模型的旧 Bloom 开关未恢复");
                root.SetActive(false);
                cam.Render();
                var released = Read(resized);
                Require(released[120 * 320 + 160].a == 0, "移除模型后画面残留");

                File.WriteAllText(Path.Combine(folder, "result.json"),
                    JsonUtility.ToJson(new Result { meanError = error, maxAlphaError = alphaError,
                        visiblePixels = visible, bloomChange = bloomChange, bloomAlphaError = bloomAlphaError,
                        uiUnchanged = true, resizedVisiblePixels = resizedVisible, restored = true,
                        occludedVisiblePixels = occludedVisible, bloomMilliseconds = bloomMs, noBloomMilliseconds = noBloomMs,
                        edgePixels = edgePixels, edgeErrorPremultiplied = edgePremultiplied, edgeErrorStraight = edgeStraight,
                        helperOpaquePixels = opaquePixels, helperLeakPixels = helperLeaks,
                        helperEmptyPixels = emptyPixels, helperShownPixels = helperShown,
                        appFrameError = frameError, appFrameAlphaError = frameAlphaError,
                        headPitchDrift = pitchDrift, headYawTurn = yawTurn }, true));
                Debug.Log($"[BloomValidation] PASS: mean={error:F6}/255, alpha={alphaError}, " +
                          $"bloom change={bloomChange:F6}, UI/resize/model release verified");
                cam.targetTexture = null;
                foreach (var rt in new[] { output, hdr, resized }) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                ValidateMainScene(bundle, folder);
                exitCode = 0;
            }
            catch (Exception ex) { Debug.LogException(ex); }
            finally
            {
                Shader.SetGlobalFloat("_MEDeferViewTransform", 0f);
                RenderTexture.active = null;
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (bundle != null) bundle.Unload(true);
            }
            EditorApplication.Exit(exitCode);
        }

        [Serializable]
        private class Result
        {
            public double meanError, bloomChange, bloomMilliseconds, noBloomMilliseconds;
            public double edgeErrorPremultiplied, edgeErrorStraight, appFrameError;
            public int appFrameAlphaError;
            public float headPitchDrift, headYawTurn;
            public int maxAlphaError, bloomAlphaError, visiblePixels, resizedVisiblePixels, occludedVisiblePixels;
            public int edgePixels, helperOpaquePixels, helperLeakPixels, helperEmptyPixels, helperShownPixels;
            public bool uiUnchanged, restored;
        }

        private static RenderTexture Target(int w, int h, RenderTextureFormat format, RenderTextureReadWrite rw, int aa)
        {
            var rt = new RenderTexture(w, h, 24, format, rw) { antiAliasing = aa };
            rt.Create();
            return rt;
        }

        private static Color32[] Read(RenderTexture rt)
        {
            var previous = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex.GetPixels32();
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static double Measure(Camera camera, RenderTexture target)
        {
            var previous = RenderTexture.active;
            var pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    camera.Render();
                    RenderTexture.active = target;
                    // 同步一次 GPU，测完整渲染而非仅向驱动提交命令；结果包含此回读开销。
                    pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                }
                return watch.Elapsed.TotalMilliseconds / 20;
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(pixel); }
        }

        private static void ValidateMainScene(AssetBundle bundle, string folder)
        {
            EditorSceneManager.OpenScene("Assets/MATE ENGINE - Scenes/Mate Engine Main.unity");
            var loader = UnityEngine.Object.FindFirstObjectByType<VRMLoader>();
            loader.mainModel.SetActive(false);
            var root = UnityEngine.Object.Instantiate(bundle.LoadAllAssets<GameObject>()[0]);
            root.transform.SetParent(loader.customModelOutput.transform, false);
            var directions = root.GetComponent<MEModelDirections>();
            directions.UpdateDirections();
            var camera = Camera.main;
            // 走 LateUpdate 的同一接入路径，而不是手动挂组件。
            Require(directions.BindBloom(), "正式主场景模型自动接入失败");
            Require(camera.TryGetComponent(out MEModelBloom effect) && effect.IsBoundTo(directions), "自动接入未绑定主相机");
            camera.gameObject.AddComponent<RenderStyleImageEffect>();
            foreach (var handler in UnityEngine.Object.FindObjectsByType<SettingsHandlerToggles>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                MEModelBloom.ApplySetting(handler.bloomObject, true);
            var target = Target(1024, 1024, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 4);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                var model = Read(target);
                Difference(model, model, out _, out int visible);
                Require(visible > 100 && visible < model.Length * 0.9f, "正式主场景模型为空或透明背景被填满");
                Write(model, 1024, 1024, Path.Combine(folder, "main-scene.png"));
                var menu = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Single(t => t.name == "SettingsMenuCanvas");
                menu.gameObject.SetActive(true);
                Canvas.ForceUpdateCanvases();
                camera.Render();
                Write(Read(target), 1024, 1024, Path.Combine(folder, "main-scene-menu.png"));
                Debug.Log($"[BloomValidation] Main scene and camera-space menu rendered; visible={visible}");
            }
            finally
            {
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static double Difference(Color32[] a, Color32[] b, out int alpha, out int visible)
        {
            long sum = 0;
            int compared = 0;
            alpha = visible = 0;
            for (int i = 0; i < a.Length; i++)
            {
                alpha = Math.Max(alpha, Math.Abs(a[i].a - b[i].a));
                if (a[i].a > 0) visible++;
                // Player 清零完全透明处的 RGB，避免不可见辉光被随后绘制的 UI 混入。
                if (a[i].a == 0 && b[i].a == 0) continue;
                compared++;
                sum += Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b);
            }
            return compared == 0 ? 0 : (double)sum / (compared * 3);
        }

        // 只统计参照图中部分覆盖（0 < alpha < 255）的像素。
        private static double EdgeDifference(Color32[] a, Color32[] reference, out int pixels, out int alpha)
        {
            long sum = 0;
            pixels = alpha = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (reference[i].a == 0 || reference[i].a == 255) continue;
                pixels++;
                alpha = Math.Max(alpha, Math.Abs(a[i].a - reference[i].a));
                sum += Math.Abs(a[i].r - reference[i].r) + Math.Abs(a[i].g - reference[i].g) + Math.Abs(a[i].b - reference[i].b);
            }
            return pixels == 0 ? 0 : (double)sum / (pixels * 3);
        }

        private static bool Same(Color32 a, Color32 b) =>
            Math.Abs(a.r - b.r) <= 1 && Math.Abs(a.g - b.g) <= 1 && Math.Abs(a.b - b.b) <= 1 && Math.Abs(a.a - b.a) <= 1;

        private static void Write(Color32[] pixels, int w, int h, string path)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
