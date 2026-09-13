using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Wave 3 scope filter coverage: folder/refresh collectors filter by
    /// BatchScope and clear-target selection (CollectFolderPaths plus
    /// HasThumbnail, mirroring ClearFolderThumbnails) stays scoped.
    /// </summary>
    [TestFixture]
    public class UniThumbBatchScopeFilterTests
    {
        #region Fields

        private const string k_Folder = "Assets/__UniThumbBatchScopeFilter";
        private const string k_ScenePath = "Assets/__UniThumbBatchScopeFilter/FilterScene.unity";
        private const string k_PrefabPath = "Assets/__UniThumbBatchScopeFilter/FilterPrefab.prefab";

        private GameObject _source;
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
            Selection.activeObject = null;
            if (_source != null)
            {
                Object.DestroyImmediate(_source);
                _source = null;
            }
            UniThumbStorage.Delete(k_ScenePath);
            string prefabGuid = AssetDatabase.AssetPathToGUID(k_PrefabPath);
            if (!string.IsNullOrEmpty(prefabGuid))
            {
                UniThumbStorage.DeleteByGuid(prefabGuid);
            }
            RestorePreviousScene();
            if (AssetDatabase.IsValidFolder(k_Folder))
            {
                AssetDatabase.DeleteAsset(k_Folder);
            }
            UniThumbStorage.ClearCache();
            UniThumbGuard.Exit();
        }

        #endregion

        #region Folder Scope

        [Test]
        public void CollectFolderPaths_All_ContainsSceneAndPrefab()
        {
            EnsureTempAssets();

            List<string> all = UniThumbBatchMenus.CollectFolderPaths(
                k_Folder,
                UniThumbBatchMenus.BatchScope.All
            );
            Assert.Contains(k_ScenePath, all, "All must include the folder scene.");
            Assert.Contains(k_PrefabPath, all, "All must include the folder prefab.");
        }

        [Test]
        public void CollectFolderPaths_ScenesOnly_ExcludesPrefabs()
        {
            EnsureTempAssets();

            List<string> scenesOnly = UniThumbBatchMenus.CollectFolderPaths(
                k_Folder,
                UniThumbBatchMenus.BatchScope.ScenesOnly
            );
            Assert.Contains(k_ScenePath, scenesOnly, "ScenesOnly must include the folder scene.");
            Assert.IsFalse(
                scenesOnly.Contains(k_PrefabPath),
                "ScenesOnly must never return prefabs."
            );
            foreach (string path in scenesOnly)
            {
                Assert.IsFalse(
                    UniThumbBatchMenus.IsPrefabAssetPath(path),
                    "ScenesOnly must never return prefabs: '" + path + "'."
                );
            }
        }

        [Test]
        public void CollectFolderPaths_PrefabsOnly_ExcludesScenes()
        {
            EnsureTempAssets();

            List<string> prefabsOnly = UniThumbBatchMenus.CollectFolderPaths(
                k_Folder,
                UniThumbBatchMenus.BatchScope.PrefabsOnly
            );
            Assert.Contains(
                k_PrefabPath,
                prefabsOnly,
                "PrefabsOnly must include the folder prefab."
            );
            Assert.IsFalse(
                prefabsOnly.Contains(k_ScenePath),
                "PrefabsOnly must never return scenes."
            );
            foreach (string path in prefabsOnly)
            {
                Assert.IsTrue(
                    UniThumbBatchMenus.IsPrefabAssetPath(path),
                    "PrefabsOnly must never return scenes: '" + path + "'."
                );
            }
        }

        [Test]
        public void LegacyWrappers_MatchScopedCore()
        {
            EnsureTempAssets();

            List<string> legacyMixed = UniThumbBatchMenus.CollectFolderScenePaths(k_Folder);
            List<string> scopedAll = UniThumbBatchMenus.CollectFolderPaths(
                k_Folder,
                UniThumbBatchMenus.BatchScope.All
            );
            CollectionAssert.AreEquivalent(
                scopedAll,
                legacyMixed,
                "Mixed wrapper must equal All core."
            );

            List<string> legacyPrefabs = UniThumbBatchMenus.CollectFolderPrefabPaths(k_Folder);
            List<string> scopedPrefabs = UniThumbBatchMenus.CollectFolderPaths(
                k_Folder,
                UniThumbBatchMenus.BatchScope.PrefabsOnly
            );
            CollectionAssert.AreEquivalent(
                scopedPrefabs,
                legacyPrefabs,
                "Prefab wrapper must equal PrefabsOnly core."
            );
        }

        #endregion

        #region Refresh Scope

        [Test]
        public void CollectRefreshWork_ScenesOnly_ReturnsNoPrefabs()
        {
            List<string> work = UniThumbBatchMenus.CollectRefreshWork(
                false,
                UniThumbBatchMenus.BatchScope.ScenesOnly
            );
            Assert.IsNotNull(work);
            foreach (string path in work)
            {
                Assert.IsFalse(
                    UniThumbBatchMenus.IsPrefabAssetPath(path),
                    "ScenesOnly refresh must never queue prefabs: '" + path + "'."
                );
            }
        }

        [Test]
        public void CollectRefreshWork_PrefabsOnly_ReturnsOnlyPrefabs()
        {
            List<string> work = UniThumbBatchMenus.CollectRefreshWork(
                false,
                UniThumbBatchMenus.BatchScope.PrefabsOnly
            );
            Assert.IsNotNull(work);
            foreach (string path in work)
            {
                Assert.IsTrue(
                    UniThumbBatchMenus.IsPrefabAssetPath(path),
                    "PrefabsOnly refresh must never queue scenes: '" + path + "'."
                );
            }
        }

        [Test]
        public void CollectRefreshWork_All_SupersetsScopedWork()
        {
            List<string> all = UniThumbBatchMenus.CollectRefreshWork(
                false,
                UniThumbBatchMenus.BatchScope.All
            );
            List<string> scenesOnly = UniThumbBatchMenus.CollectRefreshWork(
                false,
                UniThumbBatchMenus.BatchScope.ScenesOnly
            );
            List<string> prefabsOnly = UniThumbBatchMenus.CollectRefreshWork(
                false,
                UniThumbBatchMenus.BatchScope.PrefabsOnly
            );
            foreach (string path in scenesOnly)
            {
                Assert.Contains(path, all, "All refresh must include every ScenesOnly entry.");
            }
            foreach (string path in prefabsOnly)
            {
                Assert.Contains(path, all, "All refresh must include every PrefabsOnly entry.");
            }
        }

        #endregion

        #region Clear Targets

        [Test]
        public void CollectClearTargets_ScopeFiltersByThumbnailPresence()
        {
            EnsureTempAssets();
            byte[] png = CreateTestPng();
            Assert.IsTrue(UniThumbStorage.Save(k_ScenePath, png), "Scene thumbnail must save.");
            string prefabGuid = AssetDatabase.AssetPathToGUID(k_PrefabPath);
            Assert.IsFalse(string.IsNullOrEmpty(prefabGuid), "Prefab GUID must resolve.");
            Assert.IsTrue(
                UniThumbStorage.SavePrefabThumbnail(prefabGuid, png),
                "Prefab thumbnail must save."
            );

            List<string> all = CollectClearTargets(k_Folder, UniThumbBatchMenus.BatchScope.All);
            Assert.Contains(k_ScenePath, all);
            Assert.Contains(k_PrefabPath, all);

            List<string> scenesOnly = CollectClearTargets(
                k_Folder,
                UniThumbBatchMenus.BatchScope.ScenesOnly
            );
            Assert.Contains(k_ScenePath, scenesOnly);
            Assert.IsFalse(scenesOnly.Contains(k_PrefabPath));

            List<string> prefabsOnly = CollectClearTargets(
                k_Folder,
                UniThumbBatchMenus.BatchScope.PrefabsOnly
            );
            Assert.Contains(k_PrefabPath, prefabsOnly);
            Assert.IsFalse(prefabsOnly.Contains(k_ScenePath));
        }

        [Test]
        public void CollectClearTargets_MissingThumbnail_ExcludedFromCount()
        {
            EnsureTempAssets();
            byte[] png = CreateTestPng();
            Assert.IsTrue(UniThumbStorage.Save(k_ScenePath, png), "Scene thumbnail must save.");

            List<string> all = CollectClearTargets(k_Folder, UniThumbBatchMenus.BatchScope.All);
            Assert.Contains(k_ScenePath, all);
            Assert.IsFalse(
                all.Contains(k_PrefabPath),
                "Prefab without a thumbnail must not count as a clear target."
            );

            List<string> prefabsOnly = CollectClearTargets(
                k_Folder,
                UniThumbBatchMenus.BatchScope.PrefabsOnly
            );
            Assert.IsEmpty(prefabsOnly, "No prefab thumbnail means zero prefab clear targets.");
        }

        [Test]
        public void BuildClearFolderMessage_StatesScopeCountAndFolder()
        {
            AssertClearMessage(UniThumbBatchMenus.BatchScope.All, "scene/prefab(s)");
            AssertClearMessage(UniThumbBatchMenus.BatchScope.ScenesOnly, "scene(s)");
            AssertClearMessage(UniThumbBatchMenus.BatchScope.PrefabsOnly, "prefab(s)");
        }

        #endregion

        #region Private Methods

        private void AssertClearMessage(UniThumbBatchMenus.BatchScope scope, string noun)
        {
            string message = UniThumbWindow.BuildClearFolderMessage(scope, 2, k_Folder);
            StringAssert.Contains(
                "2",
                message,
                "Scope " + scope + " message must state the count."
            );
            StringAssert.Contains(
                noun,
                message,
                "Scope " + scope + " message must state the noun."
            );
            StringAssert.Contains(
                k_Folder,
                message,
                "Scope " + scope + " message must state the folder."
            );
        }

        /// <summary>
        /// Window ClearFolderThumbnails selection seam: scoped folder assets
        /// filtered by thumbnail presence before the confirm dialog.
        /// </summary>
        private static List<string> CollectClearTargets(
            string folderPath,
            UniThumbBatchMenus.BatchScope scope
        )
        {
            List<string> assets = UniThumbBatchMenus.CollectFolderPaths(folderPath, scope);
            var targets = new List<string>(assets.Count);
            for (int i = 0; i < assets.Count; i++)
            {
                if (UniThumbStorage.HasThumbnail(assets[i]))
                {
                    targets.Add(assets[i]);
                }
            }
            return targets;
        }

        private void EnsureTempAssets()
        {
            if (!AssetDatabase.IsValidFolder(k_Folder))
            {
                string guid = AssetDatabase.CreateFolder("Assets", "__UniThumbBatchScopeFilter");
                Assert.IsFalse(string.IsNullOrEmpty(guid), "Temp filter folder must be created.");
            }
            if (_prevScenePath == null)
            {
                _prevScenePath = EditorSceneManager.GetActiveScene().path;
            }
            bool needScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(k_ScenePath) == null;
            if (needScene)
            {
                Scene scene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Single
                );
                EditorSceneManager.SaveScene(scene, k_ScenePath);
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(k_PrefabPath) == null)
            {
                _source = new GameObject("UniThumbBatchScopeFilterSource");
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(_source, k_PrefabPath);
                Assert.IsNotNull(saved, "Temp prefab asset must be created.");
            }
            AssetDatabase.Refresh();
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<SceneAsset>(k_ScenePath),
                "Temp scene asset must exist."
            );
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(k_PrefabPath),
                "Temp prefab asset must exist."
            );
        }

        private void RestorePreviousScene()
        {
            if (_prevScenePath == null)
            {
                return;
            }
            try
            {
                if (string.IsNullOrEmpty(_prevScenePath))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.OpenScene(_prevScenePath, OpenSceneMode.Single);
                }
            }
            finally
            {
                _prevScenePath = null;
            }
        }

        private static byte[] CreateTestPng()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(new Color[] { Color.red, Color.green, Color.blue, Color.white });
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        #endregion
    }
}
