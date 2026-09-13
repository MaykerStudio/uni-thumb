using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbPreviewRefreshTests
    {
        private UniThumbWindow _window;
        private readonly List<string> _tempAssets = new List<string>();

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            UniThumbWindow.PreviewDragProbeOverride = () => false;
            _window = EditorWindow.GetWindow<UniThumbWindow>();
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbWindow.PreviewDragProbeOverride = null;
            UniThumbWindow.GenerateProgressDisplayOverride = null;
            UniThumbWindow.GenerateProgressClearOverride = null;
            Selection.activeObject = null;
            for (int i = _tempAssets.Count - 1; i >= 0; i--)
            {
                AssetDatabase.DeleteAsset(_tempAssets[i]);
            }
            _tempAssets.Clear();
            if (_window != null)
            {
                // Drop any pending preview so asset-cleanup events cannot
                // storm heavy renders on idle ticks between tests.
                _window.CancelPendingPreview();
                _window.Close();
            }
            UniThumbGuard.Exit();
        }

        #region Helpers

        /// <summary>
        /// Checks whether the given window's RenderLivePreview method is
        /// currently subscribed to the static EditorApplication.update event.
        /// Uses reflection on the backing delegate field.
        /// </summary>
        private static bool IsRenderLivePreviewSubscribed(UniThumbWindow window)
        {
            FieldInfo field = typeof(EditorApplication).GetField(
                "update",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
            );
            if (field == null)
            {
                return false;
            }

            Delegate d = (Delegate)field.GetValue(null);
            if (d == null)
            {
                return false;
            }

            MethodInfo target = typeof(UniThumbWindow).GetMethod(
                "RenderLivePreview",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
            if (target == null)
            {
                return false;
            }

            foreach (Delegate entry in d.GetInvocationList())
            {
                if (
                    entry.Target is UniThumbWindow targetWindow
                    && targetWindow == window
                    && entry.Method.Name == "RenderLivePreview"
                )
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Guard-held subscription preservation

        [Test]
        public void PreviewDirty_GuardHeldDuringBatch_KeepsSubscriptionAndDirtyFlag()
        {
            // Batch starts: guard legitimately held across ticks.
            Assert.IsTrue(UniThumbGuard.TryEnter(), "TryEnter should succeed");

            // Background mode change mid-batch.
            _window.MarkPreviewDirty();
            Assert.IsTrue(_window.IsPreviewDirty, "Preview should be dirty after MarkPreviewDirty");

            // Update tick fires while guard held.
            _window.RenderLivePreview();

            // Core regression: _previewDirty stays true and subscription
            // remains active. Pre-fix the subscription was dead here.
            Assert.IsTrue(
                _window.IsPreviewDirty,
                "_previewDirty must stay true while guard is held"
            );
            Assert.IsTrue(
                IsRenderLivePreviewSubscribed(_window),
                "RenderLivePreview must remain subscribed to EditorApplication.update while guard is held"
            );
        }

        [Test]
        public void PreviewDirty_AfterGuardRelease_RendersAndUnsubscribes()
        {
            // Batch starts and a preview is flagged dirty mid-batch.
            Assert.IsTrue(UniThumbGuard.TryEnter(), "TryEnter should succeed");
            _window.MarkPreviewDirty();

            // Tick while guard held -- subscription survives.
            _window.RenderLivePreview();
            Assert.IsTrue(_window.IsPreviewDirty, "Still dirty while guard held");

            // Batch ends.
            UniThumbGuard.Exit();
            Assert.IsFalse(UniThumbGuard.IsGenerating, "Guard should be released");

            // Next update tick: render consumes the dirty flag.
            _window.RenderLivePreview();

            Assert.IsFalse(
                _window.IsPreviewDirty,
                "_previewDirty must be false after render consumed it"
            );
            Assert.IsFalse(
                IsRenderLivePreviewSubscribed(_window),
                "RenderLivePreview must be unsubscribed after rendering"
            );
        }

        [Test]
        public void PreviewDirty_SecondChangeAfterRelease_ReArmsSubscription()
        {
            // Full cycle: dirty -> guard held -> tick -> release -> render
            Assert.IsTrue(UniThumbGuard.TryEnter(), "TryEnter should succeed");
            _window.MarkPreviewDirty();
            _window.RenderLivePreview();
            UniThumbGuard.Exit();
            _window.RenderLivePreview();
            Assert.IsFalse(_window.IsPreviewDirty, "First render consumed");

            // Second background mode change: must re-arm the subscription.
            _window.MarkPreviewDirty();

            Assert.IsTrue(
                _window.IsPreviewDirty,
                "_previewDirty must be true after second MarkPreviewDirty"
            );
            Assert.IsTrue(
                IsRenderLivePreviewSubscribed(_window),
                "RenderLivePreview must be re-subscribed after second MarkPreviewDirty"
            );
        }

        #endregion

        #region Selection equality, debounce, and render skip

        [Test]
        public void SelectionChanged_SameGuid_SetsDirtyWithZeroRenders()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbRefresh_SameGuid.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                // Delta-based: the window instance is shared across tests and
                // idle ticks may render between them.
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(assetPath);
                RenderWhenIdle();
                Assert.IsFalse(_window.IsPreviewDirty, "First render must consume dirty.");
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "First selection must render once."
                );

                _window.OnProjectSelectionChanged();
                Assert.IsTrue(_window.IsPreviewDirty, "Same-GUID selection must still flag dirty.");
                int skipsBefore = _window.PreviewSkipCount;
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Same-GUID selection must run zero heavy renders."
                );
                Assert.AreEqual(skipsBefore + 1, _window.PreviewSkipCount, "Skip must count.");
                Assert.IsFalse(_window.IsPreviewDirty, "Skip must consume dirty.");
                Assert.IsFalse(IsRenderLivePreviewSubscribed(_window), "Skip must unsubscribe.");
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void SelectionChanged_NewGuid_RendersOnce()
        {
            string pathA = SaveTempCubePrefab("__UniThumbRefresh_NewA.prefab");
            string pathB = SaveTempCubePrefab("__UniThumbRefresh_NewB.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(pathA);
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "First selection must render."
                );

                SelectPrefab(pathB);
                Assert.IsTrue(_window.IsPreviewDirty, "New selection must flag dirty.");
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 2,
                    _window.PreviewRenderCount,
                    "New selection must re-render."
                );
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void Burst_MarkPreviewDirty_YieldsSingleHeavyRender()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbRefresh_Burst.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                int skipsBeforeAll = _window.PreviewSkipCount;
                SelectPrefab(assetPath);
                _window.MarkPreviewDirty();
                _window.MarkPreviewDirty();
                _window.MarkPreviewDirty();
                _window.MarkPreviewDirty();
                _window.MarkPreviewDirty();
                Assert.IsTrue(
                    IsRenderLivePreviewSubscribed(_window),
                    "Burst must collapse to one subscription."
                );
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Burst must yield one render."
                );
                Assert.IsFalse(_window.IsPreviewDirty);
                Assert.IsFalse(IsRenderLivePreviewSubscribed(_window));

                _window.MarkPreviewDirty();
                _window.MarkPreviewDirty();
                _window.MarkPreviewDirty();
                _window.MarkPreviewDirty();
                _window.MarkPreviewDirty();
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Unchanged burst must skip the heavy render."
                );
                Assert.AreEqual(
                    skipsBeforeAll + 1,
                    _window.PreviewSkipCount,
                    "Unchanged burst must skip once."
                );
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void HierarchyBurst_DebouncesToSingleRender()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbRefresh_Debounce.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(assetPath);
                _window.OnHierarchyChangedForPreview();
                _window.OnHierarchyChangedForPreview();
                _window.OnHierarchyChangedForPreview();
                _window.OnHierarchyChangedForPreview();
                _window.OnHierarchyChangedForPreview();
                Assert.IsTrue(
                    IsRenderLivePreviewSubscribed(_window),
                    "Burst must collapse to one subscription."
                );
                _window.RenderLivePreview();
                Assert.AreEqual(
                    rendersBefore,
                    _window.PreviewRenderCount,
                    "Render inside the debounce window must defer."
                );
                Assert.IsTrue(_window.IsPreviewDirty, "Deferred preview must stay dirty.");
                Assert.IsTrue(
                    IsRenderLivePreviewSubscribed(_window),
                    "Deferred preview must stay subscribed."
                );
                System.Threading.Thread.Sleep(260);
                _window.RenderLivePreview();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "One render must fire after the window."
                );
                Assert.IsFalse(_window.IsPreviewDirty);
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void Preview_AfterPrefabRender_ShowsThumbnailImage()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbRefresh_Thumb.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(assetPath);
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Prefab preview must render."
                );
                Image image = _window.rootVisualElement.Q<Image>("preview-image");
                Assert.IsNotNull(image, "preview-image must exist.");
                Assert.IsNotNull(image.image, "Rendered thumbnail must reach the preview image.");
                IResolvedStyle resolved = image.resolvedStyle;
                UnityEngine.Debug.Log(
                    "[UniThumb] Refresh thumbnail spot: display="
                        + resolved.display
                        + " width="
                        + resolved.width
                        + " height="
                        + resolved.height
                );
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void SettingsFingerprint_ChangesFlipKey()
        {
            CaptureSettings defaults = UniThumbCapture.CreateDefaultSettings();
            string keyA = UniThumbWindow.BuildPreviewRenderKey(
                "Assets/A.prefab",
                "guidA",
                null,
                defaults
            );
            string keyB = UniThumbWindow.BuildPreviewRenderKey(
                "Assets/A.prefab",
                "guidA",
                null,
                defaults
            );
            Assert.AreEqual(keyA, keyB, "Identical inputs must key identically.");
            CaptureSettings changed = defaults;
            changed.OrbitYaw += 10f;
            Assert.AreNotEqual(
                keyA,
                UniThumbWindow.BuildPreviewRenderKey("Assets/A.prefab", "guidA", null, changed),
                "Setting change must flip the key."
            );
            Assert.AreNotEqual(
                keyA,
                UniThumbWindow.BuildPreviewRenderKey("Assets/B.prefab", "guidB", null, defaults),
                "Target change must flip the key."
            );
            string sceneKey = UniThumbWindow.BuildPreviewRenderKey(
                null,
                "guidS",
                "Assets/S.unity",
                defaults
            );
            Assert.AreNotEqual(keyA, sceneKey, "Scene target must key differently.");
        }

        private void SelectPrefab(string assetPath)
        {
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(Selection.activeObject, "Temp prefab must load: " + assetPath);
            _window.OnProjectSelectionChanged();
        }

        /// <summary>
        /// Renders past the new-key idle defer: new selections wait for the
        /// quiet window, so tests arm idle-readiness before ticking.
        /// </summary>
        private void RenderWhenIdle()
        {
            _window.PreviewIdleReadyForTest();
            _window.RenderLivePreview();
        }

        private string SaveTempCubePrefab(string file)
        {
            GameObject root = new GameObject("RefreshCubeRoot");
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            string path = "Assets/" + file;
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            Assert.IsNotNull(saved, "Prefab asset must save: " + path);
            _tempAssets.Add(path);
            return path;
        }

        #endregion
    }
}
