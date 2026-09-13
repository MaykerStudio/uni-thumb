using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbPrefabLightingTests
    {
        [Test]
        public void Fallback_PerspectiveNoSceneLight_ReturnsTempLight3D()
        {
            Assert.AreEqual(
                PrefabFallbackLight.TempLight3D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    false,
                    false,
                    false
                )
            );
        }

        [Test]
        public void Fallback_PerspectiveWithSceneLightNoOverride_ReturnsTempLight3D()
        {
            // The neutralize step disables every scene light, so a scene
            // directional cannot light the prefab: the fallback always keys.
            Assert.AreEqual(
                PrefabFallbackLight.TempLight3D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    false,
                    false,
                    true
                )
            );
        }

        [Test]
        public void Fallback_PerspectiveWithSceneLightAndOverride_ReturnsTempLight3D()
        {
            Assert.AreEqual(
                PrefabFallbackLight.TempLight3D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    false,
                    true,
                    true
                )
            );
        }

        [Test]
        public void Fallback_OrthographicWith3DContent_ReturnsTempLight3D()
        {
            // A Global Light2D does not light 3D meshes: ortho captures of
            // 3D content need a directional key.
            Assert.AreEqual(
                PrefabFallbackLight.TempLight3D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    true,
                    false,
                    false,
                    true,
                    false
                )
            );
        }

        [Test]
        public void Fallback_OrthographicSpriteOnly_ReturnsTempLight2D()
        {
            Assert.AreEqual(
                PrefabFallbackLight.TempLight2D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    true,
                    false,
                    false,
                    false,
                    false
                )
            );
        }

        [Test]
        public void Fallback_PrefabWithOwnKeyLight_ReturnsNone()
        {
            // A prefab carrying its own enabled key lights itself: no temp
            // avoids double-lighting nested/variant prefabs.
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    false,
                    false,
                    false,
                    true,
                    true
                )
            );
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    true,
                    false,
                    false,
                    false,
                    true
                )
            );
        }

        [Test]
        public void Fallback_ExplicitLightingMode_ReturnsNone()
        {
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.Light3D,
                    true,
                    false,
                    false,
                    false
                )
            );
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.Light2D,
                    true,
                    true,
                    false,
                    false
                )
            );
        }

        [Test]
        public void Fallback_ScenePath_ReturnsNone()
        {
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    false,
                    false,
                    true,
                    false
                )
            );
        }

        [Test]
        public void Content_ParticleLineTrailBillboard_ReturnsTrue()
        {
            // Ortho captures of particle/line/trail content need a
            // directional key: misrouting them to TempLight2D fires a
            // spurious Light2D warning on 3D projects.
            GameObject particleRoot = new GameObject("UniThumbTestParticles");
            GameObject lineRoot = new GameObject("UniThumbTestLine");
            GameObject trailRoot = new GameObject("UniThumbTestTrail");
            GameObject billboardRoot = new GameObject("UniThumbTestBillboard");
            try
            {
                particleRoot.AddComponent<ParticleSystemRenderer>();
                lineRoot.AddComponent<LineRenderer>();
                trailRoot.AddComponent<TrailRenderer>();
                billboardRoot.AddComponent<BillboardRenderer>();
                Assert.IsTrue(
                    UniThumbCapture.PrefabSubtreeHas3DContent(particleRoot),
                    "Particle-only prefab misrouted to TempLight2D."
                );
                Assert.IsTrue(
                    UniThumbCapture.PrefabSubtreeHas3DContent(lineRoot),
                    "Line-only prefab misrouted to TempLight2D."
                );
                Assert.IsTrue(
                    UniThumbCapture.PrefabSubtreeHas3DContent(trailRoot),
                    "Trail-only prefab misrouted to TempLight2D."
                );
                Assert.IsTrue(
                    UniThumbCapture.PrefabSubtreeHas3DContent(billboardRoot),
                    "Billboard-only prefab misrouted to TempLight2D."
                );
            }
            finally
            {
                Object.DestroyImmediate(particleRoot);
                Object.DestroyImmediate(lineRoot);
                Object.DestroyImmediate(trailRoot);
                Object.DestroyImmediate(billboardRoot);
            }
        }

        [Test]
        public void Content_SpriteOnly_ReturnsFalse()
        {
            GameObject root = new GameObject("UniThumbTestSprite");
            try
            {
                root.AddComponent<SpriteRenderer>();
                Assert.IsFalse(UniThumbCapture.PrefabSubtreeHas3DContent(root));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Fallback_OrthographicParticleContent_ReturnsTempLight3D()
        {
            GameObject root = new GameObject("UniThumbTestOrthoParticles");
            try
            {
                root.AddComponent<ParticleSystemRenderer>();
                bool has3DContent = UniThumbCapture.PrefabSubtreeHas3DContent(root);
                Assert.AreEqual(
                    PrefabFallbackLight.TempLight3D,
                    UniThumbCapture.ResolvePrefabFallbackLight(
                        LightingMode.None,
                        true,
                        true,
                        false,
                        false,
                        has3DContent,
                        false
                    )
                );
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Pipeline_3DProject_DoesNotReport2DOnly()
        {
            Assume.That(
                GraphicsSettings.currentRenderPipeline != null,
                "Test project must use a render pipeline."
            );
            Assume.That(
                UniThumbCapture.PipelineHasOnly2DRenderers(),
                Is.False,
                "Test project must use a 3D URP pipeline."
            );
            Assert.IsFalse(UniThumbCapture.PipelineHasOnly2DRenderers());
        }

        [Test]
        public void EnsureFallback_TempLight3DWithoutCameraData_EmitsNoWarning()
        {
            // A bare camera carries no URP camera data, so the renderer
            // switch fails indeterminately (not a Renderer2D-only
            // pipeline): the None fallback must stay silent and keep the
            // directional key only. Previously this warned spuriously
            // about missing 2D renderers on 3D projects.
            Assume.That(
                UniThumbCapture.PipelineHasOnly2DRenderers(),
                Is.False,
                "Test project must use a 3D URP pipeline."
            );
            GameObject camGo = new GameObject("UniThumbTestFallbackCam");
            GameObject tempLight2D = null;
            GameObject tempLight3D = null;
            List<UniThumbCapture.Light2DSnapshot> disabledGlobals = null;
            var uniThumbWarnings = new List<string>();
            void CaptureWarning(string message, string trace, LogType type)
            {
                if (type == LogType.Warning && message != null && message.StartsWith("[UniThumb]"))
                {
                    uniThumbWarnings.Add(message);
                }
            }
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                Application.logMessageReceived += CaptureWarning;
                try
                {
                    UniThumbCapture.EnsurePrefabFallbackLights(
                        PrefabFallbackLight.TempLight3D,
                        cam,
                        settings,
                        null,
                        out tempLight2D,
                        out tempLight3D,
                        out disabledGlobals
                    );
                }
                finally
                {
                    Application.logMessageReceived -= CaptureWarning;
                }
                Assert.IsNotNull(tempLight3D, "Directional key was not created.");
                Assert.IsNull(tempLight2D, "Spurious Global Light2D on a 3D pipeline.");
                Assert.IsNull(disabledGlobals, "No globals should be disabled.");
                Assert.IsEmpty(
                    uniThumbWarnings,
                    "Spurious warning: " + string.Join(" | ", uniThumbWarnings)
                );
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(tempLight2D);
                Object.DestroyImmediate(tempLight3D);
            }
        }

        [Test]
        public void EnsureFallback_TempLight2D_CreatesGlobalLightSilently()
        {
            // The sprite-only ortho path is untouched by the None-mode
            // silence fix: it still creates the Global Light2D without
            // warnings when the Light2D type is available.
            GameObject tempLight2D = UniThumbCapture.CreateTempLight2D(1f);
            try
            {
                if (tempLight2D == null)
                {
                    Assert.Ignore("Light2D type not available in this project.");
                }
                Assert.IsNotNull(tempLight2D);
            }
            finally
            {
                Object.DestroyImmediate(tempLight2D);
            }
        }

        [Test]
        public void ShouldDowngradeToLight2D_OnlyDowngradesWhenGenuinely2DOnly()
        {
            // Genuine Renderer2D-only pipelines still warn and downgrade;
            // indeterminate switch failures stay silent.
            Assert.IsTrue(UniThumbCapture.ShouldDowngradeToLight2D(false, true));
            Assert.IsFalse(UniThumbCapture.ShouldDowngradeToLight2D(false, false));
            Assert.IsFalse(UniThumbCapture.ShouldDowngradeToLight2D(true, true));
            Assert.IsFalse(UniThumbCapture.ShouldDowngradeToLight2D(true, false));
        }

        [Test]
        public void EnsureFallback_Renderer2DOnly_CreatesLight2DOnlySilently()
        {
            // Renderer2D-only pipelines run zero 3D features: no renderer
            // switch attempt, no TempLight3D, no 3D warning. A Global Light2D
            // is created directly so sprite content stays lit; 3D meshes fall
            // back to the neutral ambient. The pipeline check is forced via
            // the test seam (the active pipeline only switches
            // asynchronously, so a swapped asset cannot be observed
            // synchronously in EditMode).
            GameObject camGo = new GameObject("UniThumbTest2DFallbackCam");
            GameObject tempLight2D = null;
            GameObject tempLight3D = null;
            List<UniThumbCapture.Light2DSnapshot> disabledGlobals = null;
            UniThumbCapture.SetRenderer2DOnlyForTest(true);
            var uniThumbWarnings = new List<string>();
            void CaptureWarning(string message, string trace, LogType type)
            {
                if (type == LogType.Warning && message != null && message.StartsWith("[UniThumb]"))
                {
                    uniThumbWarnings.Add(message);
                }
            }
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                Application.logMessageReceived += CaptureWarning;
                try
                {
                    UniThumbCapture.EnsurePrefabFallbackLights(
                        PrefabFallbackLight.TempLight3D,
                        cam,
                        settings,
                        null,
                        out tempLight2D,
                        out tempLight3D,
                        out disabledGlobals
                    );
                }
                finally
                {
                    Application.logMessageReceived -= CaptureWarning;
                }
                Assert.IsNull(tempLight3D, "No TempLight3D on a 2D-only pipeline.");
                Assert.IsEmpty(
                    uniThumbWarnings,
                    "No 3D warnings on 2D-only: " + string.Join(" | ", uniThumbWarnings)
                );
                if (tempLight2D == null)
                {
                    Assert.Ignore("Light2D type not available in this project.");
                }
                Assert.IsNotNull(tempLight2D, "Global Light2D was not added.");
            }
            finally
            {
                UniThumbCapture.SetRenderer2DOnlyForTest(null);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(tempLight2D);
                Object.DestroyImmediate(tempLight3D);
                UniThumbCapture.RestoreGlobalLight2Ds(disabledGlobals);
            }
        }

        [Test]
        public void NoneFallbackOrientation_DefaultsAre50AndMinus30()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.BackgroundMode = BackgroundMode.SolidColor;
            settings.Light3DYaw = 10f;
            settings.Light3DPitch = 10f;
            float yaw;
            float pitch;
            UniThumbCapture.ResolveNoneFallbackOrientation(settings, out yaw, out pitch);
            Assert.AreEqual(50f, yaw, 0.001f);
            Assert.AreEqual(-30f, pitch, 0.001f);
        }

        [Test]
        public void NoneFallbackOrientation_RespectsCustomLimits()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.BackgroundMode = BackgroundMode.SolidColor;
            settings.Light3DYawMin = 0f;
            settings.Light3DYawMax = 20f;
            settings.Light3DPitchMin = 0f;
            settings.Light3DPitchMax = 20f;
            float yaw;
            float pitch;
            UniThumbCapture.ResolveNoneFallbackOrientation(settings, out yaw, out pitch);
            Assert.AreEqual(20f, yaw, 0.001f);
            Assert.AreEqual(0f, pitch, 0.001f);
        }

        [Test]
        public void NoneFallbackOrientation_LegacyZeroLimitsNormalizeToDefaults()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.BackgroundMode = BackgroundMode.SolidColor;
            settings.Light3DYawMin = 0f;
            settings.Light3DYawMax = 0f;
            settings.Light3DPitchMin = 0f;
            settings.Light3DPitchMax = 0f;
            float yaw;
            float pitch;
            UniThumbCapture.ResolveNoneFallbackOrientation(settings, out yaw, out pitch);
            Assert.AreEqual(50f, yaw, 0.001f);
            Assert.AreEqual(-30f, pitch, 0.001f);
        }

        [Test]
        public void NoneFallbackOrientation_SkyboxProcedural_LastResortIs50()
        {
            Shader skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader == null)
            {
                Assert.Ignore("Procedural skybox shader unavailable in this project.");
            }
            Material skybox = new Material(skyShader);
            Material originalSkybox = RenderSettings.skybox;
            try
            {
                RenderSettings.skybox = skybox;
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.BackgroundMode = BackgroundMode.Skybox;
                float yaw;
                float pitch;
                UniThumbCapture.ResolveNoneFallbackOrientation(settings, out yaw, out pitch);
                Assert.AreEqual(50f, yaw, 0.001f);
                Assert.AreEqual(50f, pitch, 0.001f);
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                Object.DestroyImmediate(skybox);
            }
        }

        [Test]
        public void EnsureFallback_TempLight3DAimsWithFramingNotDefaults()
        {
            Assume.That(
                UniThumbCapture.PipelineHasOnly2DRenderers(),
                Is.False,
                "Test project must use a 3D URP pipeline."
            );
            GameObject camGo = new GameObject("UniThumbTestFallbackOrientationCam");
            GameObject tempLight2D = null;
            GameObject tempLight3D = null;
            List<UniThumbCapture.Light2DSnapshot> disabledGlobals = null;
            Material originalSkybox = RenderSettings.skybox;
            try
            {
                RenderSettings.skybox = null;
                Camera cam = camGo.AddComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.BackgroundMode = BackgroundMode.SolidColor;
                settings.Light3DYaw = 10f;
                settings.Light3DPitch = 10f;
                cam.aspect = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    cam,
                    settings,
                    Vector3.zero,
                    1f,
                    new Vector3(1f, 1f, 1f),
                    true,
                    false
                );
                float aimYaw;
                float aimPitch;
                UniThumbCapture.ResolvePrefabFallbackAim(cam, settings, out aimYaw, out aimPitch);
                UniThumbCapture.EnsurePrefabFallbackLights(
                    PrefabFallbackLight.TempLight3D,
                    cam,
                    settings,
                    null,
                    out tempLight2D,
                    out tempLight3D,
                    out disabledGlobals
                );
                Assert.IsNotNull(tempLight3D);
                Assert.IsNull(tempLight2D);
                Quaternion expected = Quaternion.Euler(aimPitch, aimYaw, 0f);
                Assert.AreEqual(
                    expected.eulerAngles.x,
                    tempLight3D.transform.rotation.eulerAngles.x,
                    0.5f
                );
                Assert.AreEqual(
                    expected.eulerAngles.y,
                    tempLight3D.transform.rotation.eulerAngles.y,
                    0.5f
                );
                Assert.That(aimYaw, Is.Not.EqualTo(50f).Within(0.5f));
            }
            finally
            {
                RenderSettings.skybox = originalSkybox;
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(tempLight2D);
                Object.DestroyImmediate(tempLight3D);
            }
        }

        [Test]
        public void FallbackAim_PerspectiveMatchesFramingDirection()
        {
            GameObject camGo = new GameObject("UniThumbTestAimPerspective");
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.BackgroundMode = BackgroundMode.SolidColor;
                settings.orthographic = false;
                cam.aspect = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    cam,
                    settings,
                    Vector3.zero,
                    1f,
                    new Vector3(1f, 1f, 1f),
                    true,
                    false
                );
                float yaw;
                float pitch;
                UniThumbCapture.ResolvePrefabFallbackAim(cam, settings, out yaw, out pitch);
                Quaternion lightRotation = Quaternion.Euler(pitch, yaw, 0f);
                Vector3 lightForward = lightRotation * Vector3.forward;
                Assert.AreEqual(cam.transform.forward.x, lightForward.x, 0.01f);
                Assert.AreEqual(cam.transform.forward.y, lightForward.y, 0.01f);
                Assert.AreEqual(cam.transform.forward.z, lightForward.z, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void FallbackAim_Ortho3DMatchesFramingDirection()
        {
            GameObject camGo = new GameObject("UniThumbTestAimOrtho");
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.BackgroundMode = BackgroundMode.SolidColor;
                settings.orthographic = true;
                cam.aspect = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    cam,
                    settings,
                    new Vector3(2f, 1f, 0f),
                    1f,
                    new Vector3(1f, 1f, 1f),
                    true,
                    false
                );
                float yaw;
                float pitch;
                UniThumbCapture.ResolvePrefabFallbackAim(cam, settings, out yaw, out pitch);
                Quaternion lightRotation = Quaternion.Euler(pitch, yaw, 0f);
                Vector3 lightForward = lightRotation * Vector3.forward;
                Assert.AreEqual(cam.transform.forward.x, lightForward.x, 0.01f);
                Assert.AreEqual(cam.transform.forward.y, lightForward.y, 0.01f);
                Assert.AreEqual(cam.transform.forward.z, lightForward.z, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void FallbackAim_OffCenterBoundsAimsAtCenter()
        {
            GameObject camGo = new GameObject("UniThumbTestAimOffCenter");
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.BackgroundMode = BackgroundMode.SolidColor;
                Vector3 center = new Vector3(10f, 2f, -5f);
                cam.aspect = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    cam,
                    settings,
                    center,
                    1f,
                    new Vector3(1f, 1f, 1f),
                    true,
                    false
                );
                float yaw;
                float pitch;
                UniThumbCapture.ResolvePrefabFallbackAim(cam, settings, out yaw, out pitch);
                Quaternion lightRotation = Quaternion.Euler(pitch, yaw, 0f);
                Vector3 lightForward = lightRotation * Vector3.forward;
                Vector3 toCenter = (center - cam.transform.position).normalized;
                Assert.Greater(Vector3.Dot(lightForward, toCenter), 0.999f);
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void FallbackAim_LimitsClampAim()
        {
            GameObject camGo = new GameObject("UniThumbTestAimClamp");
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                settings.BackgroundMode = BackgroundMode.SolidColor;
                settings.Light3DYawMin = 0f;
                settings.Light3DYawMax = 20f;
                settings.Light3DPitchMin = 0f;
                settings.Light3DPitchMax = 20f;
                cam.aspect = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    cam,
                    settings,
                    Vector3.zero,
                    1f,
                    new Vector3(1f, 1f, 1f),
                    true,
                    false
                );
                float yaw;
                float pitch;
                UniThumbCapture.ResolvePrefabFallbackAim(cam, settings, out yaw, out pitch);
                Assert.GreaterOrEqual(yaw, 0f);
                Assert.LessOrEqual(yaw, 20f);
                Assert.GreaterOrEqual(pitch, 0f);
                Assert.LessOrEqual(pitch, 20f);
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void FallbackAim_NullCameraFallsBackToDefaults()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.BackgroundMode = BackgroundMode.SolidColor;
            float yaw;
            float pitch;
            UniThumbCapture.ResolvePrefabFallbackAim(null, settings, out yaw, out pitch);
            Assert.AreEqual(50f, yaw, 0.001f);
            Assert.AreEqual(-30f, pitch, 0.001f);
        }

        [Test]
        public void CreateDefaultSettings_CarriesDefaultLimits()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(-180f, settings.Light3DYawMin, 0.001f);
            Assert.AreEqual(180f, settings.Light3DYawMax, 0.001f);
            Assert.AreEqual(-89f, settings.Light3DPitchMin, 0.001f);
            Assert.AreEqual(89f, settings.Light3DPitchMax, 0.001f);
        }
    }
}
