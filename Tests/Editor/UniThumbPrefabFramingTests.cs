using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Prefab framing tests (Wave 2): orbit framing stays available while a
    /// prefab is selected, the Scene View toggle is scene-only, and prefab
    /// selection re-arms the live preview. Written first (Red): the
    /// ShouldUsePrefabOrbitFraming / ComputeFramingDisabled seams do not
    /// exist pre-fix, and prefab selection does not mark the preview dirty.
    /// </summary>
    [TestFixture]
    public class UniThumbPrefabFramingTests
    {
        private const string k_TempPrefabPath = "Assets/__UniThumbTest_FramingPrefab.prefab";

        private GameObject _tempSource;
        private GameObject _tempPrefabAsset;
        private UnityEngine.Object _previousSelection;
        private UniThumbWindow _window;

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            _previousSelection = Selection.activeObject;
            Selection.activeObject = null;
            _window = EditorWindow.GetWindow<UniThumbWindow>();
        }

        [TearDown]
        public void TearDown()
        {
            Selection.activeObject = _previousSelection;
            _previousSelection = null;
            if (_tempSource != null)
            {
                Object.DestroyImmediate(_tempSource);
                _tempSource = null;
            }
            if (_tempPrefabAsset != null)
            {
                string guid = AssetDatabase.AssetPathToGUID(k_TempPrefabPath);
                if (!string.IsNullOrEmpty(guid))
                {
                    UniThumbStorage.DeleteByGuid(guid);
                }
                AssetDatabase.DeleteAsset(k_TempPrefabPath);
                _tempPrefabAsset = null;
            }
            UniThumbStorage.ClearCache();
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }
            UniThumbGuard.Exit();
        }

        #region Framing mode helpers

        [Test]
        public void ShouldUsePrefabOrbitFraming_PrefabPath_ReturnsTrue()
        {
            Assert.IsTrue(UniThumbWindow.ShouldUsePrefabOrbitFraming(k_TempPrefabPath));
        }

        [Test]
        public void ShouldUsePrefabOrbitFraming_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(UniThumbWindow.ShouldUsePrefabOrbitFraming(null));
            Assert.IsFalse(UniThumbWindow.ShouldUsePrefabOrbitFraming(string.Empty));
        }

        [Test]
        public void ComputeFramingDisabled_SceneViewAngleWithoutPrefab_ReturnsTrue()
        {
            Assert.IsTrue(UniThumbWindow.ComputeFramingDisabled(true, null));
            Assert.IsTrue(UniThumbWindow.ComputeFramingDisabled(true, string.Empty));
        }

        [Test]
        public void ComputeFramingDisabled_SceneViewAngleWithPrefab_ReturnsFalse()
        {
            Assert.IsFalse(UniThumbWindow.ComputeFramingDisabled(true, k_TempPrefabPath));
        }

        [Test]
        public void ComputeFramingDisabled_OrbitMode_ReturnsFalse()
        {
            Assert.IsFalse(UniThumbWindow.ComputeFramingDisabled(false, null));
            Assert.IsFalse(UniThumbWindow.ComputeFramingDisabled(false, k_TempPrefabPath));
        }

        #endregion

        #region Prefab live preview arming

        [Test]
        public void PrefabSelection_ReArmsLivePreview()
        {
            _tempSource = new GameObject("UniThumbFramingTempSource");
            _tempSource.AddComponent<BoxCollider>();
            _tempPrefabAsset = PrefabUtility.SaveAsPrefabAsset(_tempSource, k_TempPrefabPath);
            Assert.IsNotNull(_tempPrefabAsset, "Temp prefab asset must be created.");
            Selection.activeObject = _tempPrefabAsset;

            _window.OnProjectSelectionChanged();

            Assert.IsTrue(
                _window.IsPrefabPreview,
                "Window must report prefab preview mode while a prefab is selected."
            );
            Assert.IsTrue(
                _window.IsPreviewDirty,
                "Prefab selection must mark the preview dirty so framing edits re-render live."
            );
            Assert.IsFalse(
                _window.IsFramingDisabled,
                "Framing must stay enabled in prefab mode even with Scene View angle on."
            );
        }

        #endregion
    }
}
