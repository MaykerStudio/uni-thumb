using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbPrefabIsolationTests
    {
        [Test]
        public void IsPrefabAssetPath_PrefabExtension_ReturnsTrue()
        {
            Assert.IsTrue(UniThumbCapture.IsPrefabAssetPath("Assets/Foo.prefab"));
            Assert.IsTrue(UniThumbCapture.IsPrefabAssetPath("Assets/Foo.PREFAB"));
        }

        [Test]
        public void IsPrefabAssetPath_NonPrefabOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(UniThumbCapture.IsPrefabAssetPath("Assets/Foo.unity"));
            Assert.IsFalse(UniThumbCapture.IsPrefabAssetPath(string.Empty));
            Assert.IsFalse(UniThumbCapture.IsPrefabAssetPath(null));
        }

        [Test]
        public void TryGetPrefabStageAssetPath_NoStageOpen_ReturnsFalse()
        {
            bool hasStage = UniThumbCapture.TryGetPrefabStageAssetPath(out string stagePath);
            Assert.IsFalse(hasStage, "No Prefab Stage is open in the test runner.");
            Assert.IsNull(stagePath);
        }

        [Test]
        public void TryResolvePrefabAssetPath_InvalidPathNoStage_ReturnsFalse()
        {
            Assert.IsFalse(
                UniThumbCapture.TryResolvePrefabAssetPath(
                    "Assets/NotAPrefab.unity",
                    out string resolved
                )
            );
            Assert.IsNull(resolved);
            Assert.IsFalse(UniThumbCapture.TryResolvePrefabAssetPath(null, out resolved));
            Assert.IsNull(resolved);
        }

        [Test]
        public void PrefabLightingNeedsNeutralize_NoneMode_ReturnsTrue()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Light2DMode = LightingMode.None;
            Assert.IsTrue(UniThumbCapture.PrefabLightingNeedsNeutralize(settings));
        }

        [Test]
        public void PrefabLightingNeedsNeutralize_ExplicitModes_ReturnsFalse()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Light2DMode = LightingMode.Light2D;
            Assert.IsFalse(UniThumbCapture.PrefabLightingNeedsNeutralize(settings));
            settings.Light2DMode = LightingMode.Light3D;
            Assert.IsFalse(UniThumbCapture.PrefabLightingNeedsNeutralize(settings));
        }

        [Test]
        public void SnapshotRestorePrefabEnvironment_SolidColorMode_NeutralizesAndRestores()
        {
            Material originalSkybox = RenderSettings.skybox;
            bool originalFog = RenderSettings.fog;
            AmbientMode originalAmbientMode = RenderSettings.ambientMode;
            Color originalAmbientLight = RenderSettings.ambientLight;
            float originalAmbientIntensity = RenderSettings.ambientIntensity;
            float originalReflection = RenderSettings.reflectionIntensity;
            try
            {
                // Snapshot the pristine state BEFORE any test mutation: the
                // restore below must heal the original, not the mutated state.
                UniThumbCapture.PrefabEnvSnapshot snapshot =
                    UniThumbCapture.SnapshotPrefabEnvironment();
                Assert.IsTrue(snapshot.Valid);

                RenderSettings.fog = true;
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = 0f;
                RenderSettings.reflectionIntensity = 1f;

                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.BackgroundMode = BackgroundMode.SolidColor;
                UniThumbCapture.NeutralizePrefabEnvironment(settings);

                Assert.IsNull(RenderSettings.skybox);
                Assert.IsFalse(RenderSettings.fog);
                Assert.AreEqual(AmbientMode.Flat, RenderSettings.ambientMode);
                Assert.AreEqual(new Color(0.5f, 0.5f, 0.5f, 1f), RenderSettings.ambientLight);
                Assert.AreEqual(1f, RenderSettings.ambientIntensity);
                Assert.AreEqual(0f, RenderSettings.reflectionIntensity);

                UniThumbCapture.RestorePrefabEnvironment(snapshot);
                Assert.AreEqual(originalSkybox, RenderSettings.skybox);
                Assert.AreEqual(originalFog, RenderSettings.fog);
                Assert.AreEqual(originalAmbientMode, RenderSettings.ambientMode);
                Assert.AreEqual(originalAmbientLight, RenderSettings.ambientLight);
                Assert.AreEqual(originalAmbientIntensity, RenderSettings.ambientIntensity);
                Assert.AreEqual(originalReflection, RenderSettings.reflectionIntensity);
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                RenderSettings.fog = originalFog;
                RenderSettings.ambientMode = originalAmbientMode;
                RenderSettings.ambientLight = originalAmbientLight;
                RenderSettings.ambientIntensity = originalAmbientIntensity;
                RenderSettings.reflectionIntensity = originalReflection;
            }
        }

        [Test]
        public void NeutralizePrefabEnvironment_SkyboxMode_PreservesSkybox()
        {
            Shader skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader == null)
            {
                Assert.Ignore("Procedural skybox shader unavailable in this project.");
            }
            Material skybox = new Material(skyShader);
            Material originalSkybox = RenderSettings.skybox;
            bool originalFog = RenderSettings.fog;
            try
            {
                RenderSettings.skybox = skybox;
                RenderSettings.fog = true;

                UniThumbCapture.PrefabEnvSnapshot snapshot =
                    UniThumbCapture.SnapshotPrefabEnvironment();
                try
                {
                    CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                    settings.BackgroundMode = BackgroundMode.Skybox;
                    UniThumbCapture.NeutralizePrefabEnvironment(settings);

                    Assert.AreEqual(skybox, RenderSettings.skybox);
                    Assert.IsFalse(RenderSettings.fog);
                }
                finally
                {
                    UniThumbCapture.RestorePrefabEnvironment(snapshot);
                }
                Assert.AreEqual(skybox, RenderSettings.skybox);
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                RenderSettings.fog = originalFog;
                Object.DestroyImmediate(skybox);
            }
        }

        [Test]
        public void RestorePrefabEnvironment_InvalidSnapshot_IsNoOp()
        {
            Assert.DoesNotThrow(() =>
                UniThumbCapture.RestorePrefabEnvironment(new UniThumbCapture.PrefabEnvSnapshot())
            );
        }

        [Test]
        public void DisableSceneVolumes_VolumeOutsideInstance_DisabledAndRestored()
        {
            System.Type volumeType = FindVolumeTypeForTest();
            if (volumeType == null)
            {
                Assert.Ignore("SRP Volume type unavailable in this project.");
            }

            GameObject instanceRoot = new GameObject("PrefabIsolationInstance");
            GameObject outsideGo = new GameObject("PrefabIsolationOutside");
            Component outsideVolume = outsideGo.AddComponent(volumeType);
            Behaviour outsideBehaviour = outsideVolume as Behaviour;
            Assert.IsNotNull(outsideBehaviour);
            outsideBehaviour.enabled = true;
            try
            {
                List<UniThumbCapture.VolumeSnapshot> snapshots =
                    UniThumbCapture.DisableSceneVolumes(instanceRoot, null);
                Assert.IsNotNull(snapshots);
                try
                {
                    Assert.IsFalse(outsideBehaviour.enabled);
                }
                finally
                {
                    UniThumbCapture.RestoreSceneVolumes(snapshots);
                }
                Assert.IsTrue(outsideBehaviour.enabled);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void DisableSceneVolumes_VolumeInsideInstance_KeepsEnabled()
        {
            System.Type volumeType = FindVolumeTypeForTest();
            if (volumeType == null)
            {
                Assert.Ignore("SRP Volume type unavailable in this project.");
            }

            GameObject instanceRoot = new GameObject("PrefabIsolationInstance");
            GameObject insideGo = new GameObject("PrefabIsolationInside");
            insideGo.transform.SetParent(instanceRoot.transform, false);
            Component insideVolume = insideGo.AddComponent(volumeType);
            Behaviour insideBehaviour = insideVolume as Behaviour;
            Assert.IsNotNull(insideBehaviour);
            insideBehaviour.enabled = true;
            try
            {
                List<UniThumbCapture.VolumeSnapshot> snapshots =
                    UniThumbCapture.DisableSceneVolumes(instanceRoot, null);
                try
                {
                    Assert.IsTrue(insideBehaviour.enabled);
                }
                finally
                {
                    UniThumbCapture.RestoreSceneVolumes(snapshots);
                }
            }
            finally
            {
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void ReenableSubtreeLights_RestoresInstanceLightsOnly()
        {
            GameObject instanceRoot = new GameObject("PrefabLightInstance");
            GameObject insideGo = new GameObject("PrefabLightInside");
            insideGo.transform.SetParent(instanceRoot.transform, false);
            Light insideLight = insideGo.AddComponent<Light>();
            insideLight.enabled = true;
            insideLight.intensity = 2f;
            GameObject outsideGo = new GameObject("PrefabLightOutside");
            Light outsideLight = outsideGo.AddComponent<Light>();
            outsideLight.enabled = true;
            try
            {
                var snapshots = new List<UniThumbCapture.LightSnapshot>
                {
                    new UniThumbCapture.LightSnapshot
                    {
                        Light = insideLight,
                        Enabled = true,
                        Intensity = 2f,
                    },
                    new UniThumbCapture.LightSnapshot
                    {
                        Light = outsideLight,
                        Enabled = true,
                        Intensity = 1f,
                    },
                };
                insideLight.enabled = false;
                insideLight.intensity = 0f;
                outsideLight.enabled = false;
                outsideLight.intensity = 0f;

                UniThumbCapture.ReenableSubtreeLights(instanceRoot, snapshots);

                Assert.IsTrue(insideLight.enabled);
                Assert.AreEqual(2f, insideLight.intensity);
                Assert.IsFalse(outsideLight.enabled);
                Assert.AreEqual(0f, outsideLight.intensity);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void IsInPrefabSubtree_RootSelfChildAndOutside_Classified()
        {
            GameObject root = new GameObject("SubtreeRoot");
            GameObject child = new GameObject("SubtreeChild");
            child.transform.SetParent(root.transform, false);
            GameObject outside = new GameObject("SubtreeOutside");
            try
            {
                Assert.IsTrue(UniThumbCapture.IsInPrefabSubtree(root.transform, root.transform));
                Assert.IsTrue(UniThumbCapture.IsInPrefabSubtree(child.transform, root.transform));
                Assert.IsFalse(
                    UniThumbCapture.IsInPrefabSubtree(outside.transform, root.transform)
                );
                Assert.IsFalse(UniThumbCapture.IsInPrefabSubtree(null, root.transform));
                Assert.IsFalse(UniThumbCapture.IsInPrefabSubtree(child.transform, null));
            }
            finally
            {
                Object.DestroyImmediate(outside);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ReenableSubtreeLights_LightOnRoot_Restored()
        {
            GameObject instanceRoot = new GameObject("PrefabRootLightInstance");
            Light rootLight = instanceRoot.AddComponent<Light>();
            rootLight.type = LightType.Directional;
            rootLight.enabled = true;
            rootLight.intensity = 2f;
            GameObject outsideGo = new GameObject("PrefabRootLightOutside");
            Light outsideLight = outsideGo.AddComponent<Light>();
            outsideLight.enabled = true;
            try
            {
                var snapshots = new List<UniThumbCapture.LightSnapshot>
                {
                    new UniThumbCapture.LightSnapshot
                    {
                        Light = rootLight,
                        Enabled = true,
                        Intensity = 2f,
                    },
                    new UniThumbCapture.LightSnapshot
                    {
                        Light = outsideLight,
                        Enabled = true,
                        Intensity = 1f,
                    },
                };
                rootLight.enabled = false;
                rootLight.intensity = 0f;
                outsideLight.enabled = false;
                outsideLight.intensity = 0f;

                UniThumbCapture.ReenableSubtreeLights(instanceRoot, snapshots);

                Assert.IsTrue(rootLight.enabled);
                Assert.AreEqual(2f, rootLight.intensity);
                Assert.IsFalse(outsideLight.enabled);
                Assert.AreEqual(0f, outsideLight.intensity);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void DisableSceneVolumes_VolumeOnRoot_KeepsEnabled()
        {
            System.Type volumeType = FindVolumeTypeForTest();
            if (volumeType == null)
            {
                Assert.Ignore("SRP Volume type unavailable in this project.");
            }

            GameObject instanceRoot = new GameObject("PrefabVolumeRootInstance");
            Component rootVolume = instanceRoot.AddComponent(volumeType);
            Behaviour rootBehaviour = rootVolume as Behaviour;
            Assert.IsNotNull(rootBehaviour);
            rootBehaviour.enabled = true;
            try
            {
                List<UniThumbCapture.VolumeSnapshot> snapshots =
                    UniThumbCapture.DisableSceneVolumes(instanceRoot, null);
                try
                {
                    Assert.IsTrue(rootBehaviour.enabled);
                }
                finally
                {
                    UniThumbCapture.RestoreSceneVolumes(snapshots);
                }
            }
            finally
            {
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void PrefabSubtreeHas3DContent_MeshVsEmpty_Detected()
        {
            GameObject empty = new GameObject("ContentEmpty");
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                Assert.IsFalse(UniThumbCapture.PrefabSubtreeHas3DContent(empty));
                Assert.IsTrue(UniThumbCapture.PrefabSubtreeHas3DContent(cube));
                Assert.IsTrue(UniThumbCapture.PrefabSubtreeHas3DContent(null));
            }
            finally
            {
                Object.DestroyImmediate(cube);
                Object.DestroyImmediate(empty);
            }
        }

        [Test]
        public void PrefabSubtreeHasOwnKeyLight_DirectionalOnRoot_Detected()
        {
            GameObject root = new GameObject("OwnKeyRoot");
            Light light = root.AddComponent<Light>();
            light.type = LightType.Directional;
            light.enabled = true;
            try
            {
                Assert.IsTrue(UniThumbCapture.PrefabSubtreeHasOwnKeyLight(root, true));
                light.enabled = false;
                Assert.IsFalse(UniThumbCapture.PrefabSubtreeHasOwnKeyLight(root, true));
                light.enabled = true;
                light.type = LightType.Point;
                Assert.IsFalse(UniThumbCapture.PrefabSubtreeHasOwnKeyLight(root, true));
                Assert.IsFalse(UniThumbCapture.PrefabSubtreeHasOwnKeyLight(null, true));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CapturePrefab_SolidColorMode_RendersLitPrefabPixels()
        {
            string assetPath = CreateCubePrefabAsset();
            try
            {
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    int rootsBefore = SceneManager.GetActiveScene().GetRootGameObjects().Length;
                    string scenePathBefore = SceneManager.GetActiveScene().path;
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.SolidColor);
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    Color clear = UniThumbCapture.EffectiveClearColor(settings);
                    int lit = CountDifferingPixels(pixels, clear);
                    Assert.Greater(
                        lit,
                        pixels.Length / 50,
                        "A lit cube must differ from the clear color."
                    );
                    Assert.AreEqual(
                        rootsBefore,
                        SceneManager.GetActiveScene().GetRootGameObjects().Length,
                        "Capture must not leave scene objects behind."
                    );
                    Assert.AreEqual(
                        scenePathBefore,
                        SceneManager.GetActiveScene().path,
                        "Capture must not switch scenes."
                    );
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void CapturePrefab_TransparentMode_RendersLitPrefabOverTransparent()
        {
            string assetPath = CreateCubePrefabAsset();
            try
            {
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.Transparent);
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    int transparent = 0;
                    int opaque = 0;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        if (pixels[i].a < 128)
                        {
                            transparent++;
                        }
                        else
                        {
                            opaque++;
                        }
                    }
                    Assert.Greater(
                        transparent,
                        pixels.Length / 10,
                        "Background must stay transparent."
                    );
                    Assert.Greater(
                        opaque,
                        pixels.Length / 50,
                        "The lit cube must contribute opaque pixels."
                    );
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void CapturePrefab_SkyboxMode_RendersLitPrefabPixels()
        {
            string assetPath = CreateCubePrefabAsset();
            Material originalSkybox = RenderSettings.skybox;
            try
            {
                RenderSettings.skybox = null;
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.Skybox);
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    Color clear = UniThumbCapture.EffectiveClearColor(settings);
                    int lit = CountDifferingPixels(pixels, clear);
                    Assert.Greater(
                        lit,
                        pixels.Length / 50,
                        "A lit cube must differ from the clear color."
                    );
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void CapturePrefab_PrefabStageOpen_RendersLitThumbnail()
        {
            string assetPath = CreateCubePrefabAsset();
            PrefabStage stage = PrefabStageUtility.OpenPrefab(assetPath);
            Assert.IsNotNull(stage, "Prefab Stage should open.");
            try
            {
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.SolidColor);
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    Color clear = UniThumbCapture.EffectiveClearColor(settings);
                    int lit = CountDifferingPixels(pixels, clear);
                    Assert.Greater(
                        lit,
                        pixels.Length / 50,
                        "Stage-open capture must render a lit prefab."
                    );
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void IsolateInstanceRenderers_DisabledSceneRenderer_IsIsolatedAndRestored()
        {
            GameObject instanceRoot = new GameObject("IsolateDisabledInstance");
            GameObject outsideGo = new GameObject("IsolateDisabledOutside");
            MeshRenderer outside = outsideGo.AddComponent<MeshRenderer>();
            outside.enabled = false;
            try
            {
                UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
                Assert.IsNotNull(scan);
                List<Renderer> isolated = UniThumbCapture.IsolateInstanceRenderers(
                    instanceRoot,
                    scan
                );
                try
                {
                    Assert.Contains(outside, isolated);
                    Assert.IsTrue(outside.forceRenderingOff);
                }
                finally
                {
                    UniThumbCapture.RestoreIsolatedRenderers(isolated);
                }
                Assert.IsFalse(outside.forceRenderingOff);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void IsolateInstanceRenderers_InactiveSceneRenderer_IsIsolatedAndRestored()
        {
            GameObject instanceRoot = new GameObject("IsolateInactiveInstance");
            GameObject outsideGo = new GameObject("IsolateInactiveOutside");
            MeshRenderer outside = outsideGo.AddComponent<MeshRenderer>();
            outsideGo.SetActive(false);
            try
            {
                UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
                Assert.IsNotNull(scan);
                List<Renderer> isolated = UniThumbCapture.IsolateInstanceRenderers(
                    instanceRoot,
                    scan
                );
                try
                {
                    Assert.Contains(outside, isolated);
                    Assert.IsTrue(outside.forceRenderingOff);
                }
                finally
                {
                    UniThumbCapture.RestoreIsolatedRenderers(isolated);
                }
                Assert.IsFalse(outside.forceRenderingOff);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void IsolateInstanceCanvasRenderers_InactiveCanvasRenderer_IsolatedAndRestored()
        {
            GameObject instanceRoot = new GameObject("IsolateCanvasInstance");
            GameObject outsideGo = new GameObject("IsolateCanvasOutside");
            CanvasRenderer outside = outsideGo.AddComponent<CanvasRenderer>();
            outsideGo.AddComponent<Canvas>();
            outsideGo.SetActive(false);
            try
            {
                UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
                Assert.IsNotNull(scan);
                List<CanvasRenderer> isolated = UniThumbCapture.IsolateInstanceCanvasRenderers(
                    instanceRoot,
                    scan
                );
                try
                {
                    Assert.Contains(outside, isolated);
                    Assert.IsTrue(outside.cull);
                }
                finally
                {
                    UniThumbCapture.RestoreIsolatedCanvasRenderers(isolated);
                }
                Assert.IsFalse(outside.cull);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void IsolateInstanceTerrains_DisabledTerrain_IsolatedAndRestored()
        {
            GameObject instanceRoot = new GameObject("IsolateTerrainInstance");
            GameObject outsideGo = new GameObject("IsolateTerrainOutside");
            TerrainData data = new TerrainData();
            Terrain outside = outsideGo.AddComponent<Terrain>();
            outside.terrainData = data;
            outside.enabled = false;
            try
            {
                UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
                Assert.IsNotNull(scan);
                List<UniThumbCapture.TerrainSnapshot> isolated =
                    UniThumbCapture.IsolateInstanceTerrains(instanceRoot, scan);
                Assert.IsNotNull(isolated);
                bool found = false;
                for (int i = 0; i < isolated.Count; i++)
                {
                    if (isolated[i] != null && isolated[i].Terrain == outside)
                    {
                        found = true;
                        break;
                    }
                }
                try
                {
                    Assert.IsTrue(found, "Disabled scene terrains must be isolated.");
                }
                finally
                {
                    UniThumbCapture.RestoreIsolatedTerrains(isolated);
                }
                Assert.IsFalse(outside.enabled);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
                Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void IsolateInstanceVfx_DisabledEffect_IsolatedAndRestored()
        {
            System.Type vfxType = UniThumbCapture.FindVisualEffectType();
            if (vfxType == null)
            {
                Assert.Ignore("VFX Graph package absent in this project.");
            }
            GameObject instanceRoot = new GameObject("IsolateVfxInstance");
            GameObject outsideGo = new GameObject("IsolateVfxOutside");
            Component effectComponent = outsideGo.AddComponent(vfxType);
            Behaviour outside = effectComponent as Behaviour;
            Assert.IsNotNull(outside);
            outside.enabled = false;
            try
            {
                UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
                Assert.IsNotNull(scan);
                List<UniThumbCapture.VisualEffectSnapshot> isolated =
                    UniThumbCapture.IsolateInstanceVfx(instanceRoot, scan);
                Assert.IsNotNull(isolated);
                bool found = false;
                for (int i = 0; i < isolated.Count; i++)
                {
                    if (isolated[i] != null && isolated[i].Effect == outside)
                    {
                        found = true;
                        break;
                    }
                }
                try
                {
                    Assert.IsTrue(found, "Disabled scene effects must be isolated.");
                }
                finally
                {
                    UniThumbCapture.RestoreIsolatedVfx(isolated);
                }
                Assert.IsFalse(outside.enabled);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void BeginScan_IncludesInactiveComponentsAndCanvasRenderers()
        {
            GameObject inactiveGo = new GameObject("ScanInactiveProbe");
            MeshRenderer probeRenderer = inactiveGo.AddComponent<MeshRenderer>();
            inactiveGo.SetActive(false);
            GameObject canvasGo = new GameObject("ScanCanvasProbe");
            CanvasRenderer probeCanvasRenderer = canvasGo.AddComponent<CanvasRenderer>();
            canvasGo.AddComponent<Canvas>();
            canvasGo.SetActive(false);
            try
            {
                UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
                Assert.IsNotNull(scan);
                bool foundComponent = false;
                for (int i = 0; i < scan.Components.Length; i++)
                {
                    if (scan.Components[i] == probeRenderer)
                    {
                        foundComponent = true;
                        break;
                    }
                }
                Assert.IsTrue(
                    foundComponent,
                    "Scan must include components on inactive GameObjects."
                );
                bool foundCanvasRenderer = false;
                for (int i = 0; i < scan.CanvasRenderers.Length; i++)
                {
                    if (scan.CanvasRenderers[i] == probeCanvasRenderer)
                    {
                        foundCanvasRenderer = true;
                        break;
                    }
                }
                Assert.IsTrue(
                    foundCanvasRenderer,
                    "Scan must include canvas renderers on inactive GameObjects."
                );
                Assert.AreEqual(scan.Renderers.Length, UniThumbCapture.LastScanRendererCount);
                Assert.AreEqual(scan.Lights.Length, UniThumbCapture.LastScanLightCount);
                Assert.AreEqual(scan.Components.Length, UniThumbCapture.LastScanComponentCount);
                Assert.AreEqual(
                    scan.CanvasRenderers.Length,
                    UniThumbCapture.LastScanCanvasRendererCount
                );
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
                Object.DestroyImmediate(inactiveGo);
            }
        }

        [Test]
        public void HasPostProcessingVolumes_ScanMatchesDirectSweep()
        {
            System.Type volumeType = FindVolumeTypeForTest();
            if (volumeType == null)
            {
                Assert.Ignore("SRP Volume type unavailable in this project.");
            }
            GameObject volumeGo = new GameObject("ScanVolumeAgreementProbe");
            Component volume = volumeGo.AddComponent(volumeType);
            Behaviour behaviour = volume as Behaviour;
            Assert.IsNotNull(behaviour);
            behaviour.enabled = true;
            try
            {
                volumeGo.SetActive(true);
                UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
                Assert.AreEqual(
                    UniThumbCapture.HasPostProcessingVolumes(),
                    UniThumbCapture.HasPostProcessingVolumes(scan),
                    "Scan and direct volume checks must agree with an active volume."
                );
                Assert.IsTrue(
                    UniThumbCapture.HasPostProcessingVolumes(scan),
                    "An active scene volume must count."
                );
                volumeGo.SetActive(false);
                UniThumbCapture.ScanCache inactiveScan = UniThumbCapture.BeginScan();
                Assert.AreEqual(
                    UniThumbCapture.HasPostProcessingVolumes(),
                    UniThumbCapture.HasPostProcessingVolumes(inactiveScan),
                    "Scan and direct volume checks must agree with only an inactive volume."
                );
            }
            finally
            {
                Object.DestroyImmediate(volumeGo);
            }
        }

        [Test]
        public void DisableSceneVolumes_InactiveVolume_DisabledAndRestored()
        {
            System.Type volumeType = FindVolumeTypeForTest();
            if (volumeType == null)
            {
                Assert.Ignore("SRP Volume type unavailable in this project.");
            }
            GameObject instanceRoot = new GameObject("VolumeInactiveInstance");
            GameObject volumeGo = new GameObject("VolumeInactiveOutside");
            Component volume = volumeGo.AddComponent(volumeType);
            Behaviour behaviour = volume as Behaviour;
            Assert.IsNotNull(behaviour);
            behaviour.enabled = true;
            volumeGo.SetActive(false);
            try
            {
                UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
                Assert.IsNotNull(scan);
                List<UniThumbCapture.VolumeSnapshot> snapshots =
                    UniThumbCapture.DisableSceneVolumes(instanceRoot, scan);
                Assert.IsNotNull(snapshots);
                bool found = false;
                for (int i = 0; i < snapshots.Count; i++)
                {
                    if (snapshots[i] != null && snapshots[i].Volume == behaviour)
                    {
                        found = true;
                        break;
                    }
                }
                try
                {
                    Assert.IsTrue(found, "Inactive scene volumes must be neutralized.");
                }
                finally
                {
                    UniThumbCapture.RestoreSceneVolumes(snapshots);
                }
                Assert.IsTrue(behaviour.enabled);
            }
            finally
            {
                Object.DestroyImmediate(volumeGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        [Test]
        public void CapturePrefab_SceneCubeHidden_BurstPrefabShowsNoWhiteCube()
        {
            Shader unlit = Shader.Find("Unlit/Color");
            if (unlit == null)
            {
                Assert.Ignore("Unlit/Color shader unavailable in this project.");
            }
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "UniThumbReproWhiteCube";
            cube.transform.position = Vector3.zero;
            cube.transform.localScale = Vector3.one * 3f;
            Material white = new Material(unlit);
            white.color = Color.white;
            cube.GetComponent<MeshRenderer>().material = white;
            string assetPath = CreateBurstPrefabAsset();
            try
            {
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.SolidColor);
                    settings.ParticlePreviewTime = 1f;
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    int whiteCount = CountNearWhitePixels(pixels);
                    Assert.Less(
                        whiteCount,
                        pixels.Length / 200,
                        "Scene cube must stay isolated from the prefab capture."
                    );
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                Object.DestroyImmediate(cube);
                Object.DestroyImmediate(white);
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void CapturePrefab_TransparentBackground_BurstPrefabShowsNoWhiteCube()
        {
            Shader unlit = Shader.Find("Unlit/Color");
            if (unlit == null)
            {
                Assert.Ignore("Unlit/Color shader unavailable in this project.");
            }
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "UniThumbReproWhiteCube";
            cube.transform.position = Vector3.zero;
            cube.transform.localScale = Vector3.one * 3f;
            Material white = new Material(unlit);
            white.color = Color.white;
            cube.GetComponent<MeshRenderer>().material = white;
            string assetPath = CreateBurstPrefabAsset();
            try
            {
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.Transparent);
                    settings.ParticlePreviewTime = 1f;
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    int whiteCount = CountNearWhitePixels(pixels);
                    Assert.Less(
                        whiteCount,
                        pixels.Length / 200,
                        "Scene cube must stay isolated from the transparent prefab capture."
                    );
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                Object.DestroyImmediate(cube);
                Object.DestroyImmediate(white);
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void NeutralizePrefabEnvironment_SkyboxMode_KeepsSkyboxAmbient()
        {
            Shader skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader == null)
            {
                Assert.Ignore("Procedural skybox shader unavailable in this project.");
            }
            Material skybox = new Material(skyShader);
            Material originalSkybox = RenderSettings.skybox;
            AmbientMode originalAmbientMode = RenderSettings.ambientMode;
            Color originalAmbientLight = RenderSettings.ambientLight;
            float originalAmbientIntensity = RenderSettings.ambientIntensity;
            try
            {
                RenderSettings.skybox = skybox;
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = 1f;

                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.BackgroundMode = BackgroundMode.Skybox;
                UniThumbCapture.NeutralizePrefabEnvironment(settings);

                Assert.AreEqual(skybox, RenderSettings.skybox);
                Assert.AreEqual(
                    AmbientMode.Skybox,
                    RenderSettings.ambientMode,
                    "Skybox mode must keep Skybox ambient so the sky lights the prefab."
                );
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                RenderSettings.ambientMode = originalAmbientMode;
                RenderSettings.ambientLight = originalAmbientLight;
                RenderSettings.ambientIntensity = originalAmbientIntensity;
                Object.DestroyImmediate(skybox);
            }
        }

        [Test]
        public void CapturePrefab_SkyboxMode_RendersSkyBackground()
        {
            Shader skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader == null)
            {
                Assert.Ignore("Procedural skybox shader unavailable in this project.");
            }
            string assetPath = CreateCubePrefabAsset();
            Material skybox = new Material(skyShader);
            Material originalSkybox = RenderSettings.skybox;
            try
            {
                RenderSettings.skybox = skybox;
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.Skybox);
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    Color clear = UniThumbCapture.EffectiveClearColor(settings);
                    int lit = CountDifferingPixels(pixels, clear);
                    Assert.Greater(
                        lit,
                        pixels.Length / 50,
                        "Sky and lit cube must differ from the clear color."
                    );
                    AssertSkyCornersVisible(pixels, 64, 64, clear);
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                Object.DestroyImmediate(skybox);
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void CapturePrefab_NullSkybox_WithLightingOverride_RendersLitPrefab()
        {
            string assetPath = CreateCubePrefabAsset();
            Material originalSkybox = RenderSettings.skybox;
            try
            {
                RenderSettings.skybox = null;
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.Skybox);
                    settings.UseLightingOverride = true;
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    Color clear = UniThumbCapture.EffectiveClearColor(settings);
                    int lit = CountDifferingPixels(pixels, clear);
                    Assert.Greater(
                        lit,
                        pixels.Length / 50,
                        "A lit cube must differ from the clear color under the lighting override."
                    );
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void CapturePrefab_SkyboxMode_PrefabStageOpen_RendersSkyThumbnail()
        {
            Shader skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader == null)
            {
                Assert.Ignore("Procedural skybox shader unavailable in this project.");
            }
            string assetPath = CreateCubePrefabAsset();
            Material skybox = new Material(skyShader);
            // DontSave: OpenPrefab below loads the stage scene, which unloads
            // unused assets - an unprotected temp material would be destroyed
            // (Unity fake-null) and the sky assignment would silently vanish.
            skybox.hideFlags = HideFlags.HideAndDontSave;
            Material originalSkybox = RenderSettings.skybox;
            PrefabStage stage = PrefabStageUtility.OpenPrefab(assetPath);
            Assert.IsNotNull(stage, "Prefab Stage should open.");
            try
            {
                RenderSettings.skybox = skybox;
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.Skybox);
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    Color clear = UniThumbCapture.EffectiveClearColor(settings);
                    int lit = CountDifferingPixels(pixels, clear);
                    Assert.Greater(
                        lit,
                        pixels.Length / 50,
                        "Stage-open sky capture must render sky and lit prefab."
                    );
                    AssertSkyCornersVisible(pixels, 64, 64, clear);
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                StageUtility.GoToMainStage();
                RenderSettings.skybox = originalSkybox;
                Object.DestroyImmediate(skybox);
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void CapturePrefab_SkyboxMode_OwnDirectionalKey_RendersSkyAndLitPrefab()
        {
            Shader skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader == null)
            {
                Assert.Ignore("Procedural skybox shader unavailable in this project.");
            }
            string assetPath = CreateCubePrefabWithDirectionalAsset();
            Material skybox = new Material(skyShader);
            Material originalSkybox = RenderSettings.skybox;
            try
            {
                RenderSettings.skybox = skybox;
                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    CaptureSettings settings = CreatePixelSettings(BackgroundMode.Skybox);
                    CaptureResult result = UniThumbCapture.CapturePrefab(assetPath, settings);
                    Assert.IsTrue(result.Success, "Capture failed: " + result.Warning);
                    Assert.IsNotNull(result.PngBytes);
                    Color32[] pixels = DecodePngPixels(result.PngBytes);
                    Color clear = UniThumbCapture.EffectiveClearColor(settings);
                    int lit = CountDifferingPixels(pixels, clear);
                    Assert.Greater(
                        lit,
                        pixels.Length / 50,
                        "A prefab with its own key must render lit without a temp light."
                    );
                    AssertSkyCornersVisible(pixels, 64, 64, clear);
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                Object.DestroyImmediate(skybox);
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        private static void AssertSkyCornersVisible(
            Color32[] pixels,
            int width,
            int height,
            Color clear
        )
        {
            int clearR = Mathf.RoundToInt(clear.r * 255f);
            int clearG = Mathf.RoundToInt(clear.g * 255f);
            int clearB = Mathf.RoundToInt(clear.b * 255f);
            int[] corners = new int[] { 0, width - 1, (height - 1) * width, height * width - 1 };
            int skyCorners = 0;
            int nonBlackCorners = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                Color32 pixel = pixels[corners[i]];
                if (
                    Mathf.Abs(pixel.r - clearR) > 12
                    || Mathf.Abs(pixel.g - clearG) > 12
                    || Mathf.Abs(pixel.b - clearB) > 12
                )
                {
                    skyCorners++;
                }
                if (pixel.r > 12 || pixel.g > 12 || pixel.b > 12)
                {
                    nonBlackCorners++;
                }
            }
            Assert.Greater(
                skyCorners,
                2,
                "At least 3 of 4 frame corners must show sky, not the clear color."
            );
            Assert.Greater(
                nonBlackCorners,
                2,
                "At least 3 of 4 frame corners must show a lit sky, not black (sun below horizon)."
            );
        }

        private static string CreateBurstPrefabAsset()
        {
            GameObject root = new GameObject("UniThumbBurstRepro");
            ParticleSystem system = root.AddComponent<ParticleSystem>();

            var main = system.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 1.5f;
            main.startLifetime = 1.5f;
            main.startSpeed = 6f;
            main.startSize = 0.25f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                Color.HSVToRGB(0f, 0.8f, 1f),
                Color.HSVToRGB(0.9f, 0.8f, 1f)
            );
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 300;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 120) });

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.2f;

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.yellow, 0f),
                    new GradientColorKey(Color.magenta, 0.5f),
                    new GradientColorKey(Color.blue, 1f),
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.6f),
                    new GradientAlphaKey(0f, 1f),
                }
            );
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

            string assetPath = "Assets/__UniThumbBurstRepro.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, assetPath);
            Object.DestroyImmediate(root);
            return assetPath;
        }

        private static int CountNearWhitePixels(Color32[] pixels)
        {
            int count = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                if (pixel.r > 240 && pixel.g > 240 && pixel.b > 240)
                {
                    count++;
                }
            }
            return count;
        }

        private static string CreateCubePrefabAsset()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "UniThumbBlackPreviewCube";
            string assetPath = "Assets/__UniThumbBlackPreview.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, assetPath);
            Object.DestroyImmediate(go);
            return assetPath;
        }

        private static string CreateCubePrefabWithDirectionalAsset()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "UniThumbOwnKeyCube";
            GameObject keyGo = new GameObject("OwnKey");
            Light key = keyGo.AddComponent<Light>();
            key.type = LightType.Directional;
            key.enabled = true;
            key.intensity = 1f;
            keyGo.transform.SetParent(go.transform, false);
            string assetPath = "Assets/__UniThumbOwnKey.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, assetPath);
            Object.DestroyImmediate(go);
            return assetPath;
        }

        private static CaptureSettings CreatePixelSettings(BackgroundMode mode)
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Width = 64;
            settings.Height = 64;
            settings.BackgroundMode = mode;
            settings.WantPostProcessing = false;
            settings.CaptureUi = false;
            settings.UseLightingOverride = false;
            settings.Light2DMode = LightingMode.None;
            return settings;
        }

        private static Color32[] DecodePngPixels(byte[] png)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(png), "PNG bytes must decode.");
                return tex.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        private static int CountDifferingPixels(Color32[] pixels, Color clear)
        {
            int clearR = Mathf.RoundToInt(clear.r * 255f);
            int clearG = Mathf.RoundToInt(clear.g * 255f);
            int clearB = Mathf.RoundToInt(clear.b * 255f);
            int count = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                if (
                    Mathf.Abs(pixel.r - clearR) > 12
                    || Mathf.Abs(pixel.g - clearG) > 12
                    || Mathf.Abs(pixel.b - clearB) > 12
                )
                {
                    count++;
                }
            }
            return count;
        }

        private static System.Type FindVolumeTypeForTest()
        {
            System.Type direct = System.Type.GetType(
                "UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime"
            );
            if (direct != null)
            {
                return direct;
            }
            foreach (
                System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies()
            )
            {
                System.Type candidate = null;
                try
                {
                    candidate = assembly.GetType("UnityEngine.Rendering.Volume");
                }
                catch (System.Exception)
                {
                    continue;
                }
                if (candidate != null)
                {
                    return candidate;
                }
            }
            return null;
        }
    }
}
