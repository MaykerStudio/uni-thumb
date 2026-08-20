using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbStorageStalenessTests
    {
        private const string k_TempDir = "Assets/_UniThumbTest";
        private const string k_TempScenePath = k_TempDir + "/StalenessTest.unity";
        private const string k_PrefsPrefix = "SceneThumbs.v4";
        private string _sceneGuid;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(k_TempDir))
            {
                AssetDatabase.CreateFolder("Assets", "_UniThumbTest");
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), k_TempScenePath);
            AssetDatabase.Refresh();

            _sceneGuid = AssetDatabase.AssetPathToGUID(k_TempScenePath);

            long ticks = File.GetLastWriteTimeUtc(
                Path.Combine(Path.GetDirectoryName(Application.dataPath), k_TempScenePath)
            ).Ticks;
            EditorPrefs.SetString(
                k_PrefsPrefix + "." + _sceneGuid,
                ticks.ToString(CultureInfo.InvariantCulture)
            );
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_sceneGuid))
            {
                EditorPrefs.DeleteKey(k_PrefsPrefix + "." + _sceneGuid);
                UniThumbStorage.DeleteFingerprint(_sceneGuid);
            }

            if (AssetDatabase.IsValidFolder(k_TempDir))
            {
                AssetDatabase.DeleteAsset(k_TempDir);
            }
        }

        [Test]
        public void IsSceneStale_SameFingerprint_ReturnsFalse()
        {
            UniThumbFingerprint.SceneFingerprint fp = UniThumbFingerprint.Compute();
            UniThumbStorage.SaveFingerprint(k_TempScenePath, fp);

            long ticks = File.GetLastWriteTimeUtc(
                Path.Combine(Path.GetDirectoryName(Application.dataPath), k_TempScenePath)
            ).Ticks;
            EditorPrefs.SetString(
                k_PrefsPrefix + "." + _sceneGuid,
                ticks.ToString(CultureInfo.InvariantCulture)
            );

            bool stale = UniThumbStorage.IsSceneStale(k_TempScenePath, UniThumbFingerprint.Compute);
            Assert.IsFalse(stale);
        }

        [Test]
        public void IsSceneStale_DifferentFingerprint_ReturnsTrue()
        {
            var fakeFp = new UniThumbFingerprint.SceneFingerprint(999, 999, 999, 999, 999);
            UniThumbStorage.SaveFingerprint(k_TempScenePath, fakeFp);

            long ticks = File.GetLastWriteTimeUtc(
                Path.Combine(Path.GetDirectoryName(Application.dataPath), k_TempScenePath)
            ).Ticks;
            EditorPrefs.SetString(
                k_PrefsPrefix + "." + _sceneGuid,
                ticks.ToString(CultureInfo.InvariantCulture)
            );

            bool stale = UniThumbStorage.IsSceneStale(k_TempScenePath, UniThumbFingerprint.Compute);
            Assert.IsTrue(stale);
        }

        [Test]
        public void IsSceneStale_NoFingerprint_ReturnsFalse()
        {
            bool stale = UniThumbStorage.IsSceneStale(k_TempScenePath, UniThumbFingerprint.Compute);
            Assert.IsFalse(stale);
        }
    }
}
