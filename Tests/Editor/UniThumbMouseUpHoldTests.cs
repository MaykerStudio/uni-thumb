using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// MouseUp hold regression tests (plan 20260910-mouseup-hold). The heavy
    /// prefab preview must idle-defer off the MouseUp tick, bursts must
    /// collapse to one render, own Instantiate and Destroy events must not
    /// invalidate the render key, and Generate must show cancellable
    /// progress. Cache-only refresh paths and guard semantics are unchanged.
    /// </summary>
    [TestFixture]
    public class UniThumbMouseUpHoldTests
    {
        private UniThumbWindow _window;
        private readonly List<string> _tempAssets = new List<string>();
        private readonly List<string> _tempGuids = new List<string>();

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            UniThumbWindow.PreviewDragProbeOverride = () => false;
            UniThumbWindow.GenerateProgressDisplayOverride = null;
            UniThumbWindow.GenerateProgressClearOverride = null;
            _window = EditorWindow.GetWindow<UniThumbWindow>();
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbWindow.PreviewDragProbeOverride = null;
            UniThumbWindow.GenerateProgressDisplayOverride = null;
            UniThumbWindow.GenerateProgressClearOverride = null;
            Selection.activeObject = null;
            foreach (string guid in _tempGuids)
            {
                UniThumbStorage.DeleteByGuid(guid);
            }
            _tempGuids.Clear();
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

        [Test]
        public void NewKeySelection_IdlesOffMouseUp_ThenRendersAtMostOnce()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbHold_NewKey.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                // Delta-based: the window instance is shared across tests and
                // idle ticks may render between them, so only the delta
                // inside this synchronous test is asserted.
                int rendersBefore = _window.PreviewRenderCount;
                int skipsBefore = _window.PreviewSkipCount;
                SelectPrefab(assetPath);

                // Immediate tick inside the MouseUp burst: deferred, zero heavy work.
                _window.RenderLivePreview();
                Assert.AreEqual(
                    rendersBefore,
                    _window.PreviewRenderCount,
                    "New-key selection must idle-defer, never render inside the input tick."
                );
                Assert.IsTrue(_window.IsPreviewDirty, "Deferred preview must stay dirty.");

                // Idle tick after the quiet window: exactly one heavy render.
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Idle tick must render once."
                );

                // Repeated MouseUp on the same selection: at most one total.
                _window.OnProjectSelectionChanged();
                _window.RenderLivePreview();
                _window.OnProjectSelectionChanged();
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Repeated same-key MouseUp events must add zero heavy renders."
                );
                Assert.Greater(
                    _window.PreviewSkipCount,
                    skipsBefore,
                    "Repeated same-key MouseUp events must skip."
                );

                Label caption = _window.rootVisualElement.Q<Label>("preview-caption");
                Assert.IsNotNull(caption, "preview-caption must exist.");
                Assert.IsTrue(
                    caption.text.EndsWith("(preview)"),
                    "Caption must mark the live preview, got: " + caption.text
                );
                UnityEngine.Debug.Log(
                    "[UniThumb] Hold caption spot: display="
                        + caption.resolvedStyle.display
                        + " text="
                        + caption.text
                );
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void Burst_FiveMarkPreviewDirty_YieldsSingleHeavyRender()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbHold_Burst.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(assetPath);
                for (int i = 0; i < 5; i++)
                {
                    _window.MarkPreviewDirty();
                }
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Burst of 5 must yield 1 render."
                );
                Assert.IsFalse(_window.IsPreviewDirty, "Render must consume dirty.");
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void SameKeySelection_YieldsSkipNotRender()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbHold_SameKey.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(assetPath);
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "First selection must render."
                );

                int skipsBefore = _window.PreviewSkipCount;
                _window.OnProjectSelectionChanged();
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Same-key selection must run zero heavy renders."
                );
                Assert.AreEqual(
                    skipsBefore + 1,
                    _window.PreviewSkipCount,
                    "Same-key selection must count one skip."
                );
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void OwnHierarchyEvents_DuringPreview_YieldZeroNetNewRenders()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbHold_OwnEvents.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(assetPath);
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "First selection must render."
                );
                int filteredBefore = _window.PreviewOwnEventFilteredCount;
                int skipsBefore = _window.PreviewSkipCount;

                // Simulate the Instantiate plus Destroy hierarchy events that
                // fire synchronously inside one preview: filtered, key kept.
                _window.BeginPreviewOwnMutation();
                try
                {
                    _window.OnHierarchyChangedForPreview();
                    _window.OnHierarchyChangedForPreview();
                    _window.OnHierarchyChangedForPreview();
                }
                finally
                {
                    _window.EndPreviewOwnMutation();
                }
                Assert.AreEqual(
                    filteredBefore + 3,
                    _window.PreviewOwnEventFilteredCount,
                    "Own events must be filtered, never invalidating."
                );
                Assert.IsFalse(
                    _window.IsPreviewDirty,
                    "Filtered own events must not schedule work."
                );

                // Real edit after the flight still invalidates and re-renders.
                _window.OnHierarchyChangedForPreview();
                Assert.IsTrue(_window.IsPreviewDirty, "Real edit must schedule work.");
                RenderWhenIdle();
                Assert.AreEqual(
                    rendersBefore + 2,
                    _window.PreviewRenderCount,
                    "Real edit after the flight must re-render once."
                );
                Assert.AreEqual(
                    skipsBefore,
                    _window.PreviewSkipCount,
                    "No skip may consume the real-edit render."
                );
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void DragProbe_SkipsWhileDragging_ThenRendersOnRelease()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbHold_Drag.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(assetPath);
                UniThumbWindow.PreviewDragProbeOverride = () => true;
                _window.PreviewIdleReadyForTest();
                _window.RenderLivePreview();
                Assert.AreEqual(
                    rendersBefore,
                    _window.PreviewRenderCount,
                    "Render while dragging must stay deferred."
                );
                Assert.IsTrue(_window.IsPreviewDirty, "Drag-deferred preview must stay dirty.");

                UniThumbWindow.PreviewDragProbeOverride = () => false;
                _window.RenderLivePreview();
                Assert.AreEqual(
                    rendersBefore + 1,
                    _window.PreviewRenderCount,
                    "Release must render once."
                );
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void CancelPendingPreview_DropsPendingRender()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbHold_Cancel.prefab");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                int rendersBefore = _window.PreviewRenderCount;
                SelectPrefab(assetPath);
                Assert.IsTrue(_window.IsPreviewDirty, "Selection must flag dirty.");
                _window.CancelPendingPreview();
                _window.PreviewIdleReadyForTest();
                _window.RenderLivePreview();
                Assert.AreEqual(
                    rendersBefore,
                    _window.PreviewRenderCount,
                    "Cancelled preview must not render."
                );
                Assert.IsFalse(_window.IsPreviewDirty, "Cancel must consume dirty.");
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void GeneratePrefab_ShowsProgress_AndHonoursCancel()
        {
            string assetPath = SaveTempCubePrefab("__UniThumbHold_Generate.prefab");
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            _tempGuids.Add(guid);
            int clearCount = 0;
            List<string> progressCalls = new List<string>();
            try
            {
                // Cancel path: progress shows, capture never runs, nothing saved.
                UniThumbWindow.GenerateProgressDisplayOverride = (title, label, progress) =>
                {
                    progressCalls.Add(title + "|" + label + "|" + progress);
                    return true;
                };
                UniThumbWindow.GenerateProgressClearOverride = () =>
                {
                    clearCount++;
                };
                _window.GeneratePrefabThumbnail(assetPath);
                Assert.IsTrue(
                    _window.GenerateProgressWasShown,
                    "Generate must show progress before the sync capture."
                );
                Assert.IsTrue(_window.GenerateWasCancelled, "Cancel must set cancelled state.");
                Assert.IsFalse(
                    UniThumbStorage.HasPrefabThumbnail(guid),
                    "Cancelled generate must save nothing."
                );
                Assert.AreEqual(1, clearCount, "Cancel path must clear progress in finally.");
                Assert.IsFalse(UniThumbGuard.IsGenerating, "Cancel path must release the guard.");

                // Success path: progress shows twice (capture, save), thumbnail saved.
                progressCalls.Clear();
                UniThumbWindow.GenerateProgressDisplayOverride = (title, label, progress) =>
                {
                    progressCalls.Add(title + "|" + label + "|" + progress);
                    return false;
                };
                _window.GeneratePrefabThumbnail(assetPath);
                Assert.IsTrue(_window.GenerateProgressWasShown, "Success path must show progress.");
                Assert.IsFalse(
                    _window.GenerateWasCancelled,
                    "Success path must not set cancelled state."
                );
                Assert.AreEqual(
                    2,
                    progressCalls.Count,
                    "Success path must report capture plus save progress."
                );
                Assert.IsTrue(
                    progressCalls[0].StartsWith("UniThumb Generate|"),
                    "Progress must carry the Generate title, got: " + progressCalls[0]
                );
                Assert.IsTrue(
                    UniThumbStorage.HasPrefabThumbnail(guid),
                    "Success path must save the thumbnail."
                );
                Assert.AreEqual(2, clearCount, "Success path must clear progress in finally.");
                Assert.IsFalse(UniThumbGuard.IsGenerating, "Success path must release the guard.");

                Label busy = _window.rootVisualElement.Q<Label>("generate-busy-label");
                Assert.IsNotNull(busy, "generate-busy-label must exist.");
                Assert.IsTrue(
                    busy.ClassListContains("stt-hidden"),
                    "Busy label must hide again after the guard releases."
                );
                UnityEngine.Debug.Log(
                    "[UniThumb] Hold progress spot: display="
                        + busy.resolvedStyle.display
                        + " calls="
                        + progressCalls.Count
                );
            }
            finally
            {
                UniThumbWindow.GenerateProgressDisplayOverride = null;
                UniThumbWindow.GenerateProgressClearOverride = null;
            }
        }

        private void SelectPrefab(string assetPath)
        {
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(Selection.activeObject, "Temp prefab must load: " + assetPath);
            _window.OnProjectSelectionChanged();
        }

        private void RenderWhenIdle()
        {
            _window.PreviewIdleReadyForTest();
            _window.RenderLivePreview();
        }

        private string SaveTempCubePrefab(string file)
        {
            GameObject root = new GameObject("HoldCubeRoot");
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            string path = "Assets/" + file;
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            Assert.IsNotNull(saved, "Prefab asset must save: " + path);
            _tempAssets.Add(path);
            return path;
        }
    }
}
