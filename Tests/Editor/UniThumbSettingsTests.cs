using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbSettingsTests
    {
        private UniThumbSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<UniThumbSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        [Test]
        public void Default_IconOverlayEnabled_IsTrue()
        {
            Assert.IsTrue(_settings.IconOverlayEnabled);
        }

        [Test]
        public void Default_Paddings_AreTwo()
        {
            Assert.AreEqual(2f, _settings.ListPaddingX, 0.001f);
            Assert.AreEqual(2f, _settings.ListPaddingY, 0.001f);
        }

        [Test]
        public void Default_IconMaxSize_Is128()
        {
            Assert.AreEqual(128f, _settings.IconMaxSize, 0.001f);
        }

        [Test]
        public void Default_CacheSizeMb_Is256()
        {
            Assert.AreEqual(256, _settings.CacheSizeMb);
        }

        [Test]
        public void SetListPaddingX_ClampsToRange()
        {
            _settings.SetListPaddingX(-1f);
            Assert.AreEqual(0f, _settings.ListPaddingX, 0.001f);
            _settings.SetListPaddingX(4f);
            Assert.AreEqual(4f, _settings.ListPaddingX, 0.001f);
            _settings.SetListPaddingX(20f);
            Assert.AreEqual(8f, _settings.ListPaddingX, 0.001f);
        }

        [Test]
        public void SetListPaddingY_ClampsToRange()
        {
            _settings.SetListPaddingY(-1f);
            Assert.AreEqual(0f, _settings.ListPaddingY, 0.001f);
            _settings.SetListPaddingY(4f);
            Assert.AreEqual(4f, _settings.ListPaddingY, 0.001f);
            _settings.SetListPaddingY(20f);
            Assert.AreEqual(8f, _settings.ListPaddingY, 0.001f);
        }

        [Test]
        public void SetIconMaxSize_ClampsBelowMin()
        {
            _settings.SetIconMaxSize(1f);
            Assert.AreEqual(16f, _settings.IconMaxSize, 0.001f);
        }

        [Test]
        public void SetIconMaxSize_AtMin()
        {
            _settings.SetIconMaxSize(16f);
            Assert.AreEqual(16f, _settings.IconMaxSize, 0.001f);
        }

        [Test]
        public void SetIconMaxSize_AtMax()
        {
            _settings.SetIconMaxSize(256f);
            Assert.AreEqual(256f, _settings.IconMaxSize, 0.001f);
        }

        [Test]
        public void SetIconMaxSize_ClampsAboveMax()
        {
            _settings.SetIconMaxSize(300f);
            Assert.AreEqual(256f, _settings.IconMaxSize, 0.001f);
        }

        [Test]
        public void SetCacheSizeMb_ClampsBelowMin()
        {
            _settings.SetCacheSizeMb(1);
            Assert.AreEqual(32, _settings.CacheSizeMb);
        }

        [Test]
        public void SetCacheSizeMb_AtMin()
        {
            _settings.SetCacheSizeMb(32);
            Assert.AreEqual(32, _settings.CacheSizeMb);
        }

        [Test]
        public void SetCacheSizeMb_AtMax()
        {
            _settings.SetCacheSizeMb(2048);
            Assert.AreEqual(2048, _settings.CacheSizeMb);
        }

        [Test]
        public void SetCacheSizeMb_ClampsAboveMax()
        {
            _settings.SetCacheSizeMb(9999);
            Assert.AreEqual(2048, _settings.CacheSizeMb);
        }

        [Test]
        public void SetIconOverlayEnabled_Toggles()
        {
            _settings.SetIconOverlayEnabled(false);
            Assert.IsFalse(_settings.IconOverlayEnabled);
            _settings.SetIconOverlayEnabled(true);
            Assert.IsTrue(_settings.IconOverlayEnabled);
        }

        [Test]
        public void Default_CheckForUpdates_IsFalse()
        {
            Assert.IsFalse(_settings.CheckForUpdates);
        }

        [Test]
        public void SetCheckForUpdates_Toggles()
        {
            _settings.SetCheckForUpdates(true);
            Assert.IsTrue(_settings.CheckForUpdates);
            _settings.SetCheckForUpdates(false);
            Assert.IsFalse(_settings.CheckForUpdates);
        }

        [Test]
        public void Setters_DoNotThrowOutsideAssetContext()
        {
            // ScriptableObject.CreateInstance is not an asset on disk;
            // SetDirty should still succeed (it marks the in-memory object).
            Assert.DoesNotThrow(() => _settings.SetIconOverlayEnabled(true));
            Assert.DoesNotThrow(() => _settings.SetListPaddingX(3f));
            Assert.DoesNotThrow(() => _settings.SetListPaddingY(3f));
            Assert.DoesNotThrow(() => _settings.SetIconMaxSize(20f));
            Assert.DoesNotThrow(() => _settings.SetCacheSizeMb(512));
            Assert.DoesNotThrow(() => _settings.SetCheckForUpdates(true));
        }

        [Test]
        public void Default_LightingMode_IsNone()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(LightingMode.None, settings.Light2DMode);
        }

        [Test]
        public void Default_Light2DIntensity_Is1()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(1f, settings.Light2DIntensity);
        }

        [Test]
        public void SetLightingMode_StoresValue()
        {
            _settings.SetLightingMode(LightingMode.Light2D);
            Assert.AreEqual(LightingMode.Light2D, _settings.LightingMode);
        }

        [Test]
        public void SetLight2DIntensity_StoresValue()
        {
            _settings.SetLight2DIntensity(2.5f);
            Assert.AreEqual(2.5f, _settings.Light2DIntensity);
        }

        [Test]
        public void SetLight2DIntensity_ClampsRange()
        {
            _settings.SetLight2DIntensity(10f);
            Assert.AreEqual(5f, _settings.Light2DIntensity);
            _settings.SetLight2DIntensity(-1f);
            Assert.AreEqual(0f, _settings.Light2DIntensity);
        }

        [Test]
        public void Default_Light2DSortingLayers_IsAllBits()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.IsNull(settings.Light2DSortingLayerIds);
        }

        [Test]
        public void Default_Light2DSortingLayers_Settings_IsMinus1()
        {
            Assert.AreEqual(-1, _settings.Light2DSortingLayers);
        }

        [Test]
        public void SetLight2DSortingLayers_StoresValue()
        {
            _settings.SetLight2DSortingLayers(0b101);
            Assert.AreEqual(0b101, _settings.Light2DSortingLayers);
        }

        [Test]
        public void Default_Light3DIntensity_Is1()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(1f, settings.Light3DIntensity);
        }

        [Test]
        public void Default_Light3DShadows_IsFalse()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.IsFalse(settings.Light3DShadows);
        }

        [Test]
        public void Default_Light3DColor_IsWhite()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(Color.white, settings.Light3DColor);
        }

        [Test]
        public void SetLight3DIntensity_StoresValue()
        {
            _settings.SetLight3DIntensity(3.5f);
            Assert.AreEqual(3.5f, _settings.Light3DIntensity);
        }

        [Test]
        public void SetLight3DIntensity_ClampsRange()
        {
            _settings.SetLight3DIntensity(10f);
            Assert.AreEqual(5f, _settings.Light3DIntensity);
            _settings.SetLight3DIntensity(-1f);
            Assert.AreEqual(0f, _settings.Light3DIntensity);
        }

        [Test]
        public void SetLight3DShadows_StoresValue()
        {
            _settings.SetLight3DShadows(true);
            Assert.IsTrue(_settings.Light3DShadows);
        }

        [Test]
        public void SetLight3DColor_StoresValue()
        {
            Color red = Color.red;
            _settings.SetLight3DColor(red);
            Assert.AreEqual(red, _settings.Light3DColor);
        }

        [Test]
        public void Default_Light3DYaw_Is50()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(50f, settings.Light3DYaw);
        }

        [Test]
        public void Default_Light3DPitch_IsMinus30()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(-30f, settings.Light3DPitch);
        }

        [Test]
        public void SetLight3DYaw_StoresValue()
        {
            _settings.SetLight3DYaw(90f);
            Assert.AreEqual(90f, _settings.Light3DYaw);
        }

        [Test]
        public void SetLight3DYaw_ClampsRange()
        {
            _settings.SetLight3DYaw(200f);
            Assert.AreEqual(180f, _settings.Light3DYaw);
            _settings.SetLight3DYaw(-200f);
            Assert.AreEqual(-180f, _settings.Light3DYaw);
        }

        [Test]
        public void SetLight3DPitch_StoresValue()
        {
            _settings.SetLight3DPitch(45f);
            Assert.AreEqual(45f, _settings.Light3DPitch);
        }

        [Test]
        public void SetLight3DPitch_ClampsRange()
        {
            _settings.SetLight3DPitch(95f);
            Assert.AreEqual(89f, _settings.Light3DPitch);
            _settings.SetLight3DPitch(-95f);
            Assert.AreEqual(-89f, _settings.Light3DPitch);
        }

        [Test]
        public void Default_Light3DIntensityMin_IsZero()
        {
            Assert.AreEqual(0f, _settings.Light3DIntensityMin, 0.001f);
        }

        [Test]
        public void Default_Light3DIntensityMax_Is5()
        {
            Assert.AreEqual(5f, _settings.Light3DIntensityMax, 0.001f);
        }

        [Test]
        public void Default_Light3DYawLimits_AreMinus180To180()
        {
            Assert.AreEqual(-180f, _settings.Light3DYawMin, 0.001f);
            Assert.AreEqual(180f, _settings.Light3DYawMax, 0.001f);
        }

        [Test]
        public void Default_Light3DPitchLimits_AreMinus89To89()
        {
            Assert.AreEqual(-89f, _settings.Light3DPitchMin, 0.001f);
            Assert.AreEqual(89f, _settings.Light3DPitchMax, 0.001f);
        }

        [Test]
        public void Light3DIntensityHardCap_Is10()
        {
            Assert.AreEqual(10f, UniThumbSettings.Light3DIntensityHardCap, 0.001f);
        }

        [Test]
        public void SetLight3DIntensity_RespectsCustomMax()
        {
            _settings.SetLight3DIntensityMax(8f);
            _settings.SetLight3DIntensity(9f);
            Assert.AreEqual(8f, _settings.Light3DIntensity, 0.001f);
            _settings.SetLight3DIntensity(7f);
            Assert.AreEqual(7f, _settings.Light3DIntensity, 0.001f);
            _settings.SetLight3DIntensity(-2f);
            Assert.AreEqual(0f, _settings.Light3DIntensity, 0.001f);
        }

        [Test]
        public void SetLight3DIntensityMax_ClampsToCapAndEpsilon()
        {
            _settings.SetLight3DIntensityMax(999f);
            Assert.AreEqual(10f, _settings.Light3DIntensityMax, 0.001f);
            _settings.SetLight3DIntensityMax(0f);
            Assert.AreEqual(0.01f, _settings.Light3DIntensityMax, 0.001f);
            _settings.SetLight3DIntensityMax(-5f);
            Assert.AreEqual(0.01f, _settings.Light3DIntensityMax, 0.001f);
        }

        [Test]
        public void SetLight3DIntensityMax_ReclampsCurrentIntensity()
        {
            _settings.SetLight3DIntensity(5f);
            Assert.AreEqual(5f, _settings.Light3DIntensity, 0.001f);
            _settings.SetLight3DIntensityMax(2f);
            Assert.AreEqual(2f, _settings.Light3DIntensityMax, 0.001f);
            Assert.AreEqual(2f, _settings.Light3DIntensity, 0.001f);
        }

        [Test]
        public void SetLight3DYaw_RespectsCustomRange()
        {
            _settings.SetLight3DYawMin(-90f);
            _settings.SetLight3DYawMax(90f);
            _settings.SetLight3DYaw(120f);
            Assert.AreEqual(90f, _settings.Light3DYaw, 0.001f);
            _settings.SetLight3DYaw(-120f);
            Assert.AreEqual(-90f, _settings.Light3DYaw, 0.001f);
        }

        [Test]
        public void SetLight3DPitch_RespectsCustomRange()
        {
            _settings.SetLight3DPitchMin(-45f);
            _settings.SetLight3DPitchMax(45f);
            _settings.SetLight3DPitch(60f);
            Assert.AreEqual(45f, _settings.Light3DPitch, 0.001f);
            _settings.SetLight3DPitch(-60f);
            Assert.AreEqual(-45f, _settings.Light3DPitch, 0.001f);
        }

        [Test]
        public void SetLight3DYawLimits_EnforceOrdering()
        {
            _settings.SetLight3DYawMin(200f);
            Assert.AreEqual(179.99f, _settings.Light3DYawMin, 0.01f);
            Assert.AreEqual(_settings.Light3DYawMin, _settings.Light3DYaw, 0.01f);
            _settings.SetLight3DYawMax(-200f);
            Assert.AreEqual(_settings.Light3DYawMin + 0.01f, _settings.Light3DYawMax, 0.01f);
        }

        [Test]
        public void SetLight3DPitchLimits_EnforceOrdering()
        {
            _settings.SetLight3DPitchMin(200f);
            Assert.AreEqual(88.99f, _settings.Light3DPitchMin, 0.01f);
            Assert.AreEqual(_settings.Light3DPitchMin, _settings.Light3DPitch, 0.01f);
            _settings.SetLight3DPitchMax(-200f);
            Assert.AreEqual(_settings.Light3DPitchMin + 0.01f, _settings.Light3DPitchMax, 0.01f);
        }

        [Test]
        public void LegacyNormalize_ZeroedLimits_RestoreDefaults()
        {
            SetPrivateFloat("m_Light3DIntensityMax", 0f);
            SetPrivateFloat("m_Light3DYawMin", 0f);
            SetPrivateFloat("m_Light3DYawMax", 0f);
            SetPrivateFloat("m_Light3DPitchMin", 0f);
            SetPrivateFloat("m_Light3DPitchMax", 0f);
            InvokeNormalizeLightLimits();
            Assert.AreEqual(5f, _settings.Light3DIntensityMax, 0.001f);
            Assert.AreEqual(-180f, _settings.Light3DYawMin, 0.001f);
            Assert.AreEqual(180f, _settings.Light3DYawMax, 0.001f);
            Assert.AreEqual(-89f, _settings.Light3DPitchMin, 0.001f);
            Assert.AreEqual(89f, _settings.Light3DPitchMax, 0.001f);
        }

        [Test]
        public void LegacyNormalize_OverCapMax_ClampsToCap()
        {
            SetPrivateFloat("m_Light3DIntensityMax", 999f);
            InvokeNormalizeLightLimits();
            Assert.AreEqual(10f, _settings.Light3DIntensityMax, 0.001f);
        }

        [Test]
        public void LegacyNormalize_ClampsCurrentValuesIntoRange()
        {
            SetPrivateFloat("m_Light3DIntensity", 999f);
            SetPrivateFloat("m_Light3DYaw", 999f);
            SetPrivateFloat("m_Light3DPitch", -999f);
            InvokeNormalizeLightLimits();
            Assert.AreEqual(5f, _settings.Light3DIntensity, 0.001f);
            Assert.AreEqual(180f, _settings.Light3DYaw, 0.001f);
            Assert.AreEqual(-89f, _settings.Light3DPitch, 0.001f);
        }

        [Test]
        public void ClampLight3DIntensity_BoundsToHardCap()
        {
            Assert.AreEqual(0f, UniThumbCapture.ClampLight3DIntensity(-1f), 0.001f);
            Assert.AreEqual(0f, UniThumbCapture.ClampLight3DIntensity(0f), 0.001f);
            Assert.AreEqual(5f, UniThumbCapture.ClampLight3DIntensity(5f), 0.001f);
            Assert.AreEqual(10f, UniThumbCapture.ClampLight3DIntensity(10f), 0.001f);
            Assert.AreEqual(10f, UniThumbCapture.ClampLight3DIntensity(999f), 0.001f);
        }

        [Test]
        public void ResolveLight3DIntensity_UrpPassthrough()
        {
            if (UniThumbCapture.IsHdrpPipeline())
            {
                Assert.Ignore("HDRP pipeline active; URP passthrough not applicable.");
            }
            Assert.AreEqual(0f, UniThumbCapture.ResolveLight3DIntensity(0f), 0.001f);
            Assert.AreEqual(1f, UniThumbCapture.ResolveLight3DIntensity(1f), 0.001f);
            Assert.AreEqual(5f, UniThumbCapture.ResolveLight3DIntensity(5f), 0.001f);
            Assert.AreEqual(10f, UniThumbCapture.ResolveLight3DIntensity(10f), 0.001f);
        }

        [Test]
        public void ResolveLight3DIntensity_ClampThenScaleComposition()
        {
            float _expectedHigh = UniThumbCapture.ResolveLight3DIntensity(10f);
            float _actualHigh = UniThumbCapture.ResolveLight3DIntensity(
                UniThumbCapture.ClampLight3DIntensity(999f)
            );
            Assert.AreEqual(_expectedHigh, _actualHigh, 0.001f);
            float _expectedLow = UniThumbCapture.ResolveLight3DIntensity(0f);
            float _actualLow = UniThumbCapture.ResolveLight3DIntensity(
                UniThumbCapture.ClampLight3DIntensity(-5f)
            );
            Assert.AreEqual(_expectedLow, _actualLow, 0.001f);
        }

        [Test]
        public void HdrpLightScaleConst_Is20000()
        {
            FieldInfo _field = typeof(UniThumbCapture).GetField(
                "k_HdrpLight3DIntensityScale",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(_field, "Missing k_HdrpLight3DIntensityScale.");
            Assert.AreEqual(20000f, (float)_field.GetValue(null), 0.001f);
        }

        [Test]
        public void HdrpFixedExposure_DefaultIs10()
        {
            Assert.AreEqual(10f, UniThumbCapture.HdrpFixedExposure, 0.001f);
        }

        [Test]
        public void PrefabFallbackPitchConst_Is50()
        {
            FieldInfo _field = typeof(UniThumbCapture).GetField(
                "k_PrefabFallbackKeyPitch",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(_field, "Missing k_PrefabFallbackKeyPitch.");
            Assert.AreEqual(50f, (float)_field.GetValue(null), 0.001f);
        }

        [Test]
        public void LimitsUxml_FloatFieldDefaults()
        {
            string _uxml = ReadWindowUxml();
            AssertUxmlFragment(_uxml, "limits-intensity-max", "value=\"5\"");
            AssertUxmlFragment(_uxml, "limits-yaw-min", "value=\"-180\"");
            AssertUxmlFragment(_uxml, "limits-yaw-max", "value=\"180\"");
            AssertUxmlFragment(_uxml, "limits-pitch-min", "value=\"-89\"");
            AssertUxmlFragment(_uxml, "limits-pitch-max", "value=\"89\"");
        }

        [Test]
        public void LightingUxml_SliderRangeDefaults()
        {
            string _uxml = ReadWindowUxml();
            AssertUxmlFragment(_uxml, "light3d-intensity", "low-value=\"0\"");
            AssertUxmlFragment(_uxml, "light3d-intensity", "high-value=\"5\"");
            AssertUxmlFragment(_uxml, "light3d-intensity", "value=\"1\"");
            AssertUxmlFragment(_uxml, "light3d-yaw", "low-value=\"-180\"");
            AssertUxmlFragment(_uxml, "light3d-yaw", "high-value=\"180\"");
            AssertUxmlFragment(_uxml, "light3d-yaw", "value=\"50\"");
            AssertUxmlFragment(_uxml, "light3d-pitch", "low-value=\"-89\"");
            AssertUxmlFragment(_uxml, "light3d-pitch", "high-value=\"89\"");
            AssertUxmlFragment(_uxml, "light3d-pitch", "value=\"-30\"");
        }

        [Test]
        public void SliderRange_IntensityHighFollowsCustomMax()
        {
            _settings.SetLight3DIntensityMax(8f);
            Assert.AreEqual(8f, _settings.Light3DIntensityMax, 0.001f);
            _settings.SetLight3DIntensity(9f);
            Assert.AreEqual(_settings.Light3DIntensityMax, _settings.Light3DIntensity, 0.001f);
        }

        [Test]
        public void SliderRange_YawPitchFollowCustomLimits()
        {
            _settings.SetLight3DYawMin(-90f);
            _settings.SetLight3DYawMax(90f);
            Assert.AreEqual(-90f, _settings.Light3DYawMin, 0.001f);
            Assert.AreEqual(90f, _settings.Light3DYawMax, 0.001f);
            _settings.SetLight3DPitchMin(-45f);
            _settings.SetLight3DPitchMax(45f);
            Assert.AreEqual(-45f, _settings.Light3DPitchMin, 0.001f);
            Assert.AreEqual(45f, _settings.Light3DPitchMax, 0.001f);
        }

        [Test]
        public void PrefabNeutral_SolidColorUsesMidGreyFlat()
        {
            Material _originalSkybox = RenderSettings.skybox;
            bool _originalFog = RenderSettings.fog;
            AmbientMode _originalMode = RenderSettings.ambientMode;
            Color _originalLight = RenderSettings.ambientLight;
            float _originalIntensity = RenderSettings.ambientIntensity;
            float _originalReflection = RenderSettings.reflectionIntensity;
            try
            {
                UniThumbCapture.PrefabEnvSnapshot _snapshot =
                    UniThumbCapture.SnapshotPrefabEnvironment();
                Assert.IsTrue(_snapshot.Valid);
                RenderSettings.fog = true;
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = 0f;
                RenderSettings.reflectionIntensity = 1f;
                CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
                _capture.BackgroundMode = BackgroundMode.SolidColor;
                UniThumbCapture.NeutralizePrefabEnvironment(_capture);
                Assert.IsNull(RenderSettings.skybox);
                Assert.IsFalse(RenderSettings.fog);
                Assert.AreEqual(AmbientMode.Flat, RenderSettings.ambientMode);
                Assert.AreEqual(new Color(0.5f, 0.5f, 0.5f, 1f), RenderSettings.ambientLight);
                Assert.AreEqual(1f, RenderSettings.ambientIntensity, 0.001f);
                Assert.AreEqual(0f, RenderSettings.reflectionIntensity, 0.001f);
                UniThumbCapture.RestorePrefabEnvironment(_snapshot);
                Assert.AreEqual(_originalMode, RenderSettings.ambientMode);
                Assert.AreEqual(_originalLight, RenderSettings.ambientLight);
                Assert.AreEqual(_originalIntensity, RenderSettings.ambientIntensity, 0.001f);
            }
            finally
            {
                RenderSettings.skybox = _originalSkybox;
                RenderSettings.fog = _originalFog;
                RenderSettings.ambientMode = _originalMode;
                RenderSettings.ambientLight = _originalLight;
                RenderSettings.ambientIntensity = _originalIntensity;
                RenderSettings.reflectionIntensity = _originalReflection;
            }
        }

        [Test]
        public void PrefabNeutral_RestoreLeavesSceneUnchanged()
        {
            Material _originalSkybox = RenderSettings.skybox;
            bool _originalFog = RenderSettings.fog;
            AmbientMode _originalMode = RenderSettings.ambientMode;
            Color _originalLight = RenderSettings.ambientLight;
            Color _originalSky = RenderSettings.ambientSkyColor;
            Color _originalEquator = RenderSettings.ambientEquatorColor;
            Color _originalGround = RenderSettings.ambientGroundColor;
            float _originalIntensity = RenderSettings.ambientIntensity;
            float _originalReflection = RenderSettings.reflectionIntensity;
            try
            {
                UniThumbCapture.PrefabEnvSnapshot _snapshot =
                    UniThumbCapture.SnapshotPrefabEnvironment();
                CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
                _capture.BackgroundMode = BackgroundMode.Transparent;
                UniThumbCapture.NeutralizePrefabEnvironment(_capture);
                Assert.IsNull(RenderSettings.skybox);
                Assert.AreEqual(AmbientMode.Flat, RenderSettings.ambientMode);
                UniThumbCapture.RestorePrefabEnvironment(_snapshot);
                Assert.AreEqual(_originalSkybox, RenderSettings.skybox);
                Assert.AreEqual(_originalFog, RenderSettings.fog);
                Assert.AreEqual(_originalMode, RenderSettings.ambientMode);
                Assert.AreEqual(_originalLight, RenderSettings.ambientLight);
                Assert.AreEqual(_originalSky, RenderSettings.ambientSkyColor);
                Assert.AreEqual(_originalEquator, RenderSettings.ambientEquatorColor);
                Assert.AreEqual(_originalGround, RenderSettings.ambientGroundColor);
                Assert.AreEqual(_originalIntensity, RenderSettings.ambientIntensity, 0.001f);
                Assert.AreEqual(_originalReflection, RenderSettings.reflectionIntensity, 0.001f);
            }
            finally
            {
                RenderSettings.skybox = _originalSkybox;
                RenderSettings.fog = _originalFog;
                RenderSettings.ambientMode = _originalMode;
                RenderSettings.ambientLight = _originalLight;
                RenderSettings.ambientSkyColor = _originalSky;
                RenderSettings.ambientEquatorColor = _originalEquator;
                RenderSettings.ambientGroundColor = _originalGround;
                RenderSettings.ambientIntensity = _originalIntensity;
                RenderSettings.reflectionIntensity = _originalReflection;
            }
        }

        [Test]
        public void PrefabLightingNeedsNeutralize_UnchangedPin()
        {
            CaptureSettings _none = UniThumbCapture.CreateDefaultSettings();
            _none.Light2DMode = LightingMode.None;
            Assert.IsTrue(UniThumbCapture.PrefabLightingNeedsNeutralize(_none));
            CaptureSettings _light2D = UniThumbCapture.CreateDefaultSettings();
            _light2D.Light2DMode = LightingMode.Light2D;
            Assert.IsFalse(UniThumbCapture.PrefabLightingNeedsNeutralize(_light2D));
            CaptureSettings _light3D = UniThumbCapture.CreateDefaultSettings();
            _light3D.Light2DMode = LightingMode.Light3D;
            Assert.IsFalse(UniThumbCapture.PrefabLightingNeedsNeutralize(_light3D));
        }

        [Test]
        public void Light2D_DefaultsUnchanged()
        {
            CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(LightingMode.None, _capture.Light2DMode);
            Assert.AreEqual(1f, _capture.Light2DIntensity, 0.001f);
            Assert.AreEqual(1f, _settings.Light2DIntensity, 0.001f);
        }

        [Test]
        public void Light2D_ClampUnchanged()
        {
            _settings.SetLight2DIntensity(10f);
            Assert.AreEqual(5f, _settings.Light2DIntensity, 0.001f);
            _settings.SetLight2DIntensity(-1f);
            Assert.AreEqual(0f, _settings.Light2DIntensity, 0.001f);
        }

        [Test]
        public void Light2D_UnaffectedByLight3DLimits()
        {
            float _before = _settings.Light2DIntensity;
            _settings.SetLight3DIntensityMax(8f);
            _settings.SetLight3DYawMin(-90f);
            _settings.SetLight3DYawMax(90f);
            _settings.SetLight3DPitchMin(-45f);
            _settings.SetLight3DPitchMax(45f);
            Assert.AreEqual(_before, _settings.Light2DIntensity, 0.001f);
        }

        [Test]
        public void Default_ParticlePreviewTime_Is1()
        {
            Assert.AreEqual(1f, _settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void Default_ParticlePreviewTimeLimits_Are0To5()
        {
            Assert.AreEqual(0f, _settings.ParticlePreviewTimeMin, 0.001f);
            Assert.AreEqual(5f, _settings.ParticlePreviewTimeMax, 0.001f);
        }

        [Test]
        public void Default_ParticlePreviewTime_CaptureSettings_Is1()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(1f, settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void SetParticlePreviewTime_ClampsRange()
        {
            _settings.SetParticlePreviewTime(10f);
            Assert.AreEqual(5f, _settings.ParticlePreviewTime, 0.001f);
            _settings.SetParticlePreviewTime(-1f);
            Assert.AreEqual(0f, _settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void SetParticlePreviewTime_StoresValue()
        {
            _settings.SetParticlePreviewTime(2.5f);
            Assert.AreEqual(2.5f, _settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void LegacyNormalize_ZeroedParticlePreviewTime_RestoresDefault()
        {
            SetPrivateFloat("m_ParticlePreviewTime", 0f);
            InvokeNormalizeParticlePreviewTime();
            Assert.AreEqual(1f, _settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void LegacyNormalize_OverCapParticlePreviewTime_ClampsToMax()
        {
            SetPrivateBool("m_ParticlePreviewTimeMigrated", true);
            SetPrivateFloat("m_ParticlePreviewTime", 999f);
            InvokeNormalizeParticlePreviewTime();
            Assert.AreEqual(5f, _settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void LegacyNormalize_NegativeParticlePreviewTime_ClampsToMin()
        {
            SetPrivateBool("m_ParticlePreviewTimeMigrated", true);
            SetPrivateFloat("m_ParticlePreviewTime", -5f);
            InvokeNormalizeParticlePreviewTime();
            Assert.AreEqual(0f, _settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void LegacyNormalize_ExplicitZeroSurvivesAfterMigration()
        {
            SetPrivateBool("m_ParticlePreviewTimeMigrated", true);
            SetPrivateFloat("m_ParticlePreviewTime", 0f);
            InvokeNormalizeParticlePreviewTime();
            Assert.AreEqual(
                0f,
                _settings.ParticlePreviewTime,
                0.001f,
                "Explicit user 0 after migration must survive; only legacy 0 maps to 1."
            );
            InvokeNormalizeParticlePreviewTime();
            Assert.AreEqual(0f, _settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void SetParticlePreviewTime_DoesNotThrowOutsideAssetContext()
        {
            Assert.DoesNotThrow(() => _settings.SetParticlePreviewTime(2f));
            Assert.AreEqual(2f, _settings.ParticlePreviewTime, 0.001f);
            Assert.DoesNotThrow(() => _settings.SetParticlePreviewTime(0f));
            Assert.AreEqual(0f, _settings.ParticlePreviewTime, 0.001f);
        }

        [Test]
        public void PreviewUxml_ParticleSliderRangeDefaults()
        {
            string _uxml = ReadWindowUxml();
            AssertUxmlFragment(_uxml, "particle-preview-time-slider", "low-value=\"0\"");
            AssertUxmlFragment(_uxml, "particle-preview-time-slider", "high-value=\"5\"");
            AssertUxmlFragment(_uxml, "particle-preview-time-slider", "value=\"1\"");
        }

        [Test]
        public void PreviewUxml_ParticleSliderLivesInPreviewCard()
        {
            string _uxml = ReadWindowUxml();
            int _previewIndex = _uxml.IndexOf("text=\"Preview\"", StringComparison.Ordinal);
            Assert.GreaterOrEqual(_previewIndex, 0, "UXML missing the Preview card header.");
            int _sliderIndex = _uxml.IndexOf(
                "name=\"particle-preview-time-slider\"",
                StringComparison.Ordinal
            );
            Assert.GreaterOrEqual(_sliderIndex, 0, "UXML missing the particle slider.");
            Assert.Greater(
                _sliderIndex,
                _previewIndex,
                "Particle slider must live in the Preview card after its header."
            );
        }

        [Test]
        public void ParticlePreviewTime_PersistRoundTrip()
        {
            UniThumbWindow _window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                Assert.IsTrue(_window != null);
                _settings.SetParticlePreviewTime(2.5f);
                _window.ApplyPersistedCaptureSettings(_settings);
                _window.PersistCaptureSettings(_settings);
                Assert.AreEqual(2.5f, _settings.ParticlePreviewTime, 0.001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_window);
            }
        }

        [Test]
        public void ParticlePreviewTime_BuildSettingsClamps()
        {
            UniThumbWindow _window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                Assert.IsTrue(_window != null);
                SetWindowPreviewTime(_window, 999f);
                CaptureSettings _over = InvokeBuildSettings(_window);
                Assert.AreEqual(5f, _over.ParticlePreviewTime, 0.001f);
                SetWindowPreviewTime(_window, -5f);
                CaptureSettings _under = InvokeBuildSettings(_window);
                Assert.AreEqual(0f, _under.ParticlePreviewTime, 0.001f);
                SetWindowPreviewTime(_window, 2.5f);
                CaptureSettings _mid = InvokeBuildSettings(_window);
                Assert.AreEqual(2.5f, _mid.ParticlePreviewTime, 0.001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_window);
            }
        }

        [Test]
        public void ParticleSlider_MarksPreviewDirty()
        {
            UniThumbWindow _window = EditorWindow.GetWindow<UniThumbWindow>();
            try
            {
                Assert.IsTrue(_window != null);
                Assert.IsTrue(_window.rootVisualElement != null);
                Slider _slider = _window.rootVisualElement.Q<Slider>(
                    "particle-preview-time-slider"
                );
                Assert.IsTrue(_slider != null, "Preview card must host the particle slider.");
                Assert.AreEqual(0f, _slider.lowValue, 0.001f);
                Assert.AreEqual(5f, _slider.highValue, 0.001f);
                _slider.value = 2.5f;
                Assert.IsTrue(
                    _window.IsPreviewDirty,
                    "Particle slider change must mark the preview dirty."
                );
            }
            finally
            {
                _window.Close();
            }
        }

        [Test]
        public void ParticlePreviewTime_DoesNotAffectLightPins()
        {
            _settings.SetParticlePreviewTime(3f);
            Assert.AreEqual(3f, _settings.ParticlePreviewTime, 0.001f);
            Assert.AreEqual(1f, _settings.Light2DIntensity, 0.001f);
            Assert.AreEqual(1f, _settings.Light3DIntensity, 0.001f);
            Assert.AreEqual(50f, _settings.Light3DYaw, 0.001f);
            Assert.AreEqual(-30f, _settings.Light3DPitch, 0.001f);
            CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(LightingMode.None, _capture.Light2DMode);
            Assert.AreEqual(1f, _capture.Light2DIntensity, 0.001f);
            Assert.AreEqual(50f, _capture.Light3DYaw, 0.001f);
            Assert.AreEqual(-30f, _capture.Light3DPitch, 0.001f);
        }

        private void SetPrivateFloat(string _name, float _value)
        {
            FieldInfo _field = typeof(UniThumbSettings).GetField(
                _name,
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_field, "Missing field " + _name + ".");
            _field.SetValue(_settings, _value);
        }

        private void SetPrivateBool(string _name, bool _value)
        {
            FieldInfo _field = typeof(UniThumbSettings).GetField(
                _name,
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_field, "Missing field " + _name + ".");
            _field.SetValue(_settings, _value);
        }

        private static void SetWindowPreviewTime(UniThumbWindow _window, float _value)
        {
            FieldInfo _field = typeof(UniThumbWindow).GetField(
                "_particlePreviewTime",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_field, "Missing _particlePreviewTime.");
            _field.SetValue(_window, _value);
        }

        private static CaptureSettings InvokeBuildSettings(UniThumbWindow _window)
        {
            MethodInfo _method = typeof(UniThumbWindow).GetMethod(
                "BuildSettings",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_method, "Missing BuildSettings.");
            return (CaptureSettings)_method.Invoke(_window, null);
        }

        private void InvokeNormalizeLightLimits()
        {
            MethodInfo _method = typeof(UniThumbSettings).GetMethod(
                "TryNormalizeLightLimits",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_method, "Missing TryNormalizeLightLimits.");
            _method.Invoke(_settings, null);
        }

        private void InvokeNormalizeParticlePreviewTime()
        {
            MethodInfo _method = typeof(UniThumbSettings).GetMethod(
                "TryNormalizeParticlePreviewTime",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_method, "Missing TryNormalizeParticlePreviewTime.");
            _method.Invoke(_settings, null);
        }

        private static string ReadWindowUxml()
        {
            string _assetPath = UniThumbPackagePaths.EditorFolderAssetPath + "/UniThumbWindow.uxml";
            string _fullPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", _assetPath)
            );
            Assert.IsTrue(File.Exists(_fullPath), "UXML not found at " + _fullPath + ".");
            return File.ReadAllText(_fullPath);
        }

        private static void AssertUxmlFragment(string _uxml, string _name, string _fragment)
        {
            int _index = _uxml.IndexOf("name=\"" + _name + "\"", StringComparison.Ordinal);
            Assert.GreaterOrEqual(_index, 0, "UXML missing field " + _name + ".");
            int _length = Math.Min(400, _uxml.Length - _index);
            string _window = _uxml.Substring(_index, _length);
            Assert.IsTrue(
                _window.Contains(_fragment),
                "UXML field " + _name + " missing " + _fragment + "."
            );
        }
    }
}
