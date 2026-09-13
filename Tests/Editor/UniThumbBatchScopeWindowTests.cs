using NUnit.Framework;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbBatchScopeWindowTests
    {
        [Test]
        public void BatchScopeNoun_All_ReturnsMixedNoun()
        {
            Assert.AreEqual(
                "scene/prefab(s)",
                UniThumbWindow.BatchScopeNoun(UniThumbBatchMenus.BatchScope.All)
            );
        }

        [Test]
        public void BatchScopeNoun_ScenesOnly_ReturnsSceneNoun()
        {
            Assert.AreEqual(
                "scene(s)",
                UniThumbWindow.BatchScopeNoun(UniThumbBatchMenus.BatchScope.ScenesOnly)
            );
        }

        [Test]
        public void BatchScopeNoun_PrefabsOnly_ReturnsPrefabNoun()
        {
            Assert.AreEqual(
                "prefab(s)",
                UniThumbWindow.BatchScopeNoun(UniThumbBatchMenus.BatchScope.PrefabsOnly)
            );
        }

        [Test]
        public void BuildClearFolderMessage_StatesScopeCountAndFolder()
        {
            string message = UniThumbWindow.BuildClearFolderMessage(
                UniThumbBatchMenus.BatchScope.PrefabsOnly,
                3,
                "Assets/Folder"
            );
            StringAssert.Contains("3", message);
            StringAssert.Contains("prefab(s)", message);
            StringAssert.Contains("Assets/Folder", message);
        }

        [Test]
        public void BuildClearFolderMessage_ScenesOnly_StatesSceneNoun()
        {
            string message = UniThumbWindow.BuildClearFolderMessage(
                UniThumbBatchMenus.BatchScope.ScenesOnly,
                1,
                "Assets/Folder"
            );
            StringAssert.Contains("scene(s)", message);
            StringAssert.Contains("Assets/Folder", message);
        }
    }
}
