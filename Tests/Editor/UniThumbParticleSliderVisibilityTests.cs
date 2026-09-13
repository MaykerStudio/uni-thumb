using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Particle Time slider visibility: the slider shows only while the
    /// current target (stage-open prefab wins, else the selected prefab)
    /// contains a ParticleSystem or VisualEffect; hidden for scenes and
    /// particle-less prefabs. Written first (Red): PrefabHasParticles and
    /// the visibility drive do not exist pre-fix, so the rule tests fail
    /// and the slider stays always visible.
    /// </summary>
    [TestFixture]
    public class UniThumbParticleSliderVisibilityTests
    {
        private const string k_ParticlePrefabPath = "Assets/__UniThumbTest_SliderParticle.prefab";
        private const string k_PlainPrefabPath = "Assets/__UniThumbTest_SliderPlain.prefab";

        private readonly List<GameObject> _tempSources = new List<GameObject>();
        private readonly List<string> _tempAssetPaths = new List<string>();
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
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                StageUtility.GoToMainStage();
            }
            Selection.activeObject = _previousSelection;
            _previousSelection = null;
            for (int i = 0; i < _tempSources.Count; i++)
            {
                if (_tempSources[i] != null)
                {
                    Object.DestroyImmediate(_tempSources[i]);
                }
            }
            _tempSources.Clear();
            for (int i = 0; i < _tempAssetPaths.Count; i++)
            {
                string guid = AssetDatabase.AssetPathToGUID(_tempAssetPaths[i]);
                if (!string.IsNullOrEmpty(guid))
                {
                    UniThumbStorage.DeleteByGuid(guid);
                }
                AssetDatabase.DeleteAsset(_tempAssetPaths[i]);
            }
            _tempAssetPaths.Clear();
            UniThumbStorage.ClearCache();
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }
            UniThumbGuard.Exit();
        }

        [Test]
        public void ShouldShowParticleSlider_Rule()
        {
            Assert.IsTrue(UniThumbCapture.ShouldShowParticleSlider(true, true));
            Assert.IsFalse(UniThumbCapture.ShouldShowParticleSlider(true, false));
            Assert.IsFalse(UniThumbCapture.ShouldShowParticleSlider(false, true));
            Assert.IsFalse(UniThumbCapture.ShouldShowParticleSlider(false, false));
        }

        [Test]
        public void PrefabHasParticles_NullOrMissingPath_False()
        {
            Assert.IsFalse(UniThumbCapture.PrefabHasParticles((string)null));
            Assert.IsFalse(UniThumbCapture.PrefabHasParticles(string.Empty));
            Assert.IsFalse(
                UniThumbCapture.PrefabHasParticles("Assets/__UniThumbTest_Missing.prefab")
            );
            Assert.IsFalse(UniThumbCapture.PrefabHasParticles("Assets/SomeScene.unity"));
        }

        [Test]
        public void PrefabHasParticles_NullRoot_False()
        {
            Assert.IsFalse(UniThumbCapture.PrefabHasParticles((GameObject)null));
        }

        [Test]
        public void PrefabHasParticles_EmptyRoot_False()
        {
            GameObject root = new GameObject("SliderEmptyRoot");
            try
            {
                Assert.IsFalse(UniThumbCapture.PrefabHasParticles(root));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PrefabHasParticles_ParticleSystemRoot_True()
        {
            GameObject root = new GameObject("SliderParticleRoot");
            try
            {
                root.AddComponent<ParticleSystem>();
                GameObject childGo = new GameObject("SliderParticleChild");
                childGo.transform.SetParent(root.transform, false);
                childGo.AddComponent<ParticleSystem>();
                Assert.IsTrue(UniThumbCapture.PrefabHasParticles(root));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PrefabHasParticles_VfxRoot_MatchesPackagePresence()
        {
            GameObject root = new GameObject("SliderVfxRoot");
            try
            {
                System.Type vfxType = UniThumbCapture.FindVisualEffectType();
                if (vfxType != null)
                {
                    root.AddComponent(vfxType);
                }
                Assert.AreEqual(vfxType != null, UniThumbCapture.PrefabHasParticles(root));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PrefabHasParticles_ParticleAsset_True()
        {
            GameObject asset = CreatePrefabAsset(k_ParticlePrefabPath, true);
            Assert.IsNotNull(asset);
            Assert.IsTrue(UniThumbCapture.PrefabHasParticles(k_ParticlePrefabPath));
        }

        [Test]
        public void PrefabHasParticles_PlainAsset_False()
        {
            GameObject asset = CreatePrefabAsset(k_PlainPrefabPath, false);
            Assert.IsNotNull(asset);
            Assert.IsFalse(UniThumbCapture.PrefabHasParticles(k_PlainPrefabPath));
        }

        [Test]
        public void PrefabHasParticles_LeavesSceneUnchanged()
        {
            GameObject asset = CreatePrefabAsset(k_ParticlePrefabPath, true);
            Assert.IsNotNull(asset);
            Scene scene = SceneManager.GetActiveScene();
            int rootsBefore = scene.GetRootGameObjects().Length;
            int undoBefore = Undo.GetCurrentGroup();
            Assert.IsTrue(UniThumbCapture.PrefabHasParticles(k_ParticlePrefabPath));
            Assert.AreEqual(rootsBefore, scene.GetRootGameObjects().Length);
            Assert.AreEqual(undoBefore, Undo.GetCurrentGroup());
        }

        [Test]
        public void ParticlePrefabSelected_SliderVisible()
        {
            GameObject asset = CreatePrefabAsset(k_ParticlePrefabPath, true);
            Selection.activeObject = asset;

            _window.OnProjectSelectionChanged();

            Slider slider = ParticleSlider();
            Assert.AreEqual(DisplayStyle.Flex, slider.style.display.value);
            Assert.AreEqual(DisplayStyle.Flex, slider.resolvedStyle.display);
        }

        [Test]
        public void PlainPrefabSelected_SliderHidden()
        {
            GameObject asset = CreatePrefabAsset(k_PlainPrefabPath, false);
            Selection.activeObject = asset;

            _window.OnProjectSelectionChanged();

            Slider slider = ParticleSlider();
            Assert.AreEqual(DisplayStyle.None, slider.style.display.value);
        }

        [Test]
        public void SceneSelected_SliderHidden()
        {
            Selection.activeObject = null;

            _window.OnProjectSelectionChanged();

            Slider slider = ParticleSlider();
            Assert.AreEqual(DisplayStyle.None, slider.style.display.value);
        }

        [Test]
        public void SliderValue_PreservedWhileHidden()
        {
            GameObject asset = CreatePrefabAsset(k_ParticlePrefabPath, true);
            Selection.activeObject = asset;
            _window.OnProjectSelectionChanged();
            Slider slider = ParticleSlider();
            slider.value = 2.5f;

            Selection.activeObject = null;
            _window.OnProjectSelectionChanged();

            Assert.AreEqual(DisplayStyle.None, slider.style.display.value);
            Assert.AreEqual(2.5f, slider.value, 0.001f);

            Selection.activeObject = asset;
            _window.OnProjectSelectionChanged();

            Assert.AreEqual(DisplayStyle.Flex, slider.style.display.value);
            Assert.AreEqual(2.5f, slider.value, 0.001f);
        }

        [Test]
        public void StageOpenParticlePrefab_SliderVisible()
        {
            GameObject asset = CreatePrefabAsset(k_ParticlePrefabPath, true);
            Assert.IsNotNull(asset);
            PrefabStage stage = PrefabStageUtility.OpenPrefab(k_ParticlePrefabPath);
            if (stage == null || PrefabStageUtility.GetCurrentPrefabStage() == null)
            {
                Assert.Ignore("Prefab Stage unavailable in this runner.");
            }
            try
            {
                Selection.activeObject = null;

                _window.OnProjectSelectionChanged();

                Slider slider = ParticleSlider();
                Assert.AreEqual(DisplayStyle.Flex, slider.style.display.value);
            }
            finally
            {
                StageUtility.GoToMainStage();
            }
        }

        private GameObject CreatePrefabAsset(string assetPath, bool withParticles)
        {
            GameObject source = new GameObject("UniThumbSliderTempSource");
            if (withParticles)
            {
                source.AddComponent<ParticleSystem>();
            }
            else
            {
                source.AddComponent<BoxCollider>();
            }
            _tempSources.Add(source);
            GameObject asset = PrefabUtility.SaveAsPrefabAsset(source, assetPath);
            _tempAssetPaths.Add(assetPath);
            return asset;
        }

        private Slider ParticleSlider()
        {
            Slider slider = _window.rootVisualElement.Q<Slider>("particle-preview-time-slider");
            Assert.IsNotNull(slider, "Particle Time slider must exist.");
            return slider;
        }
    }
}
