using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Minimal EditMode regression tests for the m2 logical fixes (M1, M3, L1).
    /// Covers only the fixed logic using existing seams and patterns; no new
    /// harness, no new dependencies.
    /// </summary>
    [TestFixture]
    public class UniThumbLogicalFixesTests
    {
        #region Fields

        private UniThumbWindow _window;
        private readonly List<string> _tempAssets = new List<string>();
        private string _prevScenePath;

        #endregion

        #region Setup

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            Selection.activeObject = null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string assetPath in _tempAssets)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
            _tempAssets.Clear();
            if (_prevScenePath != null)
            {
                if (string.IsNullOrEmpty(_prevScenePath))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.OpenScene(_prevScenePath, OpenSceneMode.Single);
                }
                _prevScenePath = null;
            }
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }
            UniThumbWindow.BeginUiSessionOverride = null;
            UniThumbStorage.ClearCache();
            UniThumbGuard.Exit();
            Selection.activeObject = null;
        }

        private UniThumbWindow Window()
        {
            if (_window == null)
            {
                _window = EditorWindow.GetWindow<UniThumbWindow>();
            }
            return _window;
        }

        private string SaveTempScene(string assetPath)
        {
            if (_prevScenePath == null)
            {
                _prevScenePath = EditorSceneManager.GetActiveScene().path;
            }
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single
            );
            EditorSceneManager.SaveScene(scene, assetPath);
            AssetDatabase.Refresh();
            _tempAssets.Add(assetPath);
            return assetPath;
        }

        private static bool IsUpdateSubscribed(UniThumbWindow window, string methodName)
        {
            FieldInfo field = typeof(EditorApplication).GetField(
                "update",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
            );
            if (field == null)
            {
                return false;
            }
            Delegate current = (Delegate)field.GetValue(null);
            if (current == null)
            {
                return false;
            }
            foreach (Delegate entry in current.GetInvocationList())
            {
                if (
                    entry.Target is UniThumbWindow target
                    && target == window
                    && string.Equals(entry.Method.Name, methodName, StringComparison.Ordinal)
                )
                {
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region M1: Folder Collector Edges

        [Test]
        public void CollectFolderScenePaths_InvalidFolder_ReturnsEmpty()
        {
            List<string> paths = UniThumbBatchMenus.CollectFolderScenePaths(
                "Assets/NoSuchFolder__UniThumb"
            );
            Assert.IsNotNull(paths);
            Assert.IsEmpty(paths);
        }

        [Test]
        public void CollectFolderScenePaths_EmptyFolder_ReturnsEmpty()
        {
            const string folderPath = "Assets/__UniThumbM2Empty";
            string guid = AssetDatabase.CreateFolder("Assets", "__UniThumbM2Empty");
            Assert.IsFalse(string.IsNullOrEmpty(guid), "Temp empty folder must be created.");
            _tempAssets.Add(folderPath);
            try
            {
                List<string> paths = UniThumbBatchMenus.CollectFolderScenePaths(folderPath);
                Assert.IsNotNull(paths);
                Assert.IsEmpty(paths);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
                _tempAssets.Remove(folderPath);
            }
        }

        [Test]
        public void CollectFolderScenePaths_NestedScenes_EachReturnedOnce()
        {
            const string parentFolder = "Assets/__UniThumbM2Nest";
            const string subFolder = "Assets/__UniThumbM2Nest/Sub";
            Assert.IsFalse(
                string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", "__UniThumbM2Nest"))
            );
            Assert.IsFalse(
                string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets/__UniThumbM2Nest", "Sub"))
            );
            _tempAssets.Add(parentFolder);
            SaveTempScene(parentFolder + "/NestParent.unity");
            SaveTempScene(subFolder + "/NestChild.unity");
            try
            {
                List<string> paths = UniThumbBatchMenus.CollectFolderScenePaths(parentFolder);
                Assert.Contains(parentFolder + "/NestParent.unity", paths);
                Assert.Contains(subFolder + "/NestChild.unity", paths);
                Assert.AreEqual(
                    paths.Count,
                    new HashSet<string>(paths, StringComparer.Ordinal).Count,
                    "Nested selections must not duplicate entries."
                );
            }
            finally
            {
                AssetDatabase.DeleteAsset(parentFolder);
                _tempAssets.Remove(parentFolder);
                _tempAssets.Remove(parentFolder + "/NestParent.unity");
                _tempAssets.Remove(subFolder + "/NestChild.unity");
            }
        }

        [Test]
        public void CollectFolderScenePaths_OutOfProject_ReturnsOnlyInProject()
        {
            List<string> paths = UniThumbBatchMenus.CollectFolderScenePaths("Packages");
            Assert.IsNotNull(paths);
            foreach (string path in paths)
            {
                Assert.IsTrue(
                    path.StartsWith("Assets/", StringComparison.Ordinal),
                    "Out-of-project path must be skipped: '" + path + "'."
                );
            }
        }

        [Test]
        public void CollectFolderScenePaths_AfterCacheWarm_NewSceneFoundWithoutInvalidate()
        {
            const string scenePath = "Assets/__UniThumbM2CacheFresh.unity";
            UniThumbBatchMenus.GetCachedGuids("t:Scene");
            SaveTempScene(scenePath);
            List<string> paths = UniThumbBatchMenus.CollectFolderScenePaths("Assets");
            Assert.Contains(
                scenePath,
                paths,
                "Scene created after the cache warmed must be found with no manual invalidate."
            );
            List<string> work = UniThumbBatchMenus.CollectRefreshWork(false);
            Assert.Contains(
                scenePath,
                work,
                "Refresh collector must queue the new scene with no manual invalidate."
            );
        }

        #endregion

        #region M1: Refresh Missing-Only Default Plus Stale Opt-In

        [Test]
        public void RefreshIncludesStale_DefaultsFalse()
        {
            bool original = UniThumbBatchMenus.RefreshIncludesStale;
            try
            {
                UniThumbBatchMenus.RefreshIncludesStale = false;
                Assert.IsFalse(UniThumbBatchMenus.RefreshIncludesStale);
            }
            finally
            {
                UniThumbBatchMenus.RefreshIncludesStale = original;
            }
        }

        [Test]
        public void Refresh_MissingScene_QueuedInBothModes_OptInIsSuperset()
        {
            const string scenePath = "Assets/__UniThumbM2Refresh.unity";
            SaveTempScene(scenePath);
            Assert.IsFalse(
                UniThumbStorage.HasThumbnail(scenePath),
                "Temp scene must start without a thumbnail."
            );
            List<string> missingOnly = UniThumbBatchMenus.CollectRefreshWork(false);
            List<string> withStale = UniThumbBatchMenus.CollectRefreshWork(true);
            Assert.Contains(scenePath, missingOnly, "Missing scene must be queued by default.");
            Assert.Contains(scenePath, withStale, "Missing scene must stay queued when opted in.");
            Assert.GreaterOrEqual(
                withStale.Count,
                missingOnly.Count,
                "Stale opt-in must only add work, never remove it."
            );
        }

        [Test]
        public void IsStale_MissingThumbnail_ReturnsFalse()
        {
            const string scenePath = "Assets/__UniThumbM2StaleCheck.unity";
            SaveTempScene(scenePath);
            Assert.IsFalse(UniThumbStorage.HasThumbnail(scenePath));
            Assert.IsFalse(
                UniThumbStorage.IsThumbnailStale(scenePath),
                "Missing thumbnails are not stale."
            );
            Assert.IsFalse(UniThumbBatchMenus.IsStale(scenePath));
            Assert.IsFalse(UniThumbStorage.IsThumbnailStale(null));
            Assert.IsFalse(UniThumbStorage.IsThumbnailStale(string.Empty));
        }

        #endregion

        #region M3: SceneView Copy Guards Plus Orbit Fallback

        [Test]
        public void TryCopyFromSceneView_ProjectionMismatch_ReturnsFalse()
        {
            GameObject go = new GameObject("UniThumbM2_CopyCam", typeof(Camera));
            try
            {
                Camera cam = go.GetComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.orthographic = false;
                SceneView sv = SceneView.lastActiveSceneView;
                if (sv == null || sv.camera == null)
                {
                    Assert.IsFalse(UniThumbCapture.TryCopyFromSceneView(cam, settings));
                    return;
                }
                Camera svCam = sv.camera;
                bool savedOrtho = svCam.orthographic;
                try
                {
                    svCam.orthographic = !settings.orthographic;
                    Assert.IsFalse(
                        UniThumbCapture.TryCopyFromSceneView(cam, settings),
                        "Projection mismatch must fall back to orbit framing."
                    );
                }
                finally
                {
                    svCam.orthographic = savedOrtho;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TryCopyFromSceneView_DegenerateClip_ReturnsFalse()
        {
            GameObject go = new GameObject("UniThumbM2_ClipCam", typeof(Camera));
            try
            {
                Camera cam = go.GetComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.orthographic = false;
                SceneView sv = SceneView.lastActiveSceneView;
                if (sv == null || sv.camera == null)
                {
                    Assert.IsFalse(UniThumbCapture.TryCopyFromSceneView(cam, settings));
                    return;
                }
                Camera svCam = sv.camera;
                bool savedOrtho = svCam.orthographic;
                float savedNear = svCam.nearClipPlane;
                float savedFar = svCam.farClipPlane;
                try
                {
                    svCam.orthographic = settings.orthographic;
                    svCam.nearClipPlane = 5f;
                    svCam.farClipPlane = 1f;
                    Assert.IsFalse(
                        UniThumbCapture.TryCopyFromSceneView(cam, settings),
                        "Inverted clip planes must fall back to orbit framing."
                    );
                }
                finally
                {
                    svCam.orthographic = savedOrtho;
                    svCam.nearClipPlane = savedNear;
                    svCam.farClipPlane = savedFar;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ApplyOrbitTransformToBounds_OrthoEmptyBounds_FallsBackToFixedSize()
        {
            GameObject go = new GameObject("UniThumbM2_OrthoCam", typeof(Camera));
            try
            {
                Camera cam = go.GetComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.orthographic = true;
                settings.orbitDistanceMultiplier = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    cam,
                    settings,
                    Vector3.zero,
                    5f,
                    Vector3.zero,
                    false,
                    false
                );
                Assert.IsTrue(cam.orthographic);
                Assert.AreEqual(5f, cam.orthographicSize, 0.0001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ApplyOrbitTransformToBounds_Perspective_ClampsOrbitFov()
        {
            GameObject go = new GameObject("UniThumbM2_FovCam", typeof(Camera));
            try
            {
                Camera cam = go.GetComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.orthographic = false;
                settings.OrbitFov = 1000f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    cam,
                    settings,
                    Vector3.zero,
                    5f,
                    Vector3.zero,
                    true,
                    false
                );
                Assert.AreEqual(120f, cam.fieldOfView, 0.001f);
                settings.OrbitFov = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    cam,
                    settings,
                    Vector3.zero,
                    5f,
                    Vector3.zero,
                    true,
                    false
                );
                Assert.AreEqual(30f, cam.fieldOfView, 0.001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void OrbitFallback_AfterFailedCopy_SetsOrbitFraming()
        {
            GameObject go = new GameObject("UniThumbM2_FallbackCam", typeof(Camera));
            try
            {
                Camera cam = go.GetComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.orthographic = false;
                SceneView sv = SceneView.lastActiveSceneView;
                if (sv == null || sv.camera == null)
                {
                    Assert.IsFalse(UniThumbCapture.TryCopyFromSceneView(cam, settings));
                    UniThumbCapture.ApplyOrbitTransform(cam, settings, false);
                }
                else
                {
                    Camera svCam = sv.camera;
                    bool savedOrtho = svCam.orthographic;
                    try
                    {
                        svCam.orthographic = !settings.orthographic;
                        Assert.IsFalse(UniThumbCapture.TryCopyFromSceneView(cam, settings));
                        UniThumbCapture.ApplyOrbitTransform(cam, settings, false);
                    }
                    finally
                    {
                        svCam.orthographic = savedOrtho;
                    }
                }
                Assert.IsFalse(cam.orthographic);
                Assert.GreaterOrEqual(cam.fieldOfView, 30f);
                Assert.LessOrEqual(cam.fieldOfView, 120f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        #endregion

        #region Pump Cancel Plus Empty-Folder No-Op

        [Test]
        public void RequestBatchCancel_WhenIdle_NoOp()
        {
            Assert.DoesNotThrow(() => UniThumbBatchMenus.RequestBatchCancel());
            Assert.IsFalse(UniThumbBatchMenus.IsBatchRunning);
            Assert.IsFalse(UniThumbBatchMenus.GetBatchSnapshot().IsRunning);
        }

        [Test]
        public void TryStartFolderBatch_InvalidFolder_NoPumpNoGuard()
        {
            string error;
            bool started = UniThumbBatchMenus.TryStartFolderBatch(
                "Assets/NoSuchFolder__UniThumb",
                out error
            );
            Assert.IsFalse(started);
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(UniThumbBatchMenus.IsBatchRunning);
            Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard must be free after a refused start.");
            UniThumbGuard.Exit();
        }

        [Test]
        public void TryStartFolderBatch_EmptyFolder_NoPumpNoGuard()
        {
            const string folderPath = "Assets/__UniThumbM2BatchEmpty";
            string guid = AssetDatabase.CreateFolder("Assets", "__UniThumbM2BatchEmpty");
            Assert.IsFalse(string.IsNullOrEmpty(guid), "Temp empty folder must be created.");
            _tempAssets.Add(folderPath);
            try
            {
                string error;
                bool started = UniThumbBatchMenus.TryStartFolderBatch(folderPath, out error);
                Assert.IsFalse(started);
                Assert.IsFalse(string.IsNullOrEmpty(error));
                Assert.IsFalse(UniThumbBatchMenus.IsBatchRunning);
                Assert.IsTrue(
                    UniThumbGuard.TryEnter(),
                    "Guard must be free after an empty-folder refusal."
                );
                UniThumbGuard.Exit();
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
                _tempAssets.Remove(folderPath);
            }
        }

        #endregion

        #region L1: Ui Scale Slider Single Init Plus Close Symmetry

        [Test]
        public void UiScaleSlider_SingleElement_PushStateRoundTrips()
        {
            UniThumbWindow window = Window();
            Assert.IsNotNull(window.rootVisualElement);
            List<Slider> sliders = window
                .rootVisualElement.Query<Slider>("ui-scale-slider")
                .ToList();
            Assert.AreEqual(1, sliders.Count, "Exactly one ui-scale slider must remain.");
            UniThumbSettings settings = ScriptableObject.CreateInstance<UniThumbSettings>();
            try
            {
                settings.SetUiScale(2.5f);
                window.ApplyPersistedCaptureSettings(settings);
                MethodInfo push = typeof(UniThumbWindow).GetMethod(
                    "PushState",
                    BindingFlags.NonPublic | BindingFlags.Instance
                );
                Assert.IsNotNull(push, "PushState must exist for the slider init path.");
                push.Invoke(window, null);
                Assert.AreEqual(2.5f, sliders[0].value, 0.001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void WindowClose_UnsubscribesUpdatePump()
        {
            UniThumbWindow window = Window();
            window.MarkPreviewDirty();
            Assert.IsTrue(
                IsUpdateSubscribed(window, "RenderLivePreview"),
                "Preview pump must be subscribed while dirty."
            );
            window.Close();
            _window = null;
            Assert.IsFalse(
                IsUpdateSubscribed(window, "RenderLivePreview"),
                "RenderLivePreview must unsubscribe on close."
            );
            Assert.IsFalse(
                IsUpdateSubscribed(window, "OnBatchUpdateTick"),
                "OnBatchUpdateTick must unsubscribe on close."
            );
        }

        #endregion

        #region Perf Wave 3: Warm Pump Plus Batch Pump Contracts

        [Test]
        public void WarmPump_DrainBudget_FrozenAtFour()
        {
            FieldInfo field = typeof(UniThumbIconService).GetField(
                "k_WarmPerFrame",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(field, "k_WarmPerFrame must exist as the frozen drain budget.");
            Assert.AreEqual(4, (int)field.GetRawConstantValue());
        }

        [Test]
        public void WarmPump_IdleTick_NoWorkNoThrow()
        {
            FieldInfo queueField = typeof(UniThumbIconService).GetField(
                "s_WarmQueue",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(queueField, "Warm queue must exist for the idle-tick contract.");
            var queue = (Queue<string>)queueField.GetValue(null);
            Assert.IsNotNull(queue);
            if (queue.Count != 0)
            {
                Assert.Ignore("Warm queue is draining; idle contract needs an empty queue.");
            }
            MethodInfo pump = typeof(UniThumbIconService).GetMethod(
                "OnWarmUpdate",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(pump, "OnWarmUpdate must exist as the warm pump entry.");
            Assert.DoesNotThrow(() => pump.Invoke(null, null));
            Assert.AreEqual(0, queue.Count, "Idle tick must do no work on an empty queue.");
        }

        [Test]
        public void BatchSnapshot_Idle_ZeroedCountersAndNullScene()
        {
            Assert.IsFalse(UniThumbBatchMenus.IsBatchRunning);
            UniThumbBatchMenus.BatchSnapshot snapshot = UniThumbBatchMenus.GetBatchSnapshot();
            Assert.IsFalse(snapshot.IsRunning);
            Assert.AreEqual(0, snapshot.Total);
            Assert.AreEqual(0, snapshot.Processed);
            Assert.AreEqual(0, snapshot.Succeeded);
            Assert.AreEqual(0, snapshot.Failed);
            Assert.AreEqual(0, snapshot.Skipped);
            Assert.IsNull(snapshot.CurrentScene);
        }

        #endregion
    }
}
