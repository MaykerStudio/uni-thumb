using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Overlay-fallback capture suite (plan 20260910-ui-prefab-capturefail):
    /// bounds-less UI prefabs (Overlay has no world extents) capture through
    /// a fixed fallback framing plus the prefab-scoped UI session. Covers
    /// overlay-only success at 128px, a ChallengeRoom-shaped heavy prefab
    /// (30 texts, 43 images, 3 masks, TMP variant when available), the
    /// uniform-with-retargeted-canvas acceptance gate and its genuinely-empty
    /// negative, the 3D-prefab toggle parity pin, and the session switch
    /// counter. Tests only; production stays untouched. Graphic types resolve
    /// by reflection so the test assembly needs no UnityEngine.UI reference.
    /// </summary>
    [TestFixture]
    public class UniThumbOverlayFallbackTests
    {
        private const int k_PixelSize = 128;

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
        public void OverlayOnly_Capture_Succeeds_At128()
        {
            string assetPath = CreateOverlayUiPrefab("__UniThumbFallback_Overlay.prefab");
            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            Assert.AreEqual(k_PixelSize * k_PixelSize, pixels.Length);
            int lit = CountDifferingPixels(
                pixels,
                UniThumbCapture.EffectiveClearColor(UiPixelSettings(true))
            );
            Assert.Greater(
                lit,
                pixels.Length / 50,
                "Overlay-only UI must contribute pixels at 128."
            );
        }

        [Test]
        public void OverlayOnly_HeavyUi_Capture_Succeeds_At128()
        {
            string assetPath = CreateHeavyUiPrefab("__UniThumbFallback_Heavy.prefab");
            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            int lit = CountDifferingPixels(
                pixels,
                UniThumbCapture.EffectiveClearColor(UiPixelSettings(true))
            );
            Assert.Greater(
                lit,
                100,
                "Heavy UI (30 texts, 43 images, 3 masks) must rasterize at 128."
            );
        }

        [Test]
        public void OverlayOnly_HeavyTmpUi_Capture_Succeeds_At128()
        {
            Type tmpType = FindUiType("TMPro.TextMeshProUGUI");
            if (tmpType == null)
            {
                Assert.Ignore("TMP package absent; TextMeshProUGUI type unavailable.");
            }
            object fontAsset = ResolveTmpFont();
            if (!(fontAsset is UnityEngine.Object))
            {
                Assert.Ignore("TMP font asset unavailable in this project.");
            }
            string assetPath = CreateHeavyTmpUiPrefab(
                "__UniThumbFallback_HeavyTmp.prefab",
                tmpType,
                fontAsset
            );
            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
            Color32[] pixels = DecodePngPixels(result.PngBytes);
            int lit = CountDifferingPixels(
                pixels,
                UniThumbCapture.EffectiveClearColor(UiPixelSettings(true))
            );
            Assert.Greater(lit, 100, "Heavy TMP UI must rasterize at 128.");
        }

        [Test]
        public void OverlayFallbackFraming_OnlyWhenScreenCanvas()
        {
            GameObject overlayRoot = new GameObject("FallbackOverlayRoot");
            _tempObjects.Add(overlayRoot);
            CreateCanvas("OverlayCanvas", RenderMode.ScreenSpaceOverlay, overlayRoot.transform);
            Assert.IsFalse(
                UniThumbCapture.TryGetPrefabBounds(overlayRoot, -1, out Bounds ignored),
                "Overlay-only has no world bounds."
            );
            Assert.IsTrue(
                UniThumbCapture.PrefabHasScreenSpaceCanvas(overlayRoot),
                "Overlay canvas must be detected."
            );
            Assert.IsTrue(
                UniThumbCapture.GetOverlayFallbackFraming(overlayRoot).HasValue,
                "Overlay-only prefab must get fallback framing."
            );

            GameObject cubeRoot = new GameObject("FallbackCubeRoot");
            _tempObjects.Add(cubeRoot);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(cubeRoot.transform, false);
            Assert.IsTrue(UniThumbCapture.TryGetPrefabBounds(cubeRoot, -1, out Bounds bounds));
            Assert.Greater(bounds.extents.sqrMagnitude, 0f);

            GameObject emptyRoot = new GameObject("FallbackEmptyRoot");
            _tempObjects.Add(emptyRoot);
            Assert.IsFalse(
                UniThumbCapture.TryGetPrefabBounds(emptyRoot, -1, out Bounds emptyBounds)
            );
            Assert.IsFalse(
                UniThumbCapture.PrefabHasScreenSpaceCanvas(emptyRoot),
                "Canvas-less prefab must report no screen canvas."
            );
            Assert.IsFalse(
                UniThumbCapture.GetOverlayFallbackFraming(emptyRoot).HasValue,
                "Canvas-less prefab must keep null framing."
            );
            Assert.IsFalse(
                UniThumbCapture.GetOverlayFallbackFraming(null).HasValue,
                "Null root must keep null framing."
            );
        }

        [Test]
        public void EmptyCanvas_UniformRender_AcceptedAsSuccess()
        {
            GameObject root = new GameObject("FallbackEmptyCanvasRoot");
            _tempObjects.Add(root);
            CreateCanvas("EmptyCanvas", RenderMode.ScreenSpaceOverlay, root.transform);
            string assetPath = SavePrefab(root, "__UniThumbFallback_EmptyCanvas.prefab");

            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsTrue(
                result.Success,
                "Uniform frame with a retargeted canvas must be accepted: " + result.Warning
            );
            Assert.IsNotNull(result.PngBytes, "Accepted capture must carry PNG bytes.");
        }

        [Test]
        public void EmptyPrefab_WithoutCanvas_Capture_Fails()
        {
            GameObject root = new GameObject("FallbackEmptyPrefabRoot");
            _tempObjects.Add(root);
            string assetPath = SavePrefab(root, "__UniThumbFallback_Empty.prefab");

            CaptureResult result = CaptureUiPrefab(assetPath, true);
            Assert.IsFalse(
                result.Success,
                "Genuinely-empty prefab (no retargeted canvas) must still fail."
            );
        }

        [Test]
        public void CubePrefab_CaptureUi_Toggle_Parity()
        {
            string assetPath = CreateCubeOnlyPrefab("__UniThumbFallback_Cube.prefab");
            CaptureResult on = CaptureUiPrefab(assetPath, true);
            CaptureResult off = CaptureUiPrefab(assetPath, false);
            Assert.IsTrue(on.Success, "Capture failed: " + on.Warning);
            Assert.IsTrue(off.Success, "Capture failed: " + off.Warning);
            Color32[] onPixels = DecodePngPixels(on.PngBytes);
            Color32[] offPixels = DecodePngPixels(off.PngBytes);
            Assert.AreEqual(onPixels.Length, offPixels.Length, "Pixel counts must match.");
            int differing = 0;
            for (int i = 0; i < onPixels.Length; i++)
            {
                if (!onPixels[i].Equals(offPixels[i]))
                {
                    differing++;
                }
            }
            Assert.AreEqual(0, differing, "3D prefab pixels must not depend on the UI toggle.");
        }

        [Test]
        public void SwitchedCount_TracksTouchedCanvases()
        {
            GameObject root = new GameObject("FallbackCountRoot");
            _tempObjects.Add(root);
            CreateCanvas("CountA", RenderMode.ScreenSpaceOverlay, root.transform);
            CreateCanvas("CountB", RenderMode.ScreenSpaceOverlay, root.transform);
            GameObject camGo = new GameObject("FallbackCountCamera");
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
                Assert.AreEqual(2, session.SwitchedCount, "Both overlay canvases must count.");
            }
            finally
            {
                session.Dispose();
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        private string CreateOverlayUiPrefab(string file)
        {
            GameObject root = new GameObject("FallbackOverlayRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "OverlayCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            AddLeftHalfImage(canvasGo, Color.white);
            return SavePrefab(root, file);
        }

        private string CreateHeavyUiPrefab(string file)
        {
            GameObject root = new GameObject("FallbackHeavyRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "HeavyCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            AddScaleWithScreenSizeScaler(canvasGo, new Vector2(1920f, 1080f));
            Type textType = FindUiType("UnityEngine.UI.Text");
            Assert.IsNotNull(textType, "UnityEngine.UI.Text is required for the heavy prefab.");
            for (int i = 0; i < 30; i++)
            {
                GameObject textGo = new GameObject("Txt" + i, typeof(RectTransform));
                textGo.transform.SetParent(canvasGo.transform, false);
                Component text = textGo.AddComponent(textType);
                PropertyInfo textProperty = textType.GetProperty("text");
                Assert.IsNotNull(textProperty, "Text.text property is required.");
                textProperty.SetValue(text, "Label " + i, null);
                RectTransform rect = (RectTransform)textGo.transform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.offsetMin = new Vector2(10f, -30f * (i + 1));
                rect.offsetMax = new Vector2(-10f, -30f * i);
            }
            for (int i = 0; i < 43; i++)
            {
                GameObject imageGo = new GameObject("Img" + i, typeof(RectTransform));
                imageGo.transform.SetParent(canvasGo.transform, false);
                AddImage(imageGo, Color.white);
                RectTransform rect = (RectTransform)imageGo.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            Type maskType = FindUiType("UnityEngine.UI.Mask");
            Assert.IsNotNull(maskType, "UnityEngine.UI.Mask is required for the heavy prefab.");
            for (int i = 0; i < 3; i++)
            {
                GameObject maskGo = new GameObject("Mask" + i, typeof(RectTransform));
                maskGo.transform.SetParent(canvasGo.transform, false);
                maskGo.AddComponent(maskType);
                StretchFull((RectTransform)maskGo.transform);
            }
            return SavePrefab(root, file);
        }

        private string CreateHeavyTmpUiPrefab(string file, Type tmpType, object fontAsset)
        {
            GameObject root = new GameObject("FallbackHeavyTmpRoot");
            _tempObjects.Add(root);
            GameObject canvasGo = CreateCanvas(
                "HeavyTmpCanvas",
                RenderMode.ScreenSpaceOverlay,
                root.transform
            );
            ZeroCanvasAuthoredSize(canvasGo);
            AddScaleWithScreenSizeScaler(canvasGo, new Vector2(1920f, 1080f));
            for (int i = 0; i < 30; i++)
            {
                GameObject tmpGo = new GameObject("Tmp" + i, typeof(RectTransform));
                tmpGo.transform.SetParent(canvasGo.transform, false);
                Component tmp = tmpGo.AddComponent(tmpType);
                SetProperty(tmpType, tmp, "fontAsset", fontAsset, "TMP fontAsset is required.");
                SetProperty(tmpType, tmp, "text", "TMP label " + i, "TMP text is required.");
                SetProperty(tmpType, tmp, "fontSize", 48f, "TMP fontSize is required.");
                SetProperty(tmpType, tmp, "color", Color.white, "TMP color is required.");
                RectTransform rect = (RectTransform)tmpGo.transform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.offsetMin = new Vector2(10f, -60f * (i + 1));
                rect.offsetMax = new Vector2(-10f, -60f * i);
            }
            for (int i = 0; i < 10; i++)
            {
                GameObject imageGo = new GameObject("Img" + i, typeof(RectTransform));
                imageGo.transform.SetParent(canvasGo.transform, false);
                AddImage(imageGo, Color.white);
                RectTransform rect = (RectTransform)imageGo.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            return SavePrefab(root, file);
        }

        private string CreateCubeOnlyPrefab(string file)
        {
            GameObject root = new GameObject("FallbackCubeRoot");
            _tempObjects.Add(root);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
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
            RectTransform rect = (RectTransform)canvasGo.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
        }

        private static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
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

        private static void AddScaleWithScreenSizeScaler(GameObject canvasGo, Vector2 reference)
        {
            Type scalerType = FindUiType("UnityEngine.UI.CanvasScaler");
            Assert.IsNotNull(scalerType, "CanvasScaler is required.");
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
                    if (value is UnityEngine.Object)
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
