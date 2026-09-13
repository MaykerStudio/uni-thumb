using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Prefab-only folder batch filter: CollectFolderPrefabPaths returns
    /// prefabs under a folder and never scenes, while the mixed collector
    /// keeps its scene+prefab behaviour.
    /// </summary>
    [TestFixture]
    public class UniThumbPrefabFolderFilterTests
    {
        private const string k_TempPrefabPath = "Assets/__UniThumbTest_PrefabFilter.prefab";

        private GameObject _tempSource;
        private GameObject _tempPrefabAsset;

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            Selection.activeObject = null;
        }

        [TearDown]
        public void TearDown()
        {
            Selection.activeObject = null;
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

        [Test]
        public void CollectFolderPrefabPaths_InvalidFolder_ReturnsEmpty()
        {
            List<string> paths = UniThumbBatchMenus.CollectFolderPrefabPaths(
                "Assets/NoSuchFolder__UniThumb"
            );
            Assert.IsNotNull(paths);
            Assert.IsEmpty(paths);
        }

        [Test]
        public void CollectFolderPrefabPaths_WithTempPrefab_IncludesPrefabPath()
        {
            _tempSource = new GameObject("UniThumbPrefabFilterTempSource");
            _tempPrefabAsset = PrefabUtility.SaveAsPrefabAsset(_tempSource, k_TempPrefabPath);
            Assert.IsNotNull(_tempPrefabAsset, "Temp prefab asset must be created.");

            List<string> paths = UniThumbBatchMenus.CollectFolderPrefabPaths("Assets");
            Assert.Contains(k_TempPrefabPath, paths);
        }

        [Test]
        public void CollectFolderPrefabPaths_ReturnsOnlyPrefabs()
        {
            _tempSource = new GameObject("UniThumbPrefabFilterTempSource");
            _tempPrefabAsset = PrefabUtility.SaveAsPrefabAsset(_tempSource, k_TempPrefabPath);
            Assert.IsNotNull(_tempPrefabAsset, "Temp prefab asset must be created.");

            List<string> paths = UniThumbBatchMenus.CollectFolderPrefabPaths("Assets");
            foreach (string path in paths)
            {
                Assert.IsTrue(
                    UniThumbBatchMenus.IsPrefabAssetPath(path),
                    "Prefab-only collector must never return scenes: '" + path + "'."
                );
            }
        }

        [Test]
        public void CollectFolderScenePaths_MixedFlow_StillIncludesPrefab()
        {
            _tempSource = new GameObject("UniThumbPrefabFilterTempSource");
            _tempPrefabAsset = PrefabUtility.SaveAsPrefabAsset(_tempSource, k_TempPrefabPath);
            Assert.IsNotNull(_tempPrefabAsset, "Temp prefab asset must be created.");

            List<string> paths = UniThumbBatchMenus.CollectFolderScenePaths("Assets");
            Assert.Contains(k_TempPrefabPath, paths);
        }

        [Test]
        public void CollectFolderPrefabPaths_AfterCacheWarm_NewPrefabFoundWithoutInvalidate()
        {
            UniThumbBatchMenus.GetCachedGuids("t:Prefab");
            _tempSource = new GameObject("UniThumbPrefabFilterTempSource");
            _tempPrefabAsset = PrefabUtility.SaveAsPrefabAsset(_tempSource, k_TempPrefabPath);
            Assert.IsNotNull(_tempPrefabAsset, "Temp prefab asset must be created.");

            List<string> paths = UniThumbBatchMenus.CollectFolderPrefabPaths("Assets");
            Assert.Contains(
                k_TempPrefabPath,
                paths,
                "Prefab created after the cache warmed must be found with no manual invalidate."
            );
        }
    }
}
