using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Prefab surface tests (Wave 2): window preview selection, Assets menu
    /// helpers, folder collector prefab inclusion, and icon overlay for
    /// prefabs. Written first (Red): pre-implementation they fail to compile
    /// against the missing internal seams (IsPrefabAssetPath,
    /// TryGetSelectedPrefabPath).
    /// </summary>
    [TestFixture]
    public class UniThumbPrefabSurfacesTests
    {
        private const string k_TempPrefabPath = "Assets/__UniThumbTest_SurfacesPrefab.prefab";

        private GameObject _tempSource;
        private GameObject _tempPrefabAsset;
        private UnityEngine.Object _previousSelection;

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            _previousSelection = Selection.activeObject;
            Selection.activeObject = null;
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
            UniThumbGuard.Exit();
        }

        private void CreateTempPrefabAsset()
        {
            _tempSource = new GameObject("UniThumbSurfacesTempSource");
            _tempPrefabAsset = PrefabUtility.SaveAsPrefabAsset(_tempSource, k_TempPrefabPath);
            Assert.IsNotNull(_tempPrefabAsset, "Temp prefab asset must be created.");
        }

        #region Path helper

        [Test]
        public void IsPrefabAssetPath_PrefabExtension_ReturnsTrue()
        {
            Assert.IsTrue(UniThumbBatchMenus.IsPrefabAssetPath("Assets/Foo/Bar.prefab"));
        }

        [Test]
        public void IsPrefabAssetPath_SceneExtension_ReturnsFalse()
        {
            Assert.IsFalse(UniThumbBatchMenus.IsPrefabAssetPath("Assets/Foo/Bar.unity"));
        }

        [Test]
        public void IsPrefabAssetPath_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(UniThumbBatchMenus.IsPrefabAssetPath(null));
            Assert.IsFalse(UniThumbBatchMenus.IsPrefabAssetPath(string.Empty));
        }

        #endregion

        #region Selection helper

        [Test]
        public void TryGetSelectedPrefabPath_NoSelection_ReturnsFalse()
        {
            Selection.activeObject = null;
            string prefabPath;
            Assert.IsFalse(UniThumbWindow.TryGetSelectedPrefabPath(out prefabPath));
            Assert.IsNull(prefabPath);
        }

        [Test]
        public void TryGetSelectedPrefabPath_PrefabSelected_ReturnsPath()
        {
            CreateTempPrefabAsset();
            Selection.activeObject = _tempPrefabAsset;
            string prefabPath;
            Assert.IsTrue(UniThumbWindow.TryGetSelectedPrefabPath(out prefabPath));
            Assert.AreEqual(k_TempPrefabPath, prefabPath);
        }

        #endregion

        #region Folder collector

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
        public void CollectFolderScenePaths_WithTempPrefab_IncludesPrefabPath()
        {
            CreateTempPrefabAsset();
            List<string> paths = UniThumbBatchMenus.CollectFolderScenePaths("Assets");
            Assert.Contains(k_TempPrefabPath, paths);
        }

        #endregion

        #region Icon overlay

        [Test]
        public void PrefabIcon_ApplyAndClear_RoundTrip()
        {
            CreateTempPrefabAsset();
            string guid = AssetDatabase.AssetPathToGUID(k_TempPrefabPath);
            Assert.IsFalse(string.IsNullOrEmpty(guid));

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            byte[] png;
            try
            {
                texture.SetPixels(new Color[] { Color.red, Color.green, Color.blue, Color.white });
                texture.Apply();
                png = texture.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            Assert.IsTrue(UniThumbStorage.SavePrefabThumbnail(guid, png));
            Assert.IsTrue(UniThumbIconService.ApplyIcon(k_TempPrefabPath));
            Assert.IsTrue(UniThumbIconService.ClearIcon(k_TempPrefabPath));
        }

        #endregion
    }
}
