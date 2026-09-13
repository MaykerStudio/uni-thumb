using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Resolution-framing parity suite (plan 20260910-ui-resolution-framing):
    /// the prefab UI session scale-freezes every touched canvas (scaleFactor
    /// snapshot + CanvasScaler disabled + ForceUpdate once + restore in
    /// Dispose) so the temp RT size cannot re-lay the canvas out between
    /// framing and render. Resolution wave 2 keeps the freeze machinery and
    /// derives the pass scale from the capture RT size over the scaler
    /// referenceResolution, so fixed-pixel UI holds an identical
    /// frame-fraction at 16/128/512/2048 (density only). Overlay-converted
    /// canvases take the raw RT base with no k_PrefabRtScaleMin floor (the
    /// floor broke proportionality: floored 0.25@128 vs raw 0.16, and
    /// overflowed uniform at 16); Camera canvases take the floored base.
    /// Overlay/Camera/World prefabs capture with CaptureUi on at
    /// 16/128/512/2048 with identical composition (density only), stable
    /// bounds, and exact state restore.
    /// CanvasScaler resolves by reflection so the test assembly needs no
    /// UnityEngine.UI reference and no asmdef change.
    /// </summary>
    [TestFixture]
    public class UniThumbPrefabResolutionFramingTests
    {
        #region Fields

        private readonly List<string> _tempAssets = new List<string>();
        private readonly List<GameObject> _tempObjects = new List<GameObject>();

        #endregion

        #region Unity Callbacks

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            UniThumbCapture.UiCaptureSession.ThrowInPrefabSwitchForTest = false;
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbCapture.UiCaptureSession.ThrowInPrefabSwitchForTest = false;
            for (int i = 0; i < _tempObjects.Count; i++)
            {
                GameObject go = _tempObjects[i];
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            _tempObjects.Clear();
            for (int i = _tempAssets.Count - 1; i >= 0; i--)
            {
                AssetDatabase.DeleteAsset(_tempAssets[i]);
            }
            _tempAssets.Clear();
            UniThumbGuard.Exit();
        }

        #endregion

        #region Public Methods

        [Test]
        public void BeginPrefab_Overlay_FreezesScaler_AtUiScaleOne()
        {
            GameObject root = new GameObject("ResolutionFreezeOverlayRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "FreezeOverlay",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for the freeze test.");
            Behaviour scalerBehaviour = scaler as Behaviour;
            Assert.IsNotNull(scalerBehaviour, "CanvasScaler must be a Behaviour.");
            scalerBehaviour.enabled = true;
            float scaleBefore = canvas.scaleFactor;

            GameObject camGo = new GameObject("ResolutionFreezeCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            UniThumbCapture.UiCaptureSession session = UniThumbCapture.UiCaptureSession.BeginPrefab(
                root,
                cam,
                1f
            );
            try
            {
                Assert.IsFalse(
                    scalerBehaviour.enabled,
                    "Scaler must stay disabled during the prefab pass."
                );
                Assert.AreEqual(
                    scaleBefore,
                    canvas.scaleFactor,
                    "ScaleFactor must not move at uiScale 1."
                );
                Assert.AreEqual(RenderMode.ScreenSpaceCamera, canvas.renderMode);
                Assert.AreEqual(cam, canvas.worldCamera);
            }
            finally
            {
                session.Dispose();
                Undo.RevertAllDownToGroup(undoGroup);
            }
            Assert.IsTrue(scalerBehaviour.enabled, "Scaler enabled must restore.");
            Assert.AreEqual(scaleBefore, canvas.scaleFactor, "ScaleFactor must restore.");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
            Assert.IsNull(canvas.worldCamera);
        }

        [Test]
        public void BeginPrefab_Camera_FreezesScaler_AtUiScaleOne()
        {
            GameObject root = new GameObject("ResolutionFreezeCameraRoot");
            _tempObjects.Add(root);
            GameObject authoredCamGo = new GameObject("ResolutionFreezeAuthoredCamera");
            authoredCamGo.transform.SetParent(root.transform, false);
            Camera authoredCam = authoredCamGo.AddComponent<Camera>();
            authoredCam.enabled = false;
            GameObject canvasGo = CreateCanvas(
                "FreezeCamera",
                RenderMode.ScreenSpaceCamera,
                root.transform
            );
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.worldCamera = authoredCam;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.planeDistance = 1.5f;
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for the freeze test.");
            Behaviour scalerBehaviour = scaler as Behaviour;
            Assert.IsNotNull(scalerBehaviour, "CanvasScaler must be a Behaviour.");
            scalerBehaviour.enabled = true;
            float scaleBefore = canvas.scaleFactor;

            GameObject camGo = new GameObject("ResolutionFreezeTempCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 0;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            UniThumbCapture.UiCaptureSession session = UniThumbCapture.UiCaptureSession.BeginPrefab(
                root,
                cam,
                1f
            );
            try
            {
                Assert.IsFalse(scalerBehaviour.enabled, "Camera canvas scaler must freeze too.");
                Assert.AreEqual(
                    scaleBefore,
                    canvas.scaleFactor,
                    "ScaleFactor must not move at uiScale 1."
                );
                Assert.AreEqual(cam, canvas.worldCamera);
                Assert.AreEqual(1.5f, canvas.planeDistance, "Authored planeDistance must keep.");
                Assert.AreNotEqual(0, cam.cullingMask & (1 << canvasGo.layer));
            }
            finally
            {
                session.Dispose();
                Undo.RevertAllDownToGroup(undoGroup);
            }
            Assert.IsTrue(scalerBehaviour.enabled, "Scaler enabled must restore.");
            Assert.AreEqual(scaleBefore, canvas.scaleFactor, "ScaleFactor must restore.");
            Assert.AreEqual(authoredCam, canvas.worldCamera);
            Assert.AreEqual(1.5f, canvas.planeDistance);
        }

        [Test]
        public void CapturePrefab_Overlay_MultiResolution_Parity()
        {
            string assetPath = CreateOverlayScalerPrefab("__UniThumbResolution_Overlay.prefab");
            AssertMultiResolutionParity(assetPath, RenderMode.ScreenSpaceOverlay);
        }

        [Test]
        public void CapturePrefab_Camera_MultiResolution_Parity()
        {
            string assetPath = CreateCameraScalerPrefab("__UniThumbResolution_Camera.prefab");
            AssertMultiResolutionParity(assetPath, RenderMode.ScreenSpaceCamera);
        }

        [Test]
        public void CapturePrefab_World_MultiResolution_Parity()
        {
            string assetPath = CreateWorldPrefab("__UniThumbResolution_World.prefab");
            AssertMultiResolutionParity(assetPath, RenderMode.WorldSpace);
        }

        [Test]
        public void BeginPrefab_RtProportional_TracksRtSize()
        {
            GameObject root = new GameObject("RtScaleOverlayRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "RtScaleOverlay",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for the RT scale test.");
            Behaviour scalerBehaviour = scaler as Behaviour;
            Assert.IsNotNull(scalerBehaviour, "CanvasScaler must be a Behaviour.");
            scalerBehaviour.enabled = true;
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            float scaleBefore = canvas.scaleFactor;

            GameObject camGo = new GameObject("RtScaleCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();

            // Reference 800x600, match width (0): raw base, no floor.
            // 16 -> 0.02, 128 -> 0.16, 512 -> 0.64, 2048 -> 2.56.
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 16, 0.02f, 1f);
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 128, 0.16f, 1f);
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 512, 0.64f, 1f);
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 2048, 2.56f, 1f);
        }

        [Test]
        public void BeginPrefab_RtProportional_AppliesUiScaleOnTop()
        {
            GameObject root = new GameObject("RtScaleUiScaleRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "RtScaleUiScaleOverlay",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for the UiScale test.");
            Behaviour scalerBehaviour = scaler as Behaviour;
            Assert.IsNotNull(scalerBehaviour, "CanvasScaler must be a Behaviour.");
            scalerBehaviour.enabled = true;
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            float scaleBefore = canvas.scaleFactor;

            GameObject camGo = new GameObject("RtScaleUiScaleCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();

            // RT 512 (raw base 0.64, no floor) with UiScale 2 -> 1.28.
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 512, 1.28f, 2f);
        }

        [Test]
        public void BeginPrefab_RtProportional_CameraCanvas()
        {
            GameObject root = new GameObject("RtScaleCameraRoot");
            _tempObjects.Add(root);
            GameObject authoredCamGo = new GameObject("RtScaleAuthoredCamera");
            authoredCamGo.transform.SetParent(root.transform, false);
            Camera authoredCam = authoredCamGo.AddComponent<Camera>();
            authoredCam.enabled = false;
            GameObject canvasGo = CreateCanvas(
                "RtScaleCameraCanvas",
                RenderMode.ScreenSpaceCamera,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.worldCamera = authoredCam;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.planeDistance = 1.5f;
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for the camera RT test.");
            Behaviour scalerBehaviour = scaler as Behaviour;
            Assert.IsNotNull(scalerBehaviour, "CanvasScaler must be a Behaviour.");
            scalerBehaviour.enabled = true;
            float scaleBefore = canvas.scaleFactor;

            GameObject camGo = new GameObject("RtScaleTempCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 0;

            // Camera canvases get the RT base without the UiScale multiplier.
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 2048, 2.56f, 1f);
            Assert.AreEqual(1.5f, canvas.planeDistance, "Authored planeDistance must keep.");
        }

        [Test]
        public void BeginPrefab_NoTargetTexture_LegacyFreeze()
        {
            GameObject root = new GameObject("RtScaleNoRtRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "RtScaleNoRtOverlay",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for the fallback test.");
            Behaviour scalerBehaviour = scaler as Behaviour;
            Assert.IsNotNull(scalerBehaviour, "CanvasScaler must be a Behaviour.");
            scalerBehaviour.enabled = true;
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            float scaleBefore = canvas.scaleFactor;

            // No targetTexture on the camera: legacy freeze, zero scale mutation.
            GameObject camGo = new GameObject("RtScaleNoRtCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();
            Assert.IsNull(cam.targetTexture, "Precondition: camera must carry no RT.");

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            UniThumbCapture.UiCaptureSession session = UniThumbCapture.UiCaptureSession.BeginPrefab(
                root,
                cam,
                1f
            );
            try
            {
                Assert.IsFalse(scalerBehaviour.enabled, "Scaler must freeze during the pass.");
                Assert.AreEqual(
                    scaleBefore,
                    canvas.scaleFactor,
                    "ScaleFactor must not move without an RT."
                );
            }
            finally
            {
                session.Dispose();
                Undo.RevertAllDownToGroup(undoGroup);
            }
            Assert.IsTrue(scalerBehaviour.enabled, "Scaler enabled must restore.");
            Assert.AreEqual(scaleBefore, canvas.scaleFactor, "ScaleFactor must restore.");
        }

        [Test]
        public void CapturePrefab_FixedUi_MultiResolution_Parity()
        {
            string assetPath = CreateFixedUiPrefab("__UniThumbResolution_FixedUi.prefab");
            int[] sizes = new int[] { 16, 128, 512, 2048 };
            float[] coverages = new float[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                coverages[i] = CaptureCoverageAt(assetPath, sizes[i], true);
                Assert.Greater(
                    coverages[i],
                    0.02f,
                    "Fixed UI must contribute pixels at "
                        + sizes[i]
                        + " (coverage "
                        + coverages[i]
                        + ")."
                );
            }
            AssertMaxMinusMin(
                coverages,
                0.05f,
                "Fixed-pixel UI frame-fraction must match at 16/128/512/2048."
            );
            AssertPrefabUiStateRestored(assetPath, RenderMode.ScreenSpaceOverlay);
        }

        [Test]
        public void CapturePrefab_FixedUi_ToggleOff_StableAndSuppressed()
        {
            // Toggle-off needs a 3D anchor: a UI-only prefab with CaptureUi
            // off renders the bare solid background, which capture reports as
            // UniformBackground failure by design. The mixed cube-plus-UI
            // prefab keeps the render non-uniform so suppression (off below
            // on) and cross-resolution stability stay measurable.
            string assetPath = CreateMixedCubeUiPrefab("__UniThumbResolution_FixedUiOff.prefab");
            int[] sizes = new int[] { 16, 128, 512, 2048 };
            float[] offCoverages = new float[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                float off = CaptureCoverageAt(assetPath, sizes[i], false);
                float on = CaptureCoverageAt(assetPath, sizes[i], true);
                offCoverages[i] = off;
                Assert.Less(
                    off,
                    on,
                    "Toggle-off must suppress UI at "
                        + sizes[i]
                        + " (off "
                        + off
                        + " vs on "
                        + on
                        + ")."
                );
            }
            AssertMaxMinusMin(
                offCoverages,
                0.05f,
                "Toggle-off baseline must stay stable at 16/128/512/2048."
            );
        }

        [Test]
        public void CapturePrefab_MixedCubePlusUi_MultiResolution_Parity()
        {
            string assetPath = CreateMixedCubeUiPrefab("__UniThumbResolution_Mixed.prefab");
            int[] sizes = new int[] { 16, 128, 512, 2048 };
            float[] coverages = new float[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                coverages[i] = CaptureCoverageAt(assetPath, sizes[i], true);
                Assert.Greater(
                    coverages[i],
                    0.02f,
                    "Mixed prefab must contribute pixels at "
                        + sizes[i]
                        + " (coverage "
                        + coverages[i]
                        + ")."
                );
            }
            AssertMaxMinusMin(
                coverages,
                0.05f,
                "Mixed cube-plus-UI frame-fraction must match at 16/128/512/2048."
            );
            AssertPrefabUiStateRestored(assetPath, RenderMode.ScreenSpaceOverlay);
        }

        [Test]
        public void BeginPrefab_ConstantPixelSize_FallsBackToReferenceScale()
        {
            GameObject root = new GameObject("ConstantPixelFallbackRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "ConstantPixelCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddConstantPixelSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for the fallback test.");
            Behaviour scalerBehaviour = scaler as Behaviour;
            Assert.IsNotNull(scalerBehaviour, "CanvasScaler must be a Behaviour.");
            scalerBehaviour.enabled = true;
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            float scaleBefore = canvas.scaleFactor;

            GameObject camGo = new GameObject("ConstantPixelFallbackCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();

            // No ScaleWithScreenSize reference: fallback to 800x600 Expand.
            // 16 -> 0.02, 128 -> 0.16, 512 -> 0.64 (raw, no floor).
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 16, 0.02f, 1f);
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 128, 0.16f, 1f);
            AssertRtScale(cam, root, canvas, scalerBehaviour, scaleBefore, 512, 0.64f, 1f);
        }

        [Test]
        public void BeginPrefab_NullScaler_FallsBackToReferenceScale()
        {
            GameObject root = new GameObject("NullScalerFallbackRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "NullScalerCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            Type scalerType = FindScalerType();
            Assert.IsNotNull(scalerType, "CanvasScaler type must resolve.");
            Assert.IsNull(
                canvasGo.GetComponent(scalerType),
                "Precondition: canvas must carry no CanvasScaler."
            );
            float scaleBefore = canvas.scaleFactor;

            GameObject camGo = new GameObject("NullScalerFallbackCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();

            RenderTexture rt = new RenderTexture(128, 128, 0);
            try
            {
                cam.targetTexture = rt;
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                UniThumbCapture.UiCaptureSession session =
                    UniThumbCapture.UiCaptureSession.BeginPrefab(root, cam, 1f);
                try
                {
                    Assert.AreEqual(
                        0.16f,
                        canvas.scaleFactor,
                        0.001f,
                        "Null-scaler overlay must take the raw fallback base at 128."
                    );
                }
                finally
                {
                    session.Dispose();
                    Undo.RevertAllDownToGroup(undoGroup);
                }
                Assert.AreEqual(scaleBefore, canvas.scaleFactor, "ScaleFactor must restore.");
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        [Test]
        public void CapturePrefab_ConstantPixelSize_MultiResolution_PositionParity()
        {
            string panelPath = CreateConstantPixelPanelPrefab(
                "__UniThumbResolution_ButtonPanel.prefab"
            );
            string hudPath = CreateConstantPixelHudPrefab("__UniThumbResolution_HealthBar.prefab");
            AssertPositionParity(panelPath, "ButtonPanel");
            AssertPositionParity(hudPath, "HealthBar");
            AssertPrefabUiStateRestored(panelPath, RenderMode.ScreenSpaceOverlay);
            AssertPrefabUiStateRestored(hudPath, RenderMode.ScreenSpaceOverlay);
        }

        #endregion

        #region Private Methods

        private void AssertMultiResolutionParity(string assetPath, RenderMode expectedMode)
        {
            int[] sizes = new int[] { 16, 128, 512, 2048 };
            float[] coverages = new float[sizes.Length];
            float[] assetScales = new float[sizes.Length];

            GameObject assetBefore = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(assetBefore, "Prefab asset must load: " + assetPath);
            Canvas assetCanvasBefore = assetBefore.GetComponentInChildren<Canvas>();
            Assert.IsNotNull(assetCanvasBefore, "Prefab asset canvas must exist.");
            Assert.AreEqual(expectedMode, assetCanvasBefore.renderMode);
            float planeBefore = assetCanvasBefore.planeDistance;
            float scaleBefore = assetCanvasBefore.scaleFactor;
            bool scalerEnabledBefore = ReadScalerEnabled(assetCanvasBefore);

            bool boundsOk = UniThumbCapture.TryGetPrefabBounds(
                assetBefore,
                -1,
                out Bounds boundsBefore
            );

            for (int i = 0; i < sizes.Length; i++)
            {
                CaptureSettings settings = ResolutionSettings(sizes[i]);
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                CaptureResult result;
                try
                {
                    result = UniThumbCapture.CapturePrefab(assetPath, settings);
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
                Assert.IsTrue(
                    result.Success,
                    "Capture failed at " + sizes[i] + ": " + result.Warning
                );
                Color32[] pixels = DecodePngPixels(result.PngBytes);
                Assert.AreEqual(
                    sizes[i] * sizes[i],
                    pixels.Length,
                    "Pixel count must match request."
                );
                float coverage = CoverageRatio(
                    pixels,
                    UniThumbCapture.EffectiveClearColor(settings)
                );
                coverages[i] = coverage;

                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                Assert.IsNotNull(asset, "Prefab asset must survive capture.");
                Canvas canvas = asset.GetComponentInChildren<Canvas>();
                Assert.IsNotNull(canvas, "Prefab asset canvas must survive capture.");
                Assert.AreEqual(
                    expectedMode,
                    canvas.renderMode,
                    "RenderMode must restore at " + sizes[i] + "."
                );
                Assert.AreEqual(
                    planeBefore,
                    canvas.planeDistance,
                    "PlaneDistance must restore at " + sizes[i] + "."
                );
                Assert.AreEqual(
                    scaleBefore,
                    canvas.scaleFactor,
                    "ScaleFactor must restore at " + sizes[i] + "."
                );
                Assert.AreEqual(
                    scalerEnabledBefore,
                    ReadScalerEnabled(canvas),
                    "Scaler enabled must restore at " + sizes[i] + "."
                );
                if (expectedMode == RenderMode.ScreenSpaceOverlay)
                {
                    Assert.IsNull(
                        canvas.worldCamera,
                        "Overlay worldCamera must restore at " + sizes[i] + "."
                    );
                }
                assetScales[i] = canvas.scaleFactor;
            }

            for (int i = 0; i < coverages.Length; i++)
            {
                Assert.Greater(
                    coverages[i],
                    0.02f,
                    "UI must contribute pixels at " + sizes[i] + " (coverage " + coverages[i] + ")."
                );
            }
            float min = coverages[0];
            float max = coverages[0];
            for (int i = 1; i < coverages.Length; i++)
            {
                if (coverages[i] < min)
                {
                    min = coverages[i];
                }
                if (coverages[i] > max)
                {
                    max = coverages[i];
                }
            }
            Assert.LessOrEqual(
                max - min,
                0.05f,
                "UI coverage must stay within tolerance across resolutions (min "
                    + min
                    + " max "
                    + max
                    + ")."
            );

            for (int i = 1; i < assetScales.Length; i++)
            {
                Assert.AreEqual(
                    assetScales[0],
                    assetScales[i],
                    "ScaleFactor must stay equal across resolutions."
                );
            }

            GameObject assetAfter = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(assetAfter, "Prefab asset must load after captures.");
            bool boundsAfterOk = UniThumbCapture.TryGetPrefabBounds(
                assetAfter,
                -1,
                out Bounds boundsAfter
            );
            Assert.AreEqual(boundsOk, boundsAfterOk, "Bounds presence must stay stable.");
            if (boundsOk && boundsAfterOk)
            {
                Assert.AreEqual(
                    boundsBefore.center.x,
                    boundsAfter.center.x,
                    0.001f,
                    "Bounds center.x stable."
                );
                Assert.AreEqual(
                    boundsBefore.center.y,
                    boundsAfter.center.y,
                    0.001f,
                    "Bounds center.y stable."
                );
                Assert.AreEqual(
                    boundsBefore.center.z,
                    boundsAfter.center.z,
                    0.001f,
                    "Bounds center.z stable."
                );
                Assert.AreEqual(
                    boundsBefore.extents.x,
                    boundsAfter.extents.x,
                    0.001f,
                    "Bounds extents.x stable."
                );
                Assert.AreEqual(
                    boundsBefore.extents.y,
                    boundsAfter.extents.y,
                    0.001f,
                    "Bounds extents.y stable."
                );
                Assert.AreEqual(
                    boundsBefore.extents.z,
                    boundsAfter.extents.z,
                    0.001f,
                    "Bounds extents.z stable."
                );
            }

            Assert.IsFalse(UniThumbGuard.IsGenerating, "Guard must release after captures.");
            Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard must be re-enterable after captures.");
            UniThumbGuard.Exit();
        }

        private static CaptureSettings ResolutionSettings(int size)
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Width = size;
            settings.Height = size;
            settings.BackgroundMode = BackgroundMode.SolidColor;
            settings.WantPostProcessing = false;
            settings.CaptureUi = true;
            settings.UseLightingOverride = false;
            settings.Light2DMode = LightingMode.None;
            return settings;
        }

        private void AssertRtScale(
            Camera cam,
            GameObject root,
            Canvas canvas,
            Behaviour scalerBehaviour,
            float scaleBefore,
            int rtSize,
            float expectedScale,
            float uiScale
        )
        {
            RenderTexture rt = new RenderTexture(rtSize, rtSize, 0);
            try
            {
                cam.targetTexture = rt;
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                UniThumbCapture.UiCaptureSession session =
                    UniThumbCapture.UiCaptureSession.BeginPrefab(root, cam, uiScale);
                try
                {
                    Assert.IsFalse(
                        scalerBehaviour.enabled,
                        "Scaler must stay disabled during the prefab pass."
                    );
                    Assert.AreEqual(
                        expectedScale,
                        canvas.scaleFactor,
                        0.001f,
                        "Effective scale must track RT size at " + rtSize + "."
                    );
                }
                finally
                {
                    session.Dispose();
                    Undo.RevertAllDownToGroup(undoGroup);
                }
                Assert.IsTrue(scalerBehaviour.enabled, "Scaler enabled must restore.");
                Assert.AreEqual(scaleBefore, canvas.scaleFactor, "ScaleFactor must restore.");
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private float CaptureCoverageAt(string assetPath, int size, bool captureUi)
        {
            CaptureSettings settings = ResolutionSettings(size);
            settings.CaptureUi = captureUi;
            Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
            CaptureResult result;
            try
            {
                result = UniThumbCapture.CapturePrefab(assetPath, settings);
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            Assert.IsTrue(result.Success, "Capture failed at " + size + ": " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            Assert.AreEqual(size * size, pixels.Length, "Pixel count must match request.");
            return CoverageRatio(pixels, UniThumbCapture.EffectiveClearColor(settings));
        }

        private static void AssertPrefabUiStateRestored(string assetPath, RenderMode expectedMode)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(asset, "Prefab asset must survive capture.");
            Canvas canvas = asset.GetComponentInChildren<Canvas>();
            Assert.IsNotNull(canvas, "Prefab asset canvas must survive capture.");
            Assert.AreEqual(expectedMode, canvas.renderMode, "RenderMode must restore.");
            Assert.IsNull(canvas.worldCamera, "Overlay worldCamera must restore.");
        }

        private string CreateFixedUiPrefab(string file)
        {
            GameObject root = new GameObject("ResolutionFixedUiRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "FixedUiCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for fixed-UI parity prefab.");
            // Fixed reference-pixel rect (centered 400x300): the raw
            // RT-proportional base (no floor) keeps its frame-fraction
            // constant, so the coverage matches at 16/128/512/2048
            // (density only).
            AddCenteredFixedImage(canvasGo, new Vector2(400f, 300f), Color.white);
            return SavePrefab(root, file);
        }

        private static Component AddConstantPixelSizeScaler(GameObject canvasGo)
        {
            Type scalerType = FindScalerType();
            if (scalerType == null)
            {
                return null;
            }
            Component scaler = canvasGo.AddComponent(scalerType);
            PropertyInfo modeProperty = scalerType.GetProperty("uiScaleMode");
            if (modeProperty != null && modeProperty.CanWrite)
            {
                Type modeType = modeProperty.PropertyType;
                object constantMode = Enum.ToObject(modeType, 0);
                modeProperty.SetValue(scaler, constantMode, null);
            }
            return scaler;
        }

        private string CreateConstantPixelPanelPrefab(string file)
        {
            GameObject root = new GameObject("ResolutionButtonPanelRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "ButtonPanelCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddConstantPixelSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for ButtonPanel prefab.");
            AddCenteredPanel(canvasGo, new Vector2(400f, 250f), Vector2.zero, Color.white);
            return SavePrefab(root, file);
        }

        private string CreateConstantPixelHudPrefab(string file)
        {
            GameObject root = new GameObject("ResolutionHealthBarRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "HealthBarCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddConstantPixelSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for HealthBar prefab.");
            AddCenteredPanel(canvasGo, new Vector2(360f, 120f), new Vector2(0f, 120f), Color.green);
            return SavePrefab(root, file);
        }

        private static GameObject AddCenteredPanel(
            GameObject canvasGo,
            Vector2 size,
            Vector2 anchoredPosition,
            Color color
        )
        {
            GameObject go = new GameObject("Panel", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            AddImage(go, color);
            RectTransform rect = go.transform as RectTransform;
            Assert.IsNotNull(rect, "Panel needs a RectTransform.");
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;
            return go;
        }

        private void AssertPositionParity(string assetPath, string label)
        {
            int[] sizes = new int[] { 16, 128, 512 };
            float[] centerX = new float[sizes.Length];
            float[] centerY = new float[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                Vector2 centroid = CaptureCentroidAt(assetPath, sizes[i]);
                centerX[i] = centroid.x;
                centerY[i] = centroid.y;
            }
            AssertMaxMinusMin(
                centerX,
                0.1f,
                label + " centroid.x must hold equal position at 16/128/512."
            );
            AssertMaxMinusMin(
                centerY,
                0.1f,
                label + " centroid.y must hold equal position at 16/128/512."
            );
        }

        private Vector2 CaptureCentroidAt(string assetPath, int size)
        {
            CaptureSettings settings = ResolutionSettings(size);
            settings.CaptureUi = true;
            Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
            CaptureResult result;
            try
            {
                result = UniThumbCapture.CapturePrefab(assetPath, settings);
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            Assert.IsTrue(result.Success, "Capture failed at " + size + ": " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            Assert.AreEqual(size * size, pixels.Length, "Pixel count must match request.");
            Color clear = UniThumbCapture.EffectiveClearColor(settings);
            int clearR = Mathf.RoundToInt(clear.r * 255f);
            int clearG = Mathf.RoundToInt(clear.g * 255f);
            int clearB = Mathf.RoundToInt(clear.b * 255f);
            double sumX = 0.0;
            double sumY = 0.0;
            int count = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                if (
                    Mathf.Abs(pixel.r - clearR) > 12
                    || Mathf.Abs(pixel.g - clearG) > 12
                    || Mathf.Abs(pixel.b - clearB) > 12
                )
                {
                    sumX += i % size;
                    sumY += i / size;
                    count++;
                }
            }
            Assert.Greater(count, 0, "UI must contribute pixels at " + size + ".");
            return new Vector2((float)(sumX / count / size), (float)(sumY / count / size));
        }

        private static void AssertMaxMinusMin(float[] values, float tolerance, string message)
        {
            Assert.IsNotNull(values, "Coverage array must exist.");
            Assert.Greater(values.Length, 1, "Coverage array needs two samples.");
            float min = values[0];
            float max = values[0];
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] < min)
                {
                    min = values[i];
                }
                if (values[i] > max)
                {
                    max = values[i];
                }
            }
            Assert.LessOrEqual(
                max - min,
                tolerance,
                message + " (min " + min + " max " + max + ")."
            );
        }

        private string CreateMixedCubeUiPrefab(string file)
        {
            GameObject root = new GameObject("ResolutionMixedRoot");
            _tempObjects.Add(root);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "MixedCube";
            cube.transform.SetParent(root.transform, false);
            GameObject canvasGo = CreateCanvas(
                "MixedUiCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for mixed parity prefab.");
            AddCenteredFixedImage(canvasGo, new Vector2(400f, 300f), Color.white);
            return SavePrefab(root, file);
        }

        private static GameObject AddCenteredFixedImage(
            GameObject canvasGo,
            Vector2 size,
            Color color
        )
        {
            GameObject go = new GameObject("FixedImg", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            AddImage(go, color);
            RectTransform rect = go.transform as RectTransform;
            Assert.IsNotNull(rect, "Image needs a RectTransform.");
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            return go;
        }

        private string CreateOverlayScalerPrefab(string file)
        {
            GameObject root = new GameObject("ResolutionOverlayRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "OverlayCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for overlay parity prefab.");
            AddLeftHalfImage(canvasGo, Color.white);
            return SavePrefab(root, file);
        }

        private string CreateCameraScalerPrefab(string file)
        {
            GameObject root = new GameObject("ResolutionCameraRoot");
            _tempObjects.Add(root);
            GameObject authoredCamGo = new GameObject("AuthoredCamera");
            authoredCamGo.transform.SetParent(root.transform, false);
            Camera authoredCam = authoredCamGo.AddComponent<Camera>();
            authoredCam.enabled = false;
            GameObject canvasGo = CreateCanvas(
                "CameraCanvas",
                RenderMode.ScreenSpaceCamera,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.worldCamera = authoredCam;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.planeDistance = 1f;
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo);
            Assert.IsNotNull(scaler, "CanvasScaler is required for camera parity prefab.");
            AddLeftHalfImage(canvasGo, Color.green);
            return SavePrefab(root, file);
        }

        private string CreateWorldPrefab(string file)
        {
            GameObject root = new GameObject("ResolutionWorldRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "WorldCanvas",
                RenderMode.WorldSpace,
                root.transform
            );
            RectTransform canvasRect = canvasGo.transform as RectTransform;
            Assert.IsNotNull(canvasRect, "World canvas needs a RectTransform.");
            canvasRect.sizeDelta = new Vector2(2f, 2f);
            AddFullImage(canvasGo, Color.red);
            return SavePrefab(root, file);
        }

        private static GameObject CreateCanvas(string name, RenderMode mode, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = mode;
            return go;
        }

        private static void ZeroCanvasAuthoredSize(GameObject canvasGo)
        {
            RectTransform rect = canvasGo.transform as RectTransform;
            Assert.IsNotNull(rect, "Canvas needs a RectTransform.");
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
        }

        private static Component AddScaleWithScreenSizeScaler(GameObject canvasGo)
        {
            Type scalerType = FindScalerType();
            if (scalerType == null)
            {
                return null;
            }
            Component scaler = canvasGo.AddComponent(scalerType);
            PropertyInfo modeProperty = scalerType.GetProperty("uiScaleMode");
            PropertyInfo refProperty = scalerType.GetProperty("referenceResolution");
            if (modeProperty != null && modeProperty.CanWrite)
            {
                Type modeType = modeProperty.PropertyType;
                object scaleMode = Enum.ToObject(modeType, 1);
                modeProperty.SetValue(scaler, scaleMode, null);
            }
            if (refProperty != null && refProperty.CanWrite)
            {
                refProperty.SetValue(scaler, new Vector2(800f, 600f), null);
            }
            return scaler;
        }

        private static bool ReadScalerEnabled(Canvas canvas)
        {
            if (canvas == null)
            {
                return false;
            }
            Type scalerType = FindScalerType();
            if (scalerType == null)
            {
                return false;
            }
            Component scaler = canvas.GetComponent(scalerType);
            if (scaler == null)
            {
                return false;
            }
            Behaviour behaviour = scaler as Behaviour;
            if (behaviour == null)
            {
                return false;
            }
            return behaviour.enabled;
        }

        private static Type FindScalerType()
        {
            Type direct = null;
            try
            {
                direct = Type.GetType("UnityEngine.UI.CanvasScaler, UnityEngine.UI");
            }
            catch (Exception)
            {
                direct = null;
            }
            if (direct != null)
            {
                return direct;
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = null;
                try
                {
                    candidate = assembly.GetType("UnityEngine.UI.CanvasScaler");
                }
                catch (Exception)
                {
                    continue;
                }
                if (candidate != null)
                {
                    return candidate;
                }
            }
            return null;
        }

        private static GameObject AddFullImage(GameObject canvasGo, Color color)
        {
            GameObject go = new GameObject("Img", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            AddImage(go, color);
            StretchFull(go.transform as RectTransform);
            return go;
        }

        private static GameObject AddLeftHalfImage(GameObject canvasGo, Color color)
        {
            GameObject go = new GameObject("Img", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            AddImage(go, color);
            RectTransform rect = go.transform as RectTransform;
            Assert.IsNotNull(rect, "Image needs a RectTransform.");
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return go;
        }

        private static Component AddImage(GameObject go, Color color)
        {
            Type imageType = FindUiType("UnityEngine.UI.Image");
            Assert.IsNotNull(imageType, "UnityEngine.UI.Image is required for parity pixel tests.");
            Component image = go.AddComponent(imageType);
            PropertyInfo colorProperty = imageType.GetProperty("color");
            Assert.IsNotNull(colorProperty, "Image.color property is required.");
            colorProperty.SetValue(image, color, null);
            return image;
        }

        private static void StretchFull(RectTransform rect)
        {
            Assert.IsNotNull(rect, "RectTransform is required.");
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private string SavePrefab(GameObject root, string file)
        {
            string path = "Assets/" + file;
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Assert.IsNotNull(saved, "Prefab asset must save: " + path);
            _tempAssets.Add(path);
            return path;
        }

        private static Color32[] DecodePngPixels(byte[] png)
        {
            Assert.IsNotNull(png, "PNG bytes must exist.");
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(texture.LoadImage(png), "PNG bytes must decode.");
                return texture.GetPixels32();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static float CoverageRatio(Color32[] pixels, Color clear)
        {
            int clearR = Mathf.RoundToInt(clear.r * 255f);
            int clearG = Mathf.RoundToInt(clear.g * 255f);
            int clearB = Mathf.RoundToInt(clear.b * 255f);
            int count = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                if (
                    Mathf.Abs(pixel.r - clearR) > 12
                    || Mathf.Abs(pixel.g - clearG) > 12
                    || Mathf.Abs(pixel.b - clearB) > 12
                )
                {
                    count++;
                }
            }
            return count / (float)pixels.Length;
        }

        private static Type FindUiType(string fullName)
        {
            Type direct = null;
            try
            {
                direct = Type.GetType(fullName + ", UnityEngine.UI");
            }
            catch (Exception)
            {
                direct = null;
            }
            if (direct != null)
            {
                return direct;
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = null;
                try
                {
                    candidate = assembly.GetType(fullName);
                }
                catch (Exception)
                {
                    continue;
                }
                if (candidate != null)
                {
                    return candidate;
                }
            }
            return null;
        }

        #endregion
    }
}
