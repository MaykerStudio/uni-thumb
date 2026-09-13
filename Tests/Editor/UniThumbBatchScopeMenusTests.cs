using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Wave 2 menu wiring: BatchScope is consumed (never redefined) by the
    /// folder/refresh entry points; scoped collectors filter by asset type
    /// and never throw on invalid folders (no dialogs in collectors).
    /// </summary>
    [TestFixture]
    public class UniThumbBatchScopeMenusTests
    {
        private const string k_TempPrefabPath = "Assets/__UniThumbTest_ScopeMenus.prefab";

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
                UnityEngine.Object.DestroyImmediate(_tempSource);
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
        public void BatchScope_DefinesAllScenesOnlyPrefabsOnly()
        {
            Assert.IsTrue(Enum.IsDefined(typeof(UniThumbBatchMenus.BatchScope), "All"));
            Assert.IsTrue(Enum.IsDefined(typeof(UniThumbBatchMenus.BatchScope), "ScenesOnly"));
            Assert.IsTrue(Enum.IsDefined(typeof(UniThumbBatchMenus.BatchScope), "PrefabsOnly"));
        }

        [Test]
        public void IncludesScenes_TruthTable()
        {
            Assert.IsTrue(UniThumbBatchMenus.IncludesScenes(UniThumbBatchMenus.BatchScope.All));
            Assert.IsTrue(
                UniThumbBatchMenus.IncludesScenes(UniThumbBatchMenus.BatchScope.ScenesOnly)
            );
            Assert.IsFalse(
                UniThumbBatchMenus.IncludesScenes(UniThumbBatchMenus.BatchScope.PrefabsOnly)
            );
        }

        [Test]
        public void IncludesPrefabs_TruthTable()
        {
            Assert.IsTrue(UniThumbBatchMenus.IncludesPrefabs(UniThumbBatchMenus.BatchScope.All));
            Assert.IsFalse(
                UniThumbBatchMenus.IncludesPrefabs(UniThumbBatchMenus.BatchScope.ScenesOnly)
            );
            Assert.IsTrue(
                UniThumbBatchMenus.IncludesPrefabs(UniThumbBatchMenus.BatchScope.PrefabsOnly)
            );
        }

        [Test]
        public void CollectFolderPaths_InvalidFolder_ReturnsEmptyPerScope()
        {
            foreach (
                UniThumbBatchMenus.BatchScope scope in (UniThumbBatchMenus.BatchScope[])
                    Enum.GetValues(typeof(UniThumbBatchMenus.BatchScope))
            )
            {
                List<string> paths = UniThumbBatchMenus.CollectFolderPaths(
                    "Assets/NoSuchFolder__UniThumbScope",
                    scope
                );
                Assert.IsNotNull(paths, "Scope " + scope + " must return a list.");
                Assert.IsEmpty(paths, "Scope " + scope + " must return empty.");
            }
        }

        [Test]
        public void CollectFolderPaths_ScopedFilters_SplitScenesAndPrefabs()
        {
            _tempSource = new GameObject("UniThumbScopeMenusTempSource");
            _tempPrefabAsset = PrefabUtility.SaveAsPrefabAsset(_tempSource, k_TempPrefabPath);
            Assert.IsNotNull(_tempPrefabAsset, "Temp prefab asset must be created.");

            List<string> scenesOnly = UniThumbBatchMenus.CollectFolderPaths(
                "Assets",
                UniThumbBatchMenus.BatchScope.ScenesOnly
            );
            Assert.IsFalse(
                scenesOnly.Contains(k_TempPrefabPath),
                "ScenesOnly must never return prefabs."
            );

            List<string> prefabsOnly = UniThumbBatchMenus.CollectFolderPaths(
                "Assets",
                UniThumbBatchMenus.BatchScope.PrefabsOnly
            );
            Assert.Contains(k_TempPrefabPath, prefabsOnly, "PrefabsOnly must return prefabs.");
            foreach (string path in prefabsOnly)
            {
                Assert.IsTrue(
                    UniThumbBatchMenus.IsPrefabAssetPath(path),
                    "PrefabsOnly must never return scenes: '" + path + "'."
                );
            }
        }

        [Test]
        public void CollectRefreshWork_ScopedOverloads_ReturnLists()
        {
            foreach (
                UniThumbBatchMenus.BatchScope scope in (UniThumbBatchMenus.BatchScope[])
                    Enum.GetValues(typeof(UniThumbBatchMenus.BatchScope))
            )
            {
                List<string> work = UniThumbBatchMenus.CollectRefreshWork(false, scope);
                Assert.IsNotNull(work, "Scope " + scope + " must return a list.");
            }
        }
    }
}
