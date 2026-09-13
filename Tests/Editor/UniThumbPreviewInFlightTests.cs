using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// In-flight leak proof: an unsaved-scene RenderLivePreview trigger must
    /// never leave PreviewRenderInFlight set, so switching back to a saved
    /// scene renders instead of early-outing blank forever.
    /// </summary>
    [TestFixture]
    public class UniThumbPreviewInFlightTests
    {
        #region Fields

        private UniThumbWindow _window;
        private string _originalScenePath;
        private string _tempScenePath;

        #endregion

        #region Setup

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            UniThumbWindow.PreviewDragProbeOverride = () => false;
            _originalScenePath = EditorSceneManager.GetActiveScene().path;
            _tempScenePath = null;
            _window = EditorWindow.GetWindow<UniThumbWindow>();
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbWindow.PreviewDragProbeOverride = null;
            Selection.activeObject = null;
            if (!string.IsNullOrEmpty(_tempScenePath))
            {
                AssetDatabase.DeleteAsset(_tempScenePath);
                _tempScenePath = null;
            }
            if (!string.IsNullOrEmpty(_originalScenePath))
            {
                EditorSceneManager.OpenScene(_originalScenePath);
            }
            else
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            if (_window != null)
            {
                _window.CancelPendingPreview();
                _window.Close();
                _window = null;
            }
            UniThumbGuard.Exit();
        }

        #endregion

        #region Tests

        [Test]
        public void UnsavedSceneTrigger_ThenSwitchToSaved_FlagStaysFalse()
        {
            // Arrange: a saved scene on disk to switch back to.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _tempScenePath = "Assets/__UniThumbInFlight_Temp.unity";
            bool saved = EditorSceneManager.SaveScene(
                EditorSceneManager.GetActiveScene(),
                _tempScenePath
            );
            Assert.IsTrue(saved, "Temp scene must save: " + _tempScenePath);
            Selection.activeObject = null;
            _window.OnProjectSelectionChanged();

            // Act: switch to an unsaved scene and tick the preview.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.IsTrue(
                string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path),
                "New scene must be unsaved (empty path)."
            );
            _window.PreviewIdleReadyForTest();
            _window.RenderLivePreview();

            // Assert: the unsaved early-out left no in-flight leak.
            Assert.IsFalse(
                _window.PreviewRenderInFlight,
                "Unsaved-scene early-out must not leak PreviewRenderInFlight."
            );

            // Act: switch back to the saved scene and tick again.
            EditorSceneManager.OpenScene(_tempScenePath);
            int rendersBefore = _window.PreviewRenderCount;
            _window.PreviewIdleReadyForTest();
            _window.RenderLivePreview();

            // Assert: the pending preview rendered instead of blank early-out.
            Assert.IsFalse(
                _window.PreviewRenderInFlight,
                "Flag must be false after the saved-scene render."
            );
            Assert.AreEqual(
                rendersBefore + 1,
                _window.PreviewRenderCount,
                "Saved-scene tick after unsaved trigger must render once."
            );
        }

        #endregion
    }
}
