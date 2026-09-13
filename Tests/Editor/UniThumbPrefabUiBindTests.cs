using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Prefab canvas binding tests (wave 2 bind): prefab-subtree scoped
    /// ScreenSpaceCamera rebind plus Overlay retarget with finally restore,
    /// temp-camera layer-mask expansion, and throw-safety. Written first
    /// (Red): BeginPrefab does not exist pre-fix.
    /// </summary>
    [TestFixture]
    public class UniThumbPrefabUiBindTests
    {
        private GameObject _prefabRoot;
        private GameObject _sceneGo;
        private GameObject _camGo;
        private int _undoGroup;
        private bool _previousRecordUndo;

        [SetUp]
        public void SetUp()
        {
            _previousRecordUndo = UniThumbCapture.RecordUndo;
            UniThumbCapture.RecordUndo = true;
            Undo.IncrementCurrentGroup();
            _undoGroup = Undo.GetCurrentGroup();
            UniThumbCapture.UiCaptureSession.ThrowInPrefabSwitchForTest = false;
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbCapture.UiCaptureSession.ThrowInPrefabSwitchForTest = false;
            UniThumbCapture.RecordUndo = _previousRecordUndo;
            Undo.RevertAllDownToGroup(_undoGroup);
            if (_camGo != null)
            {
                Object.DestroyImmediate(_camGo);
                _camGo = null;
            }
            if (_sceneGo != null)
            {
                Object.DestroyImmediate(_sceneGo);
                _sceneGo = null;
            }
            if (_prefabRoot != null)
            {
                Object.DestroyImmediate(_prefabRoot);
                _prefabRoot = null;
            }
        }

        [Test]
        public void BeginPrefab_ScreenSpaceCamera_RebindsAndRestores()
        {
            _prefabRoot = new GameObject("BindPrefabRoot");
            GameObject canvasGo = new GameObject("BindCameraCanvas");
            canvasGo.transform.SetParent(_prefabRoot.transform, false);
            GameObject authoredCamGo = new GameObject("BindAuthoredCamera");
            authoredCamGo.transform.SetParent(_prefabRoot.transform, false);
            Camera authoredCam = authoredCamGo.AddComponent<Camera>();
            authoredCam.enabled = false;
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            // Authored with a live camera: Unity coerces ScreenSpaceCamera
            // canvases with a null worldCamera back to Overlay (native
            // validation, verified by probe), so null cannot author the
            // Camera branch through the public API. A live authored camera
            // is also the real prefab case (scene-camera reference).
            canvas.worldCamera = authoredCam;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.planeDistance = 1.5f;
            float planeDistance = canvas.planeDistance;
            _camGo = new GameObject("BindTempCamera");
            Camera cam = _camGo.AddComponent<Camera>();
            cam.cullingMask = 0;

            UniThumbCapture.UiCaptureSession session = UniThumbCapture.UiCaptureSession.BeginPrefab(
                _prefabRoot,
                cam
            );
            try
            {
                Assert.AreEqual(RenderMode.ScreenSpaceCamera, canvas.renderMode);
                Assert.AreEqual(cam, canvas.worldCamera);
                Assert.AreEqual(planeDistance, canvas.planeDistance);
                Assert.AreNotEqual(0, cam.cullingMask & (1 << canvasGo.layer));
            }
            finally
            {
                session.Dispose();
            }
            Assert.AreEqual(authoredCam, canvas.worldCamera);
            Assert.AreEqual(RenderMode.ScreenSpaceCamera, canvas.renderMode);
            Assert.AreEqual(planeDistance, canvas.planeDistance);
        }

        [Test]
        public void BeginPrefab_Overlay_RetargetsScopedAndRestores()
        {
            _prefabRoot = new GameObject("BindPrefabRoot");
            GameObject prefabCanvasGo = new GameObject("BindPrefabOverlay");
            prefabCanvasGo.transform.SetParent(_prefabRoot.transform, false);
            Canvas prefabCanvas = prefabCanvasGo.AddComponent<Canvas>();
            prefabCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            prefabCanvas.worldCamera = null;

            _sceneGo = new GameObject("BindSceneOverlay");
            Canvas sceneCanvas = _sceneGo.AddComponent<Canvas>();
            sceneCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            sceneCanvas.worldCamera = null;

            _camGo = new GameObject("BindTempCamera");
            Camera cam = _camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.3f;
            cam.cullingMask = -1;

            UniThumbCapture.UiCaptureSession session = UniThumbCapture.UiCaptureSession.BeginPrefab(
                _prefabRoot,
                cam
            );
            try
            {
                Assert.AreEqual(RenderMode.ScreenSpaceCamera, prefabCanvas.renderMode);
                Assert.AreEqual(cam, prefabCanvas.worldCamera);
                Assert.AreEqual(
                    Mathf.Max(cam.nearClipPlane + 0.1f, 0.1f),
                    prefabCanvas.planeDistance
                );
                Assert.AreEqual(RenderMode.ScreenSpaceOverlay, sceneCanvas.renderMode);
                Assert.IsNull(sceneCanvas.worldCamera);
            }
            finally
            {
                session.Dispose();
            }
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, prefabCanvas.renderMode);
            Assert.IsNull(prefabCanvas.worldCamera);
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, sceneCanvas.renderMode);
        }

        [Test]
        public void BeginPrefab_LayerMask_ExpandsTempCameraMask()
        {
            _prefabRoot = new GameObject("BindPrefabRoot");
            GameObject canvasGo = new GameObject("BindLayerCanvas");
            canvasGo.transform.SetParent(_prefabRoot.transform, false);
            canvasGo.layer = 10;
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            _camGo = new GameObject("BindTempCamera");
            Camera cam = _camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << 0;

            UniThumbCapture.UiCaptureSession session = UniThumbCapture.UiCaptureSession.BeginPrefab(
                _prefabRoot,
                cam
            );
            try
            {
                Assert.AreNotEqual(0, cam.cullingMask & (1 << 10));
                Assert.AreEqual(RenderMode.ScreenSpaceCamera, canvas.renderMode);
            }
            finally
            {
                session.Dispose();
            }
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
            Assert.AreEqual(10, canvasGo.layer);
        }

        [Test]
        public void BeginPrefab_Throw_RestoresAllState()
        {
            _prefabRoot = new GameObject("BindPrefabRoot");
            GameObject overlayGo = new GameObject("BindThrowOverlay");
            overlayGo.transform.SetParent(_prefabRoot.transform, false);
            Canvas overlay = overlayGo.AddComponent<Canvas>();
            overlay.renderMode = RenderMode.ScreenSpaceOverlay;

            GameObject cameraGo = new GameObject("BindThrowCamera");
            cameraGo.transform.SetParent(_prefabRoot.transform, false);
            GameObject authoredCamGo = new GameObject("BindThrowAuthoredCamera");
            authoredCamGo.transform.SetParent(_prefabRoot.transform, false);
            Camera authoredCam = authoredCamGo.AddComponent<Camera>();
            authoredCam.enabled = false;
            Canvas cameraCanvas = cameraGo.AddComponent<Canvas>();
            cameraCanvas.worldCamera = authoredCam;
            cameraCanvas.renderMode = RenderMode.ScreenSpaceCamera;

            _camGo = new GameObject("BindTempCamera");
            Camera cam = _camGo.AddComponent<Camera>();

            UniThumbCapture.UiCaptureSession.ThrowInPrefabSwitchForTest = true;
            try
            {
                Assert.Throws<System.InvalidOperationException>(() =>
                {
                    UniThumbCapture.UiCaptureSession.BeginPrefab(_prefabRoot, cam).Dispose();
                });
            }
            finally
            {
                UniThumbCapture.UiCaptureSession.ThrowInPrefabSwitchForTest = false;
            }
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, overlay.renderMode);
            Assert.IsNull(overlay.worldCamera);
            Assert.AreEqual(RenderMode.ScreenSpaceCamera, cameraCanvas.renderMode);
            Assert.AreEqual(authoredCam, cameraCanvas.worldCamera);
        }

        [Test]
        public void BeginPrefab_NullArgs_NoOp()
        {
            _camGo = new GameObject("BindTempCamera");
            Camera cam = _camGo.AddComponent<Camera>();
            Assert.DoesNotThrow(() =>
            {
                UniThumbCapture.UiCaptureSession nullRoot =
                    UniThumbCapture.UiCaptureSession.BeginPrefab(null, cam);
                nullRoot.Dispose();
            });
            _prefabRoot = new GameObject("BindPrefabRoot");
            Assert.DoesNotThrow(() =>
            {
                UniThumbCapture.UiCaptureSession nullCam =
                    UniThumbCapture.UiCaptureSession.BeginPrefab(_prefabRoot, null);
                nullCam.Dispose();
            });
        }
    }
}
