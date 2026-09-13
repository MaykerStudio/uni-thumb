using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Overlay-only screen-space fill suite: an Overlay-only prefab frames
    /// with a fixed ortho fit of the canvas reference rect (not the orbit
    /// distance over synthetic bounds), so the frame fraction matches at
    /// 16 vs 128 vs 512. CanvasScaler/Image resolve by reflection so the
    /// test assembly needs no UnityEngine.UI reference and no asmdef change.
    /// </summary>
    [TestFixture]
    public class UniThumbOverlayScreenSpaceTests
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
                if (go == null)
                {
                    continue;
                }
                UnityEngine.Object.DestroyImmediate(go);
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
        public void IsOverlayOnlyPrefab_OverlayOnly_True_Camera_False()
        {
            GameObject overlayRoot = new GameObject("ScreenSpaceOverlayRoot");
            _tempObjects.Add(overlayRoot);
            CreateCanvas("OverlayCanvas", RenderMode.ScreenSpaceOverlay, overlayRoot.transform);
            Assert.IsTrue(
                UniThumbCapture.IsOverlayOnlyPrefab(overlayRoot, -1),
                "Overlay-only prefab must report overlay-only."
            );

            GameObject cameraRoot = new GameObject("ScreenSpaceCameraRoot");
            _tempObjects.Add(cameraRoot);
            GameObject authoredCamGo = new GameObject("AuthoredCamera");
            authoredCamGo.transform.SetParent(cameraRoot.transform, false);
            Camera authoredCam = authoredCamGo.AddComponent<Camera>();
            authoredCam.enabled = false;
            GameObject cameraCanvasGo = CreateCanvas(
                "CameraCanvas",
                RenderMode.ScreenSpaceCamera,
                cameraRoot.transform
            );
            Canvas cameraCanvas = cameraCanvasGo.GetComponent<Canvas>();
            Assert.IsNotNull(cameraCanvas, "Camera canvas must exist.");
            cameraCanvas.worldCamera = authoredCam;
            Assert.IsFalse(
                UniThumbCapture.IsOverlayOnlyPrefab(cameraRoot, -1),
                "ScreenSpaceCamera prefab must not report overlay-only."
            );

            GameObject cubeRoot = new GameObject("ScreenSpaceCubeRoot");
            _tempObjects.Add(cubeRoot);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(cubeRoot.transform, false);
            Assert.IsFalse(
                UniThumbCapture.IsOverlayOnlyPrefab(cubeRoot, -1),
                "3D prefab must not report overlay-only."
            );
            Assert.IsFalse(
                UniThumbCapture.IsOverlayOnlyPrefab(null, -1),
                "Null root must not report overlay-only."
            );
        }

        [Test]
        public void ApplyOverlayOnlyFraming_FixedOrtho_AcrossResolutions()
        {
            GameObject root = new GameObject("ScreenSpaceFramingRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "FramingOverlay",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo, new Vector2(800f, 600f));
            Assert.IsNotNull(scaler, "CanvasScaler is required for the framing test.");

            GameObject camGo = new GameObject("ScreenSpaceFramingCamera");
            _tempObjects.Add(camGo);
            Camera cam = camGo.AddComponent<Camera>();

            UniThumbCapture.ApplyOverlayOnlyFraming(cam, root, 16, 16);
            Assert.IsTrue(cam.orthographic, "Overlay-only fill must be orthographic.");
            float ortho16 = cam.orthographicSize;
            float near16 = cam.nearClipPlane;
            float far16 = cam.farClipPlane;

            UniThumbCapture.ApplyOverlayOnlyFraming(cam, root, 128, 128);
            float ortho128 = cam.orthographicSize;

            UniThumbCapture.ApplyOverlayOnlyFraming(cam, root, 512, 512);
            float ortho512 = cam.orthographicSize;

            Assert.AreEqual(ortho16, ortho128, 0.001f, "OrthoSize must fix at 16 vs 128.");
            Assert.AreEqual(ortho16, ortho512, 0.001f, "OrthoSize must fix at 16 vs 512.");
            Assert.AreEqual(near16, cam.nearClipPlane, "Near plane must stay fixed.");
            Assert.AreEqual(far16, cam.farClipPlane, "Far plane must stay fixed.");
            Assert.AreEqual(
                UniThumbCapture.k_OverlayOnlyNear,
                cam.nearClipPlane,
                "Near must be the fixed overlay constant."
            );
        }

        [Test]
        public void CapturePrefab_OverlayOnly_EqualFrameFraction_16_128_512()
        {
            string assetPath = CreateFixedOverlayPrefab("__UniThumbScreenSpace_Overlay.prefab");
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(asset, "Prefab asset must load: " + assetPath);
            Assert.IsTrue(
                UniThumbCapture.IsOverlayOnlyPrefab(asset, -1),
                "Fixed overlay prefab must report overlay-only."
            );

            int[] sizes = new int[] { 16, 128, 512 };
            float[] coverages = new float[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                coverages[i] = CaptureCoverageAt(assetPath, sizes[i]);
                Assert.Greater(
                    coverages[i],
                    0.02f,
                    "Overlay-only UI must contribute pixels at "
                        + sizes[i]
                        + " (coverage "
                        + coverages[i]
                        + ")."
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
                "Overlay-only frame fraction must match at 16/128/512 (min "
                    + min
                    + " max "
                    + max
                    + ")."
            );
        }

        #endregion

        #region Private Methods

        private string CreateFixedOverlayPrefab(string file)
        {
            GameObject root = new GameObject("ScreenSpaceFixedRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "FixedOverlay",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            Component scaler = AddScaleWithScreenSizeScaler(canvasGo, new Vector2(800f, 600f));
            Assert.IsNotNull(scaler, "CanvasScaler is required for the parity prefab.");
            GameObject imageGo = new GameObject("FixedImg", typeof(RectTransform));
            imageGo.transform.SetParent(canvasGo.transform, false);
            AddImage(imageGo, Color.white);
            RectTransform rect = imageGo.transform as RectTransform;
            Assert.IsNotNull(rect, "Image needs a RectTransform.");
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(400f, 300f);
            rect.anchoredPosition = Vector2.zero;
            return SavePrefab(root, file);
        }

        private float CaptureCoverageAt(string assetPath, int size)
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Width = size;
            settings.Height = size;
            settings.BackgroundMode = BackgroundMode.SolidColor;
            settings.WantPostProcessing = false;
            settings.CaptureUi = true;
            settings.UseLightingOverride = false;
            settings.Light2DMode = LightingMode.None;
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

        private static Component AddScaleWithScreenSizeScaler(
            GameObject canvasGo,
            Vector2 reference
        )
        {
            Type scalerType = FindUiType("UnityEngine.UI.CanvasScaler");
            if (scalerType == null)
            {
                return null;
            }
            Component scaler = canvasGo.AddComponent(scalerType);
            PropertyInfo modeProperty = scalerType.GetProperty("uiScaleMode");
            PropertyInfo refProperty = scalerType.GetProperty("referenceResolution");
            if (modeProperty != null && modeProperty.CanWrite)
            {
                object scaleMode = Enum.ToObject(modeProperty.PropertyType, 1);
                modeProperty.SetValue(scaler, scaleMode, null);
            }
            if (refProperty != null && refProperty.CanWrite)
            {
                refProperty.SetValue(scaler, reference, null);
            }
            return scaler;
        }

        private static Component AddImage(GameObject go, Color color)
        {
            Type imageType = FindUiType("UnityEngine.UI.Image");
            Assert.IsNotNull(imageType, "UnityEngine.UI.Image is required.");
            Component image = go.AddComponent(imageType);
            PropertyInfo colorProperty = imageType.GetProperty("color");
            Assert.IsNotNull(colorProperty, "Image.color property is required.");
            colorProperty.SetValue(image, color, null);
            return image;
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
