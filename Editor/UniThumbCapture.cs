using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace MaykerStudio.UniThumb
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
        public bool CaptureUi;

        /// <summary>
        /// UI zoom multiplier. In composite mode (CaptureUi with
        /// UseSceneViewAngle) it is applied as a center-kept sampling zoom in
        /// CompositeSceneViewUi: the canvas keeps scaleFactor 1 (full layout
        /// renders, no canvas-rect culling) and the UI band is scaled in the
        /// blend loop. In legacy square mode it overrides canvas.scaleFactor
        /// while the UI capture session is active (CanvasScaler disabled for the
        /// pass so it cannot re-apply its own scale). 1f means no override (zero
        /// mutation, byte-identical output).
        /// </summary>
        public float UiScale;
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
    public static class UniThumbCapture
    {
        #region Constants

        private const string k_LogPrefix = "[UniThumb] ";
        private const string k_TempCameraName = "__UniThumbCaptureCamera";
        private const string k_TempRenderTextureName = "__UniThumbCaptureRT";
        private const int k_MinResolution = 16;
        private const int k_MaxResolution = 4096;
        private const int k_MaxReliableRenderPixels = 16000000;

        // Fallback-only estimate of the SceneView toolbar strip (px) subtracted
        // from sv.position when sv.camera.pixelWidth/pixelHeight report no
        // viewport (0/1 before the first render).
        private const float k_SceneViewToolbarHeight = 24f;
        private const float k_DefaultOrbitFov = 60f;
        private const float k_DefaultFitFactor = 2f;
        private const float k_MaxOrbitPitch = 89f;
        private const float k_MinFarClip = 1000f;
        private const string k_TempUiPassCameraName = "__UniThumbUiPassCamera";

        /// <summary>
        /// Wave-0 probe P1 (docs/plan/20260813-scene-thumbnail-ui-composite/logs/
        /// wave0-probe-report.md): an ARGB32 UI RenderTexture holds PREMULTIPLIED
        /// color (rgb == a*C exactly), so the composite uses
        /// out.rgb = ui.rgb + scene.rgb * (1 - ui.a); out.a = 255. The
        /// straight-alpha branch stays behind the field for a one-line fallback.
        /// </summary>
        private static readonly bool s_UiBlendPremultiplied = true;

        #endregion

        #region Fields

        private static readonly byte[] s_LinearToSrgbLut = BuildLinearToSrgbLut();

        /// <summary>
        /// Mutable capture bookkeeping on a readonly holder so the class keeps
        /// zero static mutable fields (Asset Store Validator
        /// "Check Static Variables"). Domain-reload semantics unchanged: a fresh
        /// holder is created on reload, exactly like the old statics.
        /// </summary>
        private static readonly CaptureState s_State = new CaptureState();

        private sealed class CaptureState
        {
            public bool ConversionLogged;

            // Settings snapshot remembered via RememberSettings so menu/batch capture
            // paths can reuse the window's configured settings instead of hardcoded
            // defaults. Null until the window warms it; struct value semantics make
            // the stored copy immune to later mutation of the caller's instance.
            public CaptureSettings? LastSettings;
        }

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
                CaptureUi = true,
                UiScale = 1f,
            };
        }

        /// <summary>
        /// Remembers the settings snapshot so menu/batch capture paths can reuse
        /// the window's configured settings (resolution, background, lighting
        /// override, post-processing, UI, layer mask, framing). CaptureSettings
        /// is a struct, so the stored copy cannot be corrupted by later mutation
        /// of the caller's instance.
        /// </summary>
        public static void RememberSettings(CaptureSettings settings)
        {
            s_State.LastSettings = settings;
        }

        /// <summary>
        /// Returns the settings last remembered via RememberSettings, or
        /// CreateDefaultSettings() when the store was never set (e.g. before the
        /// window opened in this editor session).
        /// </summary>
        public static CaptureSettings GetLastSettingsOrDefault()
        {
            return s_State.LastSettings ?? CreateDefaultSettings();
        }

        /// <summary>
        /// True when the composite UI pass applies: CaptureUi AND UseSceneViewAngle
        /// are on, a SceneView with a camera is open, and the UI layer (5) is part
        /// of the layer mask. Sole eligibility source shared by CaptureCore and the
        /// live preview so the two cannot diverge.
        /// </summary>
        public static bool IsSceneViewUiCompositeEligible(CaptureSettings settings)
        {
            if (!settings.CaptureUi || !settings.UseSceneViewAngle)
            {
                return false;
            }
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                return false;
            }
            return (settings.layerMask.value & (1 << 5)) != 0;
        }

        /// <summary>
        /// Renders the UI in a separate pass at the SceneView's ACTUAL viewport
        /// pixel size and composites it into the (square) scene pixels with a
        /// contain-fit mapping (no cropping), so the full UI visible in the
        /// SceneView appears in the thumbnail. Returns scenePixels unchanged
        /// when not eligible, when no overlay canvas exists, or on ANY internal
        /// failure (single Debug.LogWarning; exception containment). Owns its
        /// temp camera/RT/session and destroys them in finally; the caller
        /// destroys the input only when the result differs.
        ///
        /// UI scale (settings.UiScale) is applied as a sampling zoom, never to
        /// the canvas: canvas.scaleFactor stays 1 for the whole composite pass,
        /// so the FULL canvas layout always renders into the viewport-sized RT
        /// (zero canvas-rect culling). The zoom is a center-kept band rect (see
        /// the mapping below); clip-free zoom is limited by the band-vs-
        /// thumbnail aspect headroom (~1.9x for a wide SceneView viewport).
        /// Beyond that headroom the UI band overflows the frame and outer UI is
        /// cropped at the thumbnail edges - a clean frame crop, never
        /// mid-layout culling (unavoidable for a fixed square thumbnail).
        ///
        /// Why the real SceneView camera cannot be the canvas worldCamera: the
        /// canvas would render inside the SceneView's own pass, never into the
        /// capture RenderTexture. Why the UI pass RT is sized from
        /// sv.camera.pixelWidth/pixelHeight: CanvasScaler reads
        /// renderingDisplaySize, which follows the render target, so only an RT
        /// at the SceneView viewport's pixel size reproduces the editor layout
        /// (a square or aspect-derived RT re-lays the canvas out at different
        /// proportions). Why the original canvas planeDistance is not reused:
        /// overlay canvases sit at ~100, beyond the near clip range of the orbit
        /// fallback camera, which would clip the UI away; the session re-derives
        /// it from the camera's near plane instead. Blend rationale: wave-0
        /// probe P1 proved the ARGB32 UI RT holds premultiplied color (rgb ==
        /// a*C), so out.rgb = ui.rgb + scene.rgb * (1 - ui.a) behind
        /// s_UiBlendPremultiplied; the straight branch stays for a one-line
        /// fallback flip.
        /// </summary>
        public static Texture2D CompositeSceneViewUi(
            CaptureSettings settings,
            Texture2D scenePixels
        )
        {
            if (!IsSceneViewUiCompositeEligible(settings))
            {
                return scenePixels;
            }
            if (scenePixels.width != scenePixels.height)
            {
                // Contain-fit mapping assumes square output (all current
                // callers are square); refuse non-square input rather than
                // producing letterbox offsets for a non-square target (public
                // API guard).
                return scenePixels;
            }
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                return scenePixels;
            }
            Camera svCam = sv.camera;

            // Output dimensions come from the pixels texture, not settings:
            // Capture clamps settings into width/height before CaptureCore, and
            // the downscale retry recurses at a smaller size with the original
            // settings - the composite must match the pixels it blends.
            int width = scenePixels.width;
            int height = scenePixels.height;

            // UI-pass RT sizing: match the SceneView's ACTUAL viewport pixel
            // size (sv.camera.pixelWidth/pixelHeight) so CanvasScaler lays the
            // canvas out identically to the editor (renderingDisplaySize
            // follows the render target). Fall back to the window rect minus
            // the toolbar strip when the camera reports no viewport (0/1
            // before the first render). Proportions stay exact; only the
            // 16M-px reliability cap downscales, proportionally (sqrt), so the
            // aspect survives there too. k_MinResolution floors both dims
            // defensively.
            int viewportW = svCam.pixelWidth;
            int viewportH = svCam.pixelHeight;
            if (viewportW < k_MinResolution || viewportH < k_MinResolution)
            {
                Rect windowRect = sv.position;
                viewportW = Mathf.Max(1, Mathf.RoundToInt(windowRect.width));
                viewportH = Mathf.Max(
                    1,
                    Mathf.RoundToInt(windowRect.height - k_SceneViewToolbarHeight)
                );
            }
            long viewportPixels = (long)viewportW * viewportH;
            if (viewportPixels > k_MaxReliableRenderPixels)
            {
                float shrink = Mathf.Sqrt(k_MaxReliableRenderPixels / (float)viewportPixels);
                viewportW = Mathf.Max(k_MinResolution, Mathf.RoundToInt(viewportW * shrink));
                viewportH = Mathf.Max(k_MinResolution, Mathf.RoundToInt(viewportH * shrink));
            }
            int uiW = Mathf.Max(k_MinResolution, viewportW);
            int uiH = Mathf.Max(k_MinResolution, viewportH);

            GameObject tempGo = null;
            Camera uiCam = null;
            RenderTexture wideRt = null;
            UiCaptureSession uiSession = null;
            Texture2D uiPixels = null;
            try
            {
                tempGo = new GameObject(k_TempUiPassCameraName);
                tempGo.hideFlags = HideFlags.HideAndDontSave;
                uiCam = tempGo.AddComponent<Camera>();

                // Full projection copy from the SceneView camera (no orbit
                // fallback): a ScreenSpaceCamera canvas derives its world size
                // from the projection at planeDistance (perspective
                // 2*d*tan(fov/2), ortho 2*orthoSize), so a wrong projection
                // means a wrong UI size.
                uiCam.transform.SetPositionAndRotation(
                    svCam.transform.position,
                    svCam.transform.rotation
                );
                uiCam.orthographic = svCam.orthographic;
                if (uiCam.orthographic)
                {
                    uiCam.orthographicSize = svCam.orthographicSize;
                }
                else
                {
                    uiCam.fieldOfView = svCam.fieldOfView;
                }
                // near is clamped BEFORE planeDistance derives from it.
                uiCam.nearClipPlane = Mathf.Max(0.01f, svCam.nearClipPlane);
                uiCam.farClipPlane = Mathf.Max(k_MinFarClip, svCam.farClipPlane);
                // Framing math only; the RT re-derives the aspect at render.
                uiCam.aspect = uiW / (float)uiH;

                // Depth 24 silences the URP RenderGraph "output Render Texture
                // must have a depth buffer" advisory; the UI pass never reads
                // depth, the buffer is only allocated.
                wideRt = new RenderTexture(uiW, uiH, 24, RenderTextureFormat.ARGB32);
                wideRt.Create();
                // Premultiplied neutral clear (0,0,0,0).
                ClearRenderTexture(wideRt, new Color(0f, 0f, 0f, 0f));

                uiCam.targetTexture = wideRt;
                uiCam.clearFlags = CameraClearFlags.SolidColor;
                uiCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                // Layer 5 is UI-only for the wide pass (probe P2: a
                // ScreenSpaceCamera canvas renders with a UI-layer-only mask;
                // non-canvas renderers on layer 5 would also appear, same as any
                // camera with this mask).
                uiCam.cullingMask = settings.layerMask.value & (1 << 5);

                uiSession = UiCaptureSession.BeginUiPass(uiCam);
                if (uiSession == null)
                {
                    // No overlay canvas exists; scene-only thumbnail.
                    return scenePixels;
                }
                // Canvas layout derives from renderingDisplaySize, which follows
                // the render target; refresh again after targetTexture assignment.
                Canvas.ForceUpdateCanvases();

                uiCam.Render();
                uiPixels = ReadBack(wideRt);

                // Band-rect zoom mapping: the UI pass renders the FULL canvas
                // layout at scaleFactor 1 (viewport-sized rect, zero canvas
                // culling); settings.UiScale is applied here as a center-kept
                // sampling zoom. fitScaleUi = fitScale * uiScale scales the UI
                // band in thumbnail pixels; the offsets center it and go
                // NEGATIVE when the band is wider/taller than the thumbnail
                // (the band overflows the frame - outer UI crops cleanly at the
                // thumbnail edges, never mid-layout culling). The UI band is
                // sampled BILINEARLY (4-texel weighted average, float math):
                // nearest would alias badly at 128px thumbnails (downscale in
                // landscape, possibly upscale in portrait). At uiScale == 1f
                // fitScaleUi == fitScale and the offsets/guard match the
                // previous contain-fit mapping exactly (byte-identical output).
                float fitScale = Mathf.Min(width / (float)uiW, height / (float)uiH);
                float fitScaleUi = fitScale * settings.UiScale;
                float xOffsetUi = (width - uiW * fitScaleUi) * 0.5f;
                float yOffsetUi = (height - uiH * fitScaleUi) * 0.5f;
                float bandRightUi = xOffsetUi + uiW * fitScaleUi;
                float bandBottomUi = yOffsetUi + uiH * fitScaleUi;

                // GetPixels (float Color) keeps interpolation + blend math in
                // float to avoid banding; output stays Color32 like the
                // original loop.
                Color[] sceneColors = scenePixels.GetPixels();
                Color[] uiColors = uiPixels.GetPixels();
                var output = new Color32[width * height];
                for (int y = 0; y < height; y++)
                {
                    int sceneRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        Color scene = sceneColors[sceneRow + x];
                        if (x < xOffsetUi || x >= bandRightUi || y < yOffsetUi || y >= bandBottomUi)
                        {
                            // Letterbox band: no UI covers this pixel; keep the
                            // scene pixel untouched (UI alpha 0 equivalent).
                            output[sceneRow + x] = new Color32(
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(scene.r) * 255f),
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(scene.g) * 255f),
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(scene.b) * 255f),
                                255
                            );
                            continue;
                        }
                        float uiX = (x - xOffsetUi) / fitScaleUi;
                        float uiY = (y - yOffsetUi) / fitScaleUi;
                        Color ui = SampleBilinear(uiColors, uiW, uiH, uiX, uiY);
                        float r;
                        float g;
                        float b;
                        if (s_UiBlendPremultiplied)
                        {
                            // Premultiplied (probe P1): ui.rgb already holds a*C;
                            // premultiplied rgb <= a so the sum cannot exceed 1
                            // (clamped anyway).
                            r = ui.r + scene.r * (1f - ui.a);
                            g = ui.g + scene.g * (1f - ui.a);
                            b = ui.b + scene.b * (1f - ui.a);
                        }
                        else
                        {
                            // Straight-alpha fallback (one-line const flip).
                            r = ui.r * ui.a + scene.r * (1f - ui.a);
                            g = ui.g * ui.a + scene.g * (1f - ui.a);
                            b = ui.b * ui.a + scene.b * (1f - ui.a);
                        }
                        output[sceneRow + x] = new Color32(
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(r) * 255f),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(g) * 255f),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(b) * 255f),
                            255
                        );
                    }
                }

                var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                result.SetPixels32(output);
                result.Apply();
                return result;
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "UI composite pass failed, using scene-only thumbnail: "
                        + exception.Message
                );
                return scenePixels;
            }
            finally
            {
                if (uiSession != null)
                {
                    uiSession.Dispose();
                }
                if (uiPixels != null)
                {
                    UnityEngine.Object.DestroyImmediate(uiPixels);
                }
                if (uiCam != null)
                {
                    uiCam.targetTexture = null;
                }
                if (tempGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempGo);
                }
                if (wideRt != null)
                {
                    wideRt.Release();
                    UnityEngine.Object.DestroyImmediate(wideRt);
                }
            }
        }

        public static CaptureResult Capture(CaptureSettings settings)
        {
            s_State.ConversionLogged = false;
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
            UiCaptureSession uiSession = null;

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

                if (IsSceneViewUiCompositeEligible(settings))
                {
                    // Composite mode: overlay canvases stay untouched during the
                    // scene pass; UI renders in the wide pass inside
                    // CompositeSceneViewUi after the image check.
                    uiSession = null;
                }
                else
                {
                    uiSession = settings.CaptureUi
                        ? UiCaptureSession.Begin(cam, settings.UiScale)
                        : null;
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
                    if (uiSession != null)
                    {
                        uiSession.Dispose();
                        uiSession = null;
                    }
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
                    if (IsSceneViewUiCompositeEligible(settings))
                    {
                        Texture2D composite = CompositeSceneViewUi(settings, pixels);
                        if (composite != pixels)
                        {
                            UnityEngine.Object.DestroyImmediate(pixels);
                            pixels = composite;
                        }
                    }
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
                if (uiSession != null)
                {
                    uiSession.Dispose();
                }
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

        /// <summary>
        /// 4-texel weighted (bilinear) sample of a Color[] texel grid at float
        /// texel coordinates. Integer texel indices are pixel centers; the far
        /// edge clamps to the last texel. Float math avoids banding.
        /// </summary>
        private static Color SampleBilinear(Color[] pixels, int texW, int texH, float x, float y)
        {
            int x0 = Mathf.Clamp((int)Mathf.Floor(x), 0, texW - 1);
            int y0 = Mathf.Clamp((int)Mathf.Floor(y), 0, texH - 1);
            int x1 = Mathf.Min(x0 + 1, texW - 1);
            int y1 = Mathf.Min(y0 + 1, texH - 1);
            float tx = x - x0;
            float ty = y - y0;
            Color c00 = pixels[y0 * texW + x0];
            Color c10 = pixels[y0 * texW + x1];
            Color c01 = pixels[y1 * texW + x0];
            Color c11 = pixels[y1 * texW + x1];
            float topR = c00.r + (c10.r - c00.r) * tx;
            float topG = c00.g + (c10.g - c00.g) * tx;
            float topB = c00.b + (c10.b - c00.b) * tx;
            float topA = c00.a + (c10.a - c00.a) * tx;
            float botR = c01.r + (c11.r - c01.r) * tx;
            float botG = c01.g + (c11.g - c01.g) * tx;
            float botB = c01.b + (c11.b - c01.b) * tx;
            float botA = c01.a + (c11.a - c01.a) * tx;
            return new Color(
                topR + (botR - topR) * ty,
                topG + (botG - topG) * ty,
                topB + (botB - topB) * ty,
                topA + (botA - topA) * ty
            );
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
            if (!s_State.ConversionLogged)
            {
                s_State.ConversionLogged = true;
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

        private struct CanvasUiState
        {
            public Canvas Canvas;
            public RenderMode RenderMode;
            public Camera WorldCamera;
            public float PlaneDistance;
            public int SortingOrder;
            public int Layer;

            /// <summary>
            /// True when a uiScale != 1f override mutated this canvas; the scale
            /// fields below are only restored for mutated entries.
            /// </summary>
            public bool ScaleMutated;
            public float ScaleFactor;
            public CanvasScaler Scaler;
            public bool ScalerEnabled;
        }

        /// <summary>
        /// Switches active Screen Space Overlay canvases to the given camera so they
        /// render into its target texture, and restores them on Dispose. Idempotent.
        /// Only canvases with renderMode == ScreenSpaceOverlay are touched; WorldSpace
        /// and already ScreenSpaceCamera canvases are left alone. BeginUiPass shares
        /// this snapshot/restore machinery for the composite UI pass; it returns null
        /// when no overlay canvas exists so callers can skip the pass. The composite
        /// UI pass (BeginUiPass) additionally moves touched canvases to layer 5 (UI):
        /// ScreenSpaceCamera rendering is keyed to the Canvas GameObject's layer, and
        /// the wide pass culling mask keeps only the UI layer bit, so canvases on
        /// other layers would render nothing. Layers are restored in Dispose, which
        /// runs in finally even when the pass throws.
        ///
        /// When uiScale != 1f the switch loop also overrides canvas.scaleFactor
        /// (snapshotting the original) and disables the canvas' CanvasScaler for
        /// the pass: the scaler is [ExecuteAlways] and re-applies scaleFactor on
        /// every preWillRenderCanvases, so it would fight the override. Dispose
        /// restores scaleFactor and re-enables the scaler. At uiScale == 1f zero
        /// scale mutation happens, keeping output byte-identical. The scale
        /// override serves the legacy square pass only (Begin); the composite
        /// pass (BeginUiPass) always passes 1f and never mutates scale.
        /// </summary>
        public sealed class UiCaptureSession : System.IDisposable
        {
            private readonly List<CanvasUiState> _snapshots = new List<CanvasUiState>();
            private bool _disposed;

            private UiCaptureSession() { }

            /// <summary>
            /// Begins a UI capture session for the legacy square pass. When
            /// uiScale != 1f the switched canvases' scaleFactor is overridden
            /// with the CanvasScaler disabled for the pass; Dispose restores
            /// everything even when the pass throws. Defaults to 1f (no
            /// mutation, byte-identical output).
            /// </summary>
            public static UiCaptureSession Begin(Camera cam, float uiScale = 1f)
            {
                UiCaptureSession session = new UiCaptureSession();
                try
                {
                    SwitchOverlayCanvases(session, cam, false, uiScale);
                    Canvas.ForceUpdateCanvases();
                    return session;
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            /// <summary>
            /// Composite-pass variant of Begin: same snapshot/restore machinery and
            /// planeDistance formula, plus a forced move of touched canvases to
            /// layer 5 (UI) so the wide pass culling mask (UI layer bit only)
            /// renders them. The composite pass never scales canvases: the shared
            /// switch runs with uiScale 1f, so zero canvas mutation happens
            /// (canvas.scaleFactor stays 1, CanvasScaler stays enabled) and the
            /// full canvas layout renders; settings.UiScale is applied as a
            /// center-kept zoom in the composite sampling step instead. Returns
            /// null (session already disposed) when zero Screen Space Overlay
            /// canvases exist so the caller can skip the UI pass and keep the
            /// scene-only pixels.
            /// </summary>
            public static UiCaptureSession BeginUiPass(Camera uiCam)
            {
                UiCaptureSession session = new UiCaptureSession();
                try
                {
                    int switched = SwitchOverlayCanvases(session, uiCam, true, 1f);
                    Canvas.ForceUpdateCanvases();
                    if (switched == 0)
                    {
                        session.Dispose();
                        return null;
                    }
                    return session;
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            private static int SwitchOverlayCanvases(
                UiCaptureSession session,
                Camera cam,
                bool forceUiLayer,
                float uiScale
            )
            {
                int switched = 0;
#if UNITY_6000_0_OR_NEWER
                Canvas[] canvases = UnityEngine.Object.FindObjectsByType<Canvas>();
#else
                Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
#endif
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas canvas = canvases[i];
                    if (canvas == null || !canvas.isActiveAndEnabled)
                    {
                        continue;
                    }
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    {
                        continue;
                    }
                    bool scaleMutated = false;
                    float scaleFactor = 1f;
                    CanvasScaler scaler = null;
                    bool scalerEnabled = false;
                    if (uiScale != 1f)
                    {
                        // Scale override: CanvasScaler is [ExecuteAlways] and
                        // re-applies scaleFactor on every preWillRenderCanvases
                        // while enabled, so disable it (snapshot first) or the
                        // override would not survive to the render.
                        scaleFactor = canvas.scaleFactor;
                        scaler = canvas.GetComponent<CanvasScaler>();
                        if (scaler != null)
                        {
                            scalerEnabled = scaler.enabled;
                            scaler.enabled = false;
                        }
                        canvas.scaleFactor = uiScale;
                        scaleMutated = true;
                    }
                    session._snapshots.Add(
                        new CanvasUiState
                        {
                            Canvas = canvas,
                            RenderMode = canvas.renderMode,
                            WorldCamera = canvas.worldCamera,
                            PlaneDistance = canvas.planeDistance,
                            SortingOrder = canvas.sortingOrder,
                            Layer = canvas.gameObject.layer,
                            ScaleMutated = scaleMutated,
                            ScaleFactor = scaleFactor,
                            Scaler = scaler,
                            ScalerEnabled = scalerEnabled,
                        }
                    );
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = Mathf.Max(cam.nearClipPlane + 0.1f, 0.1f);
                    if (forceUiLayer)
                    {
                        // ScreenSpaceCamera rendering is keyed to the Canvas
                        // GameObject's layer; the wide pass culling mask is
                        // layerMask & (1<<5), so overlay canvases must sit on
                        // layer 5 (UI) to render into the pass.
                        canvas.gameObject.layer = 5;
                    }
                    switched++;
                }
                return switched;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    CanvasUiState snapshot = _snapshots[i];
                    if (snapshot.Canvas == null)
                    {
                        continue;
                    }
                    snapshot.Canvas.renderMode = snapshot.RenderMode;
                    snapshot.Canvas.worldCamera = snapshot.WorldCamera;
                    snapshot.Canvas.planeDistance = snapshot.PlaneDistance;
                    snapshot.Canvas.sortingOrder = snapshot.SortingOrder;
                    snapshot.Canvas.gameObject.layer = snapshot.Layer;
                    if (snapshot.ScaleMutated)
                    {
                        snapshot.Canvas.scaleFactor = snapshot.ScaleFactor;
                        if (snapshot.Scaler != null)
                        {
                            // Re-enable the scaler before the final
                            // ForceUpdateCanvases below.
                            snapshot.Scaler.enabled = snapshot.ScalerEnabled;
                        }
                    }
                }
                _snapshots.Clear();
                Canvas.ForceUpdateCanvases();
            }
        }

        #endregion
    }
}
