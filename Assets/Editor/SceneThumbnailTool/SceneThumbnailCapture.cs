using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.SceneThumbnails
{
    /// <summary>
    /// What the capture camera clears to before rendering the scene.
    /// </summary>
    public enum BackgroundMode
    {
        Skybox,
        SolidColor,
    }

    /// <summary>
    /// Settings for a single scene thumbnail capture.
    /// </summary>
    public struct CaptureSettings
    {
        public int Width;
        public int Height;
        public Color BackgroundColor;
        public bool UseSceneViewAngle;

        /// <summary>
        /// True captures with an orthographic (2D) projection. In SceneView-angle
        /// mode the SceneView camera's orthographicSize is copied when the
        /// SceneView camera is orthographic; otherwise (and in orbit mode) the
        /// orthographicSize is derived from the fitted bounds and
        /// orbitDistanceMultiplier.
        /// </summary>
        public bool orthographic;
        public float OrbitYaw;
        public float OrbitPitch;
        public float FitFactor;

        /// <summary>
        /// Multiplier of the auto-fit orbit framing distance. Only used when
        /// UseSceneViewAngle is false (orbit mode); clamped to 0.1..10 on capture.
        /// </summary>
        public float orbitDistanceMultiplier;
        public bool UseLightingOverride;
        public bool UseHdr;
        public LayerMask layerMask;
        public BackgroundMode BackgroundMode;
        public bool WantPostProcessing;
    }

    /// <summary>
    /// Result of a scene thumbnail capture.
    /// </summary>
    public struct CaptureResult
    {
        public bool Success;
        public byte[] PngBytes;
        public string Warning;
    }

    /// <summary>
    /// Reusable pure C# API that renders the active scene to a PNG thumbnail.
    /// Never renders through SceneView.camera: a dedicated temp GameObject + Camera is used.
    /// </summary>
    public static class SceneThumbnailCapture
    {
        #region Constants

        private const string k_LogPrefix = "[SceneThumbnailTool] ";
        private const string k_TempCameraName = "__SceneThumbnailCaptureCamera";
        private const string k_TempRenderTextureName = "__SceneThumbnailCaptureRT";
        private const int k_MinResolution = 16;
        private const int k_MaxResolution = 4096;
        private const int k_MaxReliableRenderPixels = 16000000;
        private const float k_DefaultOrbitFov = 60f;
        private const float k_DefaultFitFactor = 2f;
        private const float k_MaxOrbitPitch = 89f;
        private const float k_MinFarClip = 1000f;

        #endregion

        #region Fields

        private static readonly byte[] s_LinearToSrgbLut = BuildLinearToSrgbLut();
        private static bool s_ConversionLogged;

        #endregion

        #region Public Methods

        public static CaptureSettings CreateDefaultSettings()
        {
            return new CaptureSettings
            {
                Width = 512,
                Height = 512,
                BackgroundColor = new Color(0.15f, 0.18f, 0.22f, 1f),
                UseSceneViewAngle = true,
                orthographic = false,
                OrbitYaw = 45f,
                OrbitPitch = 25f,
                FitFactor = k_DefaultFitFactor,
                orbitDistanceMultiplier = 1f,
                UseLightingOverride = false,
                UseHdr = true,
                layerMask = -1,
                BackgroundMode = BackgroundMode.Skybox,
                WantPostProcessing = true,
            };
        }

        public static CaptureResult Capture(CaptureSettings settings)
        {
            s_ConversionLogged = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new CaptureResult
                {
                    Success = false,
                    Warning = "Capture refused: play mode is active or about to change.",
                };
            }

            int width = Mathf.Clamp(settings.Width, k_MinResolution, k_MaxResolution);
            int height = Mathf.Clamp(settings.Height, k_MinResolution, k_MaxResolution);
            return CaptureCore(width, height, settings);
        }

        #endregion

        #region Private Methods

        private static CaptureResult CaptureCore(int width, int height, CaptureSettings settings)
        {
            var result = new CaptureResult { Success = false };

            List<LightSnapshot> lightSnapshots = null;
            RenderTexture rt = null;
            GameObject tempGo = null;
            Camera cam = null;

            try
            {
                tempGo = new GameObject(k_TempCameraName);
                tempGo.hideFlags = HideFlags.HideAndDontSave;
                cam = tempGo.AddComponent<Camera>();

                // The ortho fit formula reads camera.aspect before targetTexture
                // is assigned below; targetTexture re-derives the aspect at render
                // time, so this early set only feeds the fit calculation.
                cam.aspect = width / (float)height;

                if (settings.UseSceneViewAngle)
                {
                    if (!TryCopyFromSceneView(cam, settings))
                    {
                        ApplyOrbitTransform(cam, settings);
                    }
                }
                else
                {
                    ApplyOrbitTransform(cam, settings);
                }

                rt = CreateRenderTexture(
                    width,
                    height,
                    settings.UseHdr || settings.WantPostProcessing,
                    settings.BackgroundColor
                );
                cam.targetTexture = rt;
                cam.clearFlags =
                    settings.BackgroundMode == BackgroundMode.Skybox
                        ? CameraClearFlags.Skybox
                        : CameraClearFlags.SolidColor;
                cam.backgroundColor = settings.BackgroundColor;
                cam.cullingMask = settings.layerMask;

                if (settings.UseLightingOverride)
                {
                    lightSnapshots = DisableAllLights();
                }

                if (settings.WantPostProcessing)
                {
                    // URP skips Volume post-processing in offscreen renders unless the
                    // camera carries UniversalAdditionalCameraData with
                    // renderPostProcessing=true. UACD is added whenever PP is
                    // requested: SubmitRenderRequest needs it too (M9).
                    TryEnablePostProcessing(cam);
                }

                bool ppOrSkyboxActive =
                    settings.WantPostProcessing || settings.BackgroundMode == BackgroundMode.Skybox;

                bool usedSubmitFallback = false;
                Texture2D pixels;
                ImageCheck check;

                if (settings.WantPostProcessing)
                {
                    // Synchronous first: camera.Render() with
                    // UACD.renderPostProcessing=true finishes the render on the
                    // main thread before ReadPixels, so the readback cannot race
                    // a render-thread SubmitRenderRequest (cross-scene mixing).
                    // SubmitRenderRequest (full camera stack, async) is only a
                    // retry when the synchronous render looks suspicious.
                    cam.Render();
                    pixels = ReadBack(rt);
                    check = Inspect(pixels, settings.BackgroundColor, true);
                    if (check == ImageCheck.UniformOther)
                    {
                        Debug.LogWarningFormat(
                            k_LogPrefix
                                + "camera.Render() produced {0}; attempting SubmitRenderRequest fallback.",
                            Describe(check)
                        );
                        if (TrySubmitRenderRequest(cam, rt))
                        {
                            Texture2D retry = ReadBack(rt);
                            ImageCheck retryCheck = Inspect(retry, settings.BackgroundColor, true);
                            if (retryCheck == ImageCheck.Ok)
                            {
                                UnityEngine.Object.DestroyImmediate(pixels);
                                pixels = retry;
                                check = ImageCheck.Ok;
                                usedSubmitFallback = true;
                            }
                            else
                            {
                                UnityEngine.Object.DestroyImmediate(retry);
                                check = retryCheck;
                            }
                        }
                    }
                }
                else
                {
                    cam.Render();
                    pixels = ReadBack(rt);
                    check = Inspect(pixels, settings.BackgroundColor, ppOrSkyboxActive);

                    if (check == ImageCheck.UniformOther)
                    {
                        Debug.LogWarningFormat(
                            k_LogPrefix
                                + "camera.Render() produced {0}; attempting SubmitRenderRequest fallback.",
                            Describe(check)
                        );
                        if (TrySubmitRenderRequest(cam, rt))
                        {
                            Texture2D retry = ReadBack(rt);
                            ImageCheck retryCheck = Inspect(
                                retry,
                                settings.BackgroundColor,
                                ppOrSkyboxActive
                            );
                            if (retryCheck == ImageCheck.Ok)
                            {
                                UnityEngine.Object.DestroyImmediate(pixels);
                                pixels = retry;
                                check = ImageCheck.Ok;
                                usedSubmitFallback = true;
                            }
                            else
                            {
                                UnityEngine.Object.DestroyImmediate(retry);
                                check = retryCheck;
                            }
                        }
                    }
                }

                if (check != ImageCheck.Ok && ExceedsReliablePixels(width, height))
                {
                    // URP 17 RenderGraph (Unity 6) silently drops all geometry when rendering
                    // into a RenderTexture with ~2^24 total pixels (4096x4096 exactly). Retry
                    // at a safe size and upscale the result to the requested resolution.
                    int smallW;
                    int smallH;
                    Texture2D upscaled = TryDownscaleRetry(
                        width,
                        height,
                        settings,
                        out smallW,
                        out smallH
                    );
                    if (upscaled != null)
                    {
                        UnityEngine.Object.DestroyImmediate(pixels);
                        result.Success = true;
                        result.PngBytes = upscaled.EncodeToPNG();
                        result.Warning =
                            "Rendered at "
                            + smallW
                            + "x"
                            + smallH
                            + " and upscaled to "
                            + width
                            + "x"
                            + height
                            + ": URP RenderGraph drops geometry in RenderTextures above ~16M total pixels.";
                        UnityEngine.Object.DestroyImmediate(upscaled);
                        return result;
                    }
                }

                if (check == ImageCheck.Ok)
                {
                    result.Success = true;
                    result.PngBytes = pixels.EncodeToPNG();
                    if (usedSubmitFallback)
                    {
                        result.Warning =
                            "Capture succeeded via SubmitRenderRequest fallback after camera.Render() produced a suspicious image.";
                    }
                    UnityEngine.Object.DestroyImmediate(pixels);
                }
                else
                {
                    result.Warning =
                        "Render produced "
                        + Describe(check)
                        + "; returning background placeholder instead of a corrupt PNG.";
                    result.PngBytes = CreatePlaceholderPng(width, height, settings.BackgroundColor);
                    UnityEngine.Object.DestroyImmediate(pixels);
                }
            }
            finally
            {
                RestoreLights(lightSnapshots);
                if (cam != null)
                {
                    cam.targetTexture = null;
                }
                if (tempGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempGo);
                }
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }

            return result;
        }

        private static Texture2D TryDownscaleRetry(
            int width,
            int height,
            CaptureSettings settings,
            out int smallW,
            out int smallH
        )
        {
            float scale = Mathf.Sqrt(k_MaxReliableRenderPixels / (float)(width * height));
            smallW = Mathf.Clamp(Mathf.RoundToInt(width * scale), k_MinResolution, width);
            smallH = Mathf.Clamp(Mathf.RoundToInt(height * scale), k_MinResolution, height);
            if (smallW >= width && smallH >= height)
            {
                return null;
            }

            CaptureResult small = CaptureCore(smallW, smallH, settings);
            if (!small.Success)
            {
                return null;
            }
            return Upscale(small.PngBytes, width, height);
        }

        private static Texture2D Upscale(byte[] png, int width, int height)
        {
            var smallTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            smallTex.filterMode = FilterMode.Bilinear;
            if (!smallTex.LoadImage(png))
            {
                UnityEngine.Object.DestroyImmediate(smallTex);
                return null;
            }

            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            Graphics.Blit(smallTex, rt);
            var bigTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            bigTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            bigTex.Apply();
            RenderTexture.active = previousActive;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(smallTex);
            return bigTex;
        }

        private static bool TryCopyFromSceneView(Camera cam, CaptureSettings settings)
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                return false;
            }
            cam.transform.SetPositionAndRotation(
                sv.camera.transform.position,
                sv.camera.transform.rotation
            );
            if (settings.orthographic)
            {
                // Ortho + SceneView angle: copy the SceneView camera's
                // orthographicSize when it is orthographic; a perspective
                // SceneView camera falls back to the orbit fit-size framing
                // (ApplyOrbitTransform).
                if (!sv.camera.orthographic)
                {
                    return false;
                }
                cam.orthographic = true;
                cam.orthographicSize = sv.camera.orthographicSize;
            }
            else
            {
                cam.orthographic = sv.camera.orthographic;
                if (cam.orthographic)
                {
                    cam.orthographicSize = sv.camera.orthographicSize;
                }
                else
                {
                    cam.fieldOfView = sv.camera.fieldOfView;
                }
            }
            cam.nearClipPlane = Mathf.Max(0.01f, sv.camera.nearClipPlane);
            cam.farClipPlane = Mathf.Max(k_MinFarClip, sv.camera.farClipPlane);
            return true;
        }

        private static void ApplyOrbitTransform(Camera cam, CaptureSettings settings)
        {
            Vector3 center = Vector3.zero;
            float radius = 5f;
            Bounds bounds;
            bool hasBounds = TryGetSceneBounds(settings.layerMask, out bounds);
            if (hasBounds)
            {
                center = bounds.center;
                radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
            }

            float fitFactor = Mathf.Max(settings.FitFactor, 0.5f);
            float orbitDistanceMultiplier = Mathf.Clamp(
                settings.orbitDistanceMultiplier,
                0.1f,
                10f
            );
            float distance =
                radius / Mathf.Tan(k_DefaultOrbitFov * 0.5f * Mathf.Deg2Rad) * fitFactor;
            distance *= orbitDistanceMultiplier;

            cam.orthographic = settings.orthographic;
            if (settings.orthographic)
            {
                // Orthographic (2D): fit framing is driven by the larger of the
                // bounds half-height and half-width (half-width via aspect), so
                // the orbit distance only places the camera (near-plane
                // clipping). Empty bounds fall back to an origin camera with a
                // fixed half-height.
                cam.orthographicSize = hasBounds
                    ? Mathf.Max(bounds.extents.y, bounds.extents.x / cam.aspect)
                        * orbitDistanceMultiplier
                    : 5f * orbitDistanceMultiplier;
                if (!hasBounds)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "No renderers in the layer mask; orthographic capture fell back to origin framing (orthographicSize 5)."
                    );
                }
            }
            else
            {
                cam.fieldOfView = k_DefaultOrbitFov;
            }

            float yaw = settings.OrbitYaw * Mathf.Deg2Rad;
            float pitch =
                Mathf.Clamp(settings.OrbitPitch, -k_MaxOrbitPitch, k_MaxOrbitPitch) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(
                Mathf.Cos(pitch) * Mathf.Sin(yaw),
                Mathf.Sin(pitch),
                Mathf.Cos(pitch) * Mathf.Cos(yaw)
            );
            cam.transform.position = center + dir * distance;
            cam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);
            cam.nearClipPlane = Mathf.Max(0.01f, distance * 0.01f);
            cam.farClipPlane = Mathf.Max(k_MinFarClip, distance + radius * 4f);
        }

        private static bool TryGetSceneBounds(LayerMask layerMask, out Bounds bounds)
        {
#if UNITY_6000_0_OR_NEWER
            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>();
#else
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            bounds = new Bounds();
            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }
                if ((layerMask.value & (1 << renderer.gameObject.layer)) == 0)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return any;
        }

        private static RenderTexture CreateRenderTexture(
            int width,
            int height,
            bool useHdr,
            Color clearColor
        )
        {
            RenderTextureFormat format = useHdr
                ? RenderTextureFormat.DefaultHDR
                : RenderTextureFormat.Default;
            RenderTexture rt;
            try
            {
                rt = new RenderTexture(width, height, 24, format);
            }
            catch (System.Exception)
            {
                rt = new RenderTexture(width, height, 24, RenderTextureFormat.Default);
            }
            rt.name = k_TempRenderTextureName;
            rt.Create();
            ClearRenderTexture(rt, clearColor);
            return rt;
        }

        /// <summary>
        /// Defense-in-depth against premature readbacks: a fresh RenderTexture
        /// can still hold GPU memory recycled from an earlier capture (the GPU
        /// allocator reuses released RTs). Clearing right after Create makes any
        /// such readback return the clear color, which Inspect classifies as
        /// UniformBackground and rejects - never a stale scene's pixels.
        /// </summary>
        private static void ClearRenderTexture(RenderTexture rt, Color color)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                GL.Clear(true, true, color);
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        private static Texture2D ReadBack(RenderTexture rt)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                if (!rt.sRGB)
                {
                    ConvertLinearToSrgb(tex);
                }
                return tex;
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        /// <summary>
        /// HDR targets are linear (sRGB=false); ReadPixels + EncodeToPNG would store
        /// linear values as sRGB and the PNG comes out ~2.1x darker. Convert RGB via
        /// a precomputed LUT (alpha untouched) whenever the runtime target is not sRGB.
        /// </summary>
        private static void ConvertLinearToSrgb(Texture2D tex)
        {
            double startTime = EditorApplication.timeSinceStartup;
            Color32[] pixels = tex.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                pixel.r = s_LinearToSrgbLut[pixel.r];
                pixel.g = s_LinearToSrgbLut[pixel.g];
                pixel.b = s_LinearToSrgbLut[pixel.b];
                pixels[i] = pixel;
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            if (!s_ConversionLogged)
            {
                s_ConversionLogged = true;
                double elapsedMs = (EditorApplication.timeSinceStartup - startTime) * 1000.0;
                Debug.Log(
                    k_LogPrefix
                        + "linear->sRGB conversion applied (HDR target) in "
                        + elapsedMs.ToString("0")
                        + " ms."
                );
            }
        }

        /// <summary>
        /// Canonical sRGB transfer curve (0.0031308 / 12.92 / 1.055 / 2.4) via
        /// Mathf.LinearToGammaSpace, quantized byte->byte.
        /// </summary>
        private static byte[] BuildLinearToSrgbLut()
        {
            var lut = new byte[256];
            for (int i = 0; i < lut.Length; i++)
            {
                lut[i] = (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(i / 255f) * 255f);
            }
            return lut;
        }

        private static List<LightSnapshot> DisableAllLights()
        {
#if UNITY_6000_0_OR_NEWER
            Light[] lights = UnityEngine.Object.FindObjectsByType<Light>();
#else
            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
#endif
            var snapshots = new List<LightSnapshot>(lights.Length);
            foreach (Light light in lights)
            {
                if (light == null)
                {
                    continue;
                }
                snapshots.Add(
                    new LightSnapshot
                    {
                        Light = light,
                        Enabled = light.enabled,
                        Intensity = light.intensity,
                    }
                );
                light.enabled = false;
                light.intensity = 0f;
            }
            if (snapshots.Count == 0)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "No Light components found in scene; lighting override had nothing to disable."
                );
            }
            return snapshots;
        }

        private static void RestoreLights(List<LightSnapshot> snapshots)
        {
            if (snapshots == null)
            {
                return;
            }
            foreach (LightSnapshot snapshot in snapshots)
            {
                if (snapshot == null || snapshot.Light == null)
                {
                    continue;
                }
                snapshot.Light.enabled = snapshot.Enabled;
                snapshot.Light.intensity = snapshot.Intensity;
            }
        }

        private static bool TrySubmitRenderRequest(Camera cam, RenderTexture rt)
        {
#if UNITY_2022_2_OR_NEWER
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest
            {
                destination = rt,
                slice = 0,
                face = CubemapFace.Unknown,
                mipLevel = 0,
            };
            if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(cam, request))
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, request);
                return true;
            }
#endif
            return false;
        }

        /// <summary>
        /// Adds UniversalAdditionalCameraData (URP) with renderPostProcessing=true
        /// via reflection so the asmdef can stay dependency-free (references: []).
        /// Returns false (with a warning naming the missing type) when URP/UACD is
        /// unavailable; the capture then continues without post-processing.
        /// </summary>
        private static bool TryEnablePostProcessing(Camera cam)
        {
            const string k_UacdTypeName =
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime";
            try
            {
                System.Type uacdType = System.Type.GetType(k_UacdTypeName);
                if (uacdType == null)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Cannot enable post-processing: UniversalAdditionalCameraData type not found ("
                            + k_UacdTypeName
                            + "); capturing without post-processing."
                    );
                    return false;
                }
                Component component = cam.gameObject.AddComponent(uacdType);
                System.Reflection.PropertyInfo property = uacdType.GetProperty(
                    "renderPostProcessing",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                );
                if (property == null || !property.CanWrite)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Cannot enable post-processing: renderPostProcessing property not found on UniversalAdditionalCameraData; capturing without post-processing."
                    );
                    return false;
                }
                property.SetValue(component, true, null);
                return true;
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Cannot enable post-processing ("
                        + exception.GetType().Name
                        + ": "
                        + exception.Message
                        + "); capturing without post-processing."
                );
                return false;
            }
        }

        private static ImageCheck Inspect(
            Texture2D tex,
            Color backgroundColor,
            bool ppOrSkyboxActive
        )
        {
            Color32[] pixels = tex.GetPixels32();
            int step = Mathf.Max(1, pixels.Length / 4096);
            Color32 first = pixels[0];
            for (int i = step; i < pixels.Length; i += step)
            {
                Color32 p = pixels[i];
                if (p.r != first.r || p.g != first.g || p.b != first.b)
                {
                    return ImageCheck.Ok;
                }
            }
            if (ppOrSkyboxActive)
            {
                // M10: with post-processing (tonemapping shifts the clear color away
                // from BackgroundColor) or a skybox background, a uniform image is a
                // valid background - never classify it as a corrupt render.
                return ImageCheck.UniformBackground;
            }
            bool isBackground =
                Mathf.Abs(first.r - backgroundColor.r * 255f) <= 3
                && Mathf.Abs(first.g - backgroundColor.g * 255f) <= 3
                && Mathf.Abs(first.b - backgroundColor.b * 255f) <= 3;
            return isBackground ? ImageCheck.UniformBackground : ImageCheck.UniformOther;
        }

        private static string Describe(ImageCheck check)
        {
            switch (check)
            {
                case ImageCheck.UniformBackground:
                    return "a uniform background image";
                case ImageCheck.UniformOther:
                    return "an all-black or all-one-color image";
                default:
                    return "an unknown image";
            }
        }

        private static bool ExceedsReliablePixels(int width, int height)
        {
            return (long)width * height > k_MaxReliableRenderPixels;
        }

        private static byte[] CreatePlaceholderPng(int width, int height, Color color)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color32 c = color;
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = c;
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }

        #endregion

        #region Nested Types

        private enum ImageCheck
        {
            Ok,
            UniformBackground,
            UniformOther,
        }

        private sealed class LightSnapshot
        {
            public Light Light;
            public bool Enabled;
            public float Intensity;
        }

        #endregion
    }
}
