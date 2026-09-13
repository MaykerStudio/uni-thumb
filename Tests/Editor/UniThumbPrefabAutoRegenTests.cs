using System.Collections.Generic;
using NUnit.Framework;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbPrefabAutoRegenTests
    {
        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbGuard.Exit();
        }

        [Test]
        public void CollectPrefabPaths_NullInput_ReturnsEmpty()
        {
            List<string> result = UniThumbPrefabAutoRegen.CollectPrefabPaths(null);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void CollectPrefabPaths_FiltersNonPrefabPaths()
        {
            string[] paths =
            {
                "Assets/Level.unity",
                "Assets/Enemy.prefab",
                "Assets/Icon.png",
                "Assets/Settings.asset",
                null,
                string.Empty,
            };
            List<string> result = UniThumbPrefabAutoRegen.CollectPrefabPaths(paths);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Assets/Enemy.prefab", result[0]);
        }

        [Test]
        public void CollectPrefabPaths_MultiSaveKeepsAllPrefabsInOrder()
        {
            string[] paths =
            {
                "Assets/A.prefab",
                "Assets/B.prefab",
                "Assets/Level.unity",
                "Assets/C.prefab",
            };
            List<string> result = UniThumbPrefabAutoRegen.CollectPrefabPaths(paths);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("Assets/A.prefab", result[0]);
            Assert.AreEqual("Assets/B.prefab", result[1]);
            Assert.AreEqual("Assets/C.prefab", result[2]);
        }

        [Test]
        public void CollectPrefabPaths_DeduplicatesRepeatedEntries()
        {
            string[] paths = { "Assets/A.prefab", "Assets/A.prefab", "Assets/B.prefab" };
            List<string> result = UniThumbPrefabAutoRegen.CollectPrefabPaths(paths);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("Assets/A.prefab", result[0]);
            Assert.AreEqual("Assets/B.prefab", result[1]);
        }

        [Test]
        public void CollectPrefabPaths_MatchesCaseInsensitiveExtension()
        {
            string[] paths = { "Assets/Upper.PREFAB" };
            List<string> result = UniThumbPrefabAutoRegen.CollectPrefabPaths(paths);
            Assert.AreEqual(1, result.Count);
        }

        [Test]
        public void CollectPrefabPaths_VariantAndNestedPathsKeptAsSeparateEntries()
        {
            string[] paths =
            {
                "Assets/Base.prefab",
                "Assets/Variants/Base_Variant.prefab",
                "Assets/Nested/Child.prefab",
            };
            List<string> result = UniThumbPrefabAutoRegen.CollectPrefabPaths(paths);
            Assert.AreEqual(3, result.Count);
        }

        [Test]
        public void ShouldAutoRegenerate_RequiresSettingWindowAndFreeGuard()
        {
            Assert.IsTrue(UniThumbPrefabAutoRegen.ShouldAutoRegenerate(true, true, false));
            Assert.IsFalse(UniThumbPrefabAutoRegen.ShouldAutoRegenerate(false, true, false));
            Assert.IsFalse(UniThumbPrefabAutoRegen.ShouldAutoRegenerate(true, false, false));
            Assert.IsFalse(UniThumbPrefabAutoRegen.ShouldAutoRegenerate(true, true, true));
            Assert.IsFalse(UniThumbPrefabAutoRegen.ShouldAutoRegenerate(false, false, true));
        }

        [Test]
        public void HandleWillSaveAssets_ReturnsOriginalPathsUnchanged()
        {
            string[] paths = { "Assets/A.prefab", "Assets/Level.unity" };
            string[] returned = UniThumbPrefabSaveProcessor.HandleWillSaveAssetsForTest(paths);
            Assert.AreSame(paths, returned);
        }

        [Test]
        public void HandleWillSaveAssets_NullInput_ReturnsNull()
        {
            Assert.IsNull(UniThumbPrefabSaveProcessor.HandleWillSaveAssetsForTest(null));
        }
    }
}
