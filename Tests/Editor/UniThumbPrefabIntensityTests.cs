using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbPrefabIntensityTests
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
        public void Defaults_SceneIs1_PrefabIs175_LimitsIntact()
        {
            Assert.AreEqual(1f, _settings.Light3DIntensity, 0.001f);
            Assert.AreEqual(1.75f, _settings.Light3DPrefabIntensity, 0.001f);
            Assert.AreEqual(5f, _settings.Light3DIntensityMax, 0.001f);
            Assert.AreEqual(-180f, _settings.Light3DYawMin, 0.001f);
            Assert.AreEqual(180f, _settings.Light3DYawMax, 0.001f);
            Assert.AreEqual(-89f, _settings.Light3DPitchMin, 0.001f);
            Assert.AreEqual(89f, _settings.Light3DPitchMax, 0.001f);
        }

        [Test]
        public void Defaults_CaptureSettings_CarryScene1AndPrefab175()
        {
            CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(1f, _capture.Light3DIntensity, 0.001f);
            Assert.AreEqual(1.75f, _capture.Light3DPrefabIntensity, 0.001f);
        }

        [Test]
        public void SetPrefabIntensity_ClampsToActiveMax()
        {
            _settings.SetLight3DIntensityMax(2f);
            _settings.SetLight3DPrefabIntensity(9f);
            Assert.AreEqual(2f, _settings.Light3DPrefabIntensity, 0.001f);
        }

        [Test]
        public void SetPrefabIntensity_ClampsNegativeToZero()
        {
            _settings.SetLight3DPrefabIntensity(-3f);
            Assert.AreEqual(0f, _settings.Light3DPrefabIntensity, 0.001f);
        }

        [Test]
        public void SetMax_NarrowsPrefabWithoutTouchingScene()
        {
            _settings.SetLight3DIntensity(1f);
            _settings.SetLight3DPrefabIntensity(4f);
            _settings.SetLight3DIntensityMax(2f);
            Assert.AreEqual(2f, _settings.Light3DIntensityMax, 0.001f);
            Assert.AreEqual(1f, _settings.Light3DIntensity, 0.001f);
            Assert.AreEqual(2f, _settings.Light3DPrefabIntensity, 0.001f);
        }

        [Test]
        public void LegacyNormalize_ZeroPrefabRestores175()
        {
            SetPrivateFloat("m_Light3DPrefabIntensity", 0f);
            InvokeNormalizeLightLimits();
            Assert.AreEqual(1.75f, _settings.Light3DPrefabIntensity, 0.001f);
            Assert.AreEqual(1f, _settings.Light3DIntensity, 0.001f);
        }

        [Test]
        public void LegacyNormalize_OverMaxPrefabClampsToMax()
        {
            SetPrivateFloat("m_Light3DPrefabIntensity", 999f);
            InvokeNormalizeLightLimits();
            Assert.AreEqual(5f, _settings.Light3DPrefabIntensity, 0.001f);
        }

        [Test]
        public void PrefabAndScene_VaryIndependently()
        {
            _settings.SetLight3DIntensity(3f);
            Assert.AreEqual(1.75f, _settings.Light3DPrefabIntensity, 0.001f);
            _settings.SetLight3DPrefabIntensity(4f);
            Assert.AreEqual(3f, _settings.Light3DIntensity, 0.001f);
            _settings.SetLight3DIntensity(1f);
            Assert.AreEqual(4f, _settings.Light3DPrefabIntensity, 0.001f);
        }

        [Test]
        public void PrefabIntensity_ClampThenScaleComposition()
        {
            float _expectedHigh = UniThumbCapture.ResolveLight3DIntensity(10f);
            float _actualHigh = UniThumbCapture.ResolveLight3DIntensity(
                UniThumbCapture.ClampLight3DIntensity(999f)
            );
            Assert.AreEqual(_expectedHigh, _actualHigh, 0.001f);
            float _expectedPrefab = UniThumbCapture.ResolveLight3DIntensity(1.75f);
            float _actualPrefab = UniThumbCapture.ResolveLight3DIntensity(
                UniThumbCapture.ClampLight3DIntensity(1.75f)
            );
            Assert.AreEqual(_expectedPrefab, _actualPrefab, 0.001f);
        }

        [Test]
        public void PrefabIntensity_HdrpScaleCoversClampedPrefabValue()
        {
            FieldInfo _field = typeof(UniThumbCapture).GetField(
                "k_HdrpLight3DIntensityScale",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(_field, "Missing k_HdrpLight3DIntensityScale.");
            Assert.AreEqual(20000f, (float)_field.GetValue(null), 0.001f);
            if (UniThumbCapture.IsHdrpPipeline())
            {
                Assert.AreEqual(
                    35000f,
                    UniThumbCapture.ResolveLight3DIntensity(
                        UniThumbCapture.ClampLight3DIntensity(1.75f)
                    ),
                    0.5f
                );
                Assert.AreEqual(
                    200000f,
                    UniThumbCapture.ResolveLight3DIntensity(
                        UniThumbCapture.ClampLight3DIntensity(999f)
                    ),
                    0.5f
                );
            }
            else
            {
                Assert.AreEqual(
                    1.75f,
                    UniThumbCapture.ResolveLight3DIntensity(
                        UniThumbCapture.ClampLight3DIntensity(1.75f)
                    ),
                    0.001f
                );
            }
        }

        [Test]
        public void Uxml_PrefabSlider_Defaults()
        {
            string _uxml = ReadWindowUxml();
            AssertUxmlFragment(_uxml, "light3d-prefab-intensity", "low-value=\"0\"");
            AssertUxmlFragment(_uxml, "light3d-prefab-intensity", "high-value=\"5\"");
            AssertUxmlFragment(_uxml, "light3d-prefab-intensity", "value=\"1.75\"");
            AssertUxmlFragment(_uxml, "light3d-prefab-intensity", "prefab captures only");
        }

        [Test]
        public void Uxml_PrefabSlider_LivesInLightingCard()
        {
            string _uxml = ReadWindowUxml();
            int _lighting = _uxml.IndexOf("lighting-foldout", StringComparison.Ordinal);
            int _slider = _uxml.IndexOf("light3d-prefab-intensity", StringComparison.Ordinal);
            int _limits = _uxml.IndexOf("limits-card", StringComparison.Ordinal);
            Assert.GreaterOrEqual(_lighting, 0, "UXML missing lighting-foldout.");
            Assert.GreaterOrEqual(_slider, 0, "UXML missing light3d-prefab-intensity.");
            Assert.GreaterOrEqual(_limits, 0, "UXML missing limits-card.");
            Assert.Greater(_slider, _lighting, "Prefab slider is not inside the Lighting card.");
            Assert.Greater(_limits, _slider, "Prefab slider leaked into the Limits card.");
        }

        [Test]
        public void Uxml_SceneSlider_Unchanged()
        {
            string _uxml = ReadWindowUxml();
            int _index = _uxml.IndexOf("name=\"light3d-intensity\"", StringComparison.Ordinal);
            Assert.GreaterOrEqual(_index, 0, "UXML missing field light3d-intensity.");
            int _length = Math.Min(500, _uxml.Length - _index);
            Assert.IsTrue(
                _uxml.Substring(_index, _length).Contains("value=\"1\""),
                "UXML field light3d-intensity missing value=\"1\"."
            );
        }

        [Test]
        public void Aim_CenteredBounds_ParallelToViewRay()
        {
            GameObject _camGo = new GameObject("UniThumbTestPrefabAimCentered");
            try
            {
                Camera _cam = _camGo.AddComponent<Camera>();
                CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
                _capture.BackgroundMode = BackgroundMode.SolidColor;
                _cam.aspect = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    _cam,
                    _capture,
                    Vector3.zero,
                    1f,
                    new Vector3(1f, 1f, 1f),
                    true,
                    false
                );
                float _yaw;
                float _pitch;
                UniThumbCapture.ResolvePrefabFallbackAim(_cam, _capture, out _yaw, out _pitch);
                Vector3 _lightForward = Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.forward;
                Assert.Greater(
                    Vector3.Dot(_lightForward, _cam.transform.forward.normalized),
                    0.999f
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_camGo);
            }
        }

        [Test]
        public void Aim_OffCenterBounds_HitsBoundsCenter()
        {
            GameObject _camGo = new GameObject("UniThumbTestPrefabAimOffCenter");
            try
            {
                Camera _cam = _camGo.AddComponent<Camera>();
                CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
                _capture.BackgroundMode = BackgroundMode.SolidColor;
                Vector3 _center = new Vector3(10f, 2f, -5f);
                _cam.aspect = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    _cam,
                    _capture,
                    _center,
                    1f,
                    new Vector3(1f, 1f, 1f),
                    true,
                    false
                );
                float _yaw;
                float _pitch;
                UniThumbCapture.ResolvePrefabFallbackAim(_cam, _capture, out _yaw, out _pitch);
                Vector3 _lightForward = Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.forward;
                Vector3 _toCenter = (_center - _cam.transform.position).normalized;
                Assert.Greater(Vector3.Dot(_lightForward, _toCenter), 0.999f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_camGo);
            }
        }

        [Test]
        public void Aim_LimitsClampAim()
        {
            GameObject _camGo = new GameObject("UniThumbTestPrefabAimLimits");
            try
            {
                Camera _cam = _camGo.AddComponent<Camera>();
                CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
                _capture.BackgroundMode = BackgroundMode.SolidColor;
                _capture.Light3DYawMin = 0f;
                _capture.Light3DYawMax = 20f;
                _capture.Light3DPitchMin = 0f;
                _capture.Light3DPitchMax = 20f;
                _cam.aspect = 1f;
                UniThumbCapture.ApplyOrbitTransformToBounds(
                    _cam,
                    _capture,
                    Vector3.zero,
                    1f,
                    new Vector3(1f, 1f, 1f),
                    true,
                    false
                );
                float _yaw;
                float _pitch;
                UniThumbCapture.ResolvePrefabFallbackAim(_cam, _capture, out _yaw, out _pitch);
                Assert.GreaterOrEqual(_yaw, 0f);
                Assert.LessOrEqual(_yaw, 20f);
                Assert.GreaterOrEqual(_pitch, 0f);
                Assert.LessOrEqual(_pitch, 20f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_camGo);
            }
        }

        [Test]
        public void Aim_NullCamera_FallsBackToDefaults()
        {
            CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
            _capture.BackgroundMode = BackgroundMode.SolidColor;
            float _yaw;
            float _pitch;
            UniThumbCapture.ResolvePrefabFallbackAim(null, _capture, out _yaw, out _pitch);
            Assert.AreEqual(50f, _yaw, 0.001f);
            Assert.AreEqual(-30f, _pitch, 0.001f);
        }

        [Test]
        public void SceneIntensity_PathUnchanged()
        {
            CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(1f, _capture.Light3DIntensity, 0.001f);
            _settings.SetLight3DIntensity(10f);
            Assert.AreEqual(5f, _settings.Light3DIntensity, 0.001f);
            _settings.SetLight3DIntensity(-1f);
            Assert.AreEqual(0f, _settings.Light3DIntensity, 0.001f);
        }

        [Test]
        public void Light2D_PathUnchanged()
        {
            CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
            Assert.AreEqual(1f, _capture.Light2DIntensity, 0.001f);
            Assert.AreEqual(1f, _settings.Light2DIntensity, 0.001f);
            _settings.SetLight2DIntensity(10f);
            Assert.AreEqual(5f, _settings.Light2DIntensity, 0.001f);
            _settings.SetLight2DIntensity(-1f);
            Assert.AreEqual(0f, _settings.Light2DIntensity, 0.001f);
            float _before = 1f;
            _settings.SetLight2DIntensity(_before);
            _settings.SetLight3DIntensityMax(8f);
            _settings.SetLight3DPrefabIntensity(4f);
            Assert.AreEqual(_before, _settings.Light2DIntensity, 0.001f);
        }

        [Test]
        public void Fallback_OwnKey_ReturnsNone()
        {
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
        }

        [Test]
        public void ProceduralLastResort_Preserved()
        {
            FieldInfo _field = typeof(UniThumbCapture).GetField(
                "k_PrefabFallbackKeyPitch",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(_field, "Missing k_PrefabFallbackKeyPitch.");
            Assert.AreEqual(50f, (float)_field.GetValue(null), 0.001f);
            Shader _skyShader = Shader.Find("Skybox/Procedural");
            if (_skyShader == null)
            {
                Assert.Ignore("Procedural skybox shader unavailable in this project.");
            }
            Material _skybox = new Material(_skyShader);
            Material _originalSkybox = RenderSettings.skybox;
            try
            {
                RenderSettings.skybox = _skybox;
                CaptureSettings _capture = UniThumbCapture.CreateDefaultSettings();
                _capture.BackgroundMode = BackgroundMode.Skybox;
                float _yaw;
                float _pitch;
                UniThumbCapture.ResolvePrefabFallbackAim(null, _capture, out _yaw, out _pitch);
                Assert.AreEqual(50f, _yaw, 0.001f);
                Assert.AreEqual(50f, _pitch, 0.001f);
            }
            finally
            {
                RenderSettings.skybox = _originalSkybox;
                UnityEngine.Object.DestroyImmediate(_skybox);
            }
        }

        [Test]
        public void Uxml_PrefabMaxLabel_InLightingCard()
        {
            string _uxml = ReadWindowUxml();
            int _slider = _uxml.IndexOf("light3d-prefab-intensity\"", StringComparison.Ordinal);
            int _max = _uxml.IndexOf("light3d-prefab-intensity-max", StringComparison.Ordinal);
            int _limits = _uxml.IndexOf("limits-card", StringComparison.Ordinal);
            int _lighting = _uxml.IndexOf("lighting-foldout", StringComparison.Ordinal);
            Assert.GreaterOrEqual(_slider, 0, "UXML missing light3d-prefab-intensity.");
            Assert.GreaterOrEqual(_max, 0, "UXML missing light3d-prefab-intensity-max.");
            Assert.Greater(_max, _slider, "Max label is not next to the prefab slider.");
            Assert.Greater(_limits, _max, "Max label leaked into the Limits card.");
            Assert.Greater(_max, _lighting, "Max label is not inside the Lighting card.");
            AssertUxmlFragment(_uxml, "light3d-prefab-intensity-max", "stt-mini");
            AssertUxmlFragment(_uxml, "light3d-prefab-intensity-max", "stt-hint");
            AssertUxmlFragment(_uxml, "light3d-prefab-intensity-max", "Max 5.00");
        }

        [Test]
        public void Uxml_PrefabMaxLabel_LeavesSceneAndLight2DUntouched()
        {
            string _uxml = ReadWindowUxml();
            Assert.IsTrue(
                _uxml.IndexOf("light3d-intensity-max", StringComparison.Ordinal) < 0,
                "Scene slider must not gain a max label."
            );
            Assert.IsTrue(
                _uxml.IndexOf("light2d-intensity-max", StringComparison.Ordinal) < 0,
                "Light2D must stay untouched."
            );
        }

        [Test]
        public void FormatPrefabIntensityMaxLabel_DefaultsAndNarrow()
        {
            Assert.AreEqual("Max 5.00", UniThumbWindow.FormatPrefabIntensityMaxLabel(5f));
            Assert.AreEqual("Max 2.00", UniThumbWindow.FormatPrefabIntensityMaxLabel(2f));
            Assert.AreEqual("Max 1.75", UniThumbWindow.FormatPrefabIntensityMaxLabel(1.75f));
        }

        [Test]
        public void Window_RangesUpdate_SetsSliderHighAndMaxLabel()
        {
            UniThumbWindow _window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                SetWindowFloat(_window, "_light3DIntensityMax", 5f);
                SetWindowFloat(_window, "_light3DPrefabIntensity", 1.75f);
                Slider _slider = new Slider(0f, 10f);
                Label _max = new Label();
                SetWindowObject(_window, "_light3DPrefabIntensitySlider", _slider);
                SetWindowObject(_window, "_light3DPrefabIntensityMaxLabel", _max);
                InvokeWindow(_window, "UpdateLight3DSliderRanges");
                Assert.AreEqual(5f, _slider.highValue, 0.001f);
                Assert.AreEqual("Max 5.00", _max.text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_window);
            }
        }

        [Test]
        public void Window_MaxLabel_NarrowKeepsSceneSliderHigh()
        {
            UniThumbWindow _window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                SetWindowFloat(_window, "_light3DIntensityMax", 2f);
                SetWindowFloat(_window, "_light3DIntensity", 1f);
                SetWindowFloat(_window, "_light3DPrefabIntensity", 1.75f);
                Slider _scene = new Slider(0f, 10f);
                Slider _prefab = new Slider(0f, 10f);
                Label _max = new Label();
                SetWindowObject(_window, "_light3DIntensitySlider", _scene);
                SetWindowObject(_window, "_light3DPrefabIntensitySlider", _prefab);
                SetWindowObject(_window, "_light3DPrefabIntensityMaxLabel", _max);
                InvokeWindow(_window, "UpdateLight3DSliderRanges");
                Assert.AreEqual(2f, _scene.highValue, 0.001f);
                Assert.AreEqual(2f, _prefab.highValue, 0.001f);
                Assert.AreEqual("Max 2.00", _max.text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_window);
            }
        }

        [Test]
        public void Window_MaxLabel_ReopenDefaults()
        {
            UniThumbWindow _window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                SetWindowFloat(_window, "_light3DIntensityMax", 5f);
                SetWindowFloat(_window, "_light3DPrefabIntensity", 1.75f);
                Slider _prefab = new Slider(0f, 10f);
                Label _max = new Label();
                SetWindowObject(_window, "_light3DPrefabIntensitySlider", _prefab);
                SetWindowObject(_window, "_light3DPrefabIntensityMaxLabel", _max);
                InvokeWindow(_window, "UpdateLight3DSliderRanges");
                Assert.AreEqual(5f, _prefab.highValue, 0.001f);
                Assert.AreEqual("Max 5.00", _max.text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_window);
            }
        }

        [Test]
        public void Window_MaxLabel_VisibilityFollowsPrefabSlider()
        {
            UniThumbWindow _window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                Slider _prefab = new Slider(0f, 10f);
                Label _max = new Label();
                SetWindowObject(_window, "_light3DPrefabIntensitySlider", _prefab);
                SetWindowObject(_window, "_light3DPrefabIntensityMaxLabel", _max);
                SetWindowMode(_window, LightingMode.None);
                InvokeWindow(_window, "UpdateLightingVisibility");
                Assert.IsFalse(_max.ClassListContains("stt-hidden"));
                SetWindowMode(_window, LightingMode.Light3D);
                InvokeWindow(_window, "UpdateLightingVisibility");
                Assert.IsFalse(_max.ClassListContains("stt-hidden"));
                SetWindowMode(_window, LightingMode.Light2D);
                InvokeWindow(_window, "UpdateLightingVisibility");
                Assert.IsTrue(_max.ClassListContains("stt-hidden"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(_window);
            }
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

        private void InvokeNormalizeLightLimits()
        {
            MethodInfo _method = typeof(UniThumbSettings).GetMethod(
                "TryNormalizeLightLimits",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_method, "Missing TryNormalizeLightLimits.");
            _method.Invoke(_settings, null);
        }

        private static void SetWindowFloat(UniThumbWindow _window, string _name, float _value)
        {
            FieldInfo _field = typeof(UniThumbWindow).GetField(
                _name,
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_field, "Missing field " + _name + ".");
            _field.SetValue(_window, _value);
        }

        private static void SetWindowObject(UniThumbWindow _window, string _name, object _value)
        {
            FieldInfo _field = typeof(UniThumbWindow).GetField(
                _name,
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_field, "Missing field " + _name + ".");
            _field.SetValue(_window, _value);
        }

        private static void SetWindowMode(UniThumbWindow _window, LightingMode _mode)
        {
            FieldInfo _field = typeof(UniThumbWindow).GetField(
                "_lightingMode",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_field, "Missing field _lightingMode.");
            _field.SetValue(_window, _mode);
        }

        private static void InvokeWindow(UniThumbWindow _window, string _name)
        {
            MethodInfo _method = typeof(UniThumbWindow).GetMethod(
                _name,
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_method, "Missing " + _name + ".");
            _method.Invoke(_window, null);
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
            int _length = Math.Min(500, _uxml.Length - _index);
            string _window = _uxml.Substring(_index, _length);
            Assert.IsTrue(
                _window.Contains(_fragment),
                "UXML field " + _name + " missing " + _fragment + "."
            );
        }
    }
}
