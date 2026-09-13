using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Prefab UI regression suite (wave 4 final): World, Camera, and Overlay
    /// prefab UI pixel cases, nested-prefab child UI, TextMeshProUGUI subtype,
    /// isolation, restore, guard, Undo, toggle-off parity, default-on behavior,
    /// and unchanged pins (scene path, particles/VFX, intensity/aim, Refresh-All).
    /// Tests only; production stays untouched. Graphic types resolve by
    /// reflection so the test assembly needs no UnityEngine.UI reference and
    /// the TMP case degrades to Ignore when TMP or its font is absent.
    /// </summary>
    [TestFixture]
    public class UniThumbPrefabUiTests
    {
        private const int k_PixelSize = 64;

        private readonly List<string> _tempAssets = new List<string>();
        private readonly List<GameObject> _tempObjects = new List<GameObject>();

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

        [Test]
        public void WorldSpace_Bounds_IncludeUiGeometry()
        {
            GameObject root = new GameObject("UiBoundsWorldRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "WorldCanvas",
                RenderMode.WorldSpace,
                root.transform
            );
            RectTransform canvasRect = (RectTransform)canvasGo.transform;
            canvasRect.sizeDelta = new Vector2(2f, 2f);
            AddFullImage(canvasGo, Color.red);

            bool result = UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds);
            Assert.IsTrue(result, "World Space UI must expand prefab framing bounds.");
            Assert.Greater(bounds.extents.sqrMagnitude, 0.001f);
        }

        [Test]
        public void WorldSpace_Capture_RendersUiPixels_WhenOn()
        {
            string assetPath = CreateWorldUiPrefab();
            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            int lit = CountDifferingPixels(
                pixels,
                UniThumbCapture.EffectiveClearColor(UiPixelSettings(true))
            );
            Assert.Greater(
                lit,
                pixels.Length / 50,
                "World Space UI must contribute lit pixels when on."
            );
        }

        [Test]
        public void ScreenSpaceCamera_Capture_RendersUiPixels_WhenOn()
        {
            string assetPath = CreateCameraUiPrefab();
            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            int lit = CountDifferingPixels(
                pixels,
                UniThumbCapture.EffectiveClearColor(UiPixelSettings(true))
            );
            Assert.Greater(lit, pixels.Length / 50, "ScreenSpaceCamera UI must render when on.");
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(asset, "Prefab asset must survive capture.");
            Canvas assetCanvas = asset.GetComponentInChildren<Canvas>();
            Assert.IsNotNull(assetCanvas, "Prefab asset canvas must survive capture.");
            Assert.AreEqual(RenderMode.ScreenSpaceCamera, assetCanvas.renderMode);
            Assert.AreEqual(1f, assetCanvas.planeDistance);
            Camera assetCamera = asset.GetComponentInChildren<Camera>();
            Assert.IsNotNull(assetCamera, "Prefab asset camera must survive capture.");
            Assert.AreEqual(assetCamera, assetCanvas.worldCamera);
        }

        [Test]
        public void Overlay_Capture_RendersUiPixels_WhenOn()
        {
            string assetPath = CreateOverlayUiPrefab(
                "__UniThumbUiWave4_Overlay.prefab",
                Color.white
            );
            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            int lit = CountDifferingPixels(
                pixels,
                UniThumbCapture.EffectiveClearColor(UiPixelSettings(true))
            );
            Assert.Greater(lit, pixels.Length / 50, "Overlay UI must render when on.");
        }

        [Test]
        public void DefaultOn_PrefabUiRenders_WithDefaultSettings()
        {
            CaptureSettings defaults = UniThumbCapture.CreateDefaultSettings();
            Assert.IsTrue(
                defaults.CaptureUi,
                "Prefab UI renders by default (documented behavior change)."
            );
            string assetPath = CreateOverlayUiPrefab(
                "__UniThumbUiWave4_DefaultOn.prefab",
                Color.white
            );
            defaults.Width = k_PixelSize;
            defaults.Height = k_PixelSize;
            defaults.BackgroundMode = BackgroundMode.SolidColor;
            defaults.WantPostProcessing = false;
            defaults.UseLightingOverride = false;
            defaults.Light2DMode = LightingMode.None;
            Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
            CaptureResult result;
            try
            {
                result = UniThumbCapture.CapturePrefab(assetPath, defaults);
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            int lit = CountDifferingPixels(pixels, UniThumbCapture.EffectiveClearColor(defaults));
            Assert.Greater(lit, pixels.Length / 50, "Default settings must render prefab UI.");
        }

        [Test]
        public void NestedPrefab_ChildUi_RendersPixels_WhenOn()
        {
            string childPath = CreateOverlayUiPrefab(
                "__UniThumbUiWave4_NestedChild.prefab",
                Color.white
            );
            GameObject parentRoot = new GameObject("NestedUiParent");
            _tempObjects.Add(parentRoot);
            GameObject childAsset = AssetDatabase.LoadAssetAtPath<GameObject>(childPath);
            Assert.IsNotNull(childAsset, "Child prefab asset must load.");
            GameObject childInstance = PrefabUtility.InstantiatePrefab(childAsset) as GameObject;
            Assert.IsNotNull(childInstance, "Nested child instance must instantiate.");
            childInstance.transform.SetParent(parentRoot.transform, false);
            string parentPath = SavePrefab(parentRoot, "__UniThumbUiWave4_NestedParent.prefab");

            CaptureResult result = CaptureUiPrefab(parentPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            int lit = CountDifferingPixels(
                pixels,
                UniThumbCapture.EffectiveClearColor(UiPixelSettings(true))
            );
            Assert.Greater(lit, pixels.Length / 50, "Nested-prefab child UI must render when on.");
        }

        [Test]
        public void GraphicSubtype_Text_CapturesAndRestores()
        {
            Type textType = FindUiType("UnityEngine.UI.Text");
            Assert.IsNotNull(textType, "UnityEngine.UI.Text is required for this test.");
            GameObject root = new GameObject("TextUiRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "TextCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            GameObject textGo = new GameObject("Txt", typeof(RectTransform));
            textGo.transform.SetParent(canvasGo.transform, false);
            Component text = textGo.AddComponent(textType);
            PropertyInfo textProperty = textType.GetProperty("text");
            Assert.IsNotNull(textProperty, "Text.text property is required.");
            textProperty.SetValue(text, "Text-UI", null);
            StretchFull((RectTransform)textGo.transform);
            string assetPath = SavePrefab(root, "__UniThumbUiWave4_Text.prefab");

            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Assert.IsNotNull(DecodePngPixels(result.PngBytes));

            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(asset, "Prefab asset must survive capture.");
            Canvas assetCanvas = asset.GetComponentInChildren<Canvas>();
            Assert.IsNotNull(assetCanvas, "Prefab asset canvas must survive capture.");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, assetCanvas.renderMode);
            Assert.IsNull(assetCanvas.worldCamera);
        }

        [Test]
        public void TmpGraphic_RendersPixels_WhenOn()
        {
            Type tmpType = FindUiType("TMPro.TextMeshProUGUI");
            if (tmpType == null)
            {
                Assert.Ignore("TMP package absent; TextMeshProUGUI type unavailable.");
            }
            object fontAsset = ResolveTmpFont();
            UnityEngine.Object fontObject = fontAsset as UnityEngine.Object;
            if (fontObject == null)
            {
                Assert.Ignore("TMP font asset unavailable in this project.");
            }
            GameObject root = new GameObject("TmpUiRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "TmpCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            GameObject tmpGo = new GameObject("Tmp", typeof(RectTransform));
            tmpGo.transform.SetParent(canvasGo.transform, false);
            Component tmp = tmpGo.AddComponent(tmpType);
            SetProperty(
                tmpType,
                tmp,
                "fontAsset",
                fontAsset,
                "TMP fontAsset property is required."
            );
            SetProperty(tmpType, tmp, "text", "TMP-UI", "TMP text property is required.");
            SetProperty(tmpType, tmp, "fontSize", 48f, "TMP fontSize property is required.");
            SetProperty(tmpType, tmp, "color", Color.white, "TMP color property is required.");
            StretchFull((RectTransform)tmpGo.transform);
            string assetPath = SavePrefab(root, "__UniThumbUiWave4_Tmp.prefab");

            CaptureResult on = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(on.Success, "Capture failed: " + on.Warning);
            CaptureResult off = CaptureUiPrefab(assetPath, false);
            Assert.IsFalse(
                off.Success,
                "UI-only prefab with CaptureUi off renders the bare background, "
                    + "which capture reports as UniformBackground failure by design."
            );
            CaptureSettings settings = UiPixelSettings(true);
            int onLit = CountDifferingPixels(
                DecodePngPixels(on.PngBytes),
                UniThumbCapture.EffectiveClearColor(settings)
            );
            int offLit = CountDifferingPixels(
                DecodePngPixels(off.PngBytes),
                UniThumbCapture.EffectiveClearColor(settings)
            );
            Assert.Greater(onLit, offLit, "TMP must contribute pixels when on versus off.");
            Assert.Greater(onLit, 20, "TMP glyphs must produce a non-trivial pixel count when on.");
        }

        [Test]
        public void ToggleOff_SuppressesCameraAndOverlay_MatchesBaseline()
        {
            string overlayUiPath = CreateCubeWithOverlayPrefab(
                "__UniThumbUiWave4_ToggleOverlay.prefab"
            );
            string cameraUiPath = CreateCubeWithCameraPrefab(
                "__UniThumbUiWave4_ToggleCamera.prefab"
            );
            string basePath = CreateCubeOnlyPrefab("__UniThumbUiWave4_ToggleBase.prefab");
            CaptureSettings settings = UiPixelSettings(true);
            Color clear = UniThumbCapture.EffectiveClearColor(settings);

            CaptureResult overlayOn = CaptureUiPrefab(overlayUiPath, true);
            CaptureResult overlayOff = CaptureUiPrefab(overlayUiPath, false);
            CaptureResult baseline = CaptureUiPrefab(basePath, false);
            Assert.IsTrue(overlayOn.Success, "Capture failed: " + overlayOn.Warning);
            Assert.IsTrue(overlayOff.Success, "Capture failed: " + overlayOff.Warning);
            Assert.IsTrue(baseline.Success, "Capture failed: " + baseline.Warning);
            Color32[] overlayOnPixels = DecodePngPixels(overlayOn.PngBytes);
            Color32[] overlayOffPixels = DecodePngPixels(overlayOff.PngBytes);
            Color32[] basePixels = DecodePngPixels(baseline.PngBytes);
            int overlayOnLit = CountDifferingPixels(overlayOnPixels, clear);
            int overlayOffLit = CountDifferingPixels(overlayOffPixels, clear);
            int baseLit = CountDifferingPixels(basePixels, clear);
            Assert.Greater(overlayOnLit, overlayOffLit, "Overlay on must exceed off.");
            Assert.AreEqual(baseLit, overlayOffLit, "Overlay off must reproduce baseline counts.");
            AssertPixelsEqual(
                basePixels,
                overlayOffPixels,
                "Overlay off must match baseline pixels."
            );

            // Camera UI expands framing by design (wave-2 bounds include
            // Camera-space rects once laid out), so off framing differs from
            // the cube-only baseline: parity here means suppression (cube
            // visible, zero green UI pixels), not pixel equality. Exact pixel
            // parity applies to Overlay only (no world extents).
            CaptureResult cameraOn = CaptureUiPrefab(cameraUiPath, true);
            CaptureResult cameraOff = CaptureUiPrefab(cameraUiPath, false);
            Assert.IsTrue(cameraOn.Success, "Capture failed: " + cameraOn.Warning);
            Assert.IsTrue(cameraOff.Success, "Capture failed: " + cameraOff.Warning);
            Color32[] cameraOnPixels = DecodePngPixels(cameraOn.PngBytes);
            Color32[] cameraOffPixels = DecodePngPixels(cameraOff.PngBytes);
            int cameraOnLit = CountDifferingPixels(cameraOnPixels, clear);
            int cameraOffLit = CountDifferingPixels(cameraOffPixels, clear);
            Assert.Greater(cameraOnLit, cameraOffLit, "Camera UI on must exceed off.");
            Assert.Greater(
                cameraOffLit,
                cameraOffPixels.Length / 50,
                "Cube must stay visible when camera UI is suppressed."
            );
            Assert.Greater(
                CountGreenPixels(cameraOnPixels),
                0,
                "Camera UI must contribute green pixels when on."
            );
            Assert.AreEqual(
                0,
                CountGreenPixels(cameraOffPixels),
                "Camera UI must contribute no green pixels when off."
            );
        }

        [Test]
        public void ToggleOff_WorldSpace_Parity_RendersBothWays()
        {
            string assetPath = CreateWorldUiPrefab("__UniThumbUiWave4_WorldParity.prefab");
            CaptureSettings settings = UiPixelSettings(true);
            Color clear = UniThumbCapture.EffectiveClearColor(settings);
            CaptureResult on = CaptureUiPrefab(assetPath, true);
            CaptureResult off = CaptureUiPrefab(assetPath, false);
            Assert.IsTrue(on.Success, "Capture failed: " + on.Warning);
            Assert.IsTrue(off.Success, "Capture failed: " + off.Warning);
            int onLit = CountDifferingPixels(DecodePngPixels(on.PngBytes), clear);
            int offLit = CountDifferingPixels(DecodePngPixels(off.PngBytes), clear);
            Assert.Greater(
                onLit,
                DecodePngPixels(on.PngBytes).Length / 50,
                "World UI renders when on."
            );
            Assert.Greater(
                offLit,
                DecodePngPixels(off.PngBytes).Length / 50,
                "World UI renders when off too (pre-wave parity: world path never culled)."
            );
        }

        [Test]
        public void Isolation_SceneUi_CulledAndRestored()
        {
            GameObject sceneRoot = new GameObject("Wave4SceneUi");
            _tempObjects.Add(sceneRoot);
            GameObject sceneCanvasGo = CreateCanvas(
                "SceneOverlay",
                RenderMode.ScreenSpaceOverlay,
                sceneRoot.transform
            );
            Canvas sceneCanvas = sceneCanvasGo.GetComponent<Canvas>();
            AddFullImage(sceneCanvasGo, Color.white);
            CanvasRenderer sceneRenderer = sceneCanvasGo.GetComponentInChildren<CanvasRenderer>();
            Assert.IsNotNull(sceneRenderer, "Scene UI needs a CanvasRenderer for the cull check.");

            GameObject instanceRoot = new GameObject("Wave4InstanceUi");
            _tempObjects.Add(instanceRoot);
            GameObject instanceCanvasGo = CreateCanvas(
                "InstanceOverlay",
                RenderMode.ScreenSpaceOverlay,
                instanceRoot.transform
            );
            Canvas instanceCanvas = instanceCanvasGo.GetComponent<Canvas>();
            AddFullImage(instanceCanvasGo, Color.red);
            CanvasRenderer instanceRenderer =
                instanceCanvasGo.GetComponentInChildren<CanvasRenderer>();
            Assert.IsNotNull(
                instanceRenderer,
                "Instance UI needs a CanvasRenderer for the cull check."
            );

            GameObject camGo = new GameObject("Wave4TempCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();
            cam.cullingMask = -1;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            List<CanvasRenderer> isolated = null;
            UniThumbCapture.UiCaptureSession session = null;
            try
            {
                isolated = UniThumbCapture.IsolateInstanceCanvasRenderers(instanceRoot);
                session = UniThumbCapture.UiCaptureSession.BeginPrefab(instanceRoot, cam);
                Assert.IsTrue(
                    sceneRenderer.cull,
                    "Scene UI must stay culled during prefab capture."
                );
                Assert.IsFalse(instanceRenderer.cull, "Instance UI must keep rendering.");
                Assert.AreEqual(RenderMode.ScreenSpaceOverlay, sceneCanvas.renderMode);
                Assert.IsNull(sceneCanvas.worldCamera);
                Assert.AreEqual(RenderMode.ScreenSpaceCamera, instanceCanvas.renderMode);
                Assert.AreEqual(cam, instanceCanvas.worldCamera);
            }
            finally
            {
                if (session != null)
                {
                    session.Dispose();
                }
                UniThumbCapture.RestoreIsolatedCanvasRenderers(isolated);
                Undo.RevertAllDownToGroup(undoGroup);
            }
            Assert.IsFalse(sceneRenderer.cull, "Scene cull flag must restore.");
            Assert.IsFalse(instanceRenderer.cull, "Instance cull flag must restore.");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, sceneCanvas.renderMode);
            Assert.IsNull(sceneCanvas.worldCamera);
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, instanceCanvas.renderMode);
            Assert.IsNull(instanceCanvas.worldCamera);
        }

        [Test]
        public void Restore_WorldCameraAndRenderMode_Restored()
        {
            GameObject root = new GameObject("Wave4RestoreRoot");
            _tempObjects.Add(root);
            GameObject authoredCamGo = new GameObject("Wave4AuthoredCamera");
            _tempObjects.Add(authoredCamGo);
            Camera authoredCam = authoredCamGo.AddComponent<Camera>();
            GameObject cameraCanvasGo = CreateCanvas(
                "RestoreCamera",
                RenderMode.ScreenSpaceCamera,
                root.transform
            );
            Canvas cameraCanvas = cameraCanvasGo.GetComponent<Canvas>();
            cameraCanvas.worldCamera = authoredCam;
            cameraCanvas.planeDistance = 1.5f;
            GameObject overlayCanvasGo = CreateCanvas(
                "RestoreOverlay",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            Canvas overlayCanvas = overlayCanvasGo.GetComponent<Canvas>();
            float overlayPlane = overlayCanvas.planeDistance;

            GameObject tempCamGo = new GameObject("Wave4CaptureCamera");
            _tempObjects.Add(tempCamGo);
            Camera tempCam = tempCamGo.AddComponent<Camera>();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            UniThumbCapture.UiCaptureSession session = UniThumbCapture.UiCaptureSession.BeginPrefab(
                root,
                tempCam
            );
            try
            {
                Assert.AreEqual(tempCam, cameraCanvas.worldCamera);
                Assert.AreEqual(tempCam, overlayCanvas.worldCamera);
                Assert.AreEqual(RenderMode.ScreenSpaceCamera, overlayCanvas.renderMode);
            }
            finally
            {
                session.Dispose();
                Undo.RevertAllDownToGroup(undoGroup);
            }
            Assert.AreEqual(authoredCam, cameraCanvas.worldCamera);
            Assert.AreEqual(RenderMode.ScreenSpaceCamera, cameraCanvas.renderMode);
            Assert.AreEqual(1.5f, cameraCanvas.planeDistance);
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, overlayCanvas.renderMode);
            Assert.IsNull(overlayCanvas.worldCamera);
            Assert.AreEqual(overlayPlane, overlayCanvas.planeDistance);
        }

        [Test]
        public void Guard_ReleasedAndSceneUntouched_AfterCapture()
        {
            string assetPath = CreateOverlayUiPrefab("__UniThumbUiWave4_Guard.prefab", Color.white);
            int rootsBefore = SceneManager.GetActiveScene().GetRootGameObjects().Length;
            string scenePathBefore = SceneManager.GetActiveScene().path;
            Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
            try
            {
                Assert.IsTrue(UniThumbGuard.IsGenerating, "Guard must hold during the flow.");
                CaptureSettings settings = UiPixelSettings(true);
                CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                Assert.AreEqual(
                    rootsBefore,
                    SceneManager.GetActiveScene().GetRootGameObjects().Length,
                    "Capture must not leave scene objects behind."
                );
                Assert.AreEqual(
                    scenePathBefore,
                    SceneManager.GetActiveScene().path,
                    "Capture must not switch scenes."
                );
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            Assert.IsFalse(UniThumbGuard.IsGenerating, "Guard must release in finally.");
            Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard must be re-enterable after exit.");
            UniThumbGuard.Exit();
        }

        [Test]
        public void Undo_NoSceneDirtying_AfterCapture()
        {
            GameObject sceneRoot = new GameObject("Wave4UndoSceneUi");
            _tempObjects.Add(sceneRoot);
            GameObject sceneCamGo = new GameObject("Wave4UndoSceneCamera");
            _tempObjects.Add(sceneCamGo);
            Camera sceneCam = sceneCamGo.AddComponent<Camera>();
            sceneCam.enabled = false;
            GameObject sceneCanvasGo = CreateCanvas(
                "UndoSceneCamera",
                RenderMode.ScreenSpaceCamera,
                sceneRoot.transform
            );
            Canvas sceneCanvas = sceneCanvasGo.GetComponent<Canvas>();
            sceneCanvas.worldCamera = sceneCam;
            sceneCanvas.planeDistance = 2f;
            bool wasDirty = EditorSceneManager.GetActiveScene().isDirty;

            string assetPath = CreateOverlayUiPrefab("__UniThumbUiWave4_Undo.prefab", Color.white);
            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);

            Assert.AreEqual(sceneCam, sceneCanvas.worldCamera);
            Assert.AreEqual(RenderMode.ScreenSpaceCamera, sceneCanvas.renderMode);
            Assert.AreEqual(2f, sceneCanvas.planeDistance);
            Assert.AreEqual(
                wasDirty,
                EditorSceneManager.GetActiveScene().isDirty,
                "Capture must not dirty the scene."
            );
        }

        [Test]
        public void ScenePath_CaptureUnchanged_ToggleOnAndOff()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Wave4SceneCube";
            try
            {
                CaptureSettings on = UiPixelSettings(true);
                on.UseSceneViewAngle = false;
                CaptureSettings off = UiPixelSettings(false);
                off.UseSceneViewAngle = false;
                CaptureResult resultOn = UniThumbCapture.Capture(on);
                CaptureResult resultOff = UniThumbCapture.Capture(off);
                Assert.IsTrue(resultOn.Success, "Scene capture failed: " + resultOn.Warning);
                Assert.IsTrue(resultOff.Success, "Scene capture failed: " + resultOff.Warning);
                Color32[] onPixels = DecodePngPixels(resultOn.PngBytes);
                Color32[] offPixels = DecodePngPixels(resultOff.PngBytes);
                int onLit = CountDifferingPixels(onPixels, UniThumbCapture.EffectiveClearColor(on));
                int offLit = CountDifferingPixels(
                    offPixels,
                    UniThumbCapture.EffectiveClearColor(off)
                );
                Assert.AreEqual(
                    onLit,
                    offLit,
                    "UI-less scene capture must agree with toggle on and off."
                );
                AssertPixelsEqual(
                    onPixels,
                    offPixels,
                    "Scene path output must not depend on the UI toggle."
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void Pins_BoundsAndSimulate_Unchanged()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _tempObjects.Add(cube);
            Assert.IsTrue(UniThumbCapture.TryGetPrefabBounds(cube, -1, out Bounds bounds));
            Assert.Greater(bounds.extents.sqrMagnitude, 0f);
            Assert.IsFalse(UniThumbCapture.TryGetPrefabBounds(null, -1, out Bounds ignored));

            GameObject masked = new GameObject("Wave4MaskedUi");
            _tempObjects.Add(masked);
            GameObject maskedCanvasGo = CreateCanvas(
                "MaskedCanvas",
                RenderMode.WorldSpace,
                masked.transform
            );
            RectTransform maskedRect = (RectTransform)maskedCanvasGo.transform;
            maskedRect.sizeDelta = new Vector2(2f, 2f);
            AddFullImage(maskedCanvasGo, Color.red);
            SetLayerRecursively(masked, 10);
            Assert.IsFalse(
                UniThumbCapture.TryGetPrefabBounds(masked, 1 << 0, out Bounds maskedBounds),
                "Layer-excluded UI must not expand bounds."
            );

            GameObject overlayOnly = new GameObject("Wave4OverlayOnly");
            _tempObjects.Add(overlayOnly);
            GameObject overlayGo = CreateCanvas(
                "OverlayOnly",
                RenderMode.ScreenSpaceOverlay,
                overlayOnly.transform
            );
            RectTransform overlayRect = (RectTransform)overlayGo.transform;
            overlayRect.sizeDelta = new Vector2(4f, 4f);
            AddFullImage(overlayGo, Color.white);
            Assert.IsFalse(
                UniThumbCapture.TryGetPrefabBounds(overlayOnly, -1, out Bounds overlayBounds),
                "ScreenSpaceOverlay has no world extents and stays out of bounds."
            );

            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(null));
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(null));
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Light2DMode = LightingMode.None;
            Assert.IsTrue(UniThumbCapture.PrefabLightingNeedsNeutralize(settings));
            settings.Light2DMode = LightingMode.Light2D;
            Assert.IsFalse(UniThumbCapture.PrefabLightingNeedsNeutralize(settings));
            Assert.Greater(
                settings.Light3DPrefabIntensity,
                0f,
                "Prefab intensity default must stay positive."
            );
            Assert.GreaterOrEqual(settings.Light3DYaw, settings.Light3DYawMin);
            Assert.LessOrEqual(settings.Light3DYaw, settings.Light3DYawMax);
            Assert.GreaterOrEqual(settings.Light3DPitch, settings.Light3DPitchMin);
            Assert.LessOrEqual(settings.Light3DPitch, settings.Light3DPitchMax);
        }

        [Test]
        public void Pins_RefreshAll_ExistenceSemantics_Unchanged()
        {
            List<string> missing = UniThumbBatchMenus.CollectFolderScenePaths(
                "Assets/__NoSuchFolder__"
            );
            Assert.IsNotNull(missing, "Folder collector must stay null-safe.");
            Assert.AreEqual(0, missing.Count, "Unknown folder must collect nothing.");
            Assert.IsFalse(
                UniThumbStorage.HasThumbnail("Assets/__NoSuchScene__.unity"),
                "Refresh-All existence check must report missing thumbnails as missing."
            );
        }

        [Test]
        public void Preview_ResolvedStyle_SpotLogged()
        {
            UniThumbWindow window = EditorWindow.GetWindow<UniThumbWindow>();
            try
            {
                Assert.IsNotNull(window.rootVisualElement, "Window root must exist.");
                VisualElement wrap = window.rootVisualElement.Q<VisualElement>("preview-wrap");
                Assert.IsNotNull(wrap, "preview-wrap must exist.");
                VisualElement box = window.rootVisualElement.Q<VisualElement>("preview-box");
                Assert.IsNotNull(box, "preview-box must exist.");
                Image image = window.rootVisualElement.Q<Image>("preview-image");
                Assert.IsNotNull(image, "preview-image must exist.");
                Assert.IsTrue(
                    wrap.ClassListContains("stt-preview-wrap"),
                    "preview-wrap keeps its USS class."
                );
                Assert.IsTrue(
                    box.ClassListContains("stt-preview-box"),
                    "preview-box keeps its USS class."
                );
                UnityEngine.UIElements.IResolvedStyle resolved = wrap.resolvedStyle;
                Debug.Log(
                    "[UniThumb] Preview spot: display="
                        + resolved.display
                        + " flexGrow="
                        + resolved.flexGrow
                        + " width="
                        + resolved.width
                        + " height="
                        + resolved.height
                );
            }
            finally
            {
                window.Close();
            }
        }

        private string CreateWorldUiPrefab()
        {
            return CreateWorldUiPrefab("__UniThumbUiWave4_World.prefab");
        }

        private string CreateWorldUiPrefab(string file)
        {
            GameObject root = new GameObject("WorldUiRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "WorldCanvas",
                RenderMode.WorldSpace,
                root.transform
            );
            RectTransform canvasRect = (RectTransform)canvasGo.transform;
            canvasRect.sizeDelta = new Vector2(2f, 2f);
            AddFullImage(canvasGo, Color.red);
            return SavePrefab(root, file);
        }

        private string CreateCameraUiPrefab()
        {
            return CreateCameraUiPrefab("__UniThumbUiWave4_Camera.prefab");
        }

        private string CreateCameraUiPrefab(string file)
        {
            GameObject root = new GameObject("CameraUiRoot");
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
            // Genuine Camera branch: Unity coerces null-camera canvases to
            // Overlay, so the rebind path needs a live authored camera (the
            // real prefab case is a scene-camera reference).
            canvas.worldCamera = authoredCam;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.planeDistance = 1f;
            AddLeftHalfImage(canvasGo, Color.green);
            return SavePrefab(root, file);
        }

        private string CreateOverlayUiPrefab(string file, Color color)
        {
            GameObject root = new GameObject("OverlayUiRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "OverlayCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            AddLeftHalfImage(canvasGo, color);
            return SavePrefab(root, file);
        }

        private string CreateCubeWithOverlayPrefab(string file)
        {
            GameObject root = new GameObject("CubeOverlayRoot");
            _tempObjects.Add(root);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            GameObject canvasGo = CreateCanvas(
                "OverlayCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            AddLeftHalfImage(canvasGo, Color.white);
            return SavePrefab(root, file);
        }

        private string CreateCubeWithCameraPrefab(string file)
        {
            GameObject root = new GameObject("CubeCameraRoot");
            _tempObjects.Add(root);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
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
            AddLeftHalfImage(canvasGo, Color.green);
            return SavePrefab(root, file);
        }

        private string CreateCubeOnlyPrefab(string file)
        {
            GameObject root = new GameObject("CubeOnlyRoot");
            _tempObjects.Add(root);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            return SavePrefab(root, file);
        }

        private static void ZeroCanvasAuthoredSize(GameObject canvasGo)
        {
            RectTransform rect = (RectTransform)canvasGo.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
        }

        private static int CountGreenPixels(Color32[] pixels)
        {
            int count = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                if (pixel.g - pixel.r > 60 && pixel.g - pixel.b > 60)
                {
                    count++;
                }
            }
            return count;
        }

        private static void AssertPixelsEqual(Color32[] expected, Color32[] actual, string message)
        {
            Assert.AreEqual(expected.Length, actual.Length, message + " (length)");
            int differing = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                if (!expected[i].Equals(actual[i]))
                {
                    differing++;
                }
            }
            Assert.AreEqual(0, differing, message + " (differing pixels: " + differing + ")");
        }

        private static GameObject CreateCanvas(string name, RenderMode mode, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = mode;
            return go;
        }

        private static GameObject AddFullImage(GameObject canvasGo, Color color)
        {
            GameObject go = new GameObject("Img", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            AddImage(go, color);
            StretchFull((RectTransform)go.transform);
            return go;
        }

        private static GameObject AddLeftHalfImage(GameObject canvasGo, Color color)
        {
            GameObject go = new GameObject("Img", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            AddImage(go, color);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return go;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null)
            {
                return;
            }
            root.layer = layer;
            Transform parent = root.transform;
            for (int i = 0; i < parent.childCount; i++)
            {
                SetLayerRecursively(parent.GetChild(i).gameObject, layer);
            }
        }

        private static Component AddImage(GameObject go, Color color)
        {
            Type imageType = FindUiType("UnityEngine.UI.Image");
            Assert.IsNotNull(
                imageType,
                "UnityEngine.UI.Image is required for prefab UI pixel tests."
            );
            Component image = go.AddComponent(imageType);
            PropertyInfo colorProperty = imageType.GetProperty("color");
            Assert.IsNotNull(colorProperty, "Image.color property is required.");
            colorProperty.SetValue(image, color, null);
            return image;
        }

        private static void StretchFull(RectTransform rect)
        {
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

        private static CaptureResult CaptureUiPrefab(string assetPath, bool captureUi)
        {
            Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
            try
            {
                return UniThumbCapture.CapturePrefab(assetPath, UiPixelSettings(captureUi));
            }
            finally
            {
                UniThumbGuard.Exit();
            }
        }

        private static CaptureSettings UiPixelSettings(bool captureUi)
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Width = k_PixelSize;
            settings.Height = k_PixelSize;
            settings.BackgroundMode = BackgroundMode.SolidColor;
            settings.WantPostProcessing = false;
            settings.CaptureUi = captureUi;
            settings.UseLightingOverride = false;
            settings.Light2DMode = LightingMode.None;
            return settings;
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

        private static int CountDifferingPixels(Color32[] pixels, Color clear)
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
            return count;
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
            try
            {
                direct = Type.GetType(fullName + ", Unity.TextMeshPro");
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

        private static object ResolveTmpFont()
        {
            Type settingsType = FindUiType("TMPro.TMP_Settings");
            if (settingsType != null)
            {
                PropertyInfo defaultFont = settingsType.GetProperty(
                    "defaultFontAsset",
                    BindingFlags.Public | BindingFlags.Static
                );
                if (defaultFont != null)
                {
                    object value = null;
                    try
                    {
                        value = defaultFont.GetValue(null, null);
                    }
                    catch (Exception)
                    {
                        value = null;
                    }
                    UnityEngine.Object fontObject = value as UnityEngine.Object;
                    if (fontObject != null)
                    {
                        return value;
                    }
                }
            }
            Type fontType = FindUiType("TMPro.TMP_FontAsset");
            if (fontType != null)
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(fontType);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null)
                        {
                            return (object)all[i];
                        }
                    }
                }
            }
            return null;
        }

        private static void SetProperty(
            Type type,
            Component target,
            string name,
            object value,
            string message
        )
        {
            PropertyInfo property = type.GetProperty(name);
            Assert.IsNotNull(property, message);
            property.SetValue(target, value, null);
        }
    }
}
