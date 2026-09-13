using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Prefab-mode indicator tests: the window shows a Prefab chip plus
    /// context text while a prefab asset is selected, and the Scene state
    /// otherwise. Written first (Red): the SourceModeBadgeText /
    /// SourceModeTitleText seams and the badge labels do not exist pre-fix.
    /// </summary>
    [TestFixture]
    public class UniThumbPrefabModeIndicatorTests
    {
        private const string k_TempPrefabPath = "Assets/__UniThumbTest_ModePrefab.prefab";

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

        [Test]
        public void SourceModeBadgeText_Prefab_ReturnsPrefab()
        {
            Assert.AreEqual("Prefab", UniThumbWindow.SourceModeBadgeText(true));
        }

        [Test]
        public void SourceModeBadgeText_Scene_ReturnsScene()
        {
            Assert.AreEqual("Scene", UniThumbWindow.SourceModeBadgeText(false));
        }

        [Test]
        public void SourceModeTitleText_Prefab_ReturnsSelectedPrefab()
        {
            Assert.AreEqual("Selected Prefab", UniThumbWindow.SourceModeTitleText(true));
        }

        [Test]
        public void SourceModeTitleText_Scene_ReturnsActiveScene()
        {
            Assert.AreEqual("Active Scene", UniThumbWindow.SourceModeTitleText(false));
        }

        [Test]
        public void PrefabSelection_ShowsPrefabIndicator()
        {
            _tempSource = new GameObject("UniThumbModeTempSource");
            _tempSource.AddComponent<BoxCollider>();
            _tempPrefabAsset = PrefabUtility.SaveAsPrefabAsset(_tempSource, k_TempPrefabPath);
            Assert.IsNotNull(_tempPrefabAsset, "Temp prefab asset must be created.");
            Selection.activeObject = _tempPrefabAsset;

            _window.OnProjectSelectionChanged();

            Assert.IsTrue(
                _window.IsPrefabPreview,
                "Window must report prefab preview mode while a prefab is selected."
            );
            Label badge = _window.rootVisualElement.Q<Label>("source-mode-badge");
            Assert.IsNotNull(badge, "Header mode badge must exist.");
            Assert.AreEqual("Prefab", badge.text);
            Assert.IsFalse(
                badge.ClassListContains("stt-hidden"),
                "Header mode badge must be visible in prefab mode."
            );
            Label title = _window.rootVisualElement.Q<Label>("source-title-label");
            Assert.IsNotNull(title, "Header source title must exist.");
            Assert.AreEqual("Selected Prefab", title.text);
            Label previewBadge = _window.rootVisualElement.Q<Label>("preview-mode-badge");
            Assert.IsNotNull(previewBadge, "Preview mode badge must exist.");
            Assert.IsFalse(
                previewBadge.ClassListContains("stt-hidden"),
                "Preview mode badge must be visible in prefab mode."
            );
            Label sceneLabel = _window.rootVisualElement.Q<Label>("active-scene-label");
            Assert.IsNotNull(sceneLabel, "Source path label must exist.");
            StringAssert.Contains("ModePrefab", sceneLabel.text);
        }

        [Test]
        public void SceneSelection_ShowsSceneIndicator()
        {
            Selection.activeObject = null;

            _window.OnProjectSelectionChanged();

            Assert.IsFalse(
                _window.IsPrefabPreview,
                "Window must report scene mode when no prefab is selected."
            );
            Label badge = _window.rootVisualElement.Q<Label>("source-mode-badge");
            Assert.IsNotNull(badge, "Header mode badge must exist.");
            Assert.AreEqual("Scene", badge.text);
            Label title = _window.rootVisualElement.Q<Label>("source-title-label");
            Assert.IsNotNull(title, "Header source title must exist.");
            Assert.AreEqual("Active Scene", title.text);
            Label previewBadge = _window.rootVisualElement.Q<Label>("preview-mode-badge");
            Assert.IsNotNull(previewBadge, "Preview mode badge must exist.");
            Assert.IsTrue(
                previewBadge.ClassListContains("stt-hidden"),
                "Preview mode badge must stay hidden in scene mode."
            );
        }
    }
}
